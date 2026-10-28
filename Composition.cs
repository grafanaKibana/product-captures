using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace ProductCaptures;

public sealed record SourceImage(int Width, int Height, string DataUri);
public static class Composition
{
    public static string ToolRoot { get; } = LocateRoot();

    private static string LocateRoot([CallerFilePath] string sourceFile = "") => Directory.Exists(Path.Combine(AppContext.BaseDirectory, "assets")) ? AppContext.BaseDirectory : Path.GetDirectoryName(sourceFile)!;
    private static string FormatNumber(double value) => value.ToString("G", CultureInfo.InvariantCulture);
    private static string Escape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
    private static string Color(string? value, string fallback) => value is not null && Regex.IsMatch(value, "^#(?:[0-9a-f]{3}|[0-9a-f]{4}|[0-9a-f]{6}|[0-9a-f]{8})$", RegexOptions.IgnoreCase) ? value : fallback;
    private static string Translucent(string value, double opacity)
    {
        var raw = value[1..];
        var expanded = raw.Length <= 4 ? string.Concat(raw.Select(c => new string(c, 2))) : raw;
        var alpha = expanded.Length == 8 ? Convert.ToInt32(expanded[6..], 16) / 255d : 1;
        return $"#{expanded[..6]}{(int)Math.Floor(alpha * opacity * 255 + .5):x2}";
    }

    private sealed record Rect(double Left, double Top, double Width, double Height);
    private static string Rectangle(Rect rectangle) => $"position:absolute;left:{FormatNumber(rectangle.Left)}px;top:{FormatNumber(rectangle.Top)}px;width:{FormatNumber(rectangle.Width)}px;height:{FormatNumber(rectangle.Height)}px;";
    private static Rect Contain(double ratio, Rect area)
    {
        var width = Math.Min(area.Width, area.Height * ratio);
        var height = width / ratio;
        return new(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
    }

    private sealed record Geometry(Rect Frame, string IdentityStyle, string HeadingStyle, string PlatformStyle, double ImageWidth, double Offset, string Fit);
    private static Geometry Arrange(string layout, double width, double height, SourceImage? source, Appearance appearance, double unit, bool hasPlatform)
    {
        var ratio = source is { Width: > 0, Height: > 0 } ? (double)source.Width / source.Height : 1.44;
        var identityOnly = source is null;
        string Heading(double left, double top, double headingWidth, double size, string align = "left") => $"position:absolute;left:{FormatNumber(left)}px;top:{FormatNumber(top)}px;width:{FormatNumber(headingWidth)}px;font-size:{FormatNumber(size * unit)}px;text-align:{align};";
        if (layout == "column")
        {
            var area = new Rect(width * .35, height * .11, width * .61, height * .78);
            var desktop = appearance.ImageFit == "contain";
            var imageWidth = area.Width * appearance.ImageZoom;
            var frame = desktop ? Contain(ratio, area) : new Rect(area.Left, 0, area.Width, Math.Min(area.Height, imageWidth / ratio));
            if (!desktop)
            {
                frame = frame with
                {
                    Top = (height - frame.Height) / 2
                };
            }

            var identity = new Rect(identityOnly ? 0 : width * .035, frame.Top, identityOnly ? width : width * .27, frame.Height);
            return new(frame, Rectangle(identity) + "display:block;", Heading(0, 0, identity.Width, 88, "center"), "position:absolute;left:0;right:auto;top:auto;bottom:0;width:100%;max-width:none;margin:0;justify-content:center;", desktop ? frame.Width : imageWidth, desktop ? 0 : appearance.ImageOffsetX * unit, desktop ? "contain" : "crop");
        }

        if (layout == "centered")
        {
            var top = height * (hasPlatform ? .29 : .20);
            var frame = Contain(ratio, new(width * .055, top, width * .89, height * .92 - top));
            return new(frame, "display:contents;", Heading(width * .055, height * .055, width * .89, 72, "center"), $"position:absolute;left:{FormatNumber(width * .055)}px;right:auto;top:{FormatNumber(height * .16)}px;bottom:auto;width:{FormatNumber(width * .89)}px;max-width:none;margin:0;justify-content:center;", frame.Width, 0, "contain");
        }

        if (layout == "horizontal")
        {
            var imageWidth = width * .89 * appearance.ImageZoom;
            var area = new Rect(width * .055, height * .24, width * .89, height * (appearance.FrameHeight ?? .65));
            var frame = appearance.ImageFit == "contain" ? Contain(ratio, area) : area with
            {
                Height = Math.Min(area.Height, imageWidth / ratio)
            };
            return new(frame, "display:contents;", Heading(width * .055, height * .065, width * (identityOnly ? .89 : .55), 62, identityOnly ? "center" : "left"), $"position:absolute;left:{FormatNumber(width * (identityOnly ? .055 : .625))}px;right:auto;top:{FormatNumber(height * (identityOnly ? .16 : .09))}px;bottom:auto;width:{FormatNumber(width * (identityOnly ? .89 : .32))}px;max-width:none;margin:0;justify-content:{(identityOnly ? "center" : "flex-end")};", appearance.ImageFit == "contain" ? frame.Width : imageWidth, appearance.ImageFit == "contain" ? 0 : appearance.ImageOffsetX * unit, appearance.ImageFit == "contain" ? "contain" : "crop");
        }

        if (layout == "top")
        {
            var top = height * .18;
            var imageWidth = Math.Min(width * .97, ((1 + appearance.EdgeBleed) * height - top) * ratio);
            return new(new(width * .055, top, imageWidth, imageWidth / ratio), "display:contents;", Heading(width * .055, height * (hasPlatform ? .035 : .05), width * .89, 72, "center"), $"position:absolute;left:{FormatNumber(width * .055)}px;right:auto;top:{FormatNumber(height * .125)}px;bottom:auto;width:{FormatNumber(width * .89)}px;max-width:none;margin:0;justify-content:center;", imageWidth, 0, "bleed");
        }

        throw new ArgumentException("Unknown appearance.layout");
    }

    private static async Task<string> ReadAssetBase64Async(string file) => Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(ToolRoot, "assets", file)));
    public static async Task<string> RenderAsync(Config config, string theme, SourceImage? source)
    {
        if (theme is not ("light" or "dark"))
        {
            throw new ArgumentException("Unknown theme");
        }

        var appearance = config.Appearance;
        var width = appearance.Width;
        var height = appearance.Height;
        var unit = Math.Min(width / 1600d, height / 800d);
        string Px(double value) => FormatNumber(Math.Floor(value * unit * 100 + .5) / 100) + "px";
        var platform = Regex.Replace(config.Subtitle.Trim(), "^for\\s+", "", RegexOptions.IgnoreCase);
        var studio = config.Capture?.Type == "obsidian" || config.Quartz is not null || Regex.IsMatch(platform, "^(Obsidian(?:\\s*&\\s*Quartz)?|Quartz)$", RegexOptions.IgnoreCase);
        var layout = appearance.Layout ?? (studio ? "column" : "top");
        var artDirection = studio ? "studio" : "porcelain";
        var dark = theme == "dark";
        var darkBackdrop = studio || dark;
        var quartz = platform.Equals("Quartz", StringComparison.OrdinalIgnoreCase) || config.Quartz is not null;
        var paper = darkBackdrop ? Color(appearance.DarkBackground, studio ? (quartz ? "#18283e" : "#251a35") : "#10141a") : Color(appearance.Background, "#fcfcfb");
        var ink = darkBackdrop ? "#f2f4f6" : Color(appearance.Ink, "#252527");
        var studioTint = Color(appearance.Tint, quartz ? "#466e9b" : "#684487");
        var studioShade = Color(appearance.Shade, quartz ? "#243d66" : "#442b62");
        var studioGlow = Color(appearance.Glow, quartz ? "#abc9ec" : "#c5a3f1");
        var studioShadow = Color(appearance.DarkBackground, quartz ? "#18283e" : "#251a35");
        var siteTint = Color(appearance.Tint, "#dfe8e4");
        var fadeStart = appearance.FadeStart ?? .67;
        var fadeEnd = appearance.FadeEnd ?? 1;
        var blurStart = appearance.BlurStart ?? .62;
        var imageZoom = FormatNumber(appearance.ImageZoom);
        var imageOffsetX = appearance.ImageOffsetX;
        var font = await ReadAssetBase64Async("Geist.ttf");
        var name = Escape(config.Name);
        var geometry = Arrange(layout, width, height, source, appearance, unit, platform.Length > 0);
        var obsidian = Regex.IsMatch(platform, "^Obsidian(?:\\s*&\\s*Quartz)?$", RegexOptions.IgnoreCase);
        var platformContent = $"<strong>{Escape(platform)}</strong>";
        if (obsidian)
        {
            platformContent = $"<img class=\"obsidian-brand\" src=\"data:image/svg+xml;base64,{await ReadAssetBase64Async($"obsidian-lockup-{(darkBackdrop ? "dark" : "light")}.svg")}\" width=\"143\" height=\"25\" alt=\"Obsidian\" />" + (Regex.IsMatch(platform, "Quartz", RegexOptions.IgnoreCase) ? "<strong class=\"companion\">&amp; Quartz</strong>" : "");
        }

        var lockup = platform.Length == 0 ? "" : $"<div class=\"platform\" aria-label=\"for {Escape(platform)}\"{(obsidian ? " data-obsidian=\"true\"" : "")}{(quartz ? " data-quartz=\"true\"" : "")} style=\"{geometry.PlatformStyle}\"><span class=\"for-label\">FOR</span>{platformContent}</div>";
        var frames = source is null ? "" : $"<div class=\"window shot-0\" data-fit=\"{geometry.Fit}\" style=\"{Rectangle(geometry.Frame)}\"><img class=\"product-capture\" src=\"{Escape(source.DataUri)}\" alt=\"{name}\" style=\"width:{FormatNumber(geometry.ImageWidth)}px;height:auto;margin-left:{FormatNumber(geometry.Offset)}px;\" />{(studio ? "" : "<div class=\"softness\" aria-hidden=\"true\"></div>")}</div>";
        var atmosphere = studio ? $"<div class=\"atmosphere\" aria-hidden=\"true\" style=\"background-image:linear-gradient(100deg,color-mix(in srgb,{studioTint} 63%,transparent),color-mix(in srgb,{studioShade} 67%,transparent)),url(data:image/png;base64,{await ReadAssetBase64Async("alpine-plate.png")})\"></div><div class=\"light\" aria-hidden=\"true\"></div>" : $"<div class=\"site-surface\" aria-hidden=\"true\" data-material=\"optical-folds\" style=\"--site-tint:{siteTint};--site-paper:{paper};--site-light:{(dark ? "#aab8cc" : "#ffffff")};--site-shadow:{(dark ? "#050810" : "#60758b")}\"></div>";
        var backgroundPosition = appearance.BackdropPosition is "left" or "center" or "right" ? appearance.BackdropPosition : "center";
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
