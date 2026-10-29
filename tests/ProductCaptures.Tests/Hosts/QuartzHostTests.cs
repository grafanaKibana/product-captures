using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ProductCaptures.Tests.Hosts;

[TestClass]
public sealed class QuartzHostTests
{
    [TestMethod]
    public void Validate_MinimalPlugin_AcceptsDefaults()
    {
        using var fixture = new QuartzFixture();

        QuartzHost.Validate(fixture.CreateConfiguration());
    }

    [TestMethod]
    [DataRow("revision", "quartz.revision must be a pinned commit")]
    [DataRow("upstream", "quartz.upstream must be an HTTPS Git repository")]
    [DataRow("content", "Path must stay inside project")]
    [DataRow("lock", "Quartz inputs require package.json and package-lock.json")]
    [DataRow("input", "Path must stay inside project")]
    [DataRow("configuration", "Quartz options and configuration must be objects")]
    [DataRow("metadata", "Plugin package.json requires quartz.name metadata")]
    public void Validate_InvalidPlugin_RejectsExpectedBoundary(string scenario, string expectedMessage)
    {
        using var fixture = new QuartzFixture();
        Config configuration = fixture.CreateConfiguration();
        ConfigureInvalidInput(configuration, fixture, scenario);

        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => QuartzHost.Validate(configuration));

        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(fixture.OutputPath));
    }

    [TestMethod]
    public void Validate_SymlinkInFixture_RejectsLinkedInput()
    {
        using var fixture = new QuartzFixture();
        string outside = Path.Combine(fixture.Root, "outside.md");
        File.WriteAllText(outside, "outside");
        File.CreateSymbolicLink(Path.Combine(fixture.Root, "fixtures", "link.md"), outside);

        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(
            () => QuartzHost.Validate(fixture.CreateConfiguration()));

        Assert.Contains("Symlink is not allowed in host input", error.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task PrepareAsync_NonemptyUnownedCache_PreservesExistingFilesAndSourceLock()
    {
        using var fixture = new QuartzFixture();
        Directory.CreateDirectory(fixture.HostPath);
        string unownedFile = Path.Combine(fixture.HostPath, "unowned");
        await File.WriteAllTextAsync(unownedFile, "keep");

        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => QuartzHost.PrepareAsync(fixture.CreateConfiguration()));

        Assert.Contains("non-empty", error.Message, StringComparison.Ordinal);
        Assert.AreEqual("keep", await File.ReadAllTextAsync(unownedFile));
        Assert.AreEqual("{}", await File.ReadAllTextAsync(fixture.LockPath));
        Assert.IsFalse(Directory.Exists(fixture.OutputPath));
    }

    [TestMethod]
    public async Task PrepareAsync_CachedRevisionMismatch_RejectsBeforeWritingOutput()
    {
        using var fixture = new QuartzFixture();
        await fixture.InitializeCachedRepositoryAsync();

        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => QuartzHost.PrepareAsync(fixture.CreateConfiguration()));

        Assert.Contains("different revision", error.Message, StringComparison.Ordinal);
        Assert.AreEqual("{}", await File.ReadAllTextAsync(fixture.LockPath));
        Assert.IsFalse(Directory.Exists(fixture.OutputPath));
    }

    [TestMethod]
    public async Task PrepareAsync_CachedOriginMismatch_RejectsBeforeWritingOutput()
    {
        using var fixture = new QuartzFixture();
        await fixture.InitializeCachedRepositoryAsync();
        Config configuration = fixture.CreateConfiguration();
        configuration.Quartz!.Revision = (await fixture.ReadRevisionAsync()).Trim();

        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => QuartzHost.PrepareAsync(configuration));

        Assert.Contains("unexpected origin", error.Message, StringComparison.Ordinal);
        Assert.AreEqual("{}", await File.ReadAllTextAsync(fixture.LockPath));
        Assert.IsFalse(Directory.Exists(fixture.OutputPath));
    }

    private static void ConfigureInvalidInput(Config configuration, QuartzFixture fixture, string scenario)
    {
        Quartz quartz = configuration.Quartz!;
        switch (scenario)
        {
            case "revision":
                quartz.Revision = "main";
                break;
            case "upstream":
                quartz.Upstream = "file:///tmp/repo";
                break;
            case "content":
                quartz.Content = "../outside";
                break;
            case "lock":
                quartz.Inputs = ["package.json"];
                break;
            case "input":
                quartz.Inputs = ["package.json", "package-lock.json", "../outside"];
                break;
            case "configuration":
                quartz.Configuration = JsonSerializer.SerializeToElement("invalid");
                break;
            case "metadata":
                File.WriteAllText(fixture.PackagePath, "{}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown test case.");
        }
    }
}
