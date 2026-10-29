using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace ProductCaptures;

public sealed record SourceImage(int Width, int Height, string DataUri);

public static class Composition
{
    public static string ToolRoot { get; } = LocateRoot();

    private static string LocateRoot([CallerFilePath] string sourceFile = "") =>
        Directory.Exists(Path.Combine(AppContext.BaseDirectory, "assets"))
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(sourceFile)!;

    private static string FormatNumber(double value) =>
        value.ToString("G", CultureInfo.InvariantCulture);

    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&#39;");

    private static string Color(string? value, string fallback) =>
        value is not null
            && Regex.IsMatch(value,
                "^#(?:[0-9a-f]{3}|[0-9a-f]{4}|[0-9a-f]{6}|[0-9a-f]{8})$",
                RegexOptions.IgnoreCase)
            ? value
            : fallback;

    private static string Translucent(string value, double opacity)
    {
        string raw = value[1..];
        string expanded = raw.Length <= 4
            ? string.Concat(raw.Select(character => new string(character, 2)))
            : raw;
        double alpha = expanded.Length == 8
            ? Convert.ToInt32(expanded[6..], 16) / 255d
            : 1;
        return $"#{expanded[..6]}{(int)Math.Floor(alpha * opacity * 255 + .5):x2}";
    }

    private sealed record Rect(double Left, double Top, double Width, double Height);

    private sealed record Geometry(
        Rect Frame,
        string IdentityStyle,
        string HeadingStyle,
        string PlatformStyle,
        double ImageWidth,
        double Offset,
        string Fit);

    private static string Rectangle(Rect rectangle) =>
        $"position:absolute;left:{FormatNumber(rectangle.Left)}px;"
        + $"top:{FormatNumber(rectangle.Top)}px;"
        + $"width:{FormatNumber(rectangle.Width)}px;"
        + $"height:{FormatNumber(rectangle.Height)}px;";

    private static string Heading(
        double left,
        double top,
        double width,
        double size,
        double unit,
        string align = "left") =>
        $"position:absolute;left:{FormatNumber(left)}px;top:{FormatNumber(top)}px;"
        + $"width:{FormatNumber(width)}px;"
        + $"font-size:{FormatNumber(size * unit)}px;text-align:{align};";

    private static Rect Contain(double ratio, Rect area)
    {
        double width = Math.Min(area.Width, area.Height * ratio);
        double height = width / ratio;
        return new Rect(
            Left: area.Left + (area.Width - width) / 2,
            Top: area.Top + (area.Height - height) / 2,
            Width: width,
            Height: height);
    }

    private static Geometry Arrange(
        string layout,
        double width,
        double height,
        SourceImage? source,
        Appearance appearance,
        double unit,
        bool hasPlatform)
    {
        double ratio = source is { Width: > 0, Height: > 0 }
            ? (double)source.Width / source.Height
            : 1.44;
        bool identityOnly = source is null;
        return layout switch
        {
            "column" => ArrangeColumn(width, height, ratio, appearance, unit, identityOnly),
            "centered" => ArrangeCentered(width, height, ratio, unit, hasPlatform),
            "horizontal" => ArrangeHorizontal(width, height, ratio, appearance, unit, identityOnly),
            "top" => ArrangeTop(width, height, ratio, appearance, unit, hasPlatform),
            _ => throw new ArgumentException("Unknown appearance.layout")
        };
    }

    private static Geometry ArrangeColumn(
        double width,
        double height,
        double ratio,
        Appearance appearance,
        double unit,
        bool identityOnly)
    {
        var area = new Rect(width * .35, height * .11, width * .61, height * .78);
        bool containImage = appearance.ImageFit == "contain";
        double imageWidth = area.Width * appearance.ImageZoom;
        Rect frame = containImage
            ? Contain(ratio, area)
            : new Rect(area.Left, 0, area.Width, Math.Min(area.Height, imageWidth / ratio));
        if (!containImage)
        {
            frame = frame with
            {
                Top = (height - frame.Height) / 2
            };
        }

        var identity = new Rect(
            Left: identityOnly ? 0 : width * .035,
            Top: frame.Top,
            Width: identityOnly ? width : width * .27,
            Height: frame.Height);
        string platformStyle = "position:absolute;left:0;right:auto;top:auto;bottom:0;"
            + "width:100%;max-width:none;margin:0;justify-content:center;";
        return new Geometry(
            Frame: frame,
            IdentityStyle: Rectangle(identity) + "display:block;",
            HeadingStyle: Heading(0, 0, identity.Width, 88, unit, "center"),
            PlatformStyle: platformStyle,
            ImageWidth: containImage ? frame.Width : imageWidth,
            Offset: containImage ? 0 : appearance.ImageOffsetX * unit,
            Fit: containImage ? "contain" : "crop");
    }

    private static Geometry ArrangeCentered(
        double width,
        double height,
        double ratio,
        double unit,
        bool hasPlatform)
    {
        double top = height * (hasPlatform ? .29 : .20);
        var area = new Rect(width * .055, top, width * .89, height * .92 - top);
        Rect frame = Contain(ratio, area);
        string platformStyle = $"position:absolute;left:{FormatNumber(width * .055)}px;right:auto;"
            + $"top:{FormatNumber(height * .16)}px;bottom:auto;width:{FormatNumber(width * .89)}px;"
            + "max-width:none;margin:0;justify-content:center;";
        return new Geometry(
            Frame: frame,
            IdentityStyle: "display:contents;",
            HeadingStyle: Heading(width * .055, height * .055, width * .89, 72, unit, "center"),
            PlatformStyle: platformStyle,
            ImageWidth: frame.Width,
            Offset: 0,
            Fit: "contain");
    }

    private static Geometry ArrangeHorizontal(
        double width,
        double height,
        double ratio,
        Appearance appearance,
        double unit,
        bool identityOnly)
    {
        double imageWidth = width * .89 * appearance.ImageZoom;
        var area = new Rect(
            width * .055, height * .24, width * .89,
            height * (appearance.FrameHeight ?? .65));
        bool containImage = appearance.ImageFit == "contain";
        Rect frame = containImage ? Contain(ratio, area) : area with
        {
            Height = Math.Min(area.Height, imageWidth / ratio)
        };
        double headingWidth = width * (identityOnly ? .89 : .55);
        string headingAlignment = identityOnly ? "center" : "left";
        double platformLeft = width * (identityOnly ? .055 : .625);
        double platformTop = height * (identityOnly ? .16 : .09);
        double platformWidth = width * (identityOnly ? .89 : .32);
        string platformAlignment = identityOnly ? "center" : "flex-end";
        string platformStyle = $"position:absolute;left:{FormatNumber(platformLeft)}px;right:auto;"
            + $"top:{FormatNumber(platformTop)}px;bottom:auto;width:{FormatNumber(platformWidth)}px;"
            + $"max-width:none;margin:0;justify-content:{platformAlignment};";
        return new Geometry(
            Frame: frame,
            IdentityStyle: "display:contents;",
            HeadingStyle: Heading(width * .055, height * .065, headingWidth, 62, unit, headingAlignment),
            PlatformStyle: platformStyle,
            ImageWidth: containImage ? frame.Width : imageWidth,
            Offset: containImage ? 0 : appearance.ImageOffsetX * unit,
            Fit: containImage ? "contain" : "crop");
    }

    private static Geometry ArrangeTop(
        double width,
        double height,
        double ratio,
        Appearance appearance,
        double unit,
        bool hasPlatform)
    {
        double top = height * .18;
        double imageWidth = Math.Min(
            width * .97, ((1 + appearance.EdgeBleed) * height - top) * ratio);
        var frame = new Rect(width * .055, top, imageWidth, imageWidth / ratio);
        double headingTop = height * (hasPlatform ? .035 : .05);
        string platformStyle = $"position:absolute;left:{FormatNumber(width * .055)}px;right:auto;"
            + $"top:{FormatNumber(height * .125)}px;bottom:auto;width:{FormatNumber(width * .89)}px;"
            + "max-width:none;margin:0;justify-content:center;";
        return new Geometry(
            Frame: frame,
            IdentityStyle: "display:contents;",
            HeadingStyle: Heading(width * .055, headingTop, width * .89, 72, unit, "center"),
            PlatformStyle: platformStyle,
            ImageWidth: imageWidth,
            Offset: 0,
            Fit: "bleed");
    }

    private static async Task<string> ReadAssetBase64Async(string file)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(ToolRoot, "assets", file));
        return Convert.ToBase64String(bytes);
    }

    private static async Task<string> PlatformContentAsync(
        string platform,
        bool obsidian,
        bool darkBackdrop)
    {
        string content = $"<strong>{Escape(platform)}</strong>";
        if (obsidian)
        {
            string brand = await ReadAssetBase64Async(
                $"obsidian-lockup-{(darkBackdrop ? "dark" : "light")}.svg");
            string companion = Regex.IsMatch(platform, "Quartz", RegexOptions.IgnoreCase)
                ? "<strong class=\"companion\">&amp; Quartz</strong>"
                : "";
            content = $"""<img class="obsidian-brand" src="data:image/svg+xml;base64,{brand}" """
                + """width="143" height="25" alt="Obsidian" />"""
                + companion;
        }

        return content;
    }

    private static string PlatformLockup(
        string platform,
        string content,
        bool obsidian,
        bool quartz,
        Geometry geometry)
    {
        if (platform.Length == 0)
        {
            return "";
        }

        string obsidianAttribute = obsidian ? " data-obsidian=\"true\"" : "";
        string quartzAttribute = quartz ? " data-quartz=\"true\"" : "";
        return $"""<div class="platform" aria-label="for {Escape(platform)}"{obsidianAttribute}{quartzAttribute} """
            + $"""style="{geometry.PlatformStyle}"><span class="for-label">FOR</span>"""
            + content
            + "</div>";
    }

    private static string SourceFrame(
        SourceImage? source,
        Geometry geometry,
        string name,
        bool studio)
    {
        if (source is null)
        {
            return "";
        }

        string softness = studio
            ? ""
            : """<div class="softness" aria-hidden="true"></div>""";
        return $"""<div class="window shot-0" data-fit="{geometry.Fit}" style="{Rectangle(geometry.Frame)}">"""
            + $"""<img class="product-capture" src="{Escape(source.DataUri)}" alt="{name}" """
            + $"""style="width:{FormatNumber(geometry.ImageWidth)}px;height:auto;margin-left:{FormatNumber(geometry.Offset)}px;" />"""
            + softness
            + "</div>";
    }

    private static async Task<string> StudioAtmosphereAsync(string tint, string shade)
    {
        string plate = await ReadAssetBase64Async("alpine-plate.png");
        return $"""<div class="atmosphere" aria-hidden="true" style="background-image:linear-gradient(100deg,"""
            + $"""color-mix(in srgb,{tint} 63%,transparent),color-mix(in srgb,{shade} 67%,transparent)),"""
            + $"""url(data:image/png;base64,{plate})"></div><div class="light" aria-hidden="true"></div>""";
    }

    private static string SiteAtmosphere(string tint, string paper, bool dark)
    {
        string light = dark ? "#aab8cc" : "#ffffff";
        string shadow = dark ? "#050810" : "#60758b";
        return """<div class="site-surface" aria-hidden="true" data-material="optical-folds" """
            + $"""style="--site-tint:{tint};--site-paper:{paper};--site-light:{light};--site-shadow:{shadow}"></div>""";
    }

    public static async Task<string> RenderAsync(Config config, string theme, SourceImage? source)
    {
        if (theme is not ("light" or "dark"))
        {
            throw new ArgumentException("Unknown theme");
        }

        Appearance appearance = config.Appearance;
        int width = appearance.Width;
        int height = appearance.Height;
        double unit = Math.Min(width / 1600d, height / 800d);
        string Px(double value) =>
            FormatNumber(Math.Floor(value * unit * 100 + .5) / 100) + "px";

        string platform = Regex.Replace(
            config.Subtitle.Trim(), "^for\\s+", "", RegexOptions.IgnoreCase);
        bool studio = config.Capture?.Type == "obsidian"
            || config.Quartz is not null
            || Regex.IsMatch(platform,
                "^(Obsidian(?:\\s*&\\s*Quartz)?|Quartz)$", RegexOptions.IgnoreCase);
        string layout = appearance.Layout ?? (studio ? "column" : "top");
        string artDirection = studio ? "studio" : "porcelain";
        bool dark = theme == "dark";
        bool darkBackdrop = studio || dark;
        bool quartz = platform.Equals("Quartz", StringComparison.OrdinalIgnoreCase)
            || config.Quartz is not null;
        string studioPaper = quartz ? "#18283e" : "#251a35";
        string darkPaper = studio ? studioPaper : "#10141a";
        string paper = darkBackdrop
            ? Color(appearance.DarkBackground, darkPaper)
            : Color(appearance.Background, "#fcfcfb");
        string ink = darkBackdrop ? "#f2f4f6" : Color(appearance.Ink, "#252527");
        string studioTint = Color(appearance.Tint, quartz ? "#466e9b" : "#684487");
        string studioShade = Color(appearance.Shade, quartz ? "#243d66" : "#442b62");
        string studioGlow = Color(appearance.Glow, quartz ? "#abc9ec" : "#c5a3f1");
        string studioShadow = Color(appearance.DarkBackground, quartz ? "#18283e" : "#251a35");
        string siteTint = Color(appearance.Tint, "#dfe8e4");
        double fadeStart = appearance.FadeStart ?? .67;
        double fadeEnd = appearance.FadeEnd ?? 1;
        double blurStart = appearance.BlurStart ?? .62;
        string imageZoom = FormatNumber(appearance.ImageZoom);
        double imageOffsetX = appearance.ImageOffsetX;
        string font = await ReadAssetBase64Async("Geist.ttf");
        string name = Escape(config.Name);
        Geometry geometry = Arrange(
            layout, width, height, source, appearance, unit, platform.Length > 0);
        bool obsidian = Regex.IsMatch(
            platform, "^Obsidian(?:\\s*&\\s*Quartz)?$", RegexOptions.IgnoreCase);
        string platformContent = await PlatformContentAsync(platform, obsidian, darkBackdrop);
        string lockup = PlatformLockup(platform, platformContent, obsidian, quartz, geometry);
        string frames = SourceFrame(source, geometry, name, studio);
        string atmosphere = studio
            ? await StudioAtmosphereAsync(studioTint, studioShade)
            : SiteAtmosphere(siteTint, paper, dark);
        string? backgroundPosition = appearance.BackdropPosition is "left" or "center" or "right"
            ? appearance.BackdropPosition
            : "center";
        return $$"""
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width={{width}},initial-scale=1"><title>{{name}}</title><style>
  @font-face{font-family:CaptureGeist;src:url(data:font/ttf;base64,{{font}}) format('truetype');font-display:block}
  *{box-sizing:border-box}html,body{width:{{width}}px;height:{{height}}px;margin:0;overflow:hidden}body{font-family:CaptureGeist,sans-serif;background:{{paper}};color:{{ink}};-webkit-font-smoothing:antialiased}
  .canvas{position:relative;width:100%;height:100%;overflow:hidden;isolation:isolate;background:{{paper}};--zoom:{{imageZoom}}}
  .identity{display:contents}
  .atmosphere{position:absolute;inset:0;background-size:cover;background-position:{{backgroundPosition}} 70%;filter:saturate(.95) brightness(.95)}
  .light{position:absolute;inset:0;background:linear-gradient(145deg,{{(dark ? Translucent(studioGlow, 38d / 255) : "#ffffff38")}} 0%,transparent 47%,{{(dark ? Translucent(studioShadow, .2) : "#f7f8f32b")}} 94%)}
  .site-surface{position:absolute;inset:0;background:conic-gradient(from 0deg at -18% 110%,transparent 0deg 28deg,color-mix(in srgb,var(--site-tint) {{(dark ? "18" : "38")}}%,transparent) 42deg,color-mix(in srgb,var(--site-light) {{(dark ? "12" : "90")}}%,transparent) 47deg,transparent 63deg 360deg),conic-gradient(from 180deg at 118% -12%,transparent 0deg 30deg,color-mix(in srgb,var(--site-shadow) {{(dark ? "30" : "16")}}%,transparent) 46deg,color-mix(in srgb,var(--site-light) {{(dark ? "8" : "65")}}%,transparent) 51deg,transparent 69deg 360deg),linear-gradient(135deg,var(--site-paper) 12%,color-mix(in srgb,var(--site-paper) {{(dark ? "92" : "70")}}%,var(--site-tint)) 100%)}
  h1{position:absolute;left:6.5%;top:42%;width:28%;margin:0;z-index:3;font-size:{{Px(88)}};line-height:1.03;font-weight:600;letter-spacing:-.035em;overflow-wrap:anywhere;text-wrap:balance}
  .platform{position:absolute;left:6.8%;bottom:11%;max-width:28%;display:flex;align-items:baseline;gap:{{Px(16)}};z-index:3;color:{{ink}}}
  .platform span{font-size:{{Px(17)}};font-weight:450;letter-spacing:.04em;opacity:.65}.platform strong{font-size:{{Px(29)}};line-height:1.2;font-weight:600;letter-spacing:-.025em;overflow-wrap:anywhere}
  .platform .obsidian-brand{display:block;width:{{Px(200.2)}};height:{{Px(35)}};aspect-ratio:143/25;max-width:none;filter:none;transform:none;perspective:none;flex-shrink:0}
  .window{position:absolute;left:35%;top:11%;width:94%;height:100%;overflow:hidden;border-radius:{{Px(17)}};background:{{(dark ? "#1e2023" : "#fff")}};box-shadow:0 {{Px(14)}} {{Px(46)}} {{Px(-16)}} #1419233d;transform:none;perspective:none}
  .window img{display:block;width:calc(100% * var(--zoom));height:auto;max-width:none;margin-left:{{Px(imageOffsetX)}};transform:none;perspective:none}
  .porcelain .window{mask-image:linear-gradient(#000 0%,#000 52%,transparent 91%)}
  .softness{position:absolute;inset:49% 0 0;backdrop-filter:blur({{Px(5)}});mask-image:linear-gradient(transparent,#000 90%);pointer-events:none}
  .studio h1{left:68.5%;top:40%;width:28%;font-size:{{Px(82)}};font-weight:450}.studio .platform{left:68.5%;top:57%;bottom:auto;max-width:28%;color:{{(dark ? "#c7d3d9" : ink)}}}
  .studio .window{left:-2%;top:19%;width:67%;height:70.5%;border-radius:{{Px(13)}};box-shadow:0 {{Px(36)}} {{Px(100)}} {{Px(-20)}} #000b;outline:1px solid {{(dark ? "#c5d9df38" : "#ffffff70")}}}
  .studio .window img{width:calc(141% * var(--zoom))}
  .managed .identity{z-index:3}.managed .platform{align-items:center}.managed.porcelain .window{mask-image:linear-gradient(#000 0%,#000 {{FormatNumber(fadeStart * 100)}}%,transparent {{FormatNumber(fadeEnd * 100)}}%)}.managed.porcelain .softness{inset:{{FormatNumber(blurStart * 100)}}% 0 0;display:block}
  .managed .platform[data-obsidian] .for-label{position:relative;top:{{Px(1.25)}}}
  .managed .platform[data-quartz] .for-label{position:relative;top:{{Px(.55)}}}  </style></head><body><main class="canvas {{artDirection}} {{layout}} showcase managed{{(height / (double)width < .55 ? " social" : "")}}" data-art-direction="{{artDirection}}" data-theme="{{theme}}">{{atmosphere}}<div class="identity" style="{{geometry.IdentityStyle}}"><h1 style="{{geometry.HeadingStyle}}">{{name}}</h1>{{lockup}}</div><section class="scenes" aria-label="Product screenshots">{{frames}}</section></main></body></html>
""";
    }
}
