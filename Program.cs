using System.Runtime.InteropServices;

namespace ProductCaptures;

public static class Program
{
    public static string Version =>
        typeof(Program).Assembly.GetName().Version!.ToString(3);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "__exec")
        {
            return await Processes.ExecAsync(args[1..]);
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, context) =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        };
        using PosixSignalRegistration? termination = RegisterTermination(cancellation);

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

    private static PosixSignalRegistration? RegisterTermination(
        CancellationTokenSource cancellation)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });
    }

    private static async Task<int> RunCommandAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            Console.WriteLine(Version);
            return 0;
        }

        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine(
                $"Product Captures {Version}\n"
                + "Usage: --config product-captures.json [--dry-run] "
                + "[--reuse-screenshots] [--url https://preview.example]\n"
                + "Install browser: install [--with-deps]");
            return 0;
        }

        if (args.Length > 0 && args[0] == "install")
        {
            return InstallBrowser(args);
        }

        if (args.Length > 0 && args[0] == "action")
        {
            return await RunActionAsync(args, cancellationToken);
        }

        (string configPath, bool dryRun, bool reuseScreenshots, string? url) =
            ReadCaptureOptions(args);
        Config config = Config.Load(configPath);
        var options = new RunOptions(dryRun, reuseScreenshots, url);
        await CaptureEngine.RunAsync(config, options, cancellationToken);
        return 0;
    }

    private static int InstallBrowser(string[] args)
    {
        if (args.Skip(1).Any(argument => argument != "--with-deps")
            || args.Length > 2)
        {
            throw new ArgumentException("install accepts only --with-deps");
        }

        string[] browserArguments = args.Contains("--with-deps")
            ? ["install", "--with-deps", "chromium"]
            : ["install", "chromium"];
        return Microsoft.Playwright.Program.Main(browserArguments);
    }

    private static (
        string ConfigPath,
        bool DryRun,
        bool ReuseScreenshots,
        string? Url) ReadCaptureOptions(string[] args)
    {
        string configPath = "product-captures.json";
        bool dryRun = false;
        bool reuseScreenshots = false;
        string? url = null;
        var seen = new HashSet<string>();

        for (var index = 0; index < args.Length; index++)
        {
            string option = args[index];
            if (!seen.Add(option))
            {
                throw new ArgumentException($"Duplicate option: {option}");
            }

            switch (option)
            {
                case "--config":
                    configPath = ReadOptionValue(args, ref index, option);
                    break;
                case "--url":
                    url = ReadOptionValue(args, ref index, option);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--reuse-screenshots":
                    reuseScreenshots = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {option}");
            }
        }

        return (configPath, dryRun, reuseScreenshots, url);
    }

    private static string ReadOptionValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--"))
        {
            throw new ArgumentException($"Missing value for {option}");
        }

        index++;
        return args[index];
    }

    private static async Task<int> RunActionAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length != 2 || args[1] is not ("plan" or "run" or "release" or "pr"))
        {
            throw new ArgumentException("action requires plan, run, release or pr");
        }

        ActionOptions action = ActionOptions.FromEnvironment();
        Config config = Config.Load(action.Config);
        CaptureEngine.Validate(config, action.Options);
        string workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
            ?? Directory.GetCurrentDirectory();
        string artifact = Paths.Within(workspace, config.OutputDirectory);

        if (args[1] == "plan")
        {
            await WritePlanAsync(config, action, workspace, artifact, cancellationToken);
        }
        else if (args[1] == "run")
        {
            await CaptureEngine.RunAsync(config, action.Options, cancellationToken);
        }
        else if (!action.Options.DryRun
            && args[1] == "release"
            && action.ReleaseTag.Length > 0)
        {
            await Publisher.ReleaseAsync(config, action, cancellationToken);
        }
        else if (!action.Options.DryRun && args[1] == "pr" && action.UpdatePr)
        {
            await Publisher.PullRequestAsync(config, action, cancellationToken);
        }

        return 0;
    }

    private static async Task WritePlanAsync(
        Config config,
        ActionOptions action,
        string workspace,
        string artifact,
        CancellationToken cancellationToken)
    {
        await CaptureEngine.RunAsync(
            config,
            action.Options with
            {
                DryRun = true
            },
            cancellationToken);

        if (Environment.GetEnvironmentVariable("GITHUB_OUTPUT") is not { Length: > 0 } output)
        {
            return;
        }

        string artifactPath = Path.GetRelativePath(workspace, artifact).Replace('\\', '/');
        bool needsObsidian = config.Capture?.Type == "obsidian"
            && !action.Options.ReuseScreenshots;
        bool needsNode = config.Capture is not null
            && !action.Options.ReuseScreenshots
            && action.Options.Url is null
            && (config.Build is not null
                || config.Capture.Type == "obsidian"
                || config.Quartz is not null);
        string planOutput = $"artifact-path={artifactPath}\n"
            + $"needs-obsidian={needsObsidian.ToString().ToLowerInvariant()}\n"
            + $"needs-node={needsNode.ToString().ToLowerInvariant()}\n"
            + $"dry-run={action.Options.DryRun.ToString().ToLowerInvariant()}\n";
        await File.AppendAllTextAsync(output, planOutput);
    }
}

public sealed record ActionOptions(
    string Config,
    RunOptions Options,
    string ReleaseTag,
    bool UpdatePr)
{
    public static ActionOptions FromEnvironment()
    {
        string configPath = ReadEnvironment("CONFIG", "product-captures.json");
        bool dryRun = ReadBoolean("DRY_RUN", true);
        bool reuseScreenshots = ReadBoolean("REUSE_SCREENSHOTS");
        string? url = ReadEnvironment("URL") is { Length: > 0 } value ? value : null;
        var options = new RunOptions(dryRun, reuseScreenshots, url);
        string releaseTag = ReadEnvironment("RELEASE_TAG");
        bool updatePr = ReadBoolean("UPDATE_PR");

        return new ActionOptions(configPath, options, releaseTag, updatePr);
    }

    private static string ReadEnvironment(string key, string fallback = "") =>
        Environment.GetEnvironmentVariable("CAPTURE_" + key) is { Length: > 0 } value
            ? value
            : fallback;

    private static bool ReadBoolean(string key, bool fallback = false) =>
        ReadEnvironment(key, fallback ? "true" : "false") switch
        {
            "true" => true,
            "false" => false,
            _ => throw new ArgumentException($"CAPTURE_{key} must be true or false")
        };
}
