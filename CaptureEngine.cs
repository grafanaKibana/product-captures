using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Playwright;

namespace ProductCaptures;

public sealed record RunOptions(
    bool DryRun = false,
    bool ReuseScreenshots = false,
    string? Url = null);

public static class CaptureEngine
{
    public static readonly string[] Themes = ["light", "dark"];

    public static string[] OwnedFiles(Config config) => config.Capture is null
        ? ["hero.png", "hero-dark.png"]
        : [
            "captures/overview-light.png",
            "captures/overview-dark.png",
            "hero.png",
            "hero-dark.png"
        ];

    private static string[] ObsoleteFiles(Config config) => config.Capture is null
        ? ["captures/overview-light.png", "captures/overview-dark.png"]
        : [];

    public static void Validate(Config config, RunOptions options)
    {
        config.Validate();
        if (options.Url is not null && config.Capture?.Type != "web")
        {
            throw new ArgumentException("--url is only available for web captures");
        }

        if (options.Url is not null
            && (!Uri.TryCreate(options.Url, UriKind.Absolute, out Uri? configuredUrl)
                || configuredUrl.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("--url must be an absolute HTTP URL");
        }

        foreach (string file in OwnedFiles(config).Append("manifest.json").Concat(ObsoleteFiles(config)))
        {
            string destination = Paths.Within(config.Root, Path.Combine(config.OutputDirectory, file));
            if (Directory.Exists(destination))
            {
                throw new ArgumentException($"Output file is a directory: {destination}");
            }
        }

        if (options.ReuseScreenshots && config.Capture is not null)
        {
            foreach (string theme in Themes)
            {
                string savedImage = Paths.Within(
                    config.Root,
                    Path.Combine(config.OutputDirectory, $"captures/overview-{theme}.png"));
                _ = ReadImage(savedImage);
            }
        }

        if (!options.ReuseScreenshots && config.Capture?.Type == "image")
        {
            foreach (string theme in Themes)
            {
                _ = ReadImage(Paths.Within(config.Root, config.Capture.Images![theme]));
            }
        }

        if (config.Quartz is not null)
        {
            QuartzHost.Validate(config);
        }
    }

    public static async Task RunAsync(
        Config config,
        RunOptions options,
        CancellationToken cancellationToken = default)
    {
        Validate(config, options);
        if (options.DryRun)
        {
            PrintPlan(config, options);
            return;
        }

        string stage = Path.Combine(Path.GetTempPath(), "product-captures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        CaptureServer? server = null;
        try
        {
            bool fresh = config.Capture is not null && !options.ReuseScreenshots;
            string? quartzDirectory = await PrepareProductAsync(config, options, fresh, cancellationToken);
            string? baseUrl = options.Url;
            if (fresh && config.Capture?.Type == "web" && baseUrl is null)
            {
                bool configuredAbsoluteUrl = Uri.TryCreate(config.Capture.Url, UriKind.Absolute, out Uri? configuredUrl)
                    && configuredUrl.Scheme is "http" or "https";
                if (!configuredAbsoluteUrl)
                {
                    server = await CaptureServer.StartAsync(config, quartzDirectory, cancellationToken);
                    baseUrl = server.Url;
                }
            }

            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--no-sandbox"]
            });
            using CancellationTokenRegistration cancelBrowser = cancellationToken.Register(() =>
            {
                _ = browser.CloseAsync();
            });
            foreach (string theme in Themes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SourceImage? source = await CaptureSourceAsync(
                    config, options, playwright, browser, theme, baseUrl, stage, cancellationToken);
                await RenderHeroAsync(browser, config, theme, source, stage);
            }

            await WriteManifestAsync(config, options, stage, cancellationToken);
            InstallOutputs(config, stage);
            Console.WriteLine($"Generated {OwnedFiles(config).Length} PNGs in {config.OutputDirectory}");
        }
        finally
        {
            if (server is not null)
            {
                await server.DisposeAsync();
            }

            Directory.Delete(stage, true);
        }
    }

    private static void PrintPlan(Config config, RunOptions options)
    {
        var plan = new
        {
            name = config.Name,
            output = config.OutputDirectory,
            files = OwnedFiles(config),
            reuseScreenshots = options.ReuseScreenshots,
            dryRun = true
        };
        Console.WriteLine(JsonSerializer.Serialize(plan, Config.Json));
    }

    private static async Task<string?> PrepareProductAsync(
        Config config,
        RunOptions options,
        bool fresh,
        CancellationToken cancellationToken)
    {
        string? quartzDirectory = null;
        if (fresh && options.Url is null)
        {
            if (config.Build is { } build)
            {
                Console.Write(await Processes.ShellAsync(BuildCommand.Parse(build, config.Root), cancellationToken));
            }

            if (config.Quartz is not null)
            {
                quartzDirectory = await QuartzHost.PrepareAsync(config, cancellationToken);
            }
        }

        return quartzDirectory;
    }

    private static async Task<SourceImage?> CaptureSourceAsync(
        Config config,
        RunOptions options,
        IPlaywright playwright,
        IBrowser browser,
        string theme,
        string? baseUrl,
        string stage,
        CancellationToken cancellationToken)
    {
        if (config.Capture is not { } capture)
        {
            return null;
        }

        string relative = $"captures/overview-{theme}.png";
        string destination = Path.Combine(stage, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (options.ReuseScreenshots)
        {
            File.Copy(Paths.Within(config.Root, Path.Combine(config.OutputDirectory, relative)), destination);
        }
        else if (capture.Type == "image")
        {
            File.Copy(Paths.Within(config.Root, capture.Images![theme]), destination);
        }
        else if (capture.Type == "obsidian")
        {
            await ObsidianHost.CaptureAsync(config, playwright, theme, destination, cancellationToken);
        }
        else
        {
            await CaptureWebAsync(browser, config, theme, baseUrl, destination, options.Url is not null);
        }

        return ReadImage(destination);
    }

    private static async Task RenderHeroAsync(
        IBrowser browser,
        Config config,
        string theme,
        SourceImage? source,
        string stage)
    {
        string html = await Composition.RenderAsync(config, theme, source);
        IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = config.Appearance.Width,
                Height = config.Appearance.Height
            },
            DeviceScaleFactor = 1,
            ReducedMotion = ReducedMotion.Reduce,
            ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light
        });
        try
        {
            await page.SetContentAsync(html, new PageSetContentOptions
            {
                WaitUntil = WaitUntilState.Load
            });
            await page.EvaluateAsync("async () => { await document.fonts.ready; await Promise.all([...document.images].map(i=>i.decode())); await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))); }");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(stage, theme == "dark" ? "hero-dark.png" : "hero.png"),
                Animations = ScreenshotAnimations.Disabled,
                Caret = ScreenshotCaret.Hide
            });
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static async Task WriteManifestAsync(
        Config config,
        RunOptions options,
        string stage,
        CancellationToken cancellationToken)
    {
        var manifest = new
        {
            version = 2,
            tool = Program.Version,
            configHash = config.ConfigHash,
            mode = options.ReuseScreenshots ? "reuse" : "fresh",
            source = Processes.Commit(config.Root),
            outputs = OwnedFiles(config)
        };
        await File.WriteAllTextAsync(
            Path.Combine(stage, "manifest.json"),
            JsonSerializer.Serialize(manifest, Config.Json),
            cancellationToken);
    }

    private static void InstallOutputs(Config config, string stage)
    {
        // Every expensive operation completes before any consumer output is replaced.
        Dictionary<string, string?> previous = new();
        List<string> replaced = [];
        try
        {
            foreach (string file in OwnedFiles(config).Append("manifest.json").Concat(ObsoleteFiles(config)))
            {
                string destination = Paths.Within(config.Root, Path.Combine(config.OutputDirectory, file));
                string? backup = null;
                if (File.Exists(destination))
                {
                    backup = Path.Combine(stage, "backup-" + previous.Count);
                    File.Copy(destination, backup);
                }

                previous.Add(destination, backup);
            }

            foreach (string file in OwnedFiles(config).Append("manifest.json"))
            {
                string destination = Paths.Within(config.Root, Path.Combine(config.OutputDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(Path.Combine(stage, file), destination, true);
                replaced.Add(destination);
            }

            foreach (string file in ObsoleteFiles(config))
            {
                string destination = Paths.Within(config.Root, Path.Combine(config.OutputDirectory, file));
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                    replaced.Add(destination);
                }
            }
        }
        catch
        {
            foreach (string destination in replaced.AsEnumerable().Reverse())
            {
                if (previous[destination] is { } backup)
                {
                    File.Copy(backup, destination, true);
                }
                else
                {
                    File.Delete(destination);
                }
            }

            throw;
        }
    }

    internal static Dictionary<string, string> ResolveStorage(Capture capture, string theme)
    {
        return capture.Storage.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ValueKind == JsonValueKind.String
                ? entry.Value.GetString()!.Replace("$theme", theme)
                : entry.Value.GetProperty(theme).GetString()!);
    }

    public static SourceImage ReadImage(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 24
            || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            throw new ArgumentException($"Expected PNG: {path}");
        }

        int width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        if (width < 1 || height < 1)
        {
            throw new ArgumentException($"Invalid PNG dimensions: {path}");
        }

        return new SourceImage(width, height, "data:image/png;base64," + Convert.ToBase64String(bytes));
    }

    public static Task SettleAsync(IPage page) => page.EvaluateAsync("""
        async () => {
          await document.fonts.ready;
          await Promise.all([...document.images].filter(image => {
            const rect=image.getBoundingClientRect(),style=getComputedStyle(image);
            return rect.width>0&&rect.height>0&&rect.bottom>0&&rect.right>0&&rect.top<innerHeight&&rect.left<innerWidth&&style.visibility!=='hidden'&&style.visibility!=='collapse';
          }).map(image=>image.decode()));
          await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
        }
        """);

    private static async Task CaptureWebAsync(
        IBrowser browser,
        Config config,
        string theme,
        string? baseUrl,
        string destination,
        bool overrideUrl)
    {
        Capture capture = config.Capture!;
        IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = capture.Viewport.Width,
                Height = capture.Viewport.Height
            },
            DeviceScaleFactor = 1,
            ReducedMotion = ReducedMotion.Reduce,
            ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light
        });
        try
        {
            Dictionary<string, string> storage = ResolveStorage(capture, theme);
            await page.AddInitScriptAsync("for(const [k,v] of Object.entries(" + JsonSerializer.Serialize(storage) + "))localStorage.setItem(k,v)");
            bool isAbsolute = Uri.TryCreate(capture.Url, UriKind.Absolute, out Uri? absolute) && absolute.Scheme is "http" or "https";
            string path = overrideUrl && isAbsolute ? absolute!.PathAndQuery + absolute.Fragment : capture.Url;
            Uri url = isAbsolute && !overrideUrl ? absolute! : new Uri(new Uri(baseUrl!.TrimEnd('/') + "/"), path);
            IResponse? response = await page.GotoAsync(url.ToString(), new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Load
            });
            if (response is not null && !response.Ok)
            {
                throw new InvalidOperationException($"Capture HTTP {response.Status}: {url}");
            }

            foreach (CaptureAction action in capture.Actions)
            {
                ILocator? target = string.IsNullOrEmpty(action.Selector) ? null : page.Locator(action.Selector);
                switch (action.Type)
                {
                    case "click":
                        await target!.ClickAsync();
                        break;
                    case "fill":
                        await target!.FillAsync(action.Value!);
                        break;
                    case "scroll":
                        if (target is null)
                        {
                            await page.EvaluateAsync("p=>scrollTo(p.x,p.y)", new
                            {
                                x = action.X,
                                y = action.Y
                            });
                        }
                        else
                        {
                            await target.EvaluateAsync("(el,p)=>el.scrollTo(p.x,p.y)", new
                            {
                                x = action.X,
                                y = action.Y
                            });
                        }

                        break;
                }
            }

            if (capture.Ready is not null)
            {
                await page.Locator(capture.Ready).First.WaitForAsync();
            }

            if (capture.WaitFor is not null)
            {
                await page.WaitForFunctionAsync(capture.WaitFor);
            }

            if (capture.Css is not null)
            {
                await page.AddStyleTagAsync(new PageAddStyleTagOptions
                {
                    Content = capture.Css
                });
            }

            await SettleAsync(page);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = destination,
                Animations = ScreenshotAnimations.Disabled,
                Caret = ScreenshotCaret.Hide
            });
        }
        finally
        {
            await page.CloseAsync();
        }
    }
}
