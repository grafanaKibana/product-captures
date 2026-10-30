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
    public Animation? Animation
    {
        get; set;
    }
    public JsonElement? Build
    {
        get; set;
    }
    public Server? Server
    {
        get; set;
    }
    public Quartz? Quartz
    {
        get; set;
    }

    [JsonIgnore]
    public string Root { get; set; } = "";

    [JsonIgnore]
    public string ConfigPath { get; set; } = "";

    [JsonIgnore]
    public string ConfigHash { get; set; } = "";

    [JsonIgnore]
    public string OutputDirectory => Paths.Within(Root, Output);

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static Config Load(string path)
    {
        path = Path.GetFullPath(path);
        byte[] bytes = File.ReadAllBytes(path);
        Config config = JsonSerializer.Deserialize<Config>(bytes, Json)
            ?? throw new ArgumentException("Config must be an object");
        config.Root = Path.GetDirectoryName(path)!;
        config.ConfigPath = path;
        config.ConfigHash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement json = document.RootElement;
        RejectDuplicates(json);
        ValidateProperties(json);
        config.ApplyDefaults(json);
        config.Validate();
        return config;
    }

    private static void ValidateProperties(JsonElement json)
    {
        foreach (JsonProperty property in json.EnumerateObject())
        {
            if (property.Name is not (
                "name" or "subtitle" or "output" or "appearance"
                or "capture" or "build" or "server" or "quartz" or "animation"))
            {
                throw new ArgumentException($"Unknown config property: {property.Name}");
            }
        }

        if (json.TryGetProperty("appearance", out JsonElement appearance)
            && appearance.ValueKind == JsonValueKind.Object
            && appearance.TryGetProperty("layout", out JsonElement layout)
            && layout.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("appearance.layout must be a string");
        }
    }

    private void ApplyDefaults(JsonElement json)
    {
        if (!json.TryGetProperty("subtitle", out _))
        {
            Subtitle = DefaultSubtitle();
        }

        if (Appearance is not null)
        {
            Appearance.Layout ??= Quartz is not null || UsesPluginLayout()
                ? "column"
                : "top";
            if (!json.TryGetProperty("appearance", out JsonElement appearance)
                || !appearance.TryGetProperty("height", out _))
            {
                Appearance.Height = Appearance.Layout switch
                {
                    "column" or "horizontal" => 900,
                    "centered" => 1200,
                    _ => 1100
                };
            }
        }

        if (Capture is not null
            && (Quartz is not null || Capture.Type == "obsidian")
            && UsesDefaultViewportHeight(json))
        {
            Capture.Viewport.Height = 1000;
        }
    }

    private string DefaultSubtitle()
    {
        if (Quartz is not null)
        {
            return "for Quartz";
        }

        return Capture?.Type == "obsidian" ? "for Obsidian" : "";
    }

    private bool UsesPluginLayout() =>
        Capture?.Type == "obsidian"
        || Subtitle.Contains("Obsidian", StringComparison.OrdinalIgnoreCase)
        || Subtitle.Contains("Quartz", StringComparison.OrdinalIgnoreCase);

    private static bool UsesDefaultViewportHeight(JsonElement json) =>
        !json.TryGetProperty("capture", out JsonElement capture)
        || !capture.TryGetProperty("viewport", out JsonElement viewport)
        || (viewport.ValueKind == JsonValueKind.Object
            && !viewport.TryGetProperty("height", out _));

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>();
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name))
                {
                    throw new ArgumentException($"Duplicate config key: {property.Name}");
                }

                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicates(item);
            }
        }
    }

    public void Validate()
    {
        if (Subtitle is null || Output is null)
        {
            throw new ArgumentException("subtitle and output cannot be null");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("name is required");
        }

        _ = OutputDirectory;
        if (Appearance is null)
        {
            throw new ArgumentException("appearance must be an object");
        }

        ValidateAppearance();
        ValidateCapture();
        ValidateAnimation();
        if (Quartz is not null)
        {
            QuartzHost.Validate(this);
        }

        if (Build is { } build)
        {
            _ = BuildCommand.Parse(build, Root);
        }

        ValidateServer();
    }

    private void ValidateAppearance()
    {
        Appearance.Layout ??= UsesPluginLayout() ? "column" : "top";
        if (Appearance.Layout is not ("column" or "horizontal" or "centered" or "top"))
        {
            throw new ArgumentException("Unknown appearance.layout");
        }

        if (Appearance.Width is < 1 or > 8192
            || Appearance.Height is < 1 or > 8192)
        {
            throw new ArgumentException("Invalid artwork dimensions");
        }

        string?[] colors =
        [
            Appearance.Background,
            Appearance.DarkBackground,
            Appearance.Tint,
            Appearance.Shade,
            Appearance.Glow,
            Appearance.Ink
        ];
        foreach (string? color in colors)
        {
            if (color is not null && !Regex.IsMatch(color,
                "^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$"))
            {
                throw new ArgumentException("Appearance colors must be hexadecimal CSS colors");
            }
        }

        if (Appearance.Background is null
            || Appearance.ImageFit is not ("crop" or "contain")
            || !double.IsFinite(Appearance.ImageZoom)
            || Appearance.ImageZoom is <= 0 or > 10
            || !double.IsFinite(Appearance.ImageOffsetX))
        {
            throw new ArgumentException("Invalid appearance image settings");
        }

        double?[] fractions =
        [
            Appearance.FrameHeight,
            Appearance.EdgeBleed,
            Appearance.FadeStart,
            Appearance.FadeEnd,
            Appearance.BlurStart
        ];
        foreach (double? fraction in fractions)
        {
            if (fraction is { } value
                && (!double.IsFinite(value) || value is < 0 or > 1))
            {
                throw new ArgumentException("Appearance fractions must be between 0 and 1");
            }
        }

        if ((Appearance.FadeStart ?? .67) > (Appearance.FadeEnd ?? 1))
        {
            throw new ArgumentException("fadeStart must not exceed fadeEnd");
        }

        if (Appearance.BackdropPosition is not (null or "left" or "center" or "right"))
        {
            throw new ArgumentException("backdropPosition must be left, center or right");
        }
    }

    private void ValidateCapture()
    {
        if (Capture is not { } capture)
        {
            return;
        }

        if (!IsCaptureUrl(capture.Url))
        {
            throw new ArgumentException("capture.url must be relative or HTTP");
        }

        if (capture.Type is not ("web" or "obsidian" or "image"))
        {
            throw new ArgumentException("Unknown capture.type");
        }

        if (capture.Viewport is null
            || capture.Viewport.Width is < 1 or > 8192
            || capture.Viewport.Height is < 1 or > 8192)
        {
            throw new ArgumentException("Invalid capture.viewport");
        }

        if (capture.Type == "obsidian"
            && (!Regex.IsMatch(capture.AppVersion ?? "", @"^\d+\.\d+\.\d+$")
                || !Regex.IsMatch(capture.InstallerVersion ?? "", @"^\d+\.\d+\.\d+$")))
        {
            throw new ArgumentException("Obsidian versions must be pinned x.y.z releases");
        }

        if (capture.Type == "image")
        {
            ValidateImages(capture);
        }

        ValidateStorage(capture);
        ValidateActions(capture);
        Paths.Within(Root, capture.Vault);
        Paths.Within(Root, capture.PluginDir, true);
    }

    private void ValidateAnimation()
    {
        if (Animation is not { } animation)
        {
            return;
        }

        if (Capture?.Type is not ("web" or "obsidian"))
        {
            throw new ArgumentException("animation requires a live web or Obsidian capture");
        }

        if (animation.Steps is not { Count: >= 1 and <= 12 })
        {
            throw new ArgumentException("animation.steps requires between 1 and 12 steps");
        }

        foreach (AnimationStep step in animation.Steps)
        {
            if (step is null
                || string.IsNullOrWhiteSpace(step.Text)
                || step.Text.Length > 140
                || step.Type is not ("click" or "drag" or "scroll" or "focus")
                || ((step.Type is "click" or "drag") && string.IsNullOrWhiteSpace(step.Selector))
                || (step.Ready is not null && string.IsNullOrWhiteSpace(step.Ready))
                || !double.IsFinite(step.X)
                || !double.IsFinite(step.Y)
                || !double.IsFinite(step.Zoom)
                || step.Zoom is < 1 or > 2.5
                || step.DurationMs is < 400 or > 15000
                || (step.Type == "drag" && step.X == 0 && step.Y == 0))
            {
                throw new ArgumentException("Invalid animation step");
            }
        }

        if (animation.Steps.Sum(step => step.DurationMs) > 90000)
        {
            throw new ArgumentException("animation steps must total at most 90000 milliseconds");
        }
    }

    private static bool IsCaptureUrl(string? url)
    {
        if (url is null)
        {
            return false;
        }

        if (!Regex.IsMatch(url, "^[A-Za-z][A-Za-z0-9+.-]*:"))
        {
            return true;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https";
    }

    private void ValidateImages(Capture capture)
    {
        if (capture.Images is null)
        {
            throw new ArgumentException("image capture requires images");
        }

        string[] themes = ["light", "dark"];
        foreach (string theme in themes)
        {
            if (!capture.Images.TryGetValue(theme, out string? file))
            {
                throw new ArgumentException($"Missing {theme} image");
            }

            Paths.Within(Root, file);
        }
    }

    private static void ValidateStorage(Capture capture)
    {
        Dictionary<string, JsonElement> storage = capture.Storage
            ?? throw new ArgumentException("storage cannot be null");
        foreach (JsonElement value in storage.Values)
        {
            if (!IsStorageValue(value))
            {
                throw new ArgumentException("storage values must be strings or {light,dark}");
            }
        }
    }

    private static bool IsStorageValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        || (value.ValueKind == JsonValueKind.Object
            && value.EnumerateObject().Count() == 2
            && value.TryGetProperty("light", out JsonElement light)
            && light.ValueKind == JsonValueKind.String
            && value.TryGetProperty("dark", out JsonElement dark)
            && dark.ValueKind == JsonValueKind.String);

    private static void ValidateActions(Capture capture)
    {
        List<CaptureAction> actions = capture.Actions
            ?? throw new ArgumentException("actions cannot be null");
        foreach (CaptureAction action in actions)
        {
            if (action.Type is not ("click" or "fill" or "scroll")
                || (action.Type != "scroll" && string.IsNullOrWhiteSpace(action.Selector))
                || (action.Type == "fill" && action.Value is null))
            {
                throw new ArgumentException("Invalid capture action");
            }
        }
    }

    private void ValidateServer()
    {
        if (Server is not { } server)
        {
            return;
        }

        if ((server.Directory is null) == (server.Command is null))
        {
            throw new ArgumentException("server requires directory or command");
        }

        if (server.Directory is { } directory)
        {
            Paths.Within(Root, directory, true);
        }

        if (server.Port is < 1 or > 65535 || server.TimeoutMs < 1)
        {
            throw new ArgumentException("Invalid server port or timeout");
        }

        if (server.Command is not null
            && (string.IsNullOrWhiteSpace(server.Command)
                || !Uri.TryCreate(server.Url, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("server command requires an HTTP url");
        }

        if (server.Command is not null
            && Uri.TryCreate(server.Url, UriKind.Absolute, out Uri? commandUri)
            && commandUri.Host != "localhost"
            && (!System.Net.IPAddress.TryParse(commandUri.Host, out System.Net.IPAddress? address)
                || !System.Net.IPAddress.IsLoopback(address)))
        {
            throw new ArgumentException("Command server URL must be loopback");
        }
    }
}

public sealed class Appearance
{
    public string? Layout
    {
        get; set;
    }
    public int Width { get; set; } = 1600;
    public int Height { get; set; } = 1100;
    public string Background { get; set; } = "#fcfcfb";
    public string? DarkBackground
    {
        get; set;
    }
    public string? Tint
    {
        get; set;
    }
    public string? Shade
    {
        get; set;
    }
    public string? Glow
    {
        get; set;
    }
    public string? Ink
    {
        get; set;
    }
    public double ImageZoom { get; set; } = 1;
    public double ImageOffsetX
    {
        get; set;
    }
    public string ImageFit { get; set; } = "crop";
    public double? FrameHeight
    {
        get; set;
    }
    public double EdgeBleed { get; set; } = 0.06;
    public double? FadeStart
    {
        get; set;
    }
    public double? FadeEnd
    {
        get; set;
    }
    public double? BlurStart
    {
        get; set;
    }
    public string? BackdropPosition
    {
        get; set;
    }
}

public sealed class Capture
{
    public string Type { get; set; } = "web";
    public string Url { get; set; } = "/";
    public Viewport Viewport { get; set; } = new();
    public string? Ready
    {
        get; set;
    }
    public string? WaitFor
    {
        get; set;
    }
    public string? Css
    {
        get; set;
    }
    public Dictionary<string, JsonElement> Storage { get; set; } = new();
    public List<CaptureAction> Actions { get; set; } = new();
    public string Vault { get; set; } = "docs/capture-fixtures/vault";
    public string Note { get; set; } = "Product showcase.md";
    public string PluginDir { get; set; } = ".";
    public string AppVersion { get; set; } = "1.14.5";
    public string InstallerVersion { get; set; } = "1.14.5";
    public Dictionary<string, string>? Images
    {
        get; set;
    }
}

public sealed class Viewport
{
    public int Width { get; set; } = 1440;
    public int Height { get; set; } = 900;
}

public sealed class Animation
{
    public List<AnimationStep> Steps { get; set; } = [];
}

public sealed class AnimationStep
{
    public string Text { get; set; } = "";
    public string Type { get; set; } = "click";
    public string Selector { get; set; } = "";
    public string? Ready
    {
        get; set;
    }
    public double X
    {
        get; set;
    }
    public double Y
    {
        get; set;
    }
    public double Zoom { get; set; } = 1.35;
    public int DurationMs { get; set; } = 2400;
}

public sealed class CaptureAction
{
    public string Type { get; set; } = "click";
    public string Selector { get; set; } = "";
    public string? Value
    {
        get; set;
    }
    public double X
    {
        get; set;
    }
    public double Y
    {
        get; set;
    }
}

public sealed class Server
{
    public string? Directory
    {
        get; set;
    }
    public int Port { get; set; } = 4180;
    public string? Command
    {
        get; set;
    }
    public string? Url
    {
        get; set;
    }
    public int TimeoutMs { get; set; } = 60000;
}

public sealed class Quartz
{
    public string PluginDir { get; set; } = ".";
    public string Content { get; set; } = "docs/capture-fixtures";
    public string? Title
    {
        get; set;
    }
    public JsonElement? Options
    {
        get; set;
    }
    public string? Revision
    {
        get; set;
    }
    public string? Upstream
    {
        get; set;
    }
    public string[]? Inputs
    {
        get; set;
    }
    public JsonElement? Build
    {
        get; set;
    }
    public JsonElement? Configuration
    {
        get; set;
    }
}

public sealed record BuildCommand(string Command, string Cwd, int TimeoutMs)
{
    public static BuildCommand Parse(JsonElement value, string root)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            if (text is not { Length: > 0 })
            {
                throw new ArgumentException("Empty build command");
            }

            return new BuildCommand(text, root, 600000);
        }

        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Any(property =>
                property.Name is not ("command" or "cwd" or "timeoutMs")))
        {
            throw new ArgumentException("Invalid build");
        }

        string? command = value.GetProperty("command").GetString();
        int timeout = value.TryGetProperty("timeoutMs", out JsonElement configuredTimeout)
            ? configuredTimeout.GetInt32()
            : 600000;
        if (string.IsNullOrWhiteSpace(command) || timeout < 1)
        {
            throw new ArgumentException("Invalid build command or timeout");
        }

        string directory = value.TryGetProperty("cwd", out JsonElement configuredDirectory)
            ? Paths.Within(root, configuredDirectory.GetString()!, true)
            : root;
        return new BuildCommand(command, directory, timeout);
    }
}

public static class Paths
{
    public static string Within(string root, string path, bool allowRoot = false)
    {
        root = Path.GetFullPath(root);
        string full = Path.GetFullPath(path, root);
        string relative = Path.GetRelativePath(root, full);
        if ((!allowRoot && relative == ".")
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar)
            || Path.IsPathRooted(relative)
            || relative.Contains('\n')
            || relative.Contains('\r'))
        {
            throw new ArgumentException($"Path must stay inside project: {path}");
        }

        string current = root;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (new FileInfo(current).LinkTarget is not null
                || new DirectoryInfo(current).LinkTarget is not null
                || ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0))
            {
                throw new ArgumentException($"Symlinks are not allowed in managed paths: {path}");
            }
        }

        return full;
    }
}
