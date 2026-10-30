using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Microsoft.Playwright;
using ProductCaptures.Tests.Composition;
using ProductCaptures.Tests.Support;
using Xunit;

namespace ProductCaptures.Tests.Animation;

[Collection("Composition")]
public sealed class AnimationRendererTests(BrowserFixture browser) : IClassFixture<BrowserFixture>
{
    [Fact]
    public async Task CaptureAsync_ClickStep_RecordsRealInteractionInLoopingGif()
    {
        using var directory = new TemporaryDirectory();
        await using IBrowserContext context = await CreateContextAsync();
        IPage page = await context.NewPageAsync();
        page.SetDefaultTimeout(1000);
        await page.SetContentAsync("""
            <html><body style="margin:80px;font:30px sans-serif">
              <button class="next" hidden>Cached hidden tab</button>
              <button id="next" class="next" onclick="document.querySelector('#result').textContent='Selected'">Choose a tab</button>
              <p id="result">Waiting</p>
            </body></html>
            """);
        var configuration = new Config
        {
            Name = "Product",
            Subtitle = "for Obsidian",
            Animation = new ProductCaptures.Animation
            {
                Steps =
                [
                    new() { Text = "Choose another tab", Selector = ".next", Ready = "#result:text-is('Selected')", DurationMs = 1000 },
                    new() { Text = "See the whole note", Type = "focus", Zoom = 1, DurationMs = 1000 }
                ]
            }
        };
        string destination = Path.Combine(directory.Root, "showcase.gif");

        await AnimationRenderer.CaptureAsync(browser.Browser, page, configuration, "light", destination, TestContext.Current.CancellationToken);

        (await page.Locator("#result").TextContentAsync()).Should().Be("Selected");
        byte[] gif = await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken);
        Encoding.ASCII.GetString(gif, 0, 6).Should().Be("GIF89a");
        BinaryPrimitives.ReadUInt16LittleEndian(gif.AsSpan(6, 2)).Should().Be(1280);
        BinaryPrimitives.ReadUInt16LittleEndian(gif.AsSpan(8, 2)).Should().Be(900);
        Encoding.ASCII.GetString(gif).Should().Contain("NETSCAPE2.0");
        string frames = await Processes.RunAsync("ffprobe",
            ["-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames",
                "-of", "default=nokey=1:noprint_wrappers=1", destination], directory.Root, cancellationToken: TestContext.Current.CancellationToken);
        int.Parse(frames.Trim()).Should().BeGreaterThan(40);
        string duration = await Processes.RunAsync("ffprobe",
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=nokey=1:noprint_wrappers=1", destination],
            directory.Root, cancellationToken: TestContext.Current.CancellationToken);
        double.Parse(duration.Trim(), System.Globalization.CultureInfo.InvariantCulture).Should().BeApproximately(4.3, 0.15);
    }

    [Fact]
    public async Task CaptureAsync_DragStep_PreservesPointerCaptureAndRecordsIntermediateLayout()
    {
        using var directory = new TemporaryDirectory();
        await using IBrowserContext context = await browser.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            DeviceScaleFactor = 2
        });
        IPage page = await context.NewPageAsync();
        await page.SetContentAsync("""
            <html><body style="margin:0;background:white">
              <div id="panel" style="position:absolute;left:100px;top:120px;width:300px;height:200px;background:#137f68"></div>
              <div id="divider" style="position:absolute;left:392px;top:120px;width:16px;height:200px;background:#333;touch-action:none"></div>
              <script>
                const panel = document.querySelector('#panel');
                const divider = document.querySelector('#divider');
                window.moves = [];
                window.prematureRelease = false;
                let held = false;
                let startX;
                divider.addEventListener('pointerdown', event => {
                  startX = event.clientX;
                  held = true;
                  divider.setPointerCapture(event.pointerId);
                });
                divider.addEventListener('pointermove', event => {
                  if (!held) return;
                  window.moves.push({ buttons: event.buttons, captured: divider.hasPointerCapture(event.pointerId) });
                  const width = 300 + event.clientX - startX;
                  panel.style.width = `${width}px`;
                  divider.style.left = `${100 + width - 8}px`;
                });
                divider.addEventListener('pointerup', () => {
                  held = false;
                  document.body.dataset.state = 'complete';
                });
                divider.addEventListener('lostpointercapture', () => {
                  if (held) window.prematureRelease = true;
                });
                divider.addEventListener('pointercancel', () => { window.prematureRelease = true; });
              </script>
            </body></html>
            """);
        Config configuration = CreateConfiguration();
        configuration.Animation!.Steps =
        [
            new()
            {
                Text = "Resize the columns",
                Type = "drag",
                Selector = "#divider",
                Ready = "body[data-state='complete']",
                X = 180,
                Zoom = 1,
                DurationMs = 2400
            }
        ];
        string destination = Path.Combine(directory.Root, "drag.gif");

        await AnimationRenderer.CaptureAsync(browser.Browser, page, configuration, "light", destination,
            TestContext.Current.CancellationToken);

        (await page.EvaluateAsync<int>("window.moves.length")).Should().BeGreaterThanOrEqualTo(8);
        (await page.EvaluateAsync<bool>("window.moves.every(move => move.buttons === 1 && move.captured)")).Should().BeTrue();
        (await page.EvaluateAsync<bool>("window.prematureRelease")).Should().BeFalse();
        (await page.Locator("#panel").EvaluateAsync<double>("panel => panel.getBoundingClientRect().width")).Should().BeApproximately(480, 0.1);
        (await page.Locator("body").GetAttributeAsync("data-state")).Should().Be("complete");
        byte[] before = await ReadPixelAsync(directory.Root, destination, "0");
        byte[] during = await ReadPixelAsync(directory.Root, destination, "1.8");
        byte[] after = await ReadPixelAsync(directory.Root, destination, "3.2");
        before.Should().OnlyContain(channel => channel > 220);
        ((int)during[1]).Should().BeGreaterThan(during[0] + 50);
        ((int)after[1]).Should().BeGreaterThan(after[0] + 50);
    }

    [Fact]
    public async Task CaptureAsync_ClippedCaption_RejectsBeforeReplacingOutput()
    {
        using var directory = new TemporaryDirectory();
        await using IBrowserContext context = await CreateContextAsync();
        IPage page = await context.NewPageAsync();
        await page.SetContentAsync("<p>A real note</p>");
        Config configuration = CreateConfiguration();
        configuration.Animation!.Steps[0].Text = new string('W', 140);
        string destination = Path.Combine(directory.Root, "showcase.gif");
        byte[] original = Encoding.UTF8.GetBytes("preserve previous output");
        await File.WriteAllBytesAsync(destination, original, TestContext.Current.CancellationToken);

        Func<Task> capture = () => AnimationRenderer.CaptureAsync(
            browser.Browser, page, configuration, "light", destination,
            TestContext.Current.CancellationToken);

        await capture.Should().ThrowAsync<ArgumentException>().WithMessage("*Shorten this label:*");
        (await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken)).Should().Equal(original);
    }

    [Fact]
    public async Task CaptureAsync_MissingTarget_ReportsStepAndPreservesExistingOutput()
    {
        using var directory = new TemporaryDirectory();
        await using IBrowserContext context = await CreateContextAsync();
        IPage page = await context.NewPageAsync();
        page.SetDefaultTimeout(100);
        await page.SetContentAsync("<p>The expected control is absent.</p>");
        Config configuration = CreateConfiguration();
        configuration.Animation!.Steps[0].Selector = "#missing";
        configuration.Animation.Steps[0].Type = "click";
        string destination = Path.Combine(directory.Root, "showcase.gif");
        byte[] original = Encoding.UTF8.GetBytes("preserve previous output");
        await File.WriteAllBytesAsync(destination, original, TestContext.Current.CancellationToken);

        Func<Task> capture = () => AnimationRenderer.CaptureAsync(browser.Browser, page, configuration, "dark", destination, TestContext.Current.CancellationToken);

        await capture.Should().ThrowAsync<InvalidOperationException>().WithMessage("Animation step 1 (Explore the note) failed:*");
        (await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken)).Should().Equal(original);
    }

    [Fact]
    public async Task CaptureAsync_CancelledBeforeRecording_PreservesExistingOutput()
    {
        using var directory = new TemporaryDirectory();
        await using IBrowserContext context = await CreateContextAsync();
        IPage page = await context.NewPageAsync();
        string destination = Path.Combine(directory.Root, "showcase.gif");
        byte[] original = Encoding.UTF8.GetBytes("preserve previous output");
        await File.WriteAllBytesAsync(destination, original, TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Func<Task> capture = () => AnimationRenderer.CaptureAsync(
            browser.Browser, page, CreateConfiguration(), "light", destination, cancelled.Token);

        await capture.Should().ThrowAsync<OperationCanceledException>();
        (await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken)).Should().Equal(original);
    }

    private Task<IBrowserContext> CreateContextAsync() => browser.Browser.NewContextAsync(new BrowserNewContextOptions
    {
        ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        DeviceScaleFactor = 1
    });

    private static async Task<byte[]> ReadPixelAsync(string directory, string gif, string timestamp)
    {
        string destination = Path.Combine(directory, $"pixel-{timestamp}.rgb");
        await Processes.RunAsync("ffmpeg",
            ["-hide_banner", "-loglevel", "error", "-ss", timestamp, "-i", gif, "-vf", "crop=1:1:500:200",
                "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", destination],
            directory, cancellationToken: TestContext.Current.CancellationToken);
        byte[] pixel = await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken);
        pixel.Should().HaveCount(3);
        return pixel;
    }

    private static Config CreateConfiguration() => new()
    {
        Name = "Product",
        Subtitle = "for Obsidian",
        Animation = new ProductCaptures.Animation
        {
            Steps = [new() { Text = "Explore the note", Type = "focus", Zoom = 1, DurationMs = 1000 }]
        }
    };
}
