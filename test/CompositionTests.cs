using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;
using ProductCaptures;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

var defaultsDirectory = Path.Combine(Path.GetTempPath(), "capture-defaults-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(defaultsDirectory);
try
{
    var path = Path.Combine(defaultsDirectory, "product-captures.json");
    await File.WriteAllTextAsync(path, """{"name":"Plugin","capture":{"type":"obsidian","viewport":{"width":1200}}}""");
    var defaults = Config.Load(path);
    Assert(defaults.Capture!.Viewport.Width == 1200 && defaults.Capture.Viewport.Height == 1000, "A width-only override must retain the provider's height default");
}
finally
{
    Directory.Delete(defaultsDirectory, true);
}

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
Console.WriteLine($"Chromium {browser.Version}");
var pixel = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='1440' height='1000'><rect width='1440' height='1000' fill='red'/></svg>"));
var config = new Config
{
    Name = "<Product & name>",
    Subtitle = "for Obsidian",
    Appearance = new()
    {
        Layout = "column",
        Width = 1600,
        Height = 900
    }
};
var source = new SourceImage(1440, 1000, pixel);
var originalCulture = CultureInfo.CurrentCulture;
string html;
try
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
    html = await Composition.RenderAsync(config, "light", source);
}
finally
{
    CultureInfo.CurrentCulture = originalCulture;
}

Assert(html.Contains("&lt;Product &amp; name&gt;"), "Authored text must be escaped");
Assert(html.Contains("width:976px") && !html.Contains("677,777"), "Geometry must use invariant decimal formatting");
Assert(!html.Contains("http:") && !html.Contains("https:"), "Composition assets must be embedded");
foreach (var layout in new[]
{
    "column",
    "horizontal",
    "centered",
    "top"
}

)
{
    config.Appearance.Layout = layout;
    await using var page = await browser.NewPageAsync(new()
    {
        ViewportSize = new()
        {
            Width = 1600,
            Height = 900
        }
    });
    await page.SetContentAsync(await Composition.RenderAsync(config, "dark", source));
    await page.EvaluateAsync("async () => { await document.fonts.ready; await Promise.all([...document.images].map(i => i.decode())); }");
    Assert(await page.Locator(".window").CountAsync() == 1, $"{layout} must render one source panel");
    Assert(await page.Locator(".product-capture").EvaluateAsync<bool>("i => Math.abs(i.getBoundingClientRect().width / i.getBoundingClientRect().height - 1.44) < .002"), $"{layout} must preserve the source aspect ratio");
    Assert(await page.Locator(".platform").GetAttributeAsync("data-obsidian") == "true", "Official Obsidian lockup must be selected");
}

config.Appearance.Layout = "column";
config.Capture = null;
await using (var page = await browser.NewPageAsync(new()
{
    ViewportSize = new()
    {
        Width = 1600,
        Height = 900
    }
}))
{
    await page.SetContentAsync(await Composition.RenderAsync(config, "light", null));
    Assert(await page.Locator(".window").CountAsync() == 0, "Source-free artwork must omit the panel");
    Assert(await page.Locator("h1").EvaluateAsync<bool>("e => { const r=e.getBoundingClientRect(); return Math.abs(r.x+r.width/2-800)<.01; }"), "Source-free identity must be centered");
}

var pluginWithoutSubtitle = new Config
{
    Name = "Plugin",
    Subtitle = "",
    Capture = new()
    {
        Type = "obsidian"
    }
};
var pluginHtml = await Composition.RenderAsync(pluginWithoutSubtitle, "light", source);
Assert(pluginHtml.Contains("canvas studio"), "Plugin photography must not depend on a visible platform label");
Assert(!pluginHtml.Contains("class=\"platform\""), "An explicitly empty subtitle must omit its platform lockup");
Console.WriteLine("Composition contracts passed: all layouts, source ratio, escaping, embedded assets, invariant culture, source-free centering.");
// Optional immutable migration oracle, kept outside the distributable toolkit.
// Supply the archived baseline JSON to verify all approved original hero PNGs.
if (args.Length > 0)
{
    using var baseline = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
    foreach (var product in baseline.RootElement.GetProperty("products").EnumerateArray())
    {
        var old = product.GetProperty("config");
        var project = old.GetProperty("project");
        var output = old.GetProperty("outputs")[0];
        string? S(string key) => project.TryGetProperty(key, out var value) ? value.GetString() : null;
        double? D(string key) => project.TryGetProperty(key, out var value) ? value.GetDouble() : null;
        var c = new Config
        {
            Name = project.GetProperty("title").GetString()!,
            Subtitle = S("subtitle") ?? "",
            Capture = old.GetProperty("captures").GetArrayLength() == 0 ? null : new(),
            Appearance = new()
            {
                Width = output.GetProperty("width").GetInt32(),
                Height = output.GetProperty("height").GetInt32(),
                Layout = S("layout") switch
                {
                    "site-top" => "top",
                    "desktop-centered" => "centered",
                    var value => value
                },
                Background = S("background")!,
                DarkBackground = S("darkBackground"),
                Tint = S("studioTint") ?? S("siteTint"),
                Shade = S("studioShade"),
                Glow = S("studioGlow"),
                Ink = S("ink"),
                ImageFit = "crop",
                ImageZoom = D("imageZoom") ?? 1,
                ImageOffsetX = D("imageOffsetX") ?? 0,
                FadeStart = D("fadeStart"),
                FadeEnd = D("fadeEnd"),
                BlurStart = D("blurStart")
            }
        };
        var root = product.GetProperty("path").GetString()!;
        if (args.Skip(1).Contains("--compact"))
        {
            c = Config.Load(Path.Combine(root, "product-captures.json"));
        }

        var directory = Path.Combine(root, old.GetProperty("outputDir").GetString()!);
        foreach (var theme in new[]
        {
            "light",
            "dark"
        }

        )
        {
            SourceImage? image = null;
            if (c.Capture is not null)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "captures", $"overview-{theme}.png"));
                image = new(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)), "data:image/png;base64," + Convert.ToBase64String(bytes));
            }

            await using var page = await browser.NewPageAsync(new()
            {
                ViewportSize = new()
                {
                    Width = c.Appearance.Width,
                    Height = c.Appearance.Height
                },
                DeviceScaleFactor = 1
            });
            await page.SetContentAsync(await Composition.RenderAsync(c, theme, image));
            await page.EvaluateAsync("async () => { await document.fonts.ready; await Promise.all([...document.images].map(i => i.decode())); }");
            var png = await page.ScreenshotAsync();
            var approved = await File.ReadAllBytesAsync(Path.Combine(directory, theme == "light" ? "hero.png" : "hero-dark.png"));
            Assert(png.SequenceEqual(approved), $"Approved PNG differs: {product.GetProperty("repo").GetString()} {theme}");
            Console.WriteLine($"Approved PNG byte parity: {product.GetProperty("repo").GetString()} {theme}");
        }
    }
}
