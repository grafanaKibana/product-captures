using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProductCaptures;

public static class QuartzHost
{
    public const string Revision = "97a2d05f80c4c50534959b1d0d41cc4b3895625e";
    public const string Upstream = "https://github.com/jackyzha0/quartz.git";
    private static readonly string[] DefaultInputs =
    [
        "src", "types", "package.json", "package-lock.json",
        "tsconfig.json", "tsconfig.build.json", "tsup.config.ts", "LICENSE"
    ];
    public static void Validate(Config config)
    {
        if (config.Quartz is not { } quartz)
        {
            return;
        }

        if (!Regex.IsMatch(quartz.Revision ?? Revision, "^[a-f0-9]{40}$"))
        {
            throw new ArgumentException("quartz.revision must be a pinned commit");
        }

        if (!Regex.IsMatch(quartz.Upstream ?? Upstream, @"^https://[a-zA-Z0-9.-]+/[a-zA-Z0-9_./-]+\.git$"))
        {
            throw new ArgumentException("quartz.upstream must be an HTTPS Git repository");
        }

        string plugin = Paths.Within(config.Root, quartz.PluginDir, true);
        string content = Paths.Within(config.Root, quartz.Content);
        string cache = Paths.Within(config.Root, ".product-captures-cache");
        if ((Overlaps(plugin, cache) && plugin != config.Root) || Overlaps(content, cache))
        {
            throw new ArgumentException("Quartz inputs must be separate from cache");
        }

        CheckTree(content);
        string[] inputs = Inputs(quartz, plugin);
        if (!inputs.Contains("package.json") || !inputs.Contains("package-lock.json"))
        {
            throw new ArgumentException("Quartz inputs require package.json and package-lock.json");
        }

        foreach (string input in inputs)
        {
            string path = Paths.Within(plugin, input);
            if (Overlaps(path, cache))
            {
                throw new ArgumentException("Quartz inputs must be separate from cache");
            }

            CheckTree(path);
        }

        _ = Metadata(plugin);
        JsonElement?[] settings = [quartz.Options, quartz.Configuration];
        foreach (JsonElement? item in settings)
        {
            if (item is { } element && element.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Quartz options and configuration must be objects");
            }
        }

        if (quartz.Build is { } build)
        {
            _ = BuildCommand.Parse(build, plugin);
        }
    }

    private static (string Name, int Order) Metadata(string plugin)
    {
        using JsonDocument package = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(plugin, "package.json")));
        if (!package.RootElement.TryGetProperty("quartz", out JsonElement metadata)
            || metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("name", out JsonElement name)
            || name.ValueKind != JsonValueKind.String
            || !Regex.IsMatch(name.GetString()!, "^[a-z0-9]+(?:-[a-z0-9]+)*$"))
        {
            throw new ArgumentException("Plugin package.json requires quartz.name metadata");
        }

        if (metadata.TryGetProperty("defaultOrder", out JsonElement value)
            && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)))
        {
            throw new ArgumentException("Plugin quartz.defaultOrder must be an integer");
        }

        return (name.GetString()!, metadata.TryGetProperty("defaultOrder", out value) ? value.GetInt32() : 10);
    }

    private static string[] Inputs(Quartz quartz, string plugin) => quartz.Inputs
        ?? DefaultInputs.Where(input => File.Exists(Path.Combine(plugin, input))
            || Directory.Exists(Path.Combine(plugin, input))).ToArray();
    private static bool Overlaps(string a, string b) => a == b
        || a.StartsWith(b + Path.DirectorySeparatorChar)
        || b.StartsWith(a + Path.DirectorySeparatorChar);
    public static void CheckTree(string path, bool recursive = true)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException($"Symlink is not allowed in host input: {path}");
        }

        if (recursive && (attributes & FileAttributes.Directory) != 0)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            {
                CheckTree(entry);
            }
        }
    }

    private static void Copy(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(destination);
            foreach (string entry in Directory.EnumerateFileSystemEntries(source))
            {
                Copy(entry, Path.Combine(destination, Path.GetFileName(entry)));
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, true);
        }
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (KeyValuePair<string, JsonNode?> item in source)
        {
            if (item.Value is JsonObject nested && target[item.Key] is JsonObject existing)
            {
                Merge(existing, nested);
            }
            else
            {
                target[item.Key] = item.Value?.DeepClone();
            }
        }
    }

    public static async Task<string> PrepareAsync(Config config, CancellationToken ct = default)
    {
        Validate(config);
        Quartz quartz = config.Quartz!;
        string source = Paths.Within(config.Root, quartz.PluginDir, true);
        string cache = Paths.Within(config.Root, ".product-captures-cache");
        string host = Paths.Within(config.Root, ".product-captures-cache/quartz");
        string plugin = Paths.Within(config.Root, ".product-captures-cache/plugin");
        string revision = quartz.Revision ?? Revision;
        string upstream = quartz.Upstream ?? Upstream;
        Directory.CreateDirectory(cache);
        File.Delete(Paths.Within(cache, "fixture.json"));
        string npm = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";

        await PrepareHostAsync(host, revision, upstream, ct);
        ValidateHostPaths(host);
        if (!File.Exists(Path.Combine(host, "node_modules", ".package-lock.json")))
        {
            await Processes.RunAsync(
                npm,
                ["ci", "--no-audit", "--no-fund", "--cache", Path.Combine(cache, "npm-cache")],
                host,
                cancellationToken: ct);
        }

        JsonObject settings = await ReadSettingsAsync(config, quartz, host, ct);
        await PreparePluginAsync(quartz, source, plugin, cache, npm, ct);
        AddPluginSettings(settings, quartz, plugin);
        await File.WriteAllTextAsync(
            Path.Combine(host, "quartz.config.yaml"),
            settings.ToJsonString(Config.Json),
            ct);

        await PrepareFixtureAsync(config, quartz, host, ct);
        await Processes.RunAsync(
            "node", ["quartz/bootstrap-cli.mjs", "plugin", "install", "--from-config"],
            host, cancellationToken: ct);
        await Processes.RunAsync(
            "node", ["quartz/bootstrap-cli.mjs", "build"],
            host, cancellationToken: ct);
        string output = Paths.Within(host, "public");
        if (!File.Exists(Path.Combine(output, "index.html")))
        {
            throw new InvalidOperationException("Quartz build did not produce public/index.html");
        }

        return output;
    }

    private static async Task PrepareHostAsync(
        string host,
        string revision,
        string upstream,
        CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(host, ".git", "HEAD")))
        {
            Directory.CreateDirectory(host);
            if (Directory.EnumerateFileSystemEntries(host).Any())
            {
                throw new InvalidOperationException("Refusing non-empty Quartz cache");
            }

            await Processes.RunAsync("git", ["init", "-q"], host, cancellationToken: ct);
            await Processes.RunAsync("git", ["remote", "add", "origin", upstream], host, cancellationToken: ct);
            await Processes.RunAsync("git", ["fetch", "--depth", "1", "origin", revision], host, cancellationToken: ct);
            await Processes.RunAsync("git", ["checkout", "--detach", "FETCH_HEAD"], host, cancellationToken: ct);
        }

        string actualRevision = (await Processes.RunAsync(
            "git", ["rev-parse", "HEAD"], host, cancellationToken: ct)).Trim();
        if (actualRevision != revision)
        {
            throw new InvalidOperationException("Quartz cache has a different revision");
        }

        string actualOrigin = (await Processes.RunAsync(
            "git", ["remote", "get-url", "origin"], host, cancellationToken: ct)).Trim();
        if (actualOrigin != upstream)
        {
            throw new InvalidOperationException("Quartz cache has an unexpected origin");
        }
    }

    private static void ValidateHostPaths(string host)
    {
        string[] paths =
        [
            ".git",
            "node_modules",
            "package.json",
            "package-lock.json",
            "quartz.config.default.yaml",
            "quartz.config.yaml",
            "content",
            "public",
            "quartz/static/capture-font.ttf",
            "quartz/styles/custom.scss",
            "quartz/bootstrap-cli.mjs",
            ".quartz",
            "quartz.lock.json"
        ];
        foreach (string path in paths)
        {
            _ = Paths.Within(host, path);
        }
    }

    private static async Task<JsonObject> ReadSettingsAsync(
        Config config,
        Quartz quartz,
        string host,
        CancellationToken ct)
    {
        string json = await Processes.RunAsync(
            "node",
            ["-e", "const fs=require('node:fs'); console.log(JSON.stringify(require('yaml').parse(fs.readFileSync('quartz.config.default.yaml','utf8'))))"],
            host,
            cancellationToken: ct);
        JsonObject settings = JsonNode.Parse(json)!.AsObject();
        JsonObject configuration = settings["configuration"]!.AsObject();
        var defaults = new JsonObject
        {
            ["pageTitle"] = quartz.Title ?? config.Name,
            ["baseUrl"] = "example.invalid",
            ["analytics"] = null,
            ["enablePopovers"] = false,
            ["theme"] = new JsonObject
            {
                ["fontOrigin"] = "local"
            }
        };
        Merge(configuration, defaults);
        if (quartz.Configuration is { } overrides)
        {
            Merge(configuration, JsonNode.Parse(overrides.GetRawText())!.AsObject());
        }

        return settings;
    }

    private static async Task PreparePluginAsync(
        Quartz quartz,
        string source,
        string plugin,
        string cache,
        string npm,
        CancellationToken ct)
    {
        if (Directory.Exists(plugin))
        {
            Directory.Delete(plugin, true);
        }

        Directory.CreateDirectory(plugin);
        foreach (string input in Inputs(quartz, source))
        {
            Copy(Paths.Within(source, input), Paths.Within(plugin, input));
        }

        await Processes.RunAsync(
            npm,
            ["ci", "--no-audit", "--no-fund", "--cache", Path.Combine(cache, "npm-cache")],
            plugin,
            cancellationToken: ct);
        if (quartz.Build is { } build)
        {
            BuildCommand command = BuildCommand.Parse(build, plugin);
            await Processes.ShellAsync(command, ct);
        }
        else
        {
            await Processes.RunAsync(npm, ["run", "build"], plugin, cancellationToken: ct);
        }
    }

    private static void AddPluginSettings(JsonObject settings, Quartz quartz, string plugin)
    {
        (string pluginName, int order) = Metadata(plugin);
        JsonArray plugins = settings["plugins"]!.AsArray();
        var pluginSettings = new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["repo"] = plugin,
                ["name"] = pluginName
            },
            ["enabled"] = true,
            ["order"] = order,
            ["options"] = quartz.Options is { } options
                ? JsonNode.Parse(options.GetRawText())
                : new JsonObject()
        };
        plugins.Insert(0, pluginSettings);
    }

    private static async Task PrepareFixtureAsync(
        Config config,
        Quartz quartz,
        string host,
        CancellationToken ct)
    {
        string[] directories = ["content", "public", ".quartz"];
        foreach (string item in directories)
        {
            if (Directory.Exists(Path.Combine(host, item)))
            {
                Directory.Delete(Path.Combine(host, item), true);
            }
        }

        File.Delete(Path.Combine(host, "quartz.lock.json"));
        Copy(Paths.Within(config.Root, quartz.Content), Path.Combine(host, "content"));
        string assets = Path.Combine(Composition.ToolRoot, "assets");
        File.Copy(Path.Combine(assets, "Geist.ttf"), Path.Combine(host, "quartz", "static", "capture-font.ttf"), true);
        await File.WriteAllTextAsync(
            Path.Combine(host, "quartz", "styles", "custom.scss"),
            "@use \"./variables.scss\" as *;\n@font-face{font-family:\"Capture Geist\";src:url(\"/static/capture-font.ttf\") format(\"truetype\");font-display:block;}\n:root{--headerFont:\"Capture Geist\",sans-serif;--bodyFont:\"Capture Geist\",sans-serif;}\n",
            ct);
    }
}
