using System.Globalization;
using System.Text;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using CaptureComposition = ProductCaptures.Composition;

namespace ProductCaptures.Tests.Composition;

[TestClass]
public sealed class CompositionTests
{
    private static IPlaywright? playwright;
    private static IBrowser? browser;

    [ClassInitialize]
    public static async Task InitializeBrowserAsync(TestContext context)
    {
        playwright = await Playwright.CreateAsync();
        try
        {
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Timeout = 30_000
            });
        }
        catch
        {
            playwright.Dispose();
            playwright = null;
            throw;
        }

        context.WriteLine($"Chromium {browser.Version}");
    }

    [ClassCleanup]
    public static async Task DisposeBrowserAsync()
    {
        try
        {
            if (browser is not null)
            {
                await browser.DisposeAsync();
            }
        }
        finally
        {
            playwright?.Dispose();
        }
    }

    [TestMethod]
    public async Task RenderAsync_AuthoredText_EscapesHtml()
    {
        Config configuration = CreateConfiguration();

        string html = await CaptureComposition.RenderAsync(configuration, "light", CreateSource());

        Assert.Contains("&lt;Product &amp; name&gt;", html, StringComparison.Ordinal);
    }

    [TestMethod]
    [DoNotParallelize]
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

        Assert.Contains("width:976px", html, StringComparison.Ordinal);
        Assert.DoesNotContain("677,777", html, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RenderAsync_AssetReferences_AreEmbedded()
    {
        string html = await CaptureComposition.RenderAsync(CreateConfiguration(), "light", CreateSource());

        Assert.DoesNotContain("http:", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https:", html, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("column", "light")]
    [DataRow("column", "dark")]
    [DataRow("horizontal", "light")]
    [DataRow("horizontal", "dark")]
    [DataRow("centered", "light")]
    [DataRow("centered", "dark")]
    [DataRow("top", "light")]
    [DataRow("top", "dark")]
    public async Task RenderAsync_LayoutWithSource_PreservesPanelAndBranding(string layout, string theme)
    {
        Config configuration = CreateConfiguration(layout);
        await using IBrowserContext context = await CreateContextAsync(theme);

        IPage page = await RenderPageAsync(context, configuration, theme, CreateSource());

        Assert.AreEqual(1, await page.Locator(".window").CountAsync());
        double aspectRatio = await page.Locator(".product-capture").EvaluateAsync<double>("""
            image => {
                const bounds = image.getBoundingClientRect();
                return bounds.width / bounds.height;
            }
            """);
        Assert.AreEqual(1.44, aspectRatio, 0.002);
        Assert.AreEqual("true", await page.Locator(".platform").GetAttributeAsync("data-obsidian"));
    }

    [TestMethod]
    [DataRow("column", "light")]
    [DataRow("column", "dark")]
    [DataRow("horizontal", "light")]
    [DataRow("horizontal", "dark")]
    [DataRow("centered", "light")]
    [DataRow("centered", "dark")]
    [DataRow("top", "light")]
    [DataRow("top", "dark")]
    public async Task RenderAsync_SourceFreeLayout_CentersIdentityWithoutPanel(string layout, string theme)
    {
        Config configuration = CreateConfiguration(layout);
        configuration.Capture = null;
        await using IBrowserContext context = await CreateContextAsync(theme);

        IPage page = await RenderPageAsync(context, configuration, theme, source: null);

        Assert.AreEqual(0, await page.Locator(".window").CountAsync());
        double headingCenter = await page.Locator("h1").EvaluateAsync<double>("""
            heading => {
                const bounds = heading.getBoundingClientRect();
                return bounds.x + bounds.width / 2;
            }
            """);
        Assert.AreEqual(800, headingCenter, 0.01);
    }

    [TestMethod]
    public async Task RenderAsync_EmptySubtitle_KeepsPluginPhotographyWithoutLockup()
    {
        var configuration = new Config
        {
            Name = "Plugin",
            Subtitle = "",
            Capture = new Capture { Type = "obsidian" }
        };

        string html = await CaptureComposition.RenderAsync(configuration, "light", CreateSource());

        Assert.Contains("canvas studio", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"platform\"", html, StringComparison.Ordinal);
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

    private static Task<IBrowserContext> CreateContextAsync(string theme)
    {
        IBrowser currentBrowser = browser ?? throw new InvalidOperationException("Browser fixture is not initialized.");
        return currentBrowser.NewContextAsync(new BrowserNewContextOptions
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
