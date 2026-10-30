using FluentAssertions;
using ProductCaptures.Tests.Support;
using Xunit;

namespace ProductCaptures.Tests.Configuration;

public sealed class AnimationConfigurationTests
{
    [Fact]
    public async Task Load_AnimationSteps_UseBoundedDefaultsAndOwnBothThemes()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Root, "product-captures.json");
        await File.WriteAllTextAsync(path, """
            {"name":"Demo","animation":{"steps":[{"text":"Open details","selector":"#details"}]}}
            """, TestContext.Current.CancellationToken);

        Config configuration = Config.Load(path);

        AnimationStep step = configuration.Animation!.Steps.Should().ContainSingle().Which;
        step.Type.Should().Be("click");
        step.Zoom.Should().Be(1.35);
        step.DurationMs.Should().Be(2400);
        CaptureEngine.OwnedFiles(configuration).Should().BeEquivalentTo(
            "captures/overview-light.png", "captures/overview-dark.png",
            "hero.png", "hero-dark.png", "showcase.gif", "showcase-dark.gif");
        CaptureEngine.OwnedFiles(new Config { Name = "Still" }).Should().HaveCount(4);
    }

    [Theory]
    [InlineData("{\"steps\":[]}")]
    [InlineData("{\"steps\":null}")]
    [InlineData("{\"steps\":[null]}")]
    [InlineData("{\"steps\":[{\"text\":\"\",\"selector\":\"button\"}]}")]
    [InlineData("{\"steps\":[{\"text\":\"Missing target\"}]}")]
    [InlineData("{\"steps\":[{\"text\":\"Bad action\",\"type\":\"eval\"}]}")]
    [InlineData("{\"steps\":[{\"text\":\"No movement\",\"type\":\"drag\",\"selector\":\"#divider\"}]}")]
    [InlineData("{\"steps\":[{\"text\":\"Too fast\",\"type\":\"focus\",\"durationMs\":0}]}")]
    [InlineData("{\"steps\":[{\"text\":\"Too close\",\"type\":\"focus\",\"zoom\":20}]}")]
    [InlineData("{\"steps\":[{\"text\":\"Empty readiness\",\"type\":\"focus\",\"ready\":\"\"}]}")]
    public async Task Load_InvalidAnimation_RejectsBeforeCreatingOutputs(string animation)
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Root, "product-captures.json");
        await File.WriteAllTextAsync(path,
            "{\"name\":\"Invalid\",\"animation\":" + animation + "}",
            TestContext.Current.CancellationToken);

        Action load = () => Config.Load(path);

        load.Should().Throw<ArgumentException>();
        Directory.EnumerateFileSystemEntries(directory.Root).Should().ContainSingle();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"type\":\"image\",\"images\":{\"light\":\"light.png\",\"dark\":\"dark.png\"}}")]
    public async Task Load_AnimationWithoutLiveProvider_Rejects(string capture)
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Root, "product-captures.json");
        await File.WriteAllTextAsync(path,
            "{\"name\":\"Invalid\",\"capture\":" + capture
            + ",\"animation\":{\"steps\":[{\"text\":\"Overview\",\"type\":\"focus\"}]}}",
            TestContext.Current.CancellationToken);

        Action load = () => Config.Load(path);

        load.Should().Throw<ArgumentException>().WithMessage("*live web or Obsidian*");
    }

    [Fact]
    public async Task DryRun_WithAnimation_StartsNoProductOrEncoderAndWritesNothing()
    {
        using var directory = new TemporaryDirectory();
        var configuration = new Config
        {
            Name = "Plan",
            Root = directory.Root,
            Build = System.Text.Json.JsonSerializer.SerializeToElement("exit 91"),
            Animation = new ProductCaptures.Animation
            {
                Steps = [new AnimationStep { Text = "Overview", Type = "focus", Zoom = 1 }]
            }
        };

        await CaptureEngine.RunAsync(configuration, new RunOptions(DryRun: true),
            TestContext.Current.CancellationToken);

        Directory.EnumerateFileSystemEntries(directory.Root).Should().BeEmpty();
    }
}
