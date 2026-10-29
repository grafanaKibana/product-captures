using System.Globalization;
using System.Text;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;
using CaptureComposition = ProductCaptures.Composition;

namespace ProductCaptures.Tests.Composition;

[Collection("Composition")]
public sealed class CompositionTests(BrowserFixture browser) : IClassFixture<BrowserFixture>
{
    [Fact]
    public async Task RenderAsync_AuthoredText_EscapesHtml()
    {
        Config configuration = CreateConfiguration();

        string html = await CaptureComposition.RenderAsync(configuration, "light", CreateSource());

        html.Should().Contain("&lt;Product &amp; name&gt;");
    }

    [Fact]
    public async Task RenderAsync_FrenchCulture_UsesInvariantGeometry()
    {
        Config configuration = CreateConfiguration();
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        string html;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            html = await CaptureComposition.RenderAsync(configuration, "light", CreateSource());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        html.Should().Contain("width:976px");
        html.Should().NotContain("677,777");
    }

    [Fact]
    public async Task RenderAsync_AssetReferences_AreEmbedded()
    {
        string html = await CaptureComposition.RenderAsync(CreateConfiguration(), "light", CreateSource());

        html.Should().NotContain("http:");
        html.Should().NotContain("https:");
    }

    [Theory]
    [InlineData("column", "light")]
    [InlineData("column", "dark")]
    [InlineData("horizontal", "light")]
    [InlineData("horizontal", "dark")]
    [InlineData("centered", "light")]
    [InlineData("centered", "dark")]
    [InlineData("top", "light")]
    [InlineData("top", "dark")]
    public async Task RenderAsync_LayoutWithSource_PreservesPanelAndBranding(string layout, string theme)
    {
        Config configuration = CreateConfiguration(layout);
        await using IBrowserContext context = await CreateContextAsync(theme);

        IPage page = await RenderPageAsync(context, configuration, theme, CreateSource());

        (await page.Locator(".window").CountAsync()).Should().Be(1);
        double aspectRatio = await page.Locator(".product-capture").EvaluateAsync<double>("""
            image => {
                const bounds = image.getBoundingClientRect();
                return bounds.width / bounds.height;
            }
            """);
        aspectRatio.Should().BeApproximately(1.44, 0.002);
        (await page.Locator(".platform").GetAttributeAsync("data-obsidian")).Should().Be("true");
    }

    [Theory]
    [InlineData("column", "light")]
    [InlineData("column", "dark")]
    [InlineData("horizontal", "light")]
    [InlineData("horizontal", "dark")]
    [InlineData("centered", "light")]
    [InlineData("centered", "dark")]
    [InlineData("top", "light")]
    [InlineData("top", "dark")]
    public async Task RenderAsync_SourceFreeLayout_CentersIdentityWithoutPanel(string layout, string theme)
    {
        Config configuration = CreateConfiguration(layout);
        configuration.Capture = null;
        await using IBrowserContext context = await CreateContextAsync(theme);

        IPage page = await RenderPageAsync(context, configuration, theme, source: null);

        (await page.Locator(".window").CountAsync()).Should().Be(0);
        double headingCenter = await page.Locator("h1").EvaluateAsync<double>("""
            heading => {
                const bounds = heading.getBoundingClientRect();
                return bounds.x + bounds.width / 2;
            }
            """);
        headingCenter.Should().BeApproximately(800, 0.01);
    }

    [Fact]
    public async Task RenderAsync_EmptySubtitle_KeepsPluginPhotographyWithoutLockup()
    {
        var configuration = new Config
        {
            Name = "Plugin",
            Subtitle = "",
            Capture = new Capture { Type = "obsidian" }
        };

        string html = await CaptureComposition.RenderAsync(configuration, "light", CreateSource());

        html.Should().Contain("canvas studio");
        html.Should().NotContain("class=\"platform\"");
    }

    private static Config CreateConfiguration(string layout = "column") => new()
    {
        Name = "<Product & name>",
        Subtitle = "for Obsidian",
        Appearance = new Appearance
        {
            Layout = layout,
            Width = 1600,
            Height = 900
        }
    };

    private static SourceImage CreateSource()
    {
        const string svg = """
            <svg xmlns='http://www.w3.org/2000/svg' width='1440' height='1000'><rect width='1440' height='1000' fill='red'/></svg>
            """;
        string image = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        return new SourceImage(1440, 1000, image);
    }

    private Task<IBrowserContext> CreateContextAsync(string theme)
    {
        return browser.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1600, Height = 900 },
            DeviceScaleFactor = 1,
            ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light
        });
    }

    private static async Task<IPage> RenderPageAsync(
        IBrowserContext context,
        Config configuration,
        string theme,
        SourceImage? source)
    {
        IPage page = await context.NewPageAsync();
        await page.SetContentAsync(await CaptureComposition.RenderAsync(configuration, theme, source));
        await page.EvaluateAsync("""
            async () => {
                await document.fonts.ready;
                await Promise.all([...document.images].map(image => image.decode()));
            }
            """).WaitAsync(TimeSpan.FromSeconds(15));
        return page;
    }
}
