using Microsoft.Playwright;
using Xunit;

namespace ProductCaptures.Tests.Composition;

public sealed class BrowserFixture : IAsyncLifetime
{
    private IPlaywright? playwright;
    private IBrowser? browser;

    public IBrowser Browser => browser
        ?? throw new InvalidOperationException("Browser fixture is not initialized.");

    public async ValueTask InitializeAsync()
    {
        playwright = await Playwright.CreateAsync();
        try
        {
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Timeout = 30_000
            });
        }
        catch
        {
            playwright.Dispose();
            playwright = null;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (browser is not null)
            {
                await browser.DisposeAsync();
            }
        }
        finally
        {
            playwright?.Dispose();
        }
    }
}
