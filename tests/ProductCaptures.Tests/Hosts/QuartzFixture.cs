using ProductCaptures.Tests.Support;

namespace ProductCaptures.Tests.Hosts;

internal sealed class QuartzFixture : IDisposable
{
    private readonly TemporaryDirectory directory = new();

    public string Root => directory.Root;
    public string PackagePath => Path.Combine(Root, "package.json");
    public string LockPath => Path.Combine(Root, "package-lock.json");
    public string HostPath => Path.Combine(Root, ".product-captures-cache", "quartz");
    public string OutputPath => Path.Combine(Root, "docs", "assets", "product-captures");

    public QuartzFixture()
    {
        try
        {
            File.WriteAllText(PackagePath, """{"name":"quartz-test","quartz":{"name":"test"}}""");
            File.WriteAllText(LockPath, "{}");
            Directory.CreateDirectory(Path.Combine(Root, "fixtures"));
            File.WriteAllText(Path.Combine(Root, "fixtures", "index.md"), "# Fixture");
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    public Config CreateConfiguration() => new()
    {
        Root = Root,
        Name = "Test",
        Quartz = new Quartz { Content = "fixtures" }
    };

    public async Task InitializeCachedRepositoryAsync()
    {
        Directory.CreateDirectory(HostPath);
        await RunGitAsync(["init", "-q", "--object-format=sha1", "--template="]);
        await RunGitAsync(["remote", "add", "origin", "https://example.invalid/other.git"]);
        await RunGitAsync(
        [
            "-c", "user.name=Host Test",
            "-c", "user.email=host@example.invalid",
            "-c", "core.hooksPath=/dev/null",
            "-c", "commit.gpgsign=false",
            "commit", "--allow-empty", "-qm", "Fixture"
        ]);
    }

    public Task<string> ReadRevisionAsync() => RunGitAsync(["rev-parse", "HEAD"]);

    public void Dispose() => directory.Dispose();

    private Task<string> RunGitAsync(string[] arguments) =>
        Processes.RunAsync("git", arguments, HostPath, timeoutMs: 10_000);
}
