using System.Reflection;
using Npgsql;

namespace VoxelRush.Api;

/// <summary>Forward-only SQL migrations embedded in the assembly, applied in order inside one transaction each.</summary>
public static class Migrations
{
    public static async Task ApplyAsync(NpgsqlDataSource db, ILogger logger, CancellationToken ct = default)
    {
        await using NpgsqlConnection connection = await db.OpenConnectionAsync(ct);
        await using (var create = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS schema_migrations (id text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", connection))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        // Serialize concurrent API instances starting together.
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock(8008001)", connection))
        {
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        try
        {
            Assembly assembly = typeof(Migrations).Assembly;
            foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                string id = resource[(resource.IndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
                await using (var check = new NpgsqlCommand("SELECT 1 FROM schema_migrations WHERE id = $1", connection) { Parameters = { new() { Value = id } } })
                {
                    if (await check.ExecuteScalarAsync(ct) != null)
                    {
                        continue;
                    }
                }

                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
                string sql = await reader.ReadToEndAsync(ct);
                await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(ct);
                await using (var apply = new NpgsqlCommand(sql, connection, tx))
                {
                    await apply.ExecuteNonQueryAsync(ct);
                }

                await using (var record = new NpgsqlCommand("INSERT INTO schema_migrations (id) VALUES ($1)", connection, tx) { Parameters = { new() { Value = id } } })
                {
                    await record.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                logger.LogInformation("Applied migration {Migration}", id);
            }
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(8008001)", connection);
            await unlock.ExecuteNonQueryAsync(ct);
        }
    }
}
