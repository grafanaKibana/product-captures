using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProductCaptures;

public static class QuartzHost
{
    public const string Revision = "97a2d05f80c4c50534959b1d0d41cc4b3895625e";
    public const string Upstream = "https://github.com/jackyzha0/quartz.git";
    static readonly string[] DefaultInputs = ["src", "types", "package.json", "package-lock.json", "tsconfig.json", "tsconfig.build.json", "tsup.config.ts", "LICENSE"];
    public static void Validate(Config config)
    {
        if (config.Quartz is not { } quartz) return;
        if (!Regex.IsMatch(quartz.Revision ?? Revision, "^[a-f0-9]{40}$")) throw new ArgumentException("quartz.revision must be a pinned commit");
        if (!Regex.IsMatch(quartz.Upstream ?? Upstream, @"^https://[a-zA-Z0-9.-]+/[a-zA-Z0-9_./-]+\.git$")) throw new ArgumentException("quartz.upstream must be an HTTPS Git repository");
        var plugin = Paths.Within(config.Root, quartz.PluginDir, true);
        var content = Paths.Within(config.Root, quartz.Content);
        var cache = Paths.Within(config.Root, ".product-captures-cache");
        if (Overlaps(plugin, cache) && plugin != config.Root || Overlaps(content, cache)) throw new ArgumentException("Quartz inputs must be separate from cache");
        CheckTree(content);
        var inputs = Inputs(quartz, plugin);
        if (!inputs.Contains("package.json") || !inputs.Contains("package-lock.json")) throw new ArgumentException("Quartz inputs require package.json and package-lock.json");
        foreach (var input in inputs)
        {
            var path = Paths.Within(plugin, input);
            if (Overlaps(path, cache)) throw new ArgumentException("Quartz inputs must be separate from cache");
            CheckTree(path);
        }
        _ = Metadata(plugin);
        foreach (var item in new[] { quartz.Options, quartz.Configuration }) if (item is { } element && element.ValueKind != JsonValueKind.Object) throw new ArgumentException("Quartz options and configuration must be objects");
        if (quartz.Build is { } build) _ = BuildCommand.Parse(build, plugin);
    }
    static (string Name, int Order) Metadata(string plugin)
    {
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(plugin, "package.json")));
        if (!package.RootElement.TryGetProperty("quartz", out var metadata) || metadata.ValueKind != JsonValueKind.Object || !metadata.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || !Regex.IsMatch(name.GetString()!, "^[a-z0-9]+(?:-[a-z0-9]+)*$")) throw new ArgumentException("Plugin package.json requires quartz.name metadata");
        if (metadata.TryGetProperty("defaultOrder", out var value) && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _))) throw new ArgumentException("Plugin quartz.defaultOrder must be an integer");
        return (name.GetString()!, metadata.TryGetProperty("defaultOrder", out value) ? value.GetInt32() : 10);
    }
    static string[] Inputs(Quartz quartz, string plugin) => quartz.Inputs ?? DefaultInputs.Where(input => File.Exists(Path.Combine(plugin, input)) || Directory.Exists(Path.Combine(plugin, input))).ToArray();
    static bool Overlaps(string a, string b) => a == b || a.StartsWith(b + Path.DirectorySeparatorChar) || b.StartsWith(a + Path.DirectorySeparatorChar);
    public static void CheckTree(string path, bool recursive = true)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException($"Symlink is not allowed in host input: {path}");
        if (recursive && (attributes & FileAttributes.Directory) != 0) foreach (var entry in Directory.EnumerateFileSystemEntries(path)) CheckTree(entry);
    }
    static void Copy(string source, string destination)
    {
        if (Directory.Exists(source)) { Directory.CreateDirectory(destination); foreach (var entry in Directory.EnumerateFileSystemEntries(source)) Copy(entry, Path.Combine(destination, Path.GetFileName(entry))); }
        else { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, true); }
    }
    static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var item in source)
            if (item.Value is JsonObject nested && target[item.Key] is JsonObject existing) Merge(existing, nested);
            else target[item.Key] = item.Value?.DeepClone();
    }
    public static async Task<string> PrepareAsync(Config config, CancellationToken ct = default)
    {
        Validate(config);
        var quartz = config.Quartz!;
        var source = Paths.Within(config.Root, quartz.PluginDir, true);
        var cache = Paths.Within(config.Root, ".product-captures-cache");
        var host = Paths.Within(config.Root, ".product-captures-cache/quartz");
        var plugin = Paths.Within(config.Root, ".product-captures-cache/plugin");
        var revision = quartz.Revision ?? Revision; var upstream = quartz.Upstream ?? Upstream;
        Directory.CreateDirectory(cache);
        File.Delete(Paths.Within(cache, "fixture.json"));
        var npm = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
        Task<string> Run(string command, IEnumerable<string> args, string cwd) => Processes.RunAsync(command, args, cwd, cancellationToken: ct);
        if (!File.Exists(Path.Combine(host, ".git", "HEAD")))
        {
            Directory.CreateDirectory(host);
            if (Directory.EnumerateFileSystemEntries(host).Any()) throw new InvalidOperationException("Refusing non-empty Quartz cache");
            await Run("git", ["init", "-q"], host); await Run("git", ["remote", "add", "origin", upstream], host);
            await Run("git", ["fetch", "--depth", "1", "origin", revision], host); await Run("git", ["checkout", "--detach", "FETCH_HEAD"], host);
        }
        if ((await Run("git", ["rev-parse", "HEAD"], host)).Trim() != revision) throw new InvalidOperationException("Quartz cache has a different revision");
        if ((await Run("git", ["remote", "get-url", "origin"], host)).Trim() != upstream) throw new InvalidOperationException("Quartz cache has an unexpected origin");
        foreach (var path in new[] { ".git", "node_modules", "package.json", "package-lock.json", "quartz.config.default.yaml", "quartz.config.yaml", "content", "public", "quartz/static/capture-font.ttf", "quartz/styles/custom.scss", "quartz/bootstrap-cli.mjs", ".quartz", "quartz.lock.json" }) _ = Paths.Within(host, path);
        if (!File.Exists(Path.Combine(host, "node_modules", ".package-lock.json"))) await Run(npm, ["ci", "--no-audit", "--no-fund", "--cache", Path.Combine(cache, "npm-cache")], host);
        var json = await Run("node", ["-e", "const fs=require('node:fs'); console.log(JSON.stringify(require('yaml').parse(fs.readFileSync('quartz.config.default.yaml','utf8'))))"], host);
        var settings = JsonNode.Parse(json)!.AsObject();
        var configuration = settings["configuration"]!.AsObject();
        Merge(configuration, new JsonObject { ["pageTitle"] = quartz.Title ?? config.Name, ["baseUrl"] = "example.invalid", ["analytics"] = null, ["enablePopovers"] = false, ["theme"] = new JsonObject { ["fontOrigin"] = "local" } });
        if (quartz.Configuration is { } overrides) Merge(configuration, JsonNode.Parse(overrides.GetRawText())!.AsObject());
        if (Directory.Exists(plugin)) Directory.Delete(plugin, true);
        Directory.CreateDirectory(plugin);
        foreach (var input in Inputs(quartz, source)) Copy(Paths.Within(source, input), Paths.Within(plugin, input));
        await Run(npm, ["ci", "--no-audit", "--no-fund", "--cache", Path.Combine(cache, "npm-cache")], plugin);
        if (quartz.Build is { } build) { var command = BuildCommand.Parse(build, plugin); await Processes.ShellAsync(command, ct); }
        else await Run(npm, ["run", "build"], plugin);
        var (pluginName, order) = Metadata(plugin);
        settings["plugins"]!.AsArray().Insert(0, new JsonObject { ["source"] = new JsonObject { ["repo"] = plugin, ["name"] = pluginName }, ["enabled"] = true, ["order"] = order, ["options"] = quartz.Options is { } options ? JsonNode.Parse(options.GetRawText()) : new JsonObject() });
        await File.WriteAllTextAsync(Path.Combine(host, "quartz.config.yaml"), settings.ToJsonString(Config.Json), ct);
        foreach (var item in new[] { "content", "public", ".quartz" }) if (Directory.Exists(Path.Combine(host, item))) Directory.Delete(Path.Combine(host, item), true);
        File.Delete(Path.Combine(host, "quartz.lock.json"));
        Copy(Paths.Within(config.Root, quartz.Content), Path.Combine(host, "content"));
        var assets = Path.Combine(Composition.ToolRoot, "assets");
        File.Copy(Path.Combine(assets, "Geist.ttf"), Path.Combine(host, "quartz", "static", "capture-font.ttf"), true);
        await File.WriteAllTextAsync(Path.Combine(host, "quartz", "styles", "custom.scss"), "@use \"./variables.scss\" as *;\n@font-face{font-family:\"Capture Geist\";src:url(\"/static/capture-font.ttf\") format(\"truetype\");font-display:block;}\n:root{--headerFont:\"Capture Geist\",sans-serif;--bodyFont:\"Capture Geist\",sans-serif;}\n", ct);
        await Run("node", ["quartz/bootstrap-cli.mjs", "plugin", "install", "--from-config"], host);
        await Run("node", ["quartz/bootstrap-cli.mjs", "build"], host);
        var output = Paths.Within(host, "public");
        if (!File.Exists(Path.Combine(output, "index.html"))) throw new InvalidOperationException("Quartz build did not produce public/index.html");
        return output;
    }
}
