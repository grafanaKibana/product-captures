using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProductCaptures;

public static class Program
{
    public static string Version => typeof(Program).Assembly.GetName().Version!.ToString(3);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "__exec")
        {
            return await Processes.ExecAsync(args[1..]);
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        using var terminate = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });
        try
        {
            return await RunCommandAsync(args, cancellation.Token);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static async Task<int> RunCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            Console.WriteLine(Version);
            return 0;
        }

        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine($"Product Captures {Version}\nUsage: --config product-captures.json [--dry-run] [--reuse-screenshots] [--url https://preview.example]\nInstall browser: install [--with-deps]");
            return 0;
        }

        if (args.Length > 0 && args[0] == "install")
        {
            if (args.Skip(1).Any(a => a != "--with-deps") || args.Length > 2)
            {
                throw new ArgumentException("install accepts only --with-deps");
            }

            return Microsoft.Playwright.Program.Main(args.Contains("--with-deps") ? ["install", "--with-deps", "chromium"] : ["install", "chromium"]);
        }

        if (args.Length > 0 && args[0] == "action")
        {
            return await RunActionAsync(args, cancellationToken);
        }

        var configPath = "product-captures.json";
        bool dryRun = false;
        bool reuse = false;
        string? url = null;
        var seen = new HashSet<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!seen.Add(arg))
            {
                throw new ArgumentException($"Duplicate option: {arg}");
            }

            switch (arg)
            {
                case "--config":
                    configPath = Value();
                    break;
                case "--url":
                    url = Value();
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--reuse-screenshots":
                    reuse = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }

            string Value() => i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : throw new ArgumentException($"Missing value for {arg}");
        }

        await CaptureEngine.RunAsync(Config.Load(configPath), new(dryRun, reuse, url), cancellationToken);
        return 0;
    }

    private static async Task<int> RunActionAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 2 || !new[]
        {
                    "plan",
                    "run",
                    "release",
                    "pr"
                }.Contains(args[1]))
        {
            throw new ArgumentException("action requires plan, run, release or pr");
        }

        var action = ActionOptions.FromEnvironment();
        var config = Config.Load(action.Config);
        CaptureEngine.Validate(config, action.Options);
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory();
        var artifact = Paths.Within(workspace, config.OutputDirectory);
        if (args[1] == "plan")
        {
            await CaptureEngine.RunAsync(config, action.Options with
            {
                DryRun = true
            }, cancellationToken);
            if (Environment.GetEnvironmentVariable("GITHUB_OUTPUT") is { Length: > 0 } output)
            {
                await File.AppendAllTextAsync(output, $"artifact-path={Path.GetRelativePath(workspace, artifact).Replace('\\', '/')}\nneeds-obsidian={(config.Capture?.Type == "obsidian" && !action.Options.ReuseScreenshots).ToString().ToLowerInvariant()}\nneeds-node={(config.Capture is not null && !action.Options.ReuseScreenshots && action.Options.Url is null && (config.Build is not null || config.Capture.Type == "obsidian" || config.Quartz is not null)).ToString().ToLowerInvariant()}\ndry-run={action.Options.DryRun.ToString().ToLowerInvariant()}\n");
            }
        }
        else if (args[1] == "run")
        {
            await CaptureEngine.RunAsync(config, action.Options, cancellationToken);
        }
        else if (!action.Options.DryRun && args[1] == "release" && action.ReleaseTag.Length > 0)
        {
            await Publisher.ReleaseAsync(config, action, cancellationToken);
        }
        else if (!action.Options.DryRun && args[1] == "pr" && action.UpdatePr)
        {
            await Publisher.PullRequestAsync(config, action, cancellationToken);
        }

        return 0;

    }
}

public sealed record ActionOptions(string Config, RunOptions Options, string ReleaseTag, bool UpdatePr)
{
    public static ActionOptions FromEnvironment()
    {
        string Get(string key, string fallback = "") => Environment.GetEnvironmentVariable("CAPTURE_" + key) is { Length: > 0 } value ? value : fallback;
        bool Bool(string key, bool fallback = false) => Get(key, fallback ? "true" : "false") switch
        {
            "true" => true,
            "false" => false,
            _ => throw new ArgumentException($"CAPTURE_{key} must be true or false")
        };
        return new(Get("CONFIG", "product-captures.json"), new(Bool("DRY_RUN", true), Bool("REUSE_SCREENSHOTS"), Get("URL") is { Length: > 0 } url ? url : null), Get("RELEASE_TAG"), Bool("UPDATE_PR"));
    }
}
