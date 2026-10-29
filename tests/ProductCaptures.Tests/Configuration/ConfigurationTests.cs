using FluentAssertions;
using Xunit;
using ProductCaptures.Tests.Support;

namespace ProductCaptures.Tests.Configuration;

public sealed class ConfigurationTests
{
    [Fact]
    public async Task Load_WidthOnlyObsidianViewport_RetainsProviderHeight()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Root, "product-captures.json");
        await File.WriteAllTextAsync(path, """
            {"name":"Plugin","capture":{"type":"obsidian","viewport":{"width":1200}}}
            """, TestContext.Current.CancellationToken);

        Config configuration = Config.Load(path);

        configuration.Capture.Should().NotBeNull();
        configuration.Capture!.Viewport.Width.Should().Be(1200);
        configuration.Capture!.Viewport.Height.Should().Be(1000);
    }
}
