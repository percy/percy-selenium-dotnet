using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using OpenQA.Selenium;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Support.UI;

namespace PercyIO.Selenium
{
    public static partial class Percy
    {
        private static readonly string[] ChromiumBrowserNames =
            { "chrome", "chromium", "msedge", "microsoftedge", "chrome-headless-shell" };

        private const string RemoteCdpCommandName = "percyGoogCdpExecute";

        private static readonly object _viewportLock = new object();
        private static readonly HashSet<string> _widthMismatchWarnedSessions = new HashSet<string>();

        private static void ResetViewportState()
        {
            lock (_viewportLock) _widthMismatchWarnedSessions.Clear();
        }

        private static string BrowserName(WebDriver? driver) =>
            (driver as IHasCapabilities)?.Capabilities?.GetCapability("browserName")?.ToString() ?? "";

        private static bool IsChromeBrowser(WebDriver driver)
        {
            if (driver is OpenQA.Selenium.Chrome.ChromeDriver)
            {
                return true;
            }

            return BrowserName(driver).Equals("chrome", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsChromiumBrowser(WebDriver driver) =>
            driver is ChromiumDriver ||
            ChromiumBrowserNames.Contains(BrowserName(driver), StringComparer.OrdinalIgnoreCase);

        private static MethodInfo? FindExecuteCdpCommand(WebDriver driver) =>
            driver.GetType().GetMethod("ExecuteCdpCommand", new[] { typeof(string), typeof(Dictionary<string, object>) });

        // Any driver that can run CDP: ChromiumDriver (ChromeDriver, EdgeDriver) via
        // ExecuteCdpCommand, or a remote Chromium session via the HTTP
        // goog/cdp/execute endpoint. IDevTools is not used because it needs the
        // se:cdp capability, which BrowserStack Automate does not return.
        internal static Func<string, Dictionary<string, object>, object?>? ResolveCdpInvoker(WebDriver driver)
        {
            string browserName = BrowserName(driver);
            if (browserName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
                browserName.Equals("safari", StringComparison.OrdinalIgnoreCase))
                return null;

            MethodInfo? executeCdp = FindExecuteCdpCommand(driver);
            if (executeCdp != null)
                return (cmd, args) => executeCdp.Invoke(driver, new object[] { cmd, args });

            if (!IsChromiumBrowser(driver)) return null;

            // Edge drivers use the ms vendor prefix (msedgedriver rejects goog);
            // goog stays as a one-time fallback because some hubs only proxy goog.
            bool isEdge = browserName.IndexOf("edge", StringComparison.OrdinalIgnoreCase) >= 0;
            var commands = new List<string>();
            foreach (string vendor in isEdge ? new[] { "ms", "goog" } : new[] { "goog" })
            {
                commands.Add(RemoteCdpCommandName + "_" + vendor);
                driver.RegisterCustomDriverCommand(commands[commands.Count - 1],
                    new HttpCommandInfo(HttpCommandInfo.PostCommand, $"/session/{{sessionId}}/{vendor}/cdp/execute"));
            }
            return (cmd, args) =>
            {
                var parameters = new Dictionary<string, object?> { ["cmd"] = cmd, ["params"] = args };
                try
                {
                    return driver.ExecuteCustomDriverCommand(commands[0], parameters);
                }
                catch (Exception e) when (commands.Count > 1 && (e is WebDriverException || e is NotImplementedException))
                {
                    commands.RemoveAt(0);
                    return driver.ExecuteCustomDriverCommand(commands[0], parameters);
                }
            };
        }

        private static Func<string, Dictionary<string, object>, object?>? ResolveLegacyCdpInvoker(WebDriver driver)
        {
            MethodInfo? executeCdp = IsChromeBrowser(driver) ? FindExecuteCdpCommand(driver) : null;
            if (executeCdp == null) return null;
            return (cmd, args) => executeCdp.Invoke(driver, new object[] { cmd, args });
        }

        private static string UnwrapMessage(Exception e) =>
            (e is TargetInvocationException || e is AggregateException) && e.InnerException != null
                ? UnwrapMessage(e.InnerException)
                : e.Message;

        private static bool IsTruthyOption(Dictionary<string, object>? options, string key)
        {
            if (options == null || !options.TryGetValue(key, out object? value) || value == null) return false;
            if (value is bool b) return b;
            return "true".Equals(value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        // Per-snapshot option wins over .percy.yml because `options` is already the
        // merged view (config.snapshot overlaid with the snapshot's own options).
        internal static bool ResolveExactResponsiveWidth(Dictionary<string, object>? options)
        {
            if (options == null || !options.TryGetValue("exactResponsiveWidth", out object? value) || value == null)
                return true;
            if (value is bool b) return b;
            return !"false".Equals(value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        // Sets an exact viewport width for responsive DOM capture and undoes it
        // afterwards. Exact mode tries CDP, then BiDi, then a window resize corrected
        // by the window border; legacy mode (exactResponsiveWidth: false) keeps the
        // old Chrome-only CDP or plain window resize.
        internal sealed class ResponsiveViewport : IDisposable
        {
            private readonly WebDriver _driver;
            private readonly bool _exact;
            private Func<string, Dictionary<string, object>, object?>? _cdp;
            private Func<string, Dictionary<string, object>, object?>? _cdpToClear;
            private bool _bidiAvailable;
            private BiDiSession? _bidi;
            private bool _bidiUsed;
            private bool _windowUsed;

            internal ResponsiveViewport(WebDriver driver, bool exact)
            {
                _driver = driver;
                _exact = exact;
                _cdp = exact ? ResolveCdpInvoker(driver) : ResolveLegacyCdpInvoker(driver);
                _bidiAvailable = exact && BiDiSession.HasWebSocketUrl(driver);
            }

            // Exact mode compares requested widths with the viewport, not the outer
            // window, so a browser with a window border still gets resized.
            internal int InitialWidth(int windowWidth)
            {
                if (!_exact) return windowWidth;
                long? innerWidth = InnerWidth();
                return innerWidth.HasValue ? (int)innerWidth.Value : windowWidth;
            }

            internal void Resize(int width, int height, int resizeCount)
            {
                if (_cdp != null)
                {
                    try
                    {
                        _cdp("Emulation.setDeviceMetricsOverride", new Dictionary<string, object>
                        {
                            { "width", width },
                            { "height", height },
                            { "deviceScaleFactor", 1 },
                            { "mobile", false }
                        });
                        _cdpToClear = _cdp;
                        Log($"Attempting to resize using CDP for width {width} and height {height}", "debug");
                        WaitForInnerWidth(width);
                        return;
                    }
                    catch (Exception e)
                    {
                        Log($"Resizing using CDP failed, falling back for width {width}: {UnwrapMessage(e)}", "debug");
                        _cdp = null;
                    }
                }

                if (_bidiAvailable)
                {
                    try
                    {
                        _bidi ??= BiDiSession.Open(_driver);
                        _bidi.SetViewport(_driver.CurrentWindowHandle, width, height);
                        _bidiUsed = true;
                        Log($"Resized viewport using BiDi for width {width} and height {height}", "debug");
                        WaitForInnerWidth(width);
                        return;
                    }
                    catch (Exception e)
                    {
                        Log($"Resizing using BiDi failed, falling back for width {width}: {UnwrapMessage(e)}", "debug");
                        _bidiAvailable = false;
                    }
                }

                ResizeWindow(width, height, resizeCount);
            }

            private void ResizeWindow(int width, int height, int resizeCount)
            {
                _windowUsed = true;
                _driver.Manage().Window.Size = new System.Drawing.Size(width, height);
                if (!_exact)
                {
                    WaitForResizeCount(width, resizeCount);
                    return;
                }

                // The resize counter is unreliable here: a clamped window fires no
                // resize event, so wait on the viewport itself.
                WaitForInnerWidth(width);
                long? innerWidth = InnerWidth();
                if (!innerWidth.HasValue || innerWidth.Value == width) return;

                int outerWidth = _driver.Manage().Window.Size.Width;
                _driver.Manage().Window.Size = new System.Drawing.Size(outerWidth + width - (int)innerWidth.Value, height);
                WaitForInnerWidth(width);

                innerWidth = InnerWidth();
                if (innerWidth.HasValue && innerWidth.Value != width)
                    WarnWidthMismatch(width, innerWidth.Value);
            }

            private void WarnWidthMismatch(int width, long innerWidth)
            {
                string browserName = BrowserName(_driver);
                Log($"Viewport width is {innerWidth} instead of {width} on {browserName} after window resize", "debug");
                bool firstForSession;
                lock (_viewportLock)
                    firstForSession = _widthMismatchWarnedSessions.Add(_driver.SessionId?.ToString() ?? "");
                if (firstForSession)
                    Log($"Could not set an exact viewport width on {browserName} (requested {width}, got {innerWidth}). " +
                        "Start the session with webSocketUrl: true to capture exact responsive widths.", "warn");
            }

            internal void Restore(int width, int height, int resizeCount)
            {
                if (_cdpToClear != null)
                {
                    try { _cdpToClear("Emulation.clearDeviceMetricsOverride", new Dictionary<string, object>()); }
                    catch (Exception e) { Log($"Could not clear CDP viewport override: {UnwrapMessage(e)}", "debug"); }
                }
                if (_bidiUsed)
                {
                    try { _bidi!.SetViewport(_driver.CurrentWindowHandle, null, null); }
                    catch (Exception e) { Log($"Could not reset BiDi viewport: {UnwrapMessage(e)}", "debug"); }
                }
                if (_windowUsed)
                {
                    _driver.Manage().Window.Size = new System.Drawing.Size(width, height);
                    if (!_exact) WaitForResizeCount(width, resizeCount);
                }
            }

            public void Dispose()
            {
                _bidi?.Dispose();
                _bidi = null;
            }

            private long? InnerWidth()
            {
                object? value = _driver.ExecuteScript("return window.innerWidth");
                return value == null ? (long?)null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }

            private void WaitForInnerWidth(int width)
            {
                try
                {
                    new WebDriverWait(_driver, TimeSpan.FromSeconds(1)).Until(d =>
                    {
                        long? innerWidth = InnerWidth();
                        return !innerWidth.HasValue || innerWidth.Value == width;
                    });
                }
                catch (WebDriverTimeoutException)
                {
                    Log($"Timed out waiting for viewport width {width}", "debug");
                }
            }

            private void WaitForResizeCount(int width, int resizeCount)
            {
                try
                {
                    WebDriverWait wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(1));
                    wait.Until(d => (long)((IJavaScriptExecutor)d).ExecuteScript("return window.resizeCount") == resizeCount);
                }
                catch (WebDriverTimeoutException)
                {
                    Log($"Timed out waiting for window resize event for width {width}", "debug");
                }
            }
        }
    }
}
