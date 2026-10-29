using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProductCaptures.Tests.Support;

namespace ProductCaptures.Tests.Configuration;

[TestClass]
public sealed class ConfigurationTests
{
    [TestMethod]
    public async Task Load_WidthOnlyObsidianViewport_RetainsProviderHeight()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Root, "product-captures.json");
        await File.WriteAllTextAsync(path, """
            {"name":"Plugin","capture":{"type":"obsidian","viewport":{"width":1200}}}
            """);

        Config configuration = Config.Load(path);

        Assert.IsNotNull(configuration.Capture);
        Assert.AreEqual(1200, configuration.Capture.Viewport.Width);
        Assert.AreEqual(1000, configuration.Capture.Viewport.Height);
    }
}
