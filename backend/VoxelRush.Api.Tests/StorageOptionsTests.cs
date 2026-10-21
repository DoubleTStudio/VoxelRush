namespace VoxelRush.Api.Tests;

/// <summary>DEV-002: object storage settings are checked at startup, so a half-configured store never boots.</summary>
public sealed class StorageOptionsTests
{
    private static ApiOptions Options(Action<SnapshotStoreOptions> storage)
    {
        var options = new ApiOptions
        {
            ConnectionString = "Host=x",
            JwtSigningKey = Convert.ToBase64String(new byte[32]),
            TicketKey = Convert.ToBase64String(new byte[32]),
            ServiceToken = ApiHost.ServiceToken,
        };
        storage(options.Snapshots);
        return options;
    }

    [Test]
    public void NoServiceUrl_DisablesStorage_WithoutCredentials()
    {
        Assert.That(Options(_ => { }).Validate, Throws.Nothing);
    }

    [TestCase("http://127.0.0.1:18092")]
    [TestCase("https://account.r2.cloudflarestorage.com")]
    public void CompleteStore_IsAccepted(string url)
    {
        Assert.That(Options(s => { s.ServiceUrl = url; s.AccessKey = "key"; s.SecretKey = "secret"; }).Validate, Throws.Nothing);
    }

    [TestCase("127.0.0.1:18092")]
    [TestCase("not a url")]
    [TestCase("ftp://storage.test/")]
    public void InvalidServiceUrl_FailsAtStartup(string url)
    {
        Assert.That(Options(s => { s.ServiceUrl = url; s.AccessKey = "key"; s.SecretKey = "secret"; }).Validate,
            Throws.InvalidOperationException.With.Message.Contains("ServiceUrl"));
    }

    [TestCase("", "secret")]
    [TestCase("key", "")]
    [TestCase(" ", " ")]
    public void MissingCredentials_FailAtStartup(string accessKey, string secretKey)
    {
        Assert.That(Options(s => { s.ServiceUrl = "http://127.0.0.1:18092"; s.AccessKey = accessKey; s.SecretKey = secretKey; }).Validate,
            Throws.InvalidOperationException.With.Message.Contains("AccessKey"));
    }

    [Test]
    public void EmptyBucket_FailsAtStartup()
    {
        Assert.That(Options(s => { s.ServiceUrl = "http://127.0.0.1:18092"; s.AccessKey = "key"; s.SecretKey = "secret"; s.Bucket = ""; }).Validate,
            Throws.InvalidOperationException.With.Message.Contains("Bucket"));
    }
}
