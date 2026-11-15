using Microsoft.Playwright;

namespace Linkbase.Tests;

// Starts Chromium for the browser tests, installing it first if this machine doesn't have it. Without this, every CI
// image and every new Playwright version needs a separate install step, and a missing one fails every browser test.
internal static class ChromiumLauncher
{
    private static readonly SemaphoreSlim Installing = new(1, 1);

    public static async Task<IBrowser> LaunchAsync(IPlaywright playwright)
    {
        try
        {
            return await playwright.Chromium.LaunchAsync();
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.Ordinal))
        {
            await InstallAsync();
            return await playwright.Chromium.LaunchAsync();
        }
    }

    // One test at a time installs; the others wait, then find it there. On Linux the system libraries come too, which
    // needs root, so a machine without it falls back to the browser alone.
    private static async Task InstallAsync()
    {
        await Installing.WaitAsync();
        try
        {
            var attempts = OperatingSystem.IsLinux()
                ? new[] { new[] { "install", "--with-deps", "chromium" }, ["install", "chromium"] }
                : [["install", "chromium"]];
            foreach (var args in attempts)
            {
                if (await Task.Run(() => Microsoft.Playwright.Program.Main(args)) == 0)
                {
                    return;
                }
            }

            throw new InvalidOperationException("Playwright could not install Chromium. Run playwright.ps1 install chromium from the test output folder.");
        }
        finally
        {
            Installing.Release();
        }
    }
}
