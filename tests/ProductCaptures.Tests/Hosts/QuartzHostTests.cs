using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ProductCaptures.Tests.Hosts;

public sealed class QuartzHostTests
{
    [Fact]
    public void Validate_MinimalPlugin_AcceptsDefaults()
    {
        using var fixture = new QuartzFixture();

        QuartzHost.Validate(fixture.CreateConfiguration());
    }

    [Theory]
    [InlineData("revision", "quartz.revision must be a pinned commit")]
    [InlineData("upstream", "quartz.upstream must be an HTTPS Git repository")]
    [InlineData("content", "Path must stay inside project")]
    [InlineData("lock", "Quartz inputs require package.json and package-lock.json")]
    [InlineData("input", "Path must stay inside project")]
    [InlineData("configuration", "Quartz options and configuration must be objects")]
    [InlineData("metadata", "Plugin package.json requires quartz.name metadata")]
    public void Validate_InvalidPlugin_RejectsExpectedBoundary(string scenario, string expectedMessage)
    {
        using var fixture = new QuartzFixture();
        Config configuration = fixture.CreateConfiguration();
        ConfigureInvalidInput(configuration, fixture, scenario);

        Action validate = () => QuartzHost.Validate(configuration);

        ArgumentException error = validate.Should().ThrowExactly<ArgumentException>().Which;

        error.Message.Should().Contain(expectedMessage);
        Directory.Exists(fixture.OutputPath).Should().BeFalse();
    }

    [Fact]
    public void Validate_SymlinkInFixture_RejectsLinkedInput()
    {
        using var fixture = new QuartzFixture();
        string outside = Path.Combine(fixture.Root, "outside.md");
        File.WriteAllText(outside, "outside");
        File.CreateSymbolicLink(Path.Combine(fixture.Root, "fixtures", "link.md"), outside);

        Action validate = () => QuartzHost.Validate(fixture.CreateConfiguration());

        ArgumentException error = validate.Should().ThrowExactly<ArgumentException>().Which;

        error.Message.Should().Contain("Symlink is not allowed in host input");
    }

    [Fact]
    public async Task PrepareAsync_NonemptyUnownedCache_PreservesExistingFilesAndSourceLock()
    {
        using var fixture = new QuartzFixture();
        Directory.CreateDirectory(fixture.HostPath);
        string unownedFile = Path.Combine(fixture.HostPath, "unowned");
        await File.WriteAllTextAsync(unownedFile, "keep", TestContext.Current.CancellationToken);

        Func<Task> prepare = () => QuartzHost.PrepareAsync(fixture.CreateConfiguration());

        InvalidOperationException error = (await prepare.Should().ThrowExactlyAsync<InvalidOperationException>()).Which;

        error.Message.Should().Contain("non-empty");
        (await File.ReadAllTextAsync(unownedFile, TestContext.Current.CancellationToken)).Should().Be("keep");
        (await File.ReadAllTextAsync(fixture.LockPath, TestContext.Current.CancellationToken)).Should().Be("{}");
        Directory.Exists(fixture.OutputPath).Should().BeFalse();
    }

    [Fact]
    public async Task PrepareAsync_CachedRevisionMismatch_RejectsBeforeWritingOutput()
    {
        using var fixture = new QuartzFixture();
        await fixture.InitializeCachedRepositoryAsync();

        Func<Task> prepare = () => QuartzHost.PrepareAsync(fixture.CreateConfiguration());

        InvalidOperationException error = (await prepare.Should().ThrowExactlyAsync<InvalidOperationException>()).Which;

        error.Message.Should().Contain("different revision");
        (await File.ReadAllTextAsync(fixture.LockPath, TestContext.Current.CancellationToken)).Should().Be("{}");
        Directory.Exists(fixture.OutputPath).Should().BeFalse();
    }

    [Fact]
    public async Task PrepareAsync_CachedOriginMismatch_RejectsBeforeWritingOutput()
    {
        using var fixture = new QuartzFixture();
        await fixture.InitializeCachedRepositoryAsync();
        Config configuration = fixture.CreateConfiguration();
        configuration.Quartz!.Revision = (await fixture.ReadRevisionAsync()).Trim();

        Func<Task> prepare = () => QuartzHost.PrepareAsync(configuration);

        InvalidOperationException error = (await prepare.Should().ThrowExactlyAsync<InvalidOperationException>()).Which;

        error.Message.Should().Contain("unexpected origin");
        (await File.ReadAllTextAsync(fixture.LockPath, TestContext.Current.CancellationToken)).Should().Be("{}");
        Directory.Exists(fixture.OutputPath).Should().BeFalse();
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
