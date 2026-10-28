using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Playwright;

namespace ProductCaptures;

public sealed record RunOptions(bool DryRun = false, bool ReuseScreenshots = false, string? Url = null);
public static class CaptureEngine
{
    public static readonly string[] Themes = ["light", "dark"];
    public static string[] OwnedFiles(Config config) => config.Capture is null ? ["hero.png", "hero-dark.png"] : ["captures/overview-light.png", "captures/overview-dark.png", "hero.png", "hero-dark.png"];
    private static string[] ObsoleteFiles(Config config) => config.Capture is null ? ["captures/overview-light.png", "captures/overview-dark.png"] : [];
    public static void Validate(Config config, RunOptions options)
    {
        config.Validate();
        if (options.Url is not null && config.Capture?.Type != "web")
        {
            throw new ArgumentException("--url is only available for web captures");
        }

        if (options.Url is not null && (!Uri.TryCreate(options.Url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("--url must be an absolute HTTP URL");
        }

        foreach (var file in OwnedFiles(config).Append("manifest.json").Concat(ObsoleteFiles(config)))
        {
            var destination = Paths.Within(config.Root, Path.Combine(config.OutputDirectory, file));
            if (Directory.Exists(destination))
            {
                throw new ArgumentException($"Output file is a directory: {destination}");
            }
        }

        if (options.ReuseScreenshots && config.Capture is not null)
        {
            foreach (var theme in Themes)
            {
                _ = ReadImage(Paths.Within(config.Root, Path.Combine(config.OutputDirectory, $"captures/overview-{theme}.png")));
            }
        }

        if (!options.ReuseScreenshots && config.Capture?.Type == "image")
        {
            foreach (var theme in Themes)
            {
                _ = ReadImage(Paths.Within(config.Root, config.Capture.Images![theme]));
            }
        }

        if (config.Quartz is not null)
        {
            QuartzHost.Validate(config);
        }
    }

    public static async Task RunAsync(Config config, RunOptions options, CancellationToken cancellationToken = default)
    {
        Validate(config, options);
        if (options.DryRun)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                name = config.Name,
                output = config.OutputDirectory,
                files = OwnedFiles(config),
                reuseScreenshots = options.ReuseScreenshots,
                dryRun = true
            }, Config.Json));
            return;
        }

        var stage = Path.Combine(Path.GetTempPath(), "product-captures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        CaptureServer? server = null;
        try
        {
            var fresh = config.Capture is not null && !options.ReuseScreenshots;
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

            var baseUrl = options.Url;
            if (fresh && config.Capture?.Type == "web" && baseUrl is null && !(Uri.TryCreate(config.Capture.Url, UriKind.Absolute, out var configuredUrl) && configuredUrl.Scheme is "http" or "https"))
            {
                server = await CaptureServer.StartAsync(config, quartzDirectory, cancellationToken);
                baseUrl = server.Url;
            }

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new()
            {
                Headless = true,
                Args = ["--no-sandbox"]
            });
            using var cancelBrowser = cancellationToken.Register(() =>
            {
                _ = browser.CloseAsync();
            });
            foreach (var theme in Themes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SourceImage? source = null;
                if (config.Capture is { } capture)
                {
                    var relative = $"captures/overview-{theme}.png";
                    var destination = Path.Combine(stage, relative);
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

                    source = ReadImage(destination);
                }

                var html = await Composition.RenderAsync(config, theme, source);
                var page = await browser.NewPageAsync(new()
                {
                    ViewportSize = new()
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
                    await page.SetContentAsync(html, new()
                    {
                        WaitUntil = WaitUntilState.Load
                    });
                    await page.EvaluateAsync("async () => { await document.fonts.ready; await Promise.all([...document.images].map(i=>i.decode())); await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))); }");
                    await page.ScreenshotAsync(new()
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

            var manifest = new
            {
                version = 2,
                tool = Program.Version,
                configHash = config.ConfigHash,
                mode = options.ReuseScreenshots ? "reuse" : "fresh",
                source = Processes.Commit(config.Root),
                outputs = OwnedFiles(config)
            };
            await File.WriteAllTextAsync(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(manifest, Config.Json), cancellationToken);
            // Every expensive operation completes before any consumer output is replaced.
            var previous = new Dictionary<string, string?>();
            var replaced = new List<string>();
            try
            {
                foreach (var file in OwnedFiles(config).Append("manifest.json").Concat(ObsoleteFiles(config)))
                {
                    var destination = Paths.Within(config.Root, Path.Combine(config.OutputDirectory, file));
                    string? backup = null;
                    if (File.Exists(destination))
                    {
                        backup = Path.Combine(stage, "backup-" + previous.Count);
                        File.Copy(destination, backup);
                    }

                    previous.Add(destination, backup);
                }

                foreach (var file in OwnedFiles(config).Append("manifest.json"))
                {
                    var destination = Paths.Within(config.Root, Path.Combine(config.OutputDirectory, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(Path.Combine(stage, file), destination, true);
                    replaced.Add(destination);
                }

                foreach (var file in ObsoleteFiles(config))
                {
                    var destination = Paths.Within(config.Root, Path.Combine(config.OutputDirectory, file));
                    if (File.Exists(destination))
                    {
                        File.Delete(destination);
                        replaced.Add(destination);
                    }
                }
            }
            catch
            {
                foreach (var destination in replaced.AsEnumerable().Reverse())
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

    public static SourceImage ReadImage(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            throw new ArgumentException($"Expected PNG: {path}");
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        if (width < 1 || height < 1)
        {
            throw new ArgumentException($"Invalid PNG dimensions: {path}");
        }

        return new(width, height, "data:image/png;base64," + Convert.ToBase64String(bytes));
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
    private static async Task CaptureWebAsync(IBrowser browser, Config config, string theme, string? baseUrl, string destination, bool overrideUrl)
    {
        var capture = config.Capture!;
        var page = await browser.NewPageAsync(new()
        {
            ViewportSize = new()
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
            var storage = capture.Storage.ToDictionary(p => p.Key, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()!.Replace("$theme", theme) : p.Value.GetProperty(theme).GetString()!);
            await page.AddInitScriptAsync("for(const [k,v] of Object.entries(" + JsonSerializer.Serialize(storage) + "))localStorage.setItem(k,v)");
            var isAbsolute = Uri.TryCreate(capture.Url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https";
            var path = overrideUrl && isAbsolute ? absolute!.PathAndQuery + absolute.Fragment : capture.Url;
            var url = isAbsolute && !overrideUrl ? absolute! : new Uri(new Uri(baseUrl!.TrimEnd('/') + "/"), path);
            var response = await page.GotoAsync(url.ToString(), new()
            {
                WaitUntil = WaitUntilState.Load
            });
            if (response is not null && !response.Ok)
            {
                throw new InvalidOperationException($"Capture HTTP {response.Status}: {url}");
            }

            foreach (var action in capture.Actions)
            {
                var target = string.IsNullOrEmpty(action.Selector) ? null : page.Locator(action.Selector);
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
                await page.AddStyleTagAsync(new()
                {
                    Content = capture.Css
                });
            }

            await SettleAsync(page);
            await page.ScreenshotAsync(new()
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
