using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Playwright;

namespace ProductCaptures;

public static class ObsidianHost
{
    private static readonly string[] PluginFiles =
    [
        "manifest.json",
        "main.js",
        "styles.css"
    ];

    public static async Task CaptureAsync(
        Config config,
        IPlaywright playwright,
        string theme,
        string destination,
        CancellationToken ct = default)
    {
        Capture capture = config.Capture!;
        string source = Paths.Within(config.Root, capture.Vault);
        string plugin = Paths.Within(config.Root, capture.PluginDir, true);
        ValidateInputs(source, plugin);

        using JsonDocument manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(plugin, "manifest.json"), ct));
        string id = manifest.RootElement.GetProperty("id").GetString()!;
        string cache = Paths.Within(config.Root, ".product-captures-cache/obsidian");
        Directory.CreateDirectory(cache);
        string session = Path.Combine(cache, "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        string vault = Path.Combine(session, "vault");
        Process? adapter = null;
        IBrowser? browser = null;
        using CancellationTokenRegistration cancel = ct.Register(() =>
        {
            try
            {
                adapter?.StandardInput.Close();
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }

            if (browser is not null)
            {
                _ = browser.CloseAsync();
            }
        });
        try
        {
            ct.ThrowIfCancellationRequested();
            CopyVault(source, vault);
            string pluginCopy = CopyPlugin(plugin, session);
            string adapters = Path.Combine(Composition.ToolRoot, "adapters");
            await EnsureDependenciesAsync(adapters, cache, ct);

            Dictionary<string, string> storage = CaptureEngine.ResolveStorage(capture, theme);
            string options = JsonSerializer.Serialize(new
            {
                vault,
                plugin = pluginCopy,
                localStorage = storage,
                cache = Environment.GetEnvironmentVariable("PRODUCT_CAPTURE_OBSIDIAN_CACHE") ?? cache,
                appVersion = capture.AppVersion,
                installerVersion = capture.InstallerVersion
            });
            adapter = Processes.Start("node", [Path.Combine(adapters, "obsidian-launcher.mjs"), options], adapters);
            Task<string> errors = adapter.StandardError.ReadToEndAsync(ct);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(120000);
            string line = await adapter.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("Obsidian launcher failed: " + await errors);
            using JsonDocument launch = JsonDocument.Parse(line);
            string endpoint = launch.RootElement.GetProperty("endpoint").GetString()!;
            while (browser is null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (adapter.HasExited)
                {
                    throw new InvalidOperationException("Obsidian exited: " + await errors);
                }

                try
                {
                    browser = await playwright.Chromium.ConnectOverCDPAsync(endpoint, new BrowserTypeConnectOverCDPOptions
                    {
                        Timeout = 1000
                    });
                }
                catch (PlaywrightException)
                {
                    await Task.Delay(250, timeout.Token);
                }
            }

            IBrowserContext context = browser.Contexts.First();
            IPage page = await WaitForPageAsync(context, timeout.Token);

            await PreparePageAsync(page, capture, id, theme);
            await ApplyActionsAsync(page, capture.Actions);

            await WaitForCaptureAsync(page, capture);

            await CaptureEngine.SettleAsync(page);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = destination,
                Animations = ScreenshotAnimations.Disabled,
                Caret = ScreenshotCaret.Hide,
                Scale = ScreenshotScale.Css
            });
        }
        finally
        {
            if (browser is not null)
            {
                try
                {
                    await browser.CloseAsync();
                }
                catch (PlaywrightException)
                {
                }
            }

            if (adapter is not null)
            {
                try
                {
                    adapter.StandardInput.Close();
                    using var shutdown = new CancellationTokenSource(8000);
                    try
                    {
                        await adapter.WaitForExitAsync(shutdown.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Processes.Stop(adapter);
                        await adapter.WaitForExitAsync();
                    }
                }
                finally
                {
                    Processes.Stop(adapter);
                    adapter.Dispose();
                }
            }

            Directory.Delete(session, true);
        }
    }

    private static void ValidateInputs(string source, string plugin)
    {
        QuartzHost.CheckTree(source);
        foreach (string file in PluginFiles)
        {
            string input = Paths.Within(plugin, file);
            if (File.Exists(input))
            {
                QuartzHost.CheckTree(input);
            }
        }
    }

    private static string CopyPlugin(string plugin, string session)
    {
        string pluginCopy = Path.Combine(session, "plugin");
        Directory.CreateDirectory(pluginCopy);
        foreach (string file in PluginFiles)
        {
            if (File.Exists(Path.Combine(plugin, file)))
            {
                File.Copy(Path.Combine(plugin, file), Path.Combine(pluginCopy, file));
            }
        }

        return pluginCopy;
    }

    private static async Task EnsureDependenciesAsync(
        string adapters,
        string cache,
        CancellationToken ct)
    {
        string dependencyLock = Path.Combine(adapters, "package-lock.json");
        string dependencyHash = Convert.ToHexStringLower(
            SHA256.HashData(await File.ReadAllBytesAsync(dependencyLock, ct)));
        string installed = Path.Combine(adapters, ".installed");
        if (!File.Exists(installed)
            || (await File.ReadAllTextAsync(installed, ct)) != dependencyHash
            || !File.Exists(Path.Combine(adapters, "node_modules", "obsidian-launcher", "package.json")))
        {
            File.Delete(installed);
            await Processes.RunAsync(
                OperatingSystem.IsWindows() ? "npm.cmd" : "npm",
                ["ci", "--no-audit", "--no-fund", "--cache", Path.Combine(cache, "npm")],
                adapters,
                cancellationToken: ct);
            await File.WriteAllTextAsync(installed, dependencyHash, ct);
        }
    }

    private static async Task<IPage> WaitForPageAsync(
        IBrowserContext context,
        CancellationToken cancellationToken)
    {
        IPage? page = null;
        while (page is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            page = context.Pages.FirstOrDefault(candidate => candidate.Url.StartsWith("app://"));
            if (page is null)
            {
                await Task.Delay(200, cancellationToken);
            }
        }

        return page;
    }

    private static async Task PreparePageAsync(
        IPage page,
        Capture capture,
        string id,
        string theme)
    {
        await page.SetViewportSizeAsync(capture.Viewport.Width, capture.Viewport.Height);
        await page.EmulateMediaAsync(new PageEmulateMediaOptions
        {
            ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
            ReducedMotion = ReducedMotion.Reduce
        });
        await page.WaitForFunctionAsync("() => Boolean(globalThis.app?.workspace?.layoutReady)");
        await page.WaitForFunctionAsync("id => Boolean(globalThis.app?.plugins?.plugins?.[id])", id);
        await page.EvaluateAsync("""
            async ({note,theme}) => {
              const app=globalThis.app;
              app.vault.setConfig('showInlineTitle',false); app.vault.setConfig('readableLineLength',false);
              document.body.classList.toggle('theme-dark',theme==='dark');document.body.classList.toggle('theme-light',theme==='light');
              app.workspace.leftSplit?.collapse();app.workspace.rightSplit?.collapse();
              const file=app.vault.getAbstractFileByPath(note.endsWith('.md')?note:note+'.md');
              if(!file || file.extension!=='md') throw Error('Capture note not found: '+note);
              const leaf=app.workspace.getLeaf(false);await leaf.openFile(file);
              await leaf.setViewState({type:'markdown',state:{file:file.path,mode:'preview'},active:true});await document.fonts.ready;
            }
            """, new
        {
            note = capture.Note,
            theme
        });
        await page.Locator(".markdown-preview-view").Last.WaitForAsync();
    }

    private static async Task ApplyActionsAsync(
        IPage page,
        IEnumerable<CaptureAction> actions)
    {
        foreach (CaptureAction action in actions)
        {
            ILocator locator = page.Locator(string.IsNullOrWhiteSpace(action.Selector) ? ".workspace-leaf.mod-active .markdown-preview-view" : action.Selector).First;
            if (action.Type == "click")
            {
                await locator.ClickAsync();
            }
            else if (action.Type == "fill")
            {
                await locator.FillAsync(action.Value!);
            }
            else
            {
                await locator.EvaluateAsync("(element,{x,y})=>element.scrollTo(x,y)", new
                {
                    x = action.X,
                    y = action.Y
                });
            }
        }
    }

    private static async Task WaitForCaptureAsync(IPage page, Capture capture)
    {
        if (capture.Ready is not null)
        {
            await page.Locator(".workspace-leaf.mod-active .markdown-preview-view " + capture.Ready).Last.WaitForAsync();
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
    }

    private static void CopyVault(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            if (Path.GetFileName(directory) != ".obsidian")
            {
                CopyVault(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }
    }
}
