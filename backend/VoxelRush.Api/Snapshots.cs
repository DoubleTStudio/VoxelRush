using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;

namespace VoxelRush.Api;

public sealed record SnapshotUploadRequest(int X, int Y, int Z, long Revision, string? Sha256, int SizeBytes);

public sealed record SnapshotCommitRequest(int X, int Y, int Z, long Revision);

public sealed record PresignedUrl(string Url, string StorageKey, DateTimeOffset ExpiresAt);

public enum SnapshotError
{
    None,
    NotFound,
    Invalid,
    ChecksumMismatch,
    Missing,
    QuotaExceeded,
}

/// <summary>
/// Chunk snapshots in S3-compatible storage (R2/MinIO/S3) with a PostgreSQL index (SAVE_GAME storage split).
/// Game servers upload through short-lived presigned URLs, so storage credentials stay in this service; the index
/// only points at an object after its size and SHA-256 were verified. Keys are built from validated ids and
/// integers only, so no caller-supplied text reaches a storage path.
/// </summary>
public sealed class SnapshotStore(NpgsqlDataSource db, IAmazonS3 s3, ApiOptions options, TimeProvider clock)
{
    public const int MaxChunkCoordinate = 1_000_000;

    // Epoch 0 keeps the original layout; later epochs (after a restore) get their own prefix so revisions can repeat.
    public static string KeyFor(Guid world, int epoch, int x, int y, int z, long revision) => epoch == 0
        ? $"worlds/{world:N}/chunks/{x}_{y}_{z}/{revision}.snap"
        : $"worlds/{world:N}/e{epoch}/chunks/{x}_{y}_{z}/{revision}.snap";

    // Without storage the routes answer 501 before using the client, but endpoint binding still constructs it.
    public static IAmazonS3 CreateClient(SnapshotStoreOptions o) => new AmazonS3Client(
        new BasicAWSCredentials(o.AccessKey, o.SecretKey),
        new AmazonS3Config { ServiceURL = string.IsNullOrEmpty(o.ServiceUrl) ? "http://storage.invalid" : o.ServiceUrl, ForcePathStyle = true, AuthenticationRegion = o.Region });

    public async Task EnsureBucketAsync(CancellationToken ct)
    {
        try
        {
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = options.Snapshots.Bucket }, ct);
        }
        catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }
    }

    public async Task<(SnapshotError, PresignedUrl?)> CreateUploadAsync(Guid world, SnapshotUploadRequest r, CancellationToken ct)
    {
        byte[]? sha = ParseSha(r.Sha256);
        if (!ValidCoords(r.X, r.Y, r.Z) || r.Revision < 0 || sha == null || r.SizeBytes <= 0 || r.SizeBytes > options.Snapshots.MaxSnapshotBytes)
        {
            return (SnapshotError.Invalid, null);
        }

        if (await EpochAsync(world, ct) is not int epoch)
        {
            return (SnapshotError.NotFound, null);
        }

        if (await StoredBytesAsync(world, ct) is var (used, limit) && used + r.SizeBytes > limit)
        {
            return (SnapshotError.QuotaExceeded, null);
        }

        string key = KeyFor(world, epoch, r.X, r.Y, r.Z, r.Revision);
        await using NpgsqlCommand cmd = db.CreateCommand(
            "INSERT INTO chunk_snapshots (world_id, epoch, chunk_x, chunk_y, chunk_z, revision, storage_key, sha256, size_bytes, status) " +
            "VALUES ($1, $9, $2, $3, $4, $5, $6, $7, $8, 'pending') " +
            "ON CONFLICT (world_id, epoch, chunk_x, chunk_y, chunk_z, revision) DO UPDATE SET sha256 = EXCLUDED.sha256, size_bytes = EXCLUDED.size_bytes " +
            "WHERE chunk_snapshots.status = 'pending'");
        foreach (object v in new object[] { world, r.X, r.Y, r.Z, r.Revision, key, sha, r.SizeBytes, epoch })
        {
            cmd.Parameters.Add(new() { Value = v });
        }

        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
        {
            // A committed snapshot is immutable.
            return (SnapshotError.Invalid, null);
        }

        return (SnapshotError.None, Presign(key, HttpVerb.PUT));
    }

    public async Task<SnapshotError> CommitAsync(Guid world, SnapshotCommitRequest r, CancellationToken ct)
    {
        if (!ValidCoords(r.X, r.Y, r.Z) || r.Revision < 0)
        {
            return SnapshotError.Invalid;
        }

        if (await EpochAsync(world, ct) is not int epoch)
        {
            return SnapshotError.NotFound;
        }

        string key = KeyFor(world, epoch, r.X, r.Y, r.Z, r.Revision);
        byte[] expected;
        int size;
        await using (NpgsqlCommand select = db.CreateCommand("SELECT sha256, size_bytes FROM chunk_snapshots WHERE world_id = $1 AND storage_key = $2 AND status = 'pending'"))
        {
            select.Parameters.Add(new() { Value = world });
            select.Parameters.Add(new() { Value = key });
            await using NpgsqlDataReader reader = await select.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return SnapshotError.NotFound;
            }

            (expected, size) = ((byte[])reader[0], reader.GetInt32(1));
        }

        byte[] actual;
        long length;
        try
        {
            using GetObjectResponse obj = await s3.GetObjectAsync(options.Snapshots.Bucket, key, ct);
            length = obj.ContentLength;
            if (length > options.Snapshots.MaxSnapshotBytes)
            {
                return SnapshotError.ChecksumMismatch;
            }

            await using Stream body = obj.ResponseStream;
            actual = await SHA256.HashDataAsync(body, ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return SnapshotError.Missing;
        }

        if (length != size || !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            // Never index unverified data; drop it so a retry starts clean.
            await s3.DeleteObjectAsync(options.Snapshots.Bucket, key, ct);
            return SnapshotError.ChecksumMismatch;
        }

        await using NpgsqlCommand update = db.CreateCommand("UPDATE chunk_snapshots SET status = 'committed', committed_at = $3 WHERE world_id = $1 AND storage_key = $2");
        update.Parameters.Add(new() { Value = world });
        update.Parameters.Add(new() { Value = key });
        update.Parameters.Add(new() { Value = clock.GetUtcNow() });
        await update.ExecuteNonQueryAsync(ct);
        await PruneAsync(world, epoch, r.X, r.Y, r.Z, ct);
        await using NpgsqlCommand saved = db.CreateCommand("UPDATE worlds SET last_saved_at = now() WHERE id = $1");
        saved.Parameters.Add(new() { Value = world });
        await saved.ExecuteNonQueryAsync(ct);
        return SnapshotError.None;
    }

    /// <summary>A download URL for the newest committed snapshot of a chunk.</summary>
    public async Task<(SnapshotError, PresignedUrl?, long Revision, string? Sha256)> LatestAsync(Guid world, int x, int y, int z, CancellationToken ct)
    {
        if (!ValidCoords(x, y, z))
        {
            return (SnapshotError.Invalid, null, 0, null);
        }

        await using NpgsqlCommand cmd = db.CreateCommand(
            "SELECT s.storage_key, s.revision, s.sha256 FROM chunk_snapshots s JOIN worlds w ON w.id = s.world_id AND s.epoch = w.restore_epoch " +
            "WHERE s.world_id = $1 AND s.chunk_x = $2 AND s.chunk_y = $3 AND s.chunk_z = $4 AND s.status = 'committed' ORDER BY s.revision DESC LIMIT 1");
        foreach (object v in new object[] { world, x, y, z })
        {
            cmd.Parameters.Add(new() { Value = v });
        }

        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            return (SnapshotError.NotFound, null, 0, null);
        }

        return (SnapshotError.None, Presign(r.GetString(0), HttpVerb.GET), r.GetInt64(1), Convert.ToHexString((byte[])r[2]).ToLowerInvariant());
    }

    private PresignedUrl Presign(string key, HttpVerb verb)
    {
        DateTimeOffset expires = clock.GetUtcNow().AddMinutes(options.Snapshots.PresignMinutes);
        string url = s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = options.Snapshots.Bucket,
            Key = key,
            Verb = verb,
            Expires = expires.UtcDateTime,
            Protocol = options.Snapshots.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTP : Protocol.HTTPS,
        });
        return new PresignedUrl(url, key, expires);
    }

    private async Task<int?> EpochAsync(Guid world, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = db.CreateCommand("SELECT restore_epoch FROM worlds WHERE id = $1 AND status = 'active'");
        cmd.Parameters.Add(new() { Value = world });
        return await cmd.ExecuteScalarAsync(ct) as int?;
    }

    /// <summary>The owner's stored bytes (each object once, all their worlds and retained revisions) and their limit.</summary>
    public async Task<(long Used, long Limit)> StoredBytesAsync(Guid world, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = db.CreateCommand(
            "WITH owner AS (SELECT owner_id FROM worlds WHERE id = $1) " +
            "SELECT (SELECT coalesce(sum(size_bytes), 0) FROM (SELECT DISTINCT s.storage_key, s.size_bytes FROM chunk_snapshots s JOIN worlds w ON w.id = s.world_id " +
            "        WHERE w.owner_id = (SELECT owner_id FROM owner) AND s.status = 'committed') objects), " +
            "       (SELECT max_stored_bytes FROM world_quotas WHERE owner_id = (SELECT owner_id FROM owner))");
        cmd.Parameters.Add(new() { Value = world });
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetInt64(0), r.IsDBNull(1) ? options.Snapshots.MaxStoredBytesPerOwner : r.GetInt64(1));
    }

    /// <summary>
    /// Retention for one chunk: keep the newest revisions of the current epoch and everything inside the restore window;
    /// delete other rows, and an object once no row (in any epoch) points at it.
    /// </summary>
    public async Task<int> PruneAsync(Guid world, int epoch, int x, int y, int z, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = db.CreateCommand(
            "DELETE FROM chunk_snapshots WHERE world_id = $1 AND chunk_x = $2 AND chunk_y = $3 AND chunk_z = $4 AND status = 'committed' AND committed_at < $5 " +
            "AND NOT (epoch = $6 AND revision IN (SELECT revision FROM chunk_snapshots WHERE world_id = $1 AND epoch = $6 AND chunk_x = $2 AND chunk_y = $3 AND chunk_z = $4 " +
            "                                     AND status = 'committed' ORDER BY revision DESC LIMIT $7)) " +
            "RETURNING storage_key");
        foreach (object v in new object[] { world, x, y, z, clock.GetUtcNow().AddDays(-options.Snapshots.RetentionDays), epoch, options.Snapshots.RetainRevisions })
        {
            cmd.Parameters.Add(new() { Value = v });
        }

        var keys = new List<string>();
        await using (NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                keys.Add(r.GetString(0));
            }
        }

        foreach (string key in keys.Distinct())
        {
            await using NpgsqlCommand used = db.CreateCommand("SELECT 1 FROM chunk_snapshots WHERE storage_key = $1 LIMIT 1");
            used.Parameters.Add(new() { Value = key });
            if (await used.ExecuteScalarAsync(ct) == null)
            {
                await s3.DeleteObjectAsync(options.Snapshots.Bucket, key, ct);
            }
        }

        return keys.Count;
    }

    private static bool ValidCoords(int x, int y, int z) =>
        Math.Abs((long)x) <= MaxChunkCoordinate && Math.Abs((long)y) <= MaxChunkCoordinate && Math.Abs((long)z) <= MaxChunkCoordinate;

    private static byte[]? ParseSha(string? hex)
    {
        if (hex == null || hex.Length != 64)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
