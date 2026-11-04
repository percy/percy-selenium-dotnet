using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;
using RichardSzalay.MockHttp;

namespace PercyIO.Selenium.Tests
{
    // Exact responsive width (CDP / BiDi / window-delta) and the closed shadow
    // root strategies (CDP / BiDi / classic). Shares the serial collection because
    // it mutates Percy's process-wide statics.
    [Collection("HttpClientStateSerial")]
    public class ExactViewportTest : IDisposable
    {
        private readonly Func<bool> _oldEnabled;

        public ExactViewportTest()
        {
            _oldEnabled = Percy.Enabled;
            Percy.ResetInternalCaches();
            PercyDriver.cache.Clear();
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setCliConfig(JsonSerializer.Deserialize<JsonElement>("{\"snapshot\":{\"responsiveSnapshotCapture\":true}}"));
            Percy.setHttpClient(new HttpClient(Mock()));
        }

        public void Dispose()
        {
            Percy.Enabled = _oldEnabled;
            Percy.BiDiSession.CommandTimeout = TimeSpan.FromSeconds(30);
            Percy.ResetInternalCaches();
            Percy.setHttpClient(new HttpClient());
            Percy.setSessionType(null);
            PercyDriver.cache.Clear();
        }

        // ---- helpers -----------------------------------------------------------

        private static MockHttpMessageHandler Mock(string widths = "[375,1280]")
        {
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Get, Percy.CLI_API + "/percy/dom.js")
                .Respond("application/javascript", "window.PercyDOM = {};");
            mockHttp.When(HttpMethod.Get, Percy.CLI_API + "/percy/widths-config*")
                .Respond("application/json", "{\"widths\":" + widths + "}");
            mockHttp.When(HttpMethod.Post, Percy.CLI_API + "/percy/log").Respond("application/json", "{}");
            mockHttp.Fallback.Respond("application/json", "{\"success\":true}");
            return mockHttp;
        }

        private static string CapturedConsole(Action body)
        {
            var sw = new System.IO.StringWriter();
            var orig = Console.Out;
            Console.SetOut(sw);
            try { body(); }
            finally { Console.SetOut(orig); }
            return System.Text.RegularExpressions.Regex.Replace(
                sw.ToString(), @"\e\[(\d+;)*(\d+)?[ABCDHJKfmsu]", "");
        }

        private static string Script(Dictionary<string, object> p) =>
            p != null && p.ContainsKey("script") ? p["script"].ToString()! : "";

        private static List<object> Args(Dictionary<string, object> p) =>
            p != null && p.TryGetValue("args", out var a) && a is System.Collections.IEnumerable e
                ? e.Cast<object>().ToList() : new List<object>();

        // A browser whose viewport is the window width minus `border`, clamped at
        // `minWidth`, and which reports innerWidth to the SDK.
        private static void WireWindow(FakeWebDriver driver, int border, int minWidth,
            Func<string, Dictionary<string, object>, object?>? extra = null)
        {
            long windowWidth = 1280;
            driver.Handler = (cmd, p) =>
            {
                var result = extra?.Invoke(cmd, p);
                if (result != null) return result;
                if (cmd == DriverCommand.SetWindowRect && p.TryGetValue("width", out var w))
                    windowWidth = Math.Max(minWidth, Convert.ToInt64(w));
                if (cmd == DriverCommand.GetWindowRect || cmd == DriverCommand.SetWindowRect)
                    return new Dictionary<string, object> { { "width", windowWidth }, { "height", 1024L }, { "x", 0L }, { "y", 0L } };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                if (cmd == DriverCommand.GetCurrentWindowHandle) return "context-1";
                if (cmd == DriverCommand.ExecuteScript)
                {
                    string s = Script(p);
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("window.innerWidth")) return windowWidth - border;
                    if (s.Contains("window.resizeCount")) return 1L;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "<r/>" } };
                }
                return null;
            };
        }

        private static FakeWebDriver StandardDriver(FakeWebDriver driver,
            Func<string, Dictionary<string, object>, object?>? extra = null)
        {
            driver.Handler = (cmd, p) =>
            {
                var result = extra?.Invoke(cmd, p);
                if (result != null) return result;
                if (cmd == DriverCommand.GetWindowRect || cmd == DriverCommand.SetWindowRect)
                    return new Dictionary<string, object> { { "width", 1280L }, { "height", 1024L }, { "x", 0L }, { "y", 0L } };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                if (cmd == DriverCommand.GetCurrentWindowHandle) return "context-1";
                if (cmd == DriverCommand.ExecuteScript)
                {
                    string s = Script(p);
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("window.resizeCount")) return 1L;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "<r/>" } };
                }
                return null;
            };
            return driver;
        }

        private static List<string> CdpCommandsSent(FakeWebDriver driver) =>
            driver.Commands.Where(c => c.StartsWith("percyGoogCdpExecute")).ToList();

        // ===== browser matrix ===================================================

        [Theory]
        [InlineData("chrome", true)]
        [InlineData("MicrosoftEdge", true)]
        [InlineData("msedge", true)]
        [InlineData("chromium", true)]
        [InlineData("chrome-headless-shell", true)]
        [InlineData("firefox", false)]
        [InlineData("safari", false)]
        public void IsChromiumBrowser_MatchesChromiumFamilyCaseInsensitively(string browserName, bool expected)
        {
            var driver = new FakeWebDriver(FakeDriverFactory.Caps(browserName));
            Assert.Equal(expected, Percy.IsChromiumBrowser(driver));
        }

        [Theory]
        [InlineData("firefox")]
        [InlineData("Safari")]
        [InlineData("opera")]
        public void ResolveCdpInvoker_NullForNonChromiumWithoutCdp(string browserName)
        {
            var driver = new FakeWebDriver(FakeDriverFactory.Caps(browserName));
            Assert.Null(Percy.ResolveCdpInvoker(driver));
        }

        [Fact]
        public void ResolveCdpInvoker_FirefoxIsExcludedEvenWithCdpMethod()
        {
            var driver = new FakeChromeWebDriver(FakeDriverFactory.FirefoxCaps());
            Assert.Null(Percy.ResolveCdpInvoker(driver));
        }

        [Fact]
        public void ResolveCdpInvoker_UsesExecuteCdpCommandWhenPresent()
        {
            var driver = new FakeChromeWebDriver(FakeDriverFactory.Caps("MicrosoftEdge"));
            var cdp = Percy.ResolveCdpInvoker(driver)!;
            cdp("Emulation.clearDeviceMetricsOverride", new Dictionary<string, object>());
            Assert.Equal(new[] { "Emulation.clearDeviceMetricsOverride" }, driver.CdpCommands);
        }

        [Fact]
        public void ResolveCdpInvoker_RemoteChromeUsesGoogCdpExecute()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            Dictionary<string, object>? sent = null;
            driver.Handler = (cmd, p) =>
            {
                if (cmd == "percyGoogCdpExecute_goog") { sent = p; return new Dictionary<string, object> { ["ok"] = true }; }
                return null;
            };
            var cdp = Percy.ResolveCdpInvoker(driver)!;
            var result = cdp("DOM.enable", new Dictionary<string, object> { ["x"] = 1 });
            Assert.Equal("DOM.enable", sent!["cmd"]);
            Assert.Equal(1, ((Dictionary<string, object>)sent["params"])["x"]);
            Assert.NotNull(result);
        }

        [Theory]
        [InlineData("MicrosoftEdge")]
        [InlineData("msedge")]
        public void ResolveCdpInvoker_RemoteEdgeUsesMsPrefix(string browserName)
        {
            var driver = new FakeWebDriver(FakeDriverFactory.Caps(browserName));
            var paths = new List<string>();
            driver.Handler = (cmd, p) => { paths.Add(cmd); return null; };
            var cdp = Percy.ResolveCdpInvoker(driver)!;
            cdp("DOM.enable", new Dictionary<string, object>());
            Assert.Equal(new[] { "percyGoogCdpExecute_ms" }, CdpCommandsSent(driver));
        }

        [Fact]
        public void ResolveCdpInvoker_RemoteEdgeFallsBackToGoogOnce()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.Caps("MicrosoftEdge"));
            driver.Handler = (cmd, p) =>
            {
                if (cmd == "percyGoogCdpExecute_ms") throw new WebDriverException("unknown command");
                return null;
            };
            var cdp = Percy.ResolveCdpInvoker(driver)!;
            cdp("DOM.enable", new Dictionary<string, object>());
            cdp("DOM.disable", new Dictionary<string, object>());
            Assert.Equal(new[] { "percyGoogCdpExecute_ms", "percyGoogCdpExecute_goog", "percyGoogCdpExecute_goog" },
                CdpCommandsSent(driver));
        }

        [Fact]
        public void ResolveCdpInvoker_RemoteEdgeFallsBackOnNotImplemented()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.Caps("MicrosoftEdge"));
            driver.Handler = (cmd, p) =>
            {
                if (cmd == "percyGoogCdpExecute_ms") throw new NotImplementedException("unknown command");
                return null;
            };
            var cdp = Percy.ResolveCdpInvoker(driver)!;
            cdp("DOM.enable", new Dictionary<string, object>());
            Assert.Equal(new[] { "percyGoogCdpExecute_ms", "percyGoogCdpExecute_goog" }, CdpCommandsSent(driver));
        }

        [Fact]
        public void ResolveCdpInvoker_RemoteChromeUsesGoogOnly()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            driver.Handler = (cmd, p) => null;
            Percy.ResolveCdpInvoker(driver)!("DOM.enable", new Dictionary<string, object>());
            Assert.Equal(new[] { "percyGoogCdpExecute_goog" }, CdpCommandsSent(driver));
        }

        [Fact]
        public void ResolveCdpInvoker_RemoteChromeErrorPropagates()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            driver.Handler = (cmd, p) => throw new WebDriverException("unknown command");
            var cdp = Percy.ResolveCdpInvoker(driver)!;
            Assert.Throws<WebDriverException>(() => cdp("DOM.enable", new Dictionary<string, object>()));
        }

        // ===== precedence =======================================================

        [Fact]
        public void ResolveExactResponsiveWidth_DefaultsToTrue()
        {
            Assert.True(Percy.ResolveExactResponsiveWidth(null));
            Assert.True(Percy.ResolveExactResponsiveWidth(new Dictionary<string, object>()));
            Assert.True(Percy.ResolveExactResponsiveWidth(new Dictionary<string, object> { ["exactResponsiveWidth"] = null! }));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)]
        [InlineData("false", false)]
        [InlineData("FALSE", false)]
        [InlineData("true", true)]
        public void ResolveExactResponsiveWidth_ReadsBoolAndString(object value, bool expected)
        {
            Assert.Equal(expected, Percy.ResolveExactResponsiveWidth(new Dictionary<string, object> { ["exactResponsiveWidth"] = value }));
        }

        [Fact]
        public void ExactResponsiveWidth_ConfigFalse_PerSnapshotTrueWins()
        {
            Percy.setCliConfig(JsonSerializer.Deserialize<JsonElement>(
                "{\"snapshot\":{\"responsiveSnapshotCapture\":true,\"exactResponsiveWidth\":false}}"));
            var driver = StandardDriver(new FakeWebDriver(FakeDriverFactory.ChromeCaps()));
            Percy.Snapshot(driver, "Per-snapshot wins", new Dictionary<string, object> { ["exactResponsiveWidth"] = true });
            Assert.Contains("percyGoogCdpExecute_goog", driver.Commands);
            Assert.DoesNotContain(DriverCommand.SetWindowRect, driver.Commands);
        }

        [Fact]
        public void ExactResponsiveWidth_ConfigFalse_UsesLegacyWindowResizeOnRemoteChrome()
        {
            Percy.setCliConfig(JsonSerializer.Deserialize<JsonElement>(
                "{\"snapshot\":{\"responsiveSnapshotCapture\":true,\"exactResponsiveWidth\":false}}"));
            var cdp = new List<string>();
            var driver = StandardDriver(new FakeWebDriver(FakeDriverFactory.ChromeCaps()), (cmd, p) =>
            {
                if (cmd == "percyGoogCdpExecute_goog") { cdp.Add(p["cmd"].ToString()!); return new Dictionary<string, object>(); }
                return null;
            });
            Percy.Snapshot(driver, "Legacy remote chrome");
            Assert.DoesNotContain("Emulation.setDeviceMetricsOverride", cdp);
            Assert.Contains(DriverCommand.SetWindowRect, driver.Commands);
        }

        [Fact]
        public void ExactResponsiveWidth_False_LocalChromeKeepsCdpAndClearsOnRestore()
        {
            var driver = new FakeChromeWebDriver(FakeDriverFactory.ChromeCaps());
            StandardDriver(driver);
            Percy.Snapshot(driver, "Legacy chrome", new Dictionary<string, object> { ["exactResponsiveWidth"] = false });
            Assert.Equal(2, driver.CdpCommands.Count(c => c == "Emulation.setDeviceMetricsOverride"));
            Assert.Equal("Emulation.clearDeviceMetricsOverride", driver.CdpCommands.Last());
            Assert.DoesNotContain(DriverCommand.SetWindowRect, driver.Commands);
        }

        [Fact]
        public void ExactResponsiveWidth_False_EdgeUsesWindowResize()
        {
            var driver = new FakeChromeWebDriver(FakeDriverFactory.Caps("MicrosoftEdge"));
            StandardDriver(driver);
            Percy.Snapshot(driver, "Legacy edge", new Dictionary<string, object> { ["exactResponsiveWidth"] = "false" });
            Assert.DoesNotContain("Emulation.setDeviceMetricsOverride", driver.CdpCommands);
            Assert.Contains(DriverCommand.SetWindowRect, driver.Commands);
        }

        // ===== CDP path =========================================================

        [Fact]
        public void ExactViewport_RemoteChrome_OverridesThenClears()
        {
            var cdp = new List<string>();
            var driver = StandardDriver(new FakeWebDriver(FakeDriverFactory.ChromeCaps()), (cmd, p) =>
            {
                if (cmd == "percyGoogCdpExecute_goog") { cdp.Add(p["cmd"].ToString()!); return new Dictionary<string, object>(); }
                return null;
            });
            Percy.Snapshot(driver, "Remote chrome CDP");
            Assert.Equal(2, cdp.Count(c => c == "Emulation.setDeviceMetricsOverride"));
            Assert.Equal("Emulation.clearDeviceMetricsOverride", cdp.Last());
            Assert.DoesNotContain(DriverCommand.SetWindowRect, driver.Commands);
        }

        [Fact]
        public void ExactViewport_ClearFailureIsLoggedNotFatal()
        {
            var driver = new FakeChromeWebDriver(FakeDriverFactory.ChromeCaps());
            StandardDriver(driver);
            driver.CdpResult = (cmd, args) =>
            {
                if (cmd == "Emulation.clearDeviceMetricsOverride") throw new WebDriverException("clear boom");
                return new Dictionary<string, object>();
            };
            bool oldDebug = Percy.DebugEnabled;
            Percy.DebugEnabled = true;
            try
            {
                var output = CapturedConsole(() => Percy.Snapshot(driver, "Clear throws"));
                Assert.Contains("Could not clear CDP viewport override: clear boom", output);
                Assert.DoesNotContain("Could not take DOM snapshot", output);
            }
            finally { Percy.DebugEnabled = oldDebug; }
        }

        [Fact]
        public void ExactViewport_SkipsResizeWhenViewportAlreadyAtWidth()
        {
            // innerWidth is already 1280, so only 375 needs an override.
            Percy.setHttpClient(new HttpClient(Mock("[1280,375]")));
            var driver = new FakeChromeWebDriver(FakeDriverFactory.ChromeCaps());
            WireWindow(driver, border: 0, minWidth: 0);
            Percy.Snapshot(driver, "Already at width");
            Assert.Single(driver.CdpCommands, c => c == "Emulation.setDeviceMetricsOverride");
        }

        // ===== window-delta path ================================================

        [Fact]
        public void ExactViewport_WindowPathCorrectsForBorder()
        {
            // Firefox without BiDi on Windows: 16px border, no clamp.
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            var widths = new List<long>();
            WireWindow(driver, border: 16, minWidth: 0, (cmd, p) =>
            {
                if (cmd == DriverCommand.SetWindowRect) widths.Add(Convert.ToInt64(p["width"]));
                return null;
            });
            var output = CapturedConsole(() => Percy.Snapshot(driver, "Border corrected"));
            Assert.Equal(new long[] { 375, 391, 1280, 1296, 1280 }, widths);
            Assert.DoesNotContain("Could not set an exact viewport width", output);
        }

        [Fact]
        public void ExactViewport_WindowPathWarnsOncePerSessionWhenClamped()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            WireWindow(driver, border: 0, minWidth: 500);
            var output = CapturedConsole(() =>
            {
                Percy.Snapshot(driver, "Clamped 1");
                Percy.Snapshot(driver, "Clamped 2");
            });
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(output,
                "Could not set an exact viewport width on firefox \\(requested 375, got 500\\)").Count);
        }

        [Fact]
        public void ExactViewport_WindowPathWithoutInnerWidthSkipsCorrection()
        {
            var driver = StandardDriver(new FakeWebDriver(FakeDriverFactory.FirefoxCaps()));
            Percy.Snapshot(driver, "No innerWidth");
            Assert.Equal(3, driver.Commands.Count(c => c == DriverCommand.SetWindowRect));
        }

        // ===== BiDi path ========================================================

        [Fact]
        public void ExactViewport_BiDiSetsAndResetsViewport()
        {
            using var server = new FakeBiDiServer();
            var driver = StandardDriver(new FakeWebDriver(FakeDriverFactory.Caps("firefox", server.Url)));
            Percy.Snapshot(driver, "BiDi viewport");

            var viewportCalls = server.Received.Where(r => r.method == "browsingContext.setViewport").Select(r => r.parameters).ToList();
            Assert.Equal(3, viewportCalls.Count);
            Assert.Equal(375, viewportCalls[0].GetProperty("viewport").GetProperty("width").GetInt32());
            Assert.Equal(1.0, viewportCalls[0].GetProperty("devicePixelRatio").GetDouble());
            Assert.Equal("context-1", viewportCalls[0].GetProperty("context").GetString());
            Assert.Equal(JsonValueKind.Null, viewportCalls[2].GetProperty("viewport").ValueKind);
            Assert.Equal(JsonValueKind.Null, viewportCalls[2].GetProperty("devicePixelRatio").ValueKind);
            Assert.DoesNotContain(DriverCommand.SetWindowRect, driver.Commands);
        }

        [Fact]
        public void ExactViewport_BiDiFailureFallsBackToWindow()
        {
            using var server = new FakeBiDiServer();
            server.Responder = (method, p) => method == "browsingContext.setViewport" ? FakeBiDiServer.Error : "{}";
            var driver = StandardDriver(new FakeWebDriver(FakeDriverFactory.Caps("firefox", server.Url)));
            Percy.Snapshot(driver, "BiDi fails");
            Assert.Single(server.Received, r => r.method == "browsingContext.setViewport");
            Assert.Contains(DriverCommand.SetWindowRect, driver.Commands);
        }

        [Fact]
        public void ExactViewport_BiDiResetFailureIsLogged()
        {
            using var server = new FakeBiDiServer();
            server.Responder = (method, p) =>
                method == "browsingContext.setViewport" && p.GetProperty("viewport").ValueKind == JsonValueKind.Null
                    ? FakeBiDiServer.Error : "{}";
            var driver = StandardDriver(new FakeWebDriver(FakeDriverFactory.Caps("firefox", server.Url)));
            bool oldDebug = Percy.DebugEnabled;
            Percy.DebugEnabled = true;
            try
            {
                var output = CapturedConsole(() => Percy.Snapshot(driver, "BiDi reset fails"));
                Assert.Contains("Could not reset BiDi viewport", output);
            }
            finally { Percy.DebugEnabled = oldDebug; }
        }

        [Fact]
        public void ExactViewport_CdpFailureFallsBackToBiDi()
        {
            using var server = new FakeBiDiServer();
            var driver = new FakeChromeWebDriver(FakeDriverFactory.Caps("chrome", server.Url));
            StandardDriver(driver);
            driver.CdpThrows = true;
            Percy.Snapshot(driver, "CDP then BiDi");
            Assert.Single(driver.CdpCommands, c => c == "Emulation.setDeviceMetricsOverride");
            Assert.Equal(3, server.Received.Count(r => r.method == "browsingContext.setViewport"));
        }

        [Fact]
        public void BiDiSession_TimesOutWhenServerHangs()
        {
            using var server = new FakeBiDiServer();
            server.Responder = (_, __) => FakeBiDiServer.Hang;
            Percy.BiDiSession.CommandTimeout = TimeSpan.FromMilliseconds(300);
            var driver = StandardDriver(new FakeWebDriver(FakeDriverFactory.Caps("firefox", server.Url)));
            using var session = Percy.BiDiSession.Open(driver);
            Assert.Throws<WebDriverTimeoutException>(() => session.SetViewport("context-1", 375, 800));
        }

        [Fact]
        public void BiDiSession_DisposeFailureIsSwallowed()
        {
            var ex = Record.Exception(() => new Percy.BiDiSession(new object()).Dispose());
            Assert.Null(ex);
        }

        [Fact]
        public void BiDiSession_HasWebSocketUrlOnlyForNonEmptyString()
        {
            Assert.False(Percy.BiDiSession.HasWebSocketUrl(new FakeWebDriver(FakeDriverFactory.FirefoxCaps())));
            Assert.False(Percy.BiDiSession.HasWebSocketUrl(new FakeWebDriver(FakeDriverFactory.Caps("firefox", ""))));
            Assert.True(Percy.BiDiSession.HasWebSocketUrl(new FakeWebDriver(FakeDriverFactory.Caps("firefox", "wss://hub.example.test/session/x"))));
        }

        [Fact]
        public void BiDiSession_ClosedShadowRootPairsKeepsOnlyClosedRoots()
        {
            using var server = new FakeBiDiServer();
            server.Responder = (method, p) => FakeBiDiServer.EvaluateSuccess(
                "{\"type\":\"array\",\"value\":[" +
                FakeBiDiServer.Node("h1", "r1") + "," +
                FakeBiDiServer.Node("h2", "r2", "open") + "," +
                FakeBiDiServer.Node("h3") + "]}");
            var driver = StandardDriver(new FakeWebDriver(FakeDriverFactory.Caps("firefox", server.Url)));
            using var session = Percy.BiDiSession.Open(driver);
            var result = session.CallFunction("context-1", "function(){}", new[] { "s1" }, includeShadowTree: true);
            Assert.Equal(new[] { ("h1", "r1") }, Percy.BiDiSession.ClosedShadowRootPairs(result));
            Assert.Empty(Percy.BiDiSession.ClosedShadowRootPairs(null));

            var call = server.Received.Single(r => r.method == "script.callFunction").parameters;
            Assert.Equal("s1", call.GetProperty("arguments")[0].GetProperty("sharedId").GetString());
            Assert.Equal("all", call.GetProperty("serializationOptions").GetProperty("includeShadowTree").GetString());
            Assert.Equal(0, call.GetProperty("serializationOptions").GetProperty("maxDomDepth").GetInt32());
        }

        [Fact]
        public void BiDiSession_CallFunctionExceptionResultThrows()
        {
            using var server = new FakeBiDiServer();
            server.Responder = (method, p) => FakeBiDiServer.EvaluateException();
            var driver = StandardDriver(new FakeWebDriver(FakeDriverFactory.Caps("firefox", server.Url)));
            using var session = Percy.BiDiSession.Open(driver);
            Assert.Throws<WebDriverException>(() =>
                session.CallFunction("context-1", "function(){}", new string[0], includeShadowTree: false));
        }

        // ===== closed shadow roots ==============================================

        [Fact]
        public void ClosedShadow_DisableShadowDomSkipsExposure()
        {
            var driver = new FakeChromeWebDriver(FakeDriverFactory.ChromeCaps());
            Percy.ExposeClosedShadowRoots(driver, new Dictionary<string, object> { ["disableShadowDOM"] = true });
            Percy.ExposeClosedShadowRoots(driver, new Dictionary<string, object> { ["disableShadowDOM"] = "TRUE" });
            Assert.Empty(driver.CdpCommands);
            Percy.ExposeClosedShadowRoots(driver, new Dictionary<string, object> { ["disableShadowDOM"] = null! });
            Percy.ExposeClosedShadowRoots(driver, new Dictionary<string, object> { ["disableShadowDOM"] = "false" });
            Assert.Equal(2, driver.CdpCommands.Count(c => c == "DOM.enable"));
        }

        [Fact]
        public void ClosedShadow_SnapshotHonoursDisableShadowDomFromConfig()
        {
            Percy.setCliConfig(JsonSerializer.Deserialize<JsonElement>("{\"snapshot\":{\"disableShadowDOM\":true}}"));
            var driver = new FakeChromeWebDriver(FakeDriverFactory.ChromeCaps());
            StandardDriver(driver);
            Percy.Snapshot(driver, "Shadow disabled");
            Assert.Empty(driver.CdpCommands);
        }

        [Fact]
        public void ClosedShadow_RemoteChromeUsesCdp()
        {
            var cdp = new List<string>();
            var driver = StandardDriver(new FakeWebDriver(FakeDriverFactory.ChromeCaps()), (cmd, p) =>
            {
                if (cmd == "percyGoogCdpExecute_goog") { cdp.Add(p["cmd"].ToString()!); return new Dictionary<string, object>(); }
                return null;
            });
            Percy.ExposeClosedShadowRoots(driver);
            Assert.Equal(new[] { "DOM.enable", "DOM.getDocument", "DOM.disable" }, cdp);
        }

        private static readonly object ShadowRef =
            new Dictionary<string, object> { { "shadow-6066-11e4-a52e-4f735466cecf", "root-1" } };

        // Classic fallback driver: three candidate hosts (closed root, no root,
        // Safari-style null), the closed root holding one nested candidate.
        private static FakeWebDriver ClassicDriver(FakeWebDriver driver, List<List<object>> setCalls)
        {
            int nested = 0;
            return StandardDriver(driver, (cmd, p) =>
            {
                if (cmd == DriverCommand.GetElementShadowRoot)
                {
                    string id = p["id"].ToString()!;
                    if (id == "no-root") throw new NoSuchShadowRootException("no such shadow root");
                    if (id == "safari") return null;
                    return ShadowRef;
                }
                if (cmd != DriverCommand.ExecuteScript) return null;
                string s = Script(p);
                if (s.Contains("percyClosedShadowRoots.set(arguments[0], arguments[1])"))
                {
                    setCalls.Add(Args(p));
                    return nested++ == 0 ? new object[] { FakeDriverFactory.MakeElement("nested") } : new object[0];
                }
                if (s.Contains("percyClosedShadowCandidates([document])"))
                    return new object[] {
                        FakeDriverFactory.MakeElement("host"), FakeDriverFactory.MakeElement("no-root"), FakeDriverFactory.MakeElement("safari") };
                return null;
            });
        }

        [Fact]
        public void ClosedShadow_ClassicOnLocalFirefoxExposesAndRecurses()
        {
            var setCalls = new List<List<object>>();
            var driver = ClassicDriver(new FakeWebDriver(FakeDriverFactory.LocalExecutor(), FakeDriverFactory.FirefoxCaps()), setCalls);
            Percy.ExposeClosedShadowRoots(driver);
            Assert.Equal(2, setCalls.Count);
            var firstArgs = setCalls[0];
            Assert.Equal("host", ((Dictionary<string, object>)firstArgs[0])["element-6066-11e4-a52e-4f735466cecf"]);
            Assert.Equal("root-1", ((Dictionary<string, object>)firstArgs[1])["shadow-6066-11e4-a52e-4f735466cecf"]);
        }

        [Fact]
        public void ClosedShadow_ClassicStopsAtHostCap()
        {
            Percy.ClosedShadowMaxHostsSetting = "1";
            var setCalls = new List<List<object>>();
            var driver = ClassicDriver(new FakeWebDriver(FakeDriverFactory.LocalExecutor(), FakeDriverFactory.FirefoxCaps()), setCalls);
            bool oldDebug = Percy.DebugEnabled;
            Percy.DebugEnabled = true;
            try
            {
                var output = CapturedConsole(() => Percy.ExposeClosedShadowRoots(driver));
                Assert.Single(setCalls);
                Assert.Contains("Closed shadow root host limit 1 reached; 3 candidate(s) not checked", output);
            }
            finally { Percy.DebugEnabled = oldDebug; }
        }

        [Fact]
        public void ClosedShadow_ClassicInvalidCapUsesDefault()
        {
            Percy.ClosedShadowMaxHostsSetting = "lots";
            var setCalls = new List<List<object>>();
            var driver = ClassicDriver(new FakeWebDriver(FakeDriverFactory.LocalExecutor(), FakeDriverFactory.FirefoxCaps()), setCalls);
            Percy.ExposeClosedShadowRoots(driver);
            Assert.Equal(2, setCalls.Count);
        }

        [Fact]
        public void ClosedShadow_ClassicSkippedOnRemoteAndLoggedOnce()
        {
            var setCalls = new List<List<object>>();
            var driver = ClassicDriver(new FakeWebDriver(FakeDriverFactory.FirefoxCaps()), setCalls);
            var output = CapturedConsole(() =>
            {
                Percy.ExposeClosedShadowRoots(driver);
                Percy.ExposeClosedShadowRoots(driver);
            });
            Assert.Empty(setCalls);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(output,
                "closed shadow roots skipped on remote session; start the session with webSocketUrl: true or set PERCY_CLOSED_SHADOW_CLASSIC=true").Count);
        }

        [Fact]
        public void ClosedShadow_ClassicOnRemoteWhenOptedIn()
        {
            Percy.ClosedShadowClassicSetting = "true";
            var setCalls = new List<List<object>>();
            var driver = ClassicDriver(new FakeWebDriver(FakeDriverFactory.FirefoxCaps()), setCalls);
            Percy.ExposeClosedShadowRoots(driver);
            Assert.Equal(2, setCalls.Count);
        }

        [Fact]
        public void ClosedShadow_ClassicScriptFailureWarnsAndContinues()
        {
            var driver = StandardDriver(new FakeWebDriver(FakeDriverFactory.LocalExecutor(), FakeDriverFactory.FirefoxCaps()), (cmd, p) =>
                cmd == DriverCommand.ExecuteScript && Script(p).Contains("percyClosedShadowCandidates([document])")
                    ? throw new WebDriverException("script boom") : null);
            var output = CapturedConsole(() => Percy.ExposeClosedShadowRoots(driver));
            Assert.Contains("Could not expose closed shadow roots via WebDriver classic: script boom", output);
        }

        [Fact]
        public void ClosedShadow_CdpFailureFallsBackToClassic()
        {
            var setCalls = new List<List<object>>();
            var driver = new FakeChromeWebDriver(FakeDriverFactory.LocalExecutor(), FakeDriverFactory.ChromeCaps());
            ClassicDriver(driver, setCalls);
            driver.CdpThrows = true;
            Percy.ExposeClosedShadowRoots(driver);
            Assert.Equal(new[] { "DOM.enable" }, driver.CdpCommands);
            Assert.Equal(2, setCalls.Count);
        }

        [Fact]
        public void ClosedShadow_BiDiExposesNestedRoots()
        {
            using var server = new FakeBiDiServer();
            int candidateCalls = 0;
            server.Responder = (method, p) =>
            {
                if (method != "script.callFunction") return "{}";
                if (!p.TryGetProperty("serializationOptions", out _))
                    return FakeBiDiServer.EvaluateSuccess("{\"type\":\"undefined\"}");
                candidateCalls++;
                return FakeBiDiServer.EvaluateSuccess(candidateCalls switch
                {
                    1 => "{\"type\":\"array\",\"value\":[" + FakeBiDiServer.Node("h1", "r1") + "]}",
                    2 => "{\"type\":\"array\",\"value\":[" + FakeBiDiServer.Node("h2", "r2") + "]}",
                    _ => "{\"type\":\"array\",\"value\":[]}"
                });
            };
            var driver = StandardDriver(new FakeWebDriver(FakeDriverFactory.Caps("firefox", server.Url)));
            Percy.ExposeClosedShadowRoots(driver);

            var calls = server.Received.Where(r => r.method == "script.callFunction").Select(r => r.parameters).ToList();
            Assert.Equal(5, calls.Count);
            Assert.Equal(0, calls[0].GetProperty("arguments").GetArrayLength());
            var setArgs = calls[1].GetProperty("arguments").EnumerateArray().Select(a => a.GetProperty("sharedId").GetString()).ToList();
            Assert.Equal(new[] { "h1", "r1" }, setArgs);
            Assert.Equal("r1", calls[2].GetProperty("arguments")[0].GetProperty("sharedId").GetString());
        }

        [Fact]
        public void ClosedShadow_BiDiFailureFallsBackToClassic()
        {
            using var server = new FakeBiDiServer();
            server.Responder = (method, p) => FakeBiDiServer.EvaluateException();
            var setCalls = new List<List<object>>();
            var driver = ClassicDriver(new FakeWebDriver(FakeDriverFactory.LocalExecutor(), FakeDriverFactory.Caps("firefox", server.Url)), setCalls);
            Percy.ExposeClosedShadowRoots(driver);
            Assert.Single(server.Received, r => r.method == "script.callFunction");
            Assert.Equal(2, setCalls.Count);
        }

        // ===== local driver detection ===========================================

        [Theory]
        [InlineData("http://localhost:4444/", true)]
        [InlineData("http://127.0.0.1:9515/", true)]
        [InlineData("http://[::1]:9515/", true)]
        [InlineData("https://hub-cloud.browserstack.com/wd/hub/", false)]
        public void IsLocalDriver_ChecksExecutorHost(string url, bool expected)
        {
            var driver = new FakeWebDriver(new HttpCommandExecutor(new Uri(url), TimeSpan.FromSeconds(5)), FakeDriverFactory.FirefoxCaps());
            Assert.Equal(expected, Percy.IsLocalDriver(driver));
        }

        [Fact]
        public void IsLocalDriver_TrueForDriverServiceExecutor()
        {
            var service = OpenQA.Selenium.Chrome.ChromeDriverService.CreateDefaultService(System.IO.Path.GetTempPath(), "chromedriver");
            var executor = new DriverServiceCommandExecutor(service,
                new HttpCommandExecutor(new Uri("http://localhost:9515/"), TimeSpan.FromSeconds(5)));
            var driver = new FakeWebDriver(executor, FakeDriverFactory.FirefoxCaps());
            Assert.True(Percy.IsLocalDriver(driver));
        }

        [Fact]
        public void IsLocalDriver_FalseForUnknownExecutor()
        {
            var driver = new FakeWebDriver(new StubExecutor(), FakeDriverFactory.FirefoxCaps());
            Assert.False(Percy.IsLocalDriver(driver));
        }

        private sealed class StubExecutor : ICommandExecutor
        {
            public bool TryAddCommand(string commandName, CommandInfo info) => true;
            public Response Execute(Command commandToExecute) => new Response("sess-123", null, WebDriverResult.Success);
            public System.Threading.Tasks.Task<Response> ExecuteAsync(Command commandToExecute) =>
                System.Threading.Tasks.Task.FromResult(Execute(commandToExecute));
            public void Dispose() { }
        }
    }
}
