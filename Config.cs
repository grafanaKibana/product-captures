using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ProductCaptures;

public sealed class Config
{
    public string Name { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Output { get; set; } = "docs/assets/product-captures";
    public Appearance Appearance { get; set; } = new();
    public Capture? Capture { get; set; } = new();
    public JsonElement? Build { get; set; }
    public Server? Server { get; set; }
    public Quartz? Quartz { get; set; }
    [JsonIgnore] public string Root { get; set; } = "";
    [JsonIgnore] public string ConfigPath { get; set; } = "";
    [JsonIgnore] public string ConfigHash { get; set; } = "";
    [JsonIgnore] public string OutputDirectory => Paths.Within(Root, Output);
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true };
    public static Config Load(string path)
    {
        path = Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(path);
        var config = JsonSerializer.Deserialize<Config>(bytes, Json) ?? throw new ArgumentException("Config must be an object");
        config.Root = Path.GetDirectoryName(path)!; config.ConfigPath = path;
        config.ConfigHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var document = JsonDocument.Parse(bytes);
        var json = document.RootElement;
        RejectDuplicates(json);
        foreach (var property in json.EnumerateObject()) if (property.Name is not ("name" or "subtitle" or "output" or "appearance" or "capture" or "build" or "server" or "quartz")) throw new ArgumentException($"Unknown config property: {property.Name}");
        if (json.TryGetProperty("appearance", out var ap) && ap.ValueKind == JsonValueKind.Object && ap.TryGetProperty("layout", out var layout) && layout.ValueKind != JsonValueKind.String) throw new ArgumentException("appearance.layout must be a string");
        if (!json.TryGetProperty("subtitle", out _)) config.Subtitle = config.Quartz is not null ? "for Quartz" : config.Capture?.Type == "obsidian" ? "for Obsidian" : "";
        if (config.Appearance is not null)
        {
            config.Appearance.Layout ??= config.Quartz is not null || config.Capture?.Type == "obsidian" || config.Subtitle.Contains("Obsidian", StringComparison.OrdinalIgnoreCase) || config.Subtitle.Contains("Quartz", StringComparison.OrdinalIgnoreCase) ? "column" : "top";
            if (!json.TryGetProperty("appearance", out var appearance) || !appearance.TryGetProperty("height", out _)) config.Appearance.Height = config.Appearance.Layout switch { "column" or "horizontal" => 900, "centered" => 1200, _ => 1100 };
        }
        if (config.Capture is not null && (config.Quartz is not null || config.Capture.Type == "obsidian") && (!json.TryGetProperty("capture", out var capture) || !capture.TryGetProperty("viewport", out var viewport) || viewport.ValueKind == JsonValueKind.Object && !viewport.TryGetProperty("height", out _))) config.Capture.Viewport.Height = 1000;
        config.Validate(); return config;
    }
    static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>();
            foreach (var property in element.EnumerateObject()) { if (!keys.Add(property.Name)) throw new ArgumentException($"Duplicate config key: {property.Name}"); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
    public void Validate()
    {
        if (Subtitle is null || Output is null) throw new ArgumentException("subtitle and output cannot be null");
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("name is required");
        _ = OutputDirectory;
        if (Appearance is null) throw new ArgumentException("appearance must be an object");
        Appearance.Layout ??= Capture?.Type == "obsidian" || Subtitle.Contains("Obsidian", StringComparison.OrdinalIgnoreCase) || Subtitle.Contains("Quartz", StringComparison.OrdinalIgnoreCase) ? "column" : "top";
        if (!new[] { "column", "horizontal", "centered", "top" }.Contains(Appearance.Layout)) throw new ArgumentException("Unknown appearance.layout");
        if (Appearance.Width is < 1 or > 8192 || Appearance.Height is < 1 or > 8192) throw new ArgumentException("Invalid artwork dimensions");
        foreach (var color in new[] { Appearance.Background, Appearance.DarkBackground, Appearance.Tint, Appearance.Shade, Appearance.Glow, Appearance.Ink }) if (color is not null && !Regex.IsMatch(color, "^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")) throw new ArgumentException("Appearance colors must be hexadecimal CSS colors");
        if (Appearance.Background is null || Appearance.ImageFit is not ("crop" or "contain") || !double.IsFinite(Appearance.ImageZoom) || Appearance.ImageZoom is <= 0 or > 10 || !double.IsFinite(Appearance.ImageOffsetX)) throw new ArgumentException("Invalid appearance image settings");
        foreach (var fraction in new[] { Appearance.FrameHeight, Appearance.EdgeBleed, Appearance.FadeStart, Appearance.FadeEnd, Appearance.BlurStart }) if (fraction is { } f && (!double.IsFinite(f) || f is < 0 or > 1)) throw new ArgumentException("Appearance fractions must be between 0 and 1");
        if ((Appearance.FadeStart ?? .67) > (Appearance.FadeEnd ?? 1)) throw new ArgumentException("fadeStart must not exceed fadeEnd");
        if (Appearance.BackdropPosition is not (null or "left" or "center" or "right")) throw new ArgumentException("backdropPosition must be left, center or right");
        if (Capture is { } capture)
        {
            if (capture.Url is null || Regex.IsMatch(capture.Url, "^[A-Za-z][A-Za-z0-9+.-]*:") && (!Uri.TryCreate(capture.Url, UriKind.Absolute, out var captureUri) || captureUri.Scheme is not ("http" or "https"))) throw new ArgumentException("capture.url must be relative or HTTP");
            if (!new[] { "web", "obsidian", "image" }.Contains(capture.Type)) throw new ArgumentException("Unknown capture.type");
            if (capture.Viewport is null || capture.Viewport.Width is < 1 or > 8192 || capture.Viewport.Height is < 1 or > 8192) throw new ArgumentException("Invalid capture.viewport");
            if (capture.Type == "obsidian" && (!Regex.IsMatch(capture.AppVersion ?? "", @"^\d+\.\d+\.\d+$") || !Regex.IsMatch(capture.InstallerVersion ?? "", @"^\d+\.\d+\.\d+$"))) throw new ArgumentException("Obsidian versions must be pinned x.y.z releases");
            if (capture.Type == "image") { if (capture.Images is null) throw new ArgumentException("image capture requires images"); foreach (var theme in new[] { "light", "dark" }) if (!capture.Images.TryGetValue(theme, out var file)) throw new ArgumentException($"Missing {theme} image"); else Paths.Within(Root, file); }
            foreach (var value in (capture.Storage ?? throw new ArgumentException("storage cannot be null")).Values) if (value.ValueKind != JsonValueKind.String && !(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 2 && value.TryGetProperty("light", out var l) && l.ValueKind == JsonValueKind.String && value.TryGetProperty("dark", out var d) && d.ValueKind == JsonValueKind.String)) throw new ArgumentException("storage values must be strings or {light,dark}");
            foreach (var action in (capture.Actions ?? throw new ArgumentException("actions cannot be null"))) if (!new[] { "click", "fill", "scroll" }.Contains(action.Type) || (action.Type != "scroll" && string.IsNullOrWhiteSpace(action.Selector)) || (action.Type == "fill" && action.Value is null)) throw new ArgumentException("Invalid capture action");
            Paths.Within(Root, capture.Vault); Paths.Within(Root, capture.PluginDir, true);
        }
        if (Quartz is not null) QuartzHost.Validate(this);
        if (Build is { } build) _ = BuildCommand.Parse(build, Root);
        if (Server is { } server)
        {
            if ((server.Directory is null) == (server.Command is null)) throw new ArgumentException("server requires directory or command");
            if (server.Directory is { } dir) Paths.Within(Root, dir, true);
            if (server.Port is < 1 or > 65535 || server.TimeoutMs < 1) throw new ArgumentException("Invalid server port or timeout");
            if (server.Command is not null && (string.IsNullOrWhiteSpace(server.Command) || !Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) throw new ArgumentException("server command requires an HTTP url");
            if (server.Command is not null && Uri.TryCreate(server.Url, UriKind.Absolute, out var commandUri) && commandUri.Host != "localhost" && (!System.Net.IPAddress.TryParse(commandUri.Host, out var address) || !System.Net.IPAddress.IsLoopback(address))) throw new ArgumentException("Command server URL must be loopback");
        }
    }
}
public sealed class Appearance
{
    public string? Layout { get; set; }
    public int Width { get; set; } = 1600;
    public int Height { get; set; } = 1100;
    public string Background { get; set; } = "#fcfcfb";
    public string? DarkBackground { get; set; }
    public string? Tint { get; set; }
    public string? Shade { get; set; }
    public string? Glow { get; set; }
    public string? Ink { get; set; }
    public double ImageZoom { get; set; } = 1;
    public double ImageOffsetX { get; set; }
    public string ImageFit { get; set; } = "crop";
    public double? FrameHeight { get; set; }
    public double EdgeBleed { get; set; } = 0.06;
    public double? FadeStart { get; set; }
    public double? FadeEnd { get; set; }
    public double? BlurStart { get; set; }
    public string? BackdropPosition { get; set; }
}
public sealed class Capture
{
    public string Type { get; set; } = "web";
    public string Url { get; set; } = "/";
    public Viewport Viewport { get; set; } = new();
    public string? Ready { get; set; }
    public string? WaitFor { get; set; }
    public string? Css { get; set; }
    public Dictionary<string, JsonElement> Storage { get; set; } = new();
    public List<CaptureAction> Actions { get; set; } = new();
    public string Vault { get; set; } = "docs/capture-fixtures/vault";
    public string Note { get; set; } = "Product showcase.md";
    public string PluginDir { get; set; } = ".";
    public string AppVersion { get; set; } = "1.14.5";
    public string InstallerVersion { get; set; } = "1.14.5";
    public Dictionary<string, string>? Images { get; set; }
}
public sealed class Viewport { public int Width { get; set; } = 1440; public int Height { get; set; } = 900; }
public sealed class CaptureAction { public string Type { get; set; } = "click"; public string Selector { get; set; } = ""; public string? Value { get; set; } public double X { get; set; } public double Y { get; set; } }
public sealed class Server { public string? Directory { get; set; } public int Port { get; set; } = 4180; public string? Command { get; set; } public string? Url { get; set; } public int TimeoutMs { get; set; } = 60000; }
public sealed class Quartz
{
    public string PluginDir { get; set; } = ".";
    public string Content { get; set; } = "docs/capture-fixtures";
    public string? Title { get; set; }
    public JsonElement? Options { get; set; }
    public string? Revision { get; set; }
    public string? Upstream { get; set; }
    public string[]? Inputs { get; set; }
    public JsonElement? Build { get; set; }
    public JsonElement? Configuration { get; set; }
}
public sealed record BuildCommand(string Command, string Cwd, int TimeoutMs)
{
    public static BuildCommand Parse(JsonElement value, string root)
    {
        if (value.ValueKind == JsonValueKind.String) return new(value.GetString() is { Length: > 0 } s ? s : throw new ArgumentException("Empty build command"), root, 600000);
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => p.Name is not ("command" or "cwd" or "timeoutMs"))) throw new ArgumentException("Invalid build");
        var command = value.GetProperty("command").GetString();
        var timeout = value.TryGetProperty("timeoutMs", out var t) ? t.GetInt32() : 600000;
        if (string.IsNullOrWhiteSpace(command) || timeout < 1) throw new ArgumentException("Invalid build command or timeout");
        return new(command, value.TryGetProperty("cwd", out var c) ? Paths.Within(root, c.GetString()!, true) : root, timeout);
    }
}
public static class Paths
{
    public static string Within(string root, string path, bool allowRoot = false)
    {
        root = Path.GetFullPath(root); var full = Path.GetFullPath(path, root);
        var rel = Path.GetRelativePath(root, full);
        if ((!allowRoot && rel == ".") || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(rel) || rel.Contains('\n') || rel.Contains('\r')) throw new ArgumentException($"Path must stay inside project: {path}");
        var current = root;
        foreach (var part in rel.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null || (File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException($"Symlinks are not allowed in managed paths: {path}");
        }
        return full;
    }
}
