using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;

namespace ProductCaptures;

public static class ObsidianHost
{
    public static async Task CaptureAsync(Config config, IPlaywright playwright, string theme, string destination, CancellationToken ct = default)
    {
        var capture = config.Capture!;
        var source = Paths.Within(config.Root, capture.Vault);
        var plugin = Paths.Within(config.Root, capture.PluginDir, true);
        QuartzHost.CheckTree(source);
        foreach (var file in new[]
        {
            "manifest.json",
            "main.js",
            "styles.css"
        }

        )
        {
            var input = Paths.Within(plugin, file);
            if (File.Exists(input))
            {
                QuartzHost.CheckTree(input);
            }
        }

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(plugin, "manifest.json"), ct));
        var id = manifest.RootElement.GetProperty("id").GetString()!;
        var cache = Paths.Within(config.Root, ".product-captures-cache/obsidian");
        Directory.CreateDirectory(cache);
        var session = Path.Combine(cache, "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        var vault = Path.Combine(session, "vault");
        Process? adapter = null;
        IBrowser? browser = null;
        using var cancel = ct.Register(() =>
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
            var pluginCopy = Path.Combine(session, "plugin");
            Directory.CreateDirectory(pluginCopy);
            foreach (var file in new[]
            {
                "manifest.json",
                "main.js",
                "styles.css"
            }

            )
            {
                if (File.Exists(Path.Combine(plugin, file)))
                {
                    File.Copy(Path.Combine(plugin, file), Path.Combine(pluginCopy, file));
                }
            }

            var adapters = Path.Combine(Composition.ToolRoot, "adapters");
            var dependencyLock = Path.Combine(adapters, "package-lock.json");
            var dependencyHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(dependencyLock, ct)));
            var installed = Path.Combine(adapters, ".installed");
            if (!File.Exists(installed) || (await File.ReadAllTextAsync(installed, ct)) != dependencyHash || !File.Exists(Path.Combine(adapters, "node_modules", "obsidian-launcher", "package.json")))
            {
                File.Delete(installed);
                await Processes.RunAsync(OperatingSystem.IsWindows() ? "npm.cmd" : "npm", ["ci", "--no-audit", "--no-fund", "--cache", Path.Combine(cache, "npm")], adapters, cancellationToken: ct);
                await File.WriteAllTextAsync(installed, dependencyHash, ct);
            }

            var storage = capture.Storage.ToDictionary(p => p.Key, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()!.Replace("$theme", theme) : p.Value.GetProperty(theme).GetString()!);
            var options = JsonSerializer.Serialize(new
            {
                vault,
                plugin = pluginCopy,
                localStorage = storage,
                cache = Environment.GetEnvironmentVariable("PRODUCT_CAPTURE_OBSIDIAN_CACHE") ?? cache,
                appVersion = capture.AppVersion,
                installerVersion = capture.InstallerVersion
            });
            adapter = Processes.Start("node", [Path.Combine(adapters, "obsidian-launcher.mjs"), options], adapters);
            var errors = adapter.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(120000);
            var line = await adapter.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("Obsidian launcher failed: " + await errors);
            using var launch = JsonDocument.Parse(line);
            var endpoint = launch.RootElement.GetProperty("endpoint").GetString()!;
            while (browser is null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (adapter.HasExited)
                {
                    throw new InvalidOperationException("Obsidian exited: " + await errors);
                }

                try
                {
                    browser = await playwright.Chromium.ConnectOverCDPAsync(endpoint, new()
                    {
                        Timeout = 1000
                    });
                }
                catch (PlaywrightException)
                {
                    await Task.Delay(250, timeout.Token);
                }
            }

            var context = browser.Contexts.First();
            IPage? page = null;
            while (page is null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                page = context.Pages.FirstOrDefault(p => p.Url.StartsWith("app://"));
                if (page is null)
                {
                    await Task.Delay(200, timeout.Token);
                }
            }

            await page.SetViewportSizeAsync(capture.Viewport.Width, capture.Viewport.Height);
            await page.EmulateMediaAsync(new()
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
            foreach (var action in capture.Actions)
            {
                var locator = page.Locator(string.IsNullOrWhiteSpace(action.Selector) ? ".workspace-leaf.mod-active .markdown-preview-view" : action.Selector).First;
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
                await page.AddStyleTagAsync(new()
                {
                    Content = capture.Css
                });
            }

            await CaptureEngine.SettleAsync(page);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await page.ScreenshotAsync(new()
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

    private static void CopyVault(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            if (Path.GetFileName(directory) != ".obsidian")
            {
                CopyVault(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }
    }
}
