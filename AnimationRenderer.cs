using System.Buffers.Binary;
using System.ComponentModel;
using Microsoft.Playwright;

namespace ProductCaptures;

/// <summary>Records real product interactions in the shared photographic presentation.</summary>
public static class AnimationRenderer
{
    private const int FramesPerSecond = 20;
    private const int CanvasWidth = 1280;
    private const int CanvasHeight = 900;
    private const int CameraDurationMs = 650;
    private const int CursorDurationMs = 300;
    private const int FeedbackDurationMs = 400;
    private const int DragDurationMs = 700;

    /// <summary>Captures the configured steps and writes a looping, palette-optimized GIF.</summary>
    public static async Task CaptureAsync(
        IBrowser browser,
        IPage productPage,
        Config config,
        string theme,
        string destination,
        CancellationToken ct = default)
    {
        Animation animation = config.Animation
            ?? throw new ArgumentException("Animation configuration is required.");
        string temporary = Path.Combine(Path.GetTempPath(), "product-animation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        IPage? compositionPage = null;
        try
        {
            await VerifyEncoderAsync(ct);
            SourceImage initial = await CaptureProductAsync(productPage, ct);
            compositionPage = await CreateCompositionAsync(browser, config, theme, initial, ct);
            var writer = new FrameWriter(compositionPage, temporary, initial);
            var state = new FrameState(new Camera(1, 0, 0), new Point(0, 0), false,
                animation.Steps[0].Text, 1, animation.Steps.Count);
            await writer.HoldAsync(state, 650, ct);

            for (var index = 0; index < animation.Steps.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                AnimationStep step = animation.Steps[index];
                try
                {
                    state = await CaptureStepAsync(productPage, writer, state, step, index + 1, ct);
                }
                catch (Exception error) when (error is PlaywrightException or TimeoutException or InvalidOperationException)
                {
                    throw new InvalidOperationException(
                        $"Animation step {index + 1} ({step.Text}) failed: {error.Message}", error);
                }
            }

            state = await MoveCameraAsync(writer, state with
            {
                ShowCursor = false
            }, new Camera(1, 0, 0), CameraDurationMs, ct);
            await writer.HoldAsync(state, 500, ct);
            await writer.SetImageAsync(initial, blendFrames: 10, ct);
            state = state with
            {
                Text = animation.Steps[0].Text,
                Step = 1
            };
            await writer.HoldAsync(state, 500, ct);
            await EncodeAsync(temporary, destination, ct);
        }
        finally
        {
            try
            {
                if (compositionPage is not null && !compositionPage.IsClosed)
                {
                    await compositionPage.CloseAsync();
                }
            }
            catch (PlaywrightException) when (ct.IsCancellationRequested)
            {
            }
            finally
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    private static async Task<FrameState> CaptureStepAsync(
        IPage page,
        FrameWriter writer,
        FrameState state,
        AnimationStep step,
        int index,
        CancellationToken ct)
    {
        ILocator? target = string.IsNullOrWhiteSpace(step.Selector)
            ? null
            : page.Locator(step.Selector).Filter(new LocatorFilterOptions { Visible = true }).First;
        Point point = await ResolvePointAsync(target, writer.Source, ct);
        StepTiming timing = TimingFor(step);
        state = state with
        {
            Text = step.Text,
            Step = index,
            ShowCursor = false,
            Pulse = -1
        };
        state = await MoveCameraAsync(writer, state, writer.Focus(point, step.Zoom), timing.Camera, ct);
        if (step.Type != "focus")
        {
            state = await MoveCursorAsync(writer, state, point, timing.Cursor, ct);
            switch (step.Type)
            {
                case "click":
                    await target!.ClickAsync().WaitAsync(ct);
                    break;
                case "drag":
                    state = await DragAsync(page, writer, state, point, step, timing.Drag, ct);
                    break;
                case "scroll":
                    await ScrollAsync(page, target, step, ct);
                    break;
            }

            if (!string.IsNullOrWhiteSpace(step.Ready))
            {
                await page.Locator(step.Ready)
                    .Filter(new LocatorFilterOptions { Visible = true })
                    .First.WaitForAsync().WaitAsync(ct);
            }

            await writer.SetImageAsync(await CaptureProductAsync(page, ct), blendFrames: 3, ct);
            int feedbackFrames = FrameCount(timing.Feedback);
            for (var frame = 0; frame < feedbackFrames; frame++)
            {
                await writer.SaveAsync(state with
                {
                    Pulse = step.Type == "scroll" ? -1 : frame / (double)Math.Max(1, feedbackFrames - 1)
                }, ct);
            }
        }

        state = state with
        {
            Pulse = -1,
            ShowCursor = false
        };
        await writer.HoldAsync(state, timing.Hold, ct);
        return state;
    }

    private static async Task<Point> ResolvePointAsync(ILocator? target, SourceImage source, CancellationToken ct)
    {
        if (target is null)
        {
            return new Point(source.Width / 2d, source.Height / 2d);
        }

        await target.WaitForAsync().WaitAsync(ct);
        LocatorBoundingBoxResult box = await target.BoundingBoxAsync().WaitAsync(ct)
            ?? throw new InvalidOperationException("Animation target has no visible bounds.");
        var point = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
        if (point.X < 0 || point.Y < 0 || point.X > source.Width || point.Y > source.Height)
        {
            throw new InvalidOperationException("Animation target is outside the viewport; add a scroll step first.");
        }

        return point;
    }

    private static async Task<FrameState> MoveCameraAsync(
        FrameWriter writer,
        FrameState state,
        Camera destination,
        int durationMs,
        CancellationToken ct)
    {
        Camera start = state.Camera;
        int count = FrameCount(durationMs);
        for (var frame = 1; frame <= count; frame++)
        {
            double progress = EaseOut(frame / (double)count);
            var camera = new Camera(
                Mix(start.Zoom, destination.Zoom, progress),
                Mix(start.X, destination.X, progress),
                Mix(start.Y, destination.Y, progress));
            await writer.SaveAsync(state with
            {
                Camera = camera
            }, ct);
        }

        return state with
        {
            Camera = destination
        };
    }

    private static async Task<FrameState> MoveCursorAsync(
        FrameWriter writer,
        FrameState state,
        Point destination,
        int durationMs,
        CancellationToken ct)
    {
        var start = new Point(Math.Max(0, destination.X - 85), Math.Min(writer.Source.Height, destination.Y + 60));
        int count = FrameCount(durationMs);
        for (var frame = 1; frame <= count; frame++)
        {
            double progress = EaseOut(frame / (double)count);
            var cursor = new Point(Mix(start.X, destination.X, progress), Mix(start.Y, destination.Y, progress));
            await writer.SaveAsync(state with
            {
                Cursor = cursor,
                ShowCursor = true
            }, ct);
        }

        return state with
        {
            Cursor = destination,
            ShowCursor = true
        };
    }

    private static async Task<FrameState> DragAsync(
        IPage page,
        FrameWriter writer,
        FrameState state,
        Point start,
        AnimationStep step,
        int durationMs,
        CancellationToken ct)
    {
        await page.Mouse.MoveAsync((float)start.X, (float)start.Y).WaitAsync(ct);
        await page.Mouse.DownAsync().WaitAsync(ct);
        try
        {
            int count = FrameCount(durationMs);
            for (var frame = 1; frame <= count; frame++)
            {
                ct.ThrowIfCancellationRequested();
                double progress = EaseOut(frame / (double)count);
                var point = new Point(start.X + step.X * progress, start.Y + step.Y * progress);
                await page.Mouse.MoveAsync((float)point.X, (float)point.Y).WaitAsync(ct);
                await writer.SetImageAsync(await CaptureProductAsync(page, ct), blendFrames: 0, ct);
                state = state with
                {
                    Cursor = point
                };
                await writer.SaveAsync(state, ct);
            }
        }
        finally
        {
            try
            {
                await page.Mouse.UpAsync();
            }
            catch (PlaywrightException) when (ct.IsCancellationRequested)
            {
            }
        }

        return state;
    }

    private static async Task ScrollAsync(IPage page, ILocator? target, AnimationStep step, CancellationToken ct)
    {
        var position = new
        {
            x = step.X,
            y = step.Y
        };
        if (target is null)
        {
            await page.EvaluateAsync("position => window.scrollTo(position.x, position.y)", position).WaitAsync(ct);
        }
        else
        {
            await target.EvaluateAsync("(element, position) => element.scrollTo(position.x, position.y)", position).WaitAsync(ct);
        }
    }

    private static async Task<SourceImage> CaptureProductAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await CaptureEngine.SettleAsync(page).WaitAsync(ct);
        byte[] bytes = await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Animations = ScreenshotAnimations.Disabled,
            Caret = ScreenshotCaret.Hide,
            Scale = ScreenshotScale.Css
        }).WaitAsync(ct);
        int width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        return new SourceImage(width, height, "data:image/png;base64," + Convert.ToBase64String(bytes));
    }

    private static async Task<IPage> CreateCompositionAsync(
        IBrowser browser,
        Config config,
        string theme,
        SourceImage source,
        CancellationToken ct)
    {
        IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = CanvasWidth, Height = CanvasHeight },
            DeviceScaleFactor = 1,
            ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
            ReducedMotion = ReducedMotion.Reduce
        }).WaitAsync(ct);
        try
        {
            await page.SetContentAsync(await Composition.RenderAsync(config, theme, source)).WaitAsync(ct);
            await page.AddStyleTagAsync(new PageAddStyleTagOptions { Content = PresentationCss }).WaitAsync(ct);
            await page.EvaluateAsync(InitializePresentation, new
            {
                width = source.Width,
                height = source.Height
            }).WaitAsync(ct);
            await CaptureEngine.SettleAsync(page).WaitAsync(ct);
            string? oversized = await page.EvaluateAsync<string?>("""
                labels => {
                    const pill = document.querySelector('.step-pill');
                    const text = document.querySelector('.step-text');
                    document.querySelector('.step-count').textContent = `${labels.length} / ${labels.length}`;
                    for (const label of labels) {
                        text.textContent = label;
                        const bounds = pill.getBoundingClientRect();
                        if (pill.scrollWidth > pill.clientWidth + 1 || bounds.left < 0 || bounds.right > innerWidth) return label;
                    }
                    return null;
                }
                """, config.Animation!.Steps.Select(step => step.Text).ToArray()).WaitAsync(ct);
            if (oversized is not null)
            {
                throw new ArgumentException($"Animation caption does not fit the step pill. Shorten this label: {oversized}");
            }

            return page;
        }
        catch
        {
            await page.CloseAsync();
            throw;
        }
    }

    /// <summary>Checks the optional encoder before a fresh animation run starts.</summary>
    public static async Task VerifyEncoderAsync(CancellationToken ct = default)
    {
        try
        {
            await Processes.RunAsync("ffmpeg", ["-version"], Path.GetTempPath(), 10000, cancellationToken: ct);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            throw new InvalidOperationException("Animation recording requires FFmpeg on PATH. Install FFmpeg before a fresh animation run.", error);
        }
    }

    private static async Task EncodeAsync(string directory, string destination, CancellationToken ct)
    {
        string frames = Path.Combine(directory, "frame-%05d.png");
        string palette = Path.Combine(directory, "palette.png");
        string encoded = Path.Combine(directory, "showcase.gif");
        await Processes.RunAsync("ffmpeg",
            ["-hide_banner", "-loglevel", "error", "-y", "-framerate", "20", "-i", frames,
                "-vf", "palettegen=stats_mode=diff", "-frames:v", "1", palette],
            directory, cancellationToken: ct);
        await Processes.RunAsync("ffmpeg",
            ["-hide_banner", "-loglevel", "error", "-y", "-framerate", "20", "-i", frames,
                "-i", palette, "-lavfi", "paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle",
                "-loop", "0", encoded], directory, cancellationToken: ct);
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        File.Move(encoded, destination, overwrite: true);
    }

    private static int FrameCount(int milliseconds) => Math.Max(1, (int)Math.Round(milliseconds * FramesPerSecond / 1000d));

    private static StepTiming TimingFor(AnimationStep step)
    {
        int total = FrameCount(step.DurationMs);
        int camera = FrameCount(CameraDurationMs);
        int cursor = step.Type == "focus" ? 0 : FrameCount(CursorDurationMs);
        int feedback = step.Type == "focus" ? 0 : FrameCount(FeedbackDurationMs);
        int drag = step.Type == "drag" ? FrameCount(DragDurationMs) : 0;
        int reservedHold = Math.Min(FrameCount(250), total / 4);
        double scale = Math.Min(1, (total - reservedHold) / (double)(camera + cursor + feedback + drag));
        camera = Math.Max(1, (int)(camera * scale));
        cursor = cursor == 0 ? 0 : Math.Max(1, (int)(cursor * scale));
        feedback = feedback == 0 ? 0 : Math.Max(1, (int)(feedback * scale));
        drag = drag == 0 ? 0 : Math.Max(1, (int)(drag * scale));
        int hold = total - camera - cursor - feedback - drag;
        const int millisecondsPerFrame = 1000 / FramesPerSecond;
        return new StepTiming(camera * millisecondsPerFrame, cursor * millisecondsPerFrame,
            feedback * millisecondsPerFrame, drag * millisecondsPerFrame, hold * millisecondsPerFrame);
    }

    private static double EaseOut(double progress) => 1 - Math.Pow(1 - progress, 5);

    private static double Mix(double start, double end, double progress) => start + (end - start) * progress;

    private sealed record Point(double X, double Y);

    private sealed record Camera(double Zoom, double X, double Y);

    private sealed record FrameState(Camera Camera, Point Cursor, bool ShowCursor, string Text, int Step, int Total, double Pulse = -1);

    private sealed record StepTiming(int Camera, int Cursor, int Feedback, int Drag, int Hold);

    private sealed class FrameWriter(IPage page, string directory, SourceImage source)
    {
        private int frame;
        private int blendFrame;
        private int blendFrames;
        private readonly double width = Math.Min(1120, 788d * source.Width / source.Height);
        private readonly double height = Math.Min(788, 1120d * source.Height / source.Width);

        public SourceImage Source { get; } = source;

        public Camera Focus(Point point, double zoom) => new(
            zoom,
            Math.Clamp(width / 2 - point.X / Source.Width * width * zoom, -width * (zoom - 1), 0),
            Math.Clamp(height / 2 - point.Y / Source.Height * height * zoom, -height * (zoom - 1), 0));

        public async Task SetImageAsync(SourceImage image, int blendFrames, CancellationToken ct)
        {
            if (image.Width != Source.Width || image.Height != Source.Height)
            {
                throw new InvalidOperationException("The product viewport changed during animation recording.");
            }

            this.blendFrames = blendFrames;
            blendFrame = 0;
            await page.EvaluateAsync("""
                async source => {
                    const current = document.querySelector('.product-capture');
                    const previous = document.querySelector('.previous-frame');
                    previous.src = current.src;
                    current.src = source;
                    await Promise.all([current.decode(), previous.decode()]);
                }
                """, image.DataUri).WaitAsync(ct);
        }

        public async Task SaveAsync(FrameState state, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            double cursorX = (CanvasWidth - width) / 2 + state.Cursor.X / Source.Width * width * state.Camera.Zoom + state.Camera.X;
            double cursorY = 24 + (788 - height) / 2 + state.Cursor.Y / Source.Height * height * state.Camera.Zoom + state.Camera.Y;
            await page.EvaluateAsync(RenderFrame, new
            {
                zoom = state.Camera.Zoom,
                x = state.Camera.X,
                y = state.Camera.Y,
                cursorX,
                cursorY,
                cursorVisible = state.ShowCursor,
                pulse = state.Pulse,
                text = state.Text,
                step = state.Step,
                total = state.Total,
                blend = blendFrames == 0 ? 0 : Math.Max(0, 1 - ++blendFrame / (double)blendFrames)
            }).WaitAsync(ct);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = FramePath(frame++),
                Animations = ScreenshotAnimations.Disabled,
                Caret = ScreenshotCaret.Hide
            }).WaitAsync(ct);
        }

        public async Task HoldAsync(FrameState state, int durationMs, CancellationToken ct)
        {
            int count = FrameCount(durationMs);
            for (var index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (index == 0 || blendFrame < blendFrames)
                {
                    await SaveAsync(state, ct);
                }
                else
                {
                    File.Copy(FramePath(frame - 1), FramePath(frame));
                    frame++;
                }
            }
        }

        private string FramePath(int index) => Path.Combine(directory, $"frame-{index:D5}.png");
    }

    private const string PresentationCss = """
        html, body, .canvas { width: 1280px !important; height: 900px !important; }
        .identity, .softness { display: none !important; }
        .managed .window { mask-image: none; border-radius: 12px; outline: none; box-shadow: 0 26px 70px -20px #100b22a6; }
        .managed .window img { position: absolute; inset: 0; width: 100%; height: 100%; margin: 0; transform-origin: 0 0; }
        .previous-frame { pointer-events: none; }
        .step-pill { position: absolute; left: 50%; top: 838px; z-index: 4; display: flex; align-items: center; gap: 16px; max-width: 1080px; padding: 13px 22px; transform: translateX(-50%); border-radius: 999px; color: #30263d; background: #f7f4fc; box-shadow: 0 8px 24px -9px #130a2c80; font-size: 18px; line-height: 1.2; font-weight: 500; white-space: nowrap; }
        [data-theme='dark'] .step-pill { color: #f3effa; background: #2d243c; }
        .step-count { color: #6e5b83; font-size: 14px; font-variant-numeric: tabular-nums; }
        [data-theme='dark'] .step-count { color: #c3b4d7; }
        .capture-cursor { position: absolute; z-index: 6; width: 23px; height: 29px; pointer-events: none; filter: drop-shadow(1px 2px 2px #0005); }
        .click-ring { position: absolute; z-index: 5; width: 20px; height: 20px; border: 2px solid #ddd0ff; background: #a17be838; border-radius: 50%; transform: translate(-50%, -50%); pointer-events: none; }
        """;

    private const string InitializePresentation = """
        ({ width: sourceWidth, height: sourceHeight }) => {
            document.querySelector('.identity')?.remove();
            const frame = document.querySelector('.window');
            const width = Math.min(1120, 788 * sourceWidth / sourceHeight);
            const height = Math.min(788, 1120 * sourceHeight / sourceWidth);
            frame.style.cssText = `left:${(1280-width)/2}px;top:${24+(788-height)/2}px;width:${width}px;height:${height}px`;
            const image = frame.querySelector('.product-capture');
            image.style.cssText = 'width:100%;height:100%;margin:0;transform-origin:0 0';
            const previous = image.cloneNode();
            previous.className = 'previous-frame';
            previous.style.opacity = '0';
            frame.append(previous);
            const pill = document.createElement('div');
            pill.className = 'step-pill';
            pill.innerHTML = '<span class="step-count"></span><span class="step-text"></span>';
            const cursor = document.createElement('div');
            cursor.className = 'capture-cursor';
            cursor.innerHTML = '<svg viewBox="0 0 24 30" width="23" height="29" aria-hidden="true"><path d="M2 2L3 24L9 18L14 28L18 26L13 16L22 16Z" fill="#fff" stroke="#30263d" stroke-width="1.6" stroke-linejoin="round"/></svg>';
            const ring = document.createElement('div');
            ring.className = 'click-ring';
            document.querySelector('.canvas').append(pill, ring, cursor);
        }
        """;

    private const string RenderFrame = """
        state => {
            const transform = `translate(${state.x}px,${state.y}px) scale(${state.zoom})`;
            document.querySelector('.product-capture').style.transform = transform;
            const previous = document.querySelector('.previous-frame');
            previous.style.transform = transform;
            previous.style.opacity = state.blend;
            document.querySelector('.step-count').textContent = `${state.step} / ${state.total}`;
            document.querySelector('.step-text').textContent = state.text;
            const cursor = document.querySelector('.capture-cursor');
            cursor.style.left = `${state.cursorX}px`;
            cursor.style.top = `${state.cursorY}px`;
            cursor.style.opacity = state.cursorVisible ? '1' : '0';
            const ring = document.querySelector('.click-ring');
            ring.style.left = `${state.cursorX}px`;
            ring.style.top = `${state.cursorY}px`;
            const size = 18 + Math.max(0, state.pulse) * 42;
            ring.style.width = `${size}px`;
            ring.style.height = `${size}px`;
            ring.style.opacity = state.pulse < 0 ? '0' : `${0.8 * (1-state.pulse)}`;
        }
        """;
}
