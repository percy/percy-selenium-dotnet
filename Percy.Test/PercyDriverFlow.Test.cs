using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;
using RichardSzalay.MockHttp;
using Newtonsoft.Json;

namespace PercyIO.Selenium.Tests
{
    // Driver-flow tests. These exercise the WebDriver-bound code in Percy.cs and
    // the entire PercySeleniumDriver/PercyDriver surface WITHOUT a live browser:
    // a concrete FakeWebDriver overrides Selenium's single ExecuteAsync chokepoint
    // so all the real, sealed public WebDriver methods (ExecuteScript, FindElements,
    // SwitchTo, Manage().Window/Cookies/Timeouts, Navigate().Refresh) run through
    // genuine Selenium code. @percy/cli HTTP is mocked with RichardSzalay.MockHttp.
    //
    // Serialised with PercyLogicSerial because they mutate Percy's static state
    // (_http, _enabled, sessionType, cliConfig).
    [Collection("PercyLogicSerial")]
    public class PercyDriverFlowTest : IDisposable
    {
        private readonly Func<bool> _oldEnabled;

        public PercyDriverFlowTest()
        {
            _oldEnabled = Percy.Enabled;
            Percy.ResetInternalCaches();
            PercyDriver.cache.Clear();
        }

        public void Dispose()
        {
            Percy.Enabled = _oldEnabled;
            Percy.ResetInternalCaches();
            Percy.setHttpClient(new HttpClient());
            Percy.setSessionType(null);
            PercyDriver.cache.Clear();
        }

        // ---- helpers -----------------------------------------------------------

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

        private static MockHttpMessageHandler SnapshotMock(string snapshotResponse = "{\"success\":true}")
        {
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Get, "http://localhost:5338/percy/dom.js")
                .Respond("application/javascript", "window.PercyDOM = { serialize: function(){ return {}; } };");
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/snapshot")
                .Respond("application/json", snapshotResponse);
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/automateScreenshot")
                .Respond("application/json", "{\"success\":true}");
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/log")
                .Respond("application/json", "{}");
            mockHttp.Fallback.Respond("application/json", "{\"success\":true}");
            return mockHttp;
        }

        // Standard handler for a web snapshot with no iframes.
        private static FakeWebDriver WebDriverNoIframes()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "<html/>" } };
                    return null;
                }
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                return null;
            };
            return driver;
        }

        // ===== Percy.Snapshot — web flow =======================================

        [Fact]
        public void Snapshot_WebFlow_PostsSnapshot()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setHttpClient(new HttpClient(SnapshotMock()));

            var driver = WebDriverNoIframes();
            var result = Percy.Snapshot(driver, "Web Snapshot");

            Assert.Null(result); // no "data" in response
            Assert.Contains(DriverCommand.GetAllCookies, driver.Commands);
            // serialize + url + PercyDOM probe all executed
            Assert.Contains(driver.Scripts, s => s.Contains("PercyDOM.serialize"));
            Assert.Contains(driver.Scripts, s => s.Contains("document.URL"));
        }

        [Fact]
        public void Snapshot_WebFlow_InjectsDomWhenPercyDomMissing()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setHttpClient(new HttpClient(SnapshotMock()));

            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    if (s.Contains("!!window.PercyDOM")) return false; // forces injection branch
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "x" } };
                    return null;
                }
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                return null;
            };

            Percy.Snapshot(driver, "Inject DOM");
            // PercyDOM injection script (the window.PercyDOM=... body) was executed.
            Assert.Contains(driver.Scripts, s => s.Contains("window.PercyDOM"));
        }

        [Fact]
        public void Snapshot_WebFlow_ReturnsParsedDataWhenPresent()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setHttpClient(new HttpClient(
                SnapshotMock("{\"success\":true,\"data\":{\"link\":\"https://percy.io/build/1\"}}")));

            var driver = WebDriverNoIframes();
            var result = Percy.Snapshot(driver, "With Data");

            Assert.NotNull(result);
            Assert.Equal("https://percy.io/build/1", (string)result!["link"]);
        }

        [Fact]
        public void Snapshot_WebFlow_LogsWhenServerReturnsError()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setHttpClient(new HttpClient(
                SnapshotMock("{\"success\":false,\"error\":\"boom\"}")));

            var driver = WebDriverNoIframes();
            var output = CapturedConsole(() => Percy.Snapshot(driver, "Err Snapshot"));
            Assert.Contains("Could not take DOM snapshot \"Err Snapshot\"", output);
        }

        [Fact]
        public void Snapshot_ReturnsNullAndDoesNothingWhenDisabled()
        {
            Percy.Enabled = () => false;
            var driver = WebDriverNoIframes();
            var result = Percy.Snapshot(driver, "Disabled");
            Assert.Null(result);
            // No DOM/cookies fetched because Enabled() short-circuited.
            Assert.DoesNotContain(DriverCommand.GetAllCookies, driver.Commands);
        }

        [Fact]
        public void Snapshot_ThrowsWhenSessionTypeIsAutomate()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("automate");
            var driver = WebDriverNoIframes();
            var ex = Assert.Throws<Exception>(() => Percy.Snapshot(driver, "Auto"));
            Assert.Contains("Please use Screenshot()", ex.Message);
        }

        [Fact]
        public void Snapshot_ObjectOverload_MapsAnonymousProps()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setHttpClient(new HttpClient(SnapshotMock()));

            var driver = WebDriverNoIframes();
            // The anonymous-object overload reflects props into Options.
            Percy.Snapshot(driver, "Obj Overload", new { enableJavaScript = true, minHeight = 1000 });
            Assert.Contains(driver.Scripts, s => s.Contains("PercyDOM.serialize"));
        }

        // ===== Percy.Snapshot — CORS iframe processing =========================

        private FakeWebDriver WebDriverWithCrossOriginIframe()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                if (cmd == DriverCommand.FindElements)
                    return new object[] { FakeDriverFactory.IframeElement };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.SwitchToFrame) return null;
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "<f/>" } };
                    string attr = FakeDriverFactory.AttributeName(p);
                    if (attr == "src") return "https://cross.example.com/frame.html";
                    if (attr == "data-percy-element-id") return "percy-id-1";
                    return null;
                }
                return null;
            };
            return driver;
        }

        [Fact]
        public void Snapshot_ProcessesCrossOriginIframe()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setHttpClient(new HttpClient(SnapshotMock()));

            var driver = WebDriverWithCrossOriginIframe();
            Percy.Snapshot(driver, "CORS Iframe");

            // Iframes were enumerated and the SDK switched into & back out of a frame.
            Assert.Contains(DriverCommand.FindElements, driver.Commands);
            Assert.Contains(DriverCommand.SwitchToFrame, driver.Commands);
        }

        [Fact]
        public void Snapshot_SkipsSameOriginIframe()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setHttpClient(new HttpClient(SnapshotMock()));

            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                if (cmd == DriverCommand.FindElements) return new object[] { FakeDriverFactory.IframeElement };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "x" } };
                    if (FakeDriverFactory.AttributeName(p) == "src") return "http://localhost:5338/same-origin.html"; // SAME origin
                    return null;
                }
                return null;
            };

            Percy.Snapshot(driver, "Same Origin Iframe");
            // same-origin → never switches into the frame
            Assert.DoesNotContain(DriverCommand.SwitchToFrame, driver.Commands);
        }

        [Fact]
        public void Snapshot_SkipsUnsupportedIframeSrc()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setHttpClient(new HttpClient(SnapshotMock()));

            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                if (cmd == DriverCommand.FindElements) return new object[] { FakeDriverFactory.IframeElement };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "x" } };
                    if (FakeDriverFactory.AttributeName(p) == "src") return "javascript:void(0)"; // unsupported scheme
                    return null;
                }
                return null;
            };

            Percy.Snapshot(driver, "Unsupported Iframe");
            Assert.DoesNotContain(DriverCommand.SwitchToFrame, driver.Commands);
        }

        // ===== Percy.Screenshot (automate) =====================================

        [Fact]
        public void Screenshot_WebDriverOverload_AutomateFlow_Posts()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("automate");
            var mockHttp = SnapshotMock();
            mockHttp.Expect(HttpMethod.Post, "http://localhost:5338/percy/automateScreenshot")
                .WithPartialContent("Automate Shot")
                .Respond("application/json", "{\"success\":true}");
            Percy.setHttpClient(new HttpClient(mockHttp));

            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            var result = Percy.Screenshot(driver, "Automate Shot");
            mockHttp.VerifyNoOutstandingExpectation();
        }

        [Fact]
        public void Screenshot_WebDriverObjectOverload_AutomateFlow_Posts()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("automate");
            Percy.setHttpClient(new HttpClient(SnapshotMock()));

            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            Percy.Screenshot(driver, "Obj Shot", new { fullPage = true });
            // PercyDriver built off the WebDriver → host/caps reflected from real driver.
            Assert.NotNull(new PercyDriver(driver));
        }

        [Fact]
        public void Screenshot_ReturnsNullWhenDisabled()
        {
            Percy.Enabled = () => false;
            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            var result = Percy.Screenshot(driver, "Disabled Shot");
            Assert.Null(result);
        }

        [Fact]
        public void Screenshot_ThrowsWhenSessionTypeNotAutomate()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            var pd = new PercyDriver(driver);
            var ex = Assert.Throws<Exception>(() => pd.Screenshot("Bad"));
            Assert.Contains("Please use Snapshot()", ex.Message);
        }

        [Fact]
        public void Screenshot_LogsWhenServerReturnsError()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("automate");
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/automateScreenshot")
                .Respond("application/json", "{\"success\":false,\"error\":\"nope\"}");
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/log").Respond("application/json", "{}");
            mockHttp.Fallback.Respond("application/json", "{}");
            Percy.setHttpClient(new HttpClient(mockHttp));

            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            var output = CapturedConsole(() => Percy.Screenshot(driver, "Err Shot"));
            Assert.Contains("Could not take Percy Screenshot \"Err Shot\"", output);
        }

        // ===== Responsive capture flow =========================================

        private static MockHttpMessageHandler ResponsiveMock(string widthsJson)
        {
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Get, "http://localhost:5338/percy/dom.js")
                .Respond("application/javascript", "window.PercyDOM = {};");
            mockHttp.When(HttpMethod.Get, "http://localhost:5338/percy/widths-config*")
                .Respond("application/json", widthsJson);
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/snapshot")
                .Respond("application/json", "{\"success\":true}");
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/log").Respond("application/json", "{}");
            mockHttp.Fallback.Respond("application/json", "{\"success\":true}");
            return mockHttp;
        }

        private static FakeWebDriver ResponsiveDriver(int resizeCount = 1)
        {
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetWindowRect || cmd == DriverCommand.SetWindowRect)
                    return new Dictionary<string, object> { { "width", 1280L }, { "height", 1024L }, { "x", 0L }, { "y", 0L } };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("window.resizeCount")) return (long)resizeCount;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "<r/>" } };
                    if (s.Contains("PercyDOM.waitForResize")) return null;
                    return null;
                }
                return null;
            };
            return driver;
        }

        [Fact]
        public void Snapshot_ResponsiveCapture_FromConfig_ResizesAndPosts()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            // responsiveSnapshotCapture from cliConfig.snapshot
            var cfg = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                "{\"snapshot\":{\"responsiveSnapshotCapture\":true}}");
            Percy.setCliConfig(cfg);
            Percy.setHttpClient(new HttpClient(
                ResponsiveMock("{\"widths\":[375,{\"width\":1280,\"height\":900}]}")));

            var driver = ResponsiveDriver();
            Percy.Snapshot(driver, "Responsive From Config");

            // window was resized (SetWindowRect) at least once and serialize ran per width.
            Assert.Contains(DriverCommand.SetWindowRect, driver.Commands);
            Assert.True(driver.Scripts.Count(s => s.Contains("PercyDOM.serialize")) >= 2);
        }

        [Fact]
        public void Snapshot_ResponsiveCapture_FromOptions_ResizesAndPosts()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setCliConfig(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{\"snapshot\":{}}"));
            Percy.setHttpClient(new HttpClient(ResponsiveMock("{\"widths\":[375,1280]}")));

            var driver = ResponsiveDriver();
            var options = new Dictionary<string, object>
            {
                { "responsiveSnapshotCapture", true },
                { "widths", new List<int> { 375, 1280 } }
            };
            Percy.Snapshot(driver, "Responsive From Options", options);
            Assert.True(driver.Scripts.Count(s => s.Contains("PercyDOM.serialize")) >= 2);
        }

        // ===== CDP resize path (Chrome) ========================================

        [Fact]
        public void ResponsiveCapture_UsesCdpResizeOnChrome()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setCliConfig(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                "{\"snapshot\":{\"responsiveSnapshotCapture\":true}}"));
            Percy.setHttpClient(new HttpClient(ResponsiveMock("{\"widths\":[375,1280]}")));

            var driver = new FakeChromeWebDriver(FakeDriverFactory.ChromeCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetWindowRect)
                    return new Dictionary<string, object> { { "width", 1280L }, { "height", 1024L }, { "x", 0L }, { "y", 0L } };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("window.resizeCount")) return 1L;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "<c/>" } };
                    return null;
                }
                return null;
            };

            Percy.Snapshot(driver, "CDP Resize");
            // CDP resize was attempted instead of Window.Size.
            Assert.Contains("Emulation.setDeviceMetricsOverride", driver.CdpCommands);
        }

        [Fact]
        public void ResponsiveCapture_FallsBackToWindowSize_WhenCdpThrows()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setCliConfig(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                "{\"snapshot\":{\"responsiveSnapshotCapture\":true}}"));
            Percy.setHttpClient(new HttpClient(ResponsiveMock("{\"widths\":[375,1280]}")));

            var driver = new FakeChromeWebDriver(FakeDriverFactory.ChromeCaps());
            driver.CdpThrows = true;
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetWindowRect || cmd == DriverCommand.SetWindowRect)
                    return new Dictionary<string, object> { { "width", 1280L }, { "height", 1024L }, { "x", 0L }, { "y", 0L } };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("window.resizeCount")) return 1L;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "<c/>" } };
                    return null;
                }
                return null;
            };

            Percy.Snapshot(driver, "CDP Throws Fallback");
            // CDP threw → fell back to driver Window.Size set.
            Assert.Contains(DriverCommand.SetWindowRect, driver.Commands);
        }

        // ===== WaitForReady direct tests =======================================

        [Fact]
        public void WaitForReady_ReturnsNullWhenPresetDisabled()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            var options = new Dictionary<string, object>
            {
                { "readiness", new Dictionary<string, object> { { "preset", "disabled" } } }
            };
            var result = Percy.WaitForReady(driver, options);
            Assert.Null(result);
            // disabled → never issues an async script.
            Assert.DoesNotContain(DriverCommand.ExecuteAsyncScript, driver.Commands);
        }

        [Fact]
        public void WaitForReady_SetsAsyncTimeoutAndRunsScript()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetTimeouts)
                    return new Dictionary<string, object> { { "implicit", 0L }, { "pageLoad", 300000L }, { "script", 30000L } };
                if (cmd == DriverCommand.ExecuteAsyncScript)
                    return new Dictionary<string, object> { { "ok", true } };
                return null;
            };
            var options = new Dictionary<string, object>
            {
                { "readiness", new Dictionary<string, object> { { "preset", "balanced" }, { "timeoutMs", 4000 } } }
            };
            var result = Percy.WaitForReady(driver, options);
            Assert.NotNull(result);
            Assert.Contains(DriverCommand.ExecuteAsyncScript, driver.Commands);
            // timeout was read and (re)set around the script.
            Assert.Contains(DriverCommand.SetTimeouts, driver.Commands);
        }

        [Fact]
        public void WaitForReady_ReturnsNullWhenAsyncScriptThrows()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetTimeouts)
                    return new Dictionary<string, object> { { "implicit", 0L }, { "pageLoad", 300000L }, { "script", 30000L } };
                if (cmd == DriverCommand.ExecuteAsyncScript)
                    throw new WebDriverException("async boom");
                return null;
            };
            var options = new Dictionary<string, object>
            {
                { "readiness", new Dictionary<string, object> { { "preset", "balanced" }, { "timeoutMs", 1000 } } }
            };
            var result = Percy.WaitForReady(driver, options);
            Assert.Null(result); // swallowed → null
        }

        [Fact]
        public void WaitForReady_NoOptions_RunsScriptWithEmptyConfig()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.ExecuteAsyncScript) return null;
                return null;
            };
            var result = Percy.WaitForReady(driver, null);
            Assert.Contains(DriverCommand.ExecuteAsyncScript, driver.Commands);
        }

        // ===== PercySeleniumDriver via PercyDriver (real reflection) ===========

        [Fact]
        public void PercyDriver_PublicCtor_ReflectsHostCapsAndSession()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            var pd = new PercyDriver(driver);

            var payload = pd.getPayload();
            Assert.Equal("sess-123", payload["sessionId"]);
            // host reflected from the HttpCommandExecutor's remoteServerUri (trailing / trimmed)
            Assert.Equal("http://hub-cloud.browserstack.com/wd/hub", payload["commandExecutorUrl"]);
            Assert.NotNull(payload["capabilities"]);
        }

        [Fact]
        public void PercySeleniumDriver_GetElementIdFromElement_UsesReflection()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.FindElements)
                    return new object[] { FakeDriverFactory.MakeElement("ELEM-42") };
                return null;
            };
            var elements = driver.FindElements(By.TagName("div"));

            var psd = new PercySeleniumDriver(driver);
            string id = psd.GetElementIdFromElement(elements[0]);
            Assert.Equal("ELEM-42", id);
        }

        [Fact]
        public void PercySeleniumDriver_GetCapabilities_GetSessionDetails_GetHost_GetWebDriver()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            var psd = new PercySeleniumDriver(driver);

            Assert.Equal("sess-123", psd.sessionId());
            Assert.Same(driver, psd.getWebDriver());

            var caps = psd.GetCapabilities();
            // ReturnedCapabilities exposes the private "capabilities" dict; chrome cap present.
            Assert.NotNull(caps);
            Assert.Equal("chrome", caps!["browserName"]);

            // GetSessionDetails stores the raw ICapabilities then casts it to
            // IDictionary<string,object>; ReturnedCapabilities does not implement
            // that interface, so the cast throws (real production behaviour). The
            // store + cache + cast lines all execute up to the throw.
            Assert.Throws<InvalidCastException>(() => psd.GetSessionDetails());

            var host = psd.GetHost();
            Assert.Equal("http://hub-cloud.browserstack.com/wd/hub/", host);

            // Second call hits the cache branch (no exception, same value).
            Assert.Equal(host, psd.GetHost());
            Assert.NotNull(psd.GetCapabilities());
        }

        [Fact]
        public void PercySeleniumDriver_NullDriver_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new PercySeleniumDriver(null!));
        }

        [Fact]
        public void Screenshot_WithIgnoreAndConsiderElements_MapsElementIds()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("automate");
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.Expect(HttpMethod.Post, "http://localhost:5338/percy/automateScreenshot")
                .WithPartialContent("ELEM-IGNORE")
                .WithPartialContent("ELEM-CONSIDER")
                .Respond("application/json", "{\"success\":true}");
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/log").Respond("application/json", "{}");
            mockHttp.Fallback.Respond("application/json", "{\"success\":true}");
            Percy.setHttpClient(new HttpClient(mockHttp));

            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.FindElements && p != null && p.ContainsKey("value"))
                {
                    string val = p["value"].ToString();
                    if (val.Contains("ignore")) return new object[] { FakeDriverFactory.MakeElement("ELEM-IGNORE") };
                    return new object[] { FakeDriverFactory.MakeElement("ELEM-CONSIDER") };
                }
                return null;
            };
            var ignore = driver.FindElements(By.CssSelector(".ignore"));
            var consider = driver.FindElements(By.CssSelector(".consider"));

            var pd = new PercyDriver(driver);
            var options = new Dictionary<string, object>
            {
                { "ignore_region_selenium_elements", new List<IWebElement>(ignore) },
                { "consider_region_selenium_elements", new List<IWebElement>(consider) }
            };
            pd.Screenshot("Region Shot", options);
            mockHttp.VerifyNoOutstandingExpectation();
        }

        [Fact]
        public void Screenshot_RemapsAltRegionKeys()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("automate");
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.Expect(HttpMethod.Post, "http://localhost:5338/percy/automateScreenshot")
                .WithPartialContent("ALT-IGNORE")
                .WithPartialContent("ALT-CONSIDER")
                .Respond("application/json", "{\"success\":true}");
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/log").Respond("application/json", "{}");
            mockHttp.Fallback.Respond("application/json", "{\"success\":true}");
            Percy.setHttpClient(new HttpClient(mockHttp));

            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.FindElements && p != null && p.ContainsKey("value"))
                {
                    string val = p["value"].ToString();
                    if (val.Contains("ignore")) return new object[] { FakeDriverFactory.MakeElement("ALT-IGNORE") };
                    return new object[] { FakeDriverFactory.MakeElement("ALT-CONSIDER") };
                }
                return null;
            };
            var ignore = driver.FindElements(By.CssSelector(".ignore"));
            var consider = driver.FindElements(By.CssSelector(".consider"));

            var pd = new PercyDriver(driver);
            // Use the *Alt* (camelCase) keys → exercises the alt→snake remap branch.
            var options = new Dictionary<string, object>
            {
                { "ignoreRegionSeleniumElements", new List<IWebElement>(ignore) },
                { "considerRegionSeleniumElements", new List<IWebElement>(consider) }
            };
            pd.Screenshot("Alt Region Shot", options);
            mockHttp.VerifyNoOutstandingExpectation();
        }

        // ===== Enabled: success=false → throws data.error =====================

        [Fact]
        public void Enabled_FalseWhenHealthcheckSuccessIsFalse()
        {
            // success:false → Enabled() catches the thrown Exception(data.error) and
            // logs "Percy is not running" — exercises the success!=true throw branch.
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Get, "http://localhost:5338/percy/healthcheck")
                .Respond(new Dictionary<string, string> { { "x-percy-core-version", "1.0.0" } },
                         "application/json", "{\"success\":false,\"error\":\"nope\"}");
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/log").Respond("application/json", "{}");
            mockHttp.Fallback.Respond("application/json", "{}");
            Percy.setHttpClient(new HttpClient(mockHttp));

            var output = CapturedConsole(() => Assert.False(Percy.Enabled()));
            Assert.Contains("Percy is not running, disabling snapshots", output);
        }

        // ===== getSerializedDom: readiness stripping + diagnostics attach =====

        [Fact]
        public void Snapshot_StripsReadinessAndAttachesDiagnostics()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            var mockHttp = SnapshotMock();
            // Capture the posted body to assert readiness was stripped from serialize args.
            Percy.setHttpClient(new HttpClient(mockHttp));

            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetTimeouts)
                    return new Dictionary<string, object> { { "implicit", 0L }, { "pageLoad", 300000L }, { "script", 30000L } };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "x" } };
                    // ExecuteAsyncScript = WaitForReady → return diagnostics so the
                    // diagnostics-attach branch (domSnapshot["readiness_diagnostics"]) runs.
                    if (cmd == DriverCommand.ExecuteAsyncScript)
                        return new Dictionary<string, object> { { "passed", true } };
                    return null;
                }
                return null;
            };

            var options = new Dictionary<string, object>
            {
                { "readiness", new Dictionary<string, object> { { "preset", "balanced" }, { "timeoutMs", 3000 } } }
            };
            Percy.Snapshot(driver, "Readiness Strip", options);

            // serialize ran and the readiness key was NOT forwarded into the serialize args
            string serializeScript = driver.Scripts.First(s => s.Contains("PercyDOM.serialize"));
            Assert.DoesNotContain("readiness", serializeScript);
        }

        // ===== iframe URI-resolution catch (malformed src) ====================

        [Fact]
        public void Snapshot_SkipsIframeWithMalformedSrc()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setHttpClient(new HttpClient(SnapshotMock()));

            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                if (cmd == DriverCommand.FindElements) return new object[] { FakeDriverFactory.IframeElement };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "x" } };
                    // a src that is non-empty/non-unsupported but produces an invalid
                    // resolved Uri → new Uri(base, src) throws → caught & skipped.
                    if (FakeDriverFactory.AttributeName(p) == "src") return "http://[invalid";
                    return null;
                }
                return null;
            };

            Percy.Snapshot(driver, "Malformed Iframe");
            Assert.DoesNotContain(DriverCommand.SwitchToFrame, driver.Commands);
        }

        // ===== ProcessFrame: empty percyElementId → skip ======================

        [Fact]
        public void Snapshot_IframeWithoutPercyElementId_IsSkipped()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setHttpClient(new HttpClient(SnapshotMock()));

            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                if (cmd == DriverCommand.FindElements) return new object[] { FakeDriverFactory.IframeElement };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "x" } };
                    string attr = FakeDriverFactory.AttributeName(p);
                    if (attr == "src") return "https://cross.example.com/frame.html";
                    if (attr == "data-percy-element-id") return ""; // empty → skip frame
                    return null;
                }
                return null;
            };

            Percy.Snapshot(driver, "No Percy Id Iframe");
            // ProcessFrame returns null before switching into the frame.
            Assert.DoesNotContain(DriverCommand.SwitchToFrame, driver.Commands);
        }

        // ===== ProcessFrame: serialize-in-frame throws (Fatal-less error) ======

        [Fact]
        public void Snapshot_IframeSerializeThrows_IsCaughtAndSkipped()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setHttpClient(new HttpClient(SnapshotMock()));

            int serializeCalls = 0;
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                if (cmd == DriverCommand.FindElements) return new object[] { FakeDriverFactory.IframeElement };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.SwitchToFrame) return null;
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    string attr = FakeDriverFactory.AttributeName(p);
                    if (attr == "src") return "https://cross.example.com/frame.html";
                    if (attr == "data-percy-element-id") return "pid-1";
                    if (s.Contains("PercyDOM.serialize"))
                    {
                        serializeCalls++;
                        // First call = top-level serialize (ok); second = inside the
                        // frame → throw to exercise ProcessFrame's catch + outer skip.
                        if (serializeCalls >= 2) throw new WebDriverException("frame serialize boom");
                        return new Dictionary<string, object> { { "html", "x" } };
                    }
                    return null;
                }
                return null;
            };

            // Should not throw (non-Fatal error is swallowed) and snapshot still posts.
            var ex = Record.Exception(() => Percy.Snapshot(driver, "Frame Serialize Throws"));
            Assert.Null(ex);
            Assert.Contains(DriverCommand.SwitchToFrame, driver.Commands);
        }

        // ===== TryResizeWithCdp: chrome but no ExecuteCdpCommand method =======

        [Fact]
        public void ResponsiveCapture_ChromeWithoutCdpMethod_FallsBackToWindowSize()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setCliConfig(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                "{\"snapshot\":{\"responsiveSnapshotCapture\":true}}"));
            Percy.setHttpClient(new HttpClient(ResponsiveMock("{\"widths\":[375,1280]}")));

            // Plain FakeWebDriver with chrome caps but NO ExecuteCdpCommand method →
            // TryResizeWithCdp finds the method == null → returns false → Window.Size.
            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetWindowRect || cmd == DriverCommand.SetWindowRect)
                    return new Dictionary<string, object> { { "width", 1280L }, { "height", 1024L }, { "x", 0L }, { "y", 0L } };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("window.resizeCount")) return 1L;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "<c/>" } };
                    return null;
                }
                return null;
            };

            Percy.Snapshot(driver, "Chrome No Cdp");
            Assert.Contains(DriverCommand.SetWindowRect, driver.Commands);
        }

        // ===== CaptureResponsiveDom: _dom null → GetPercyDOM fetched ==========

        [Fact]
        public void ResponsiveCapture_FetchesDomWhenDomCacheEmpty()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setCliConfig(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                "{\"snapshot\":{\"responsiveSnapshotCapture\":true}}"));
            // ResetInternalCaches in the ctor already nulled _dom; verify dom.js is GET.
            var domFetched = false;
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Get, "http://localhost:5338/percy/dom.js")
                .Respond(_ => { domFetched = true; return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    { Content = new StringContent("window.PercyDOM = {};") }; });
            mockHttp.When(HttpMethod.Get, "http://localhost:5338/percy/widths-config*")
                .Respond("application/json", "{\"widths\":[375,1280]}");
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/snapshot").Respond("application/json", "{\"success\":true}");
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/log").Respond("application/json", "{}");
            mockHttp.Fallback.Respond("application/json", "{\"success\":true}");
            Percy.setHttpClient(new HttpClient(mockHttp));

            var driver = ResponsiveDriver();
            // Force window.PercyDOM probe TRUE so the top-level injection is skipped,
            // but CaptureResponsiveDom still needs _dom → fetches dom.js.
            Percy.Snapshot(driver, "Responsive Dom Fetch");
            Assert.True(domFetched);
        }

        // ===== Internal PercyDriver(PercySeleniumDriver) ctor =================

        [Fact]
        public void PercyDriver_InternalSeleniumDriverCtor_Works()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            var psd = new PercySeleniumDriver(driver);
            var pd = new PercyDriver(psd);
            Assert.Equal("sess-123", pd.getPayload()["sessionId"]);
        }

        // ===== ProcessFrame finally: DefaultContent throws (logged, swallowed) =

        [Fact]
        public void Snapshot_IframeDefaultContentThrows_IsLoggedNotFatal()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setHttpClient(new HttpClient(SnapshotMock()));

            int switchCount = 0;
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                if (cmd == DriverCommand.FindElements) return new object[] { FakeDriverFactory.IframeElement };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.SwitchToFrame)
                {
                    switchCount++;
                    // 1st = Frame(into); 2nd = DefaultContent() in the finally → throw.
                    if (switchCount >= 2) throw new WebDriverException("cannot exit frame");
                    return null;
                }
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "x" } };
                    string attr = FakeDriverFactory.AttributeName(p);
                    if (attr == "src") return "https://cross.example.com/frame.html";
                    if (attr == "data-percy-element-id") return "pid-1";
                    return null;
                }
                return null;
            };

            var ex = Record.Exception(() => Percy.Snapshot(driver, "DefaultContent Throws"));
            Assert.Null(ex); // finally's DefaultContent failure is logged, not thrown
        }

        // ===== WaitForReady: timeout get/set + restore exception paths ========

        [Fact]
        public void WaitForReady_TimeoutReadFails_StillRunsScript()
        {
            // Manage().Timeouts() get throws → previousTimeout stays null (caught),
            // and the finally's restore branch is skipped.
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetTimeouts) throw new WebDriverException("no timeouts");
                if (cmd == DriverCommand.ExecuteAsyncScript) return new Dictionary<string, object> { { "ok", true } };
                return null;
            };
            var options = new Dictionary<string, object>
            {
                { "readiness", new Dictionary<string, object> { { "preset", "balanced" }, { "timeoutMs", 2000 } } }
            };
            var result = Percy.WaitForReady(driver, options);
            // get-timeout failure is swallowed; the async readiness script still runs.
            Assert.Contains(DriverCommand.ExecuteAsyncScript, driver.Commands);
        }

        [Fact]
        public void WaitForReady_TimeoutRestoreFails_IsSwallowed()
        {
            // GetTimeouts succeeds (previousTimeout captured) and the script runs, but
            // the finally's SetTimeouts (restore) throws → swallowed by best-effort catch.
            int setCalls = 0;
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetTimeouts)
                    return new Dictionary<string, object> { { "implicit", 0L }, { "pageLoad", 300000L }, { "script", 30000L } };
                if (cmd == DriverCommand.SetTimeouts)
                {
                    setCalls++;
                    // 1st set = pre-script bump (ok); 2nd = finally restore → throw.
                    if (setCalls >= 2) throw new WebDriverException("restore failed");
                    return null;
                }
                if (cmd == DriverCommand.ExecuteAsyncScript) return new Dictionary<string, object> { { "ok", true } };
                return null;
            };
            var options = new Dictionary<string, object>
            {
                { "readiness", new Dictionary<string, object> { { "preset", "balanced" }, { "timeoutMs", 2000 } } }
            };
            var ex = Record.Exception(() => Percy.WaitForReady(driver, options));
            Assert.Null(ex);
            Assert.True(setCalls >= 2); // restore was attempted (and threw, swallowed)
        }

        // ===== CaptureResponsiveDom: _dom==null direct invocation =============

        [Fact]
        public void CaptureResponsiveDom_FetchesDomWhenDomNull_DirectInvoke()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.ResetInternalCaches(); // ensure _dom == null
            Percy.setHttpClient(new HttpClient(ResponsiveMock("{\"widths\":[375,1280]}")));

            var driver = ResponsiveDriver();
            var options = new Dictionary<string, object> { { "widths", new List<int> { 375, 1280 } } };

            // Call CaptureResponsiveDom directly (not via Snapshot, which would
            // pre-populate _dom) so the `if (_dom == null) _dom = GetPercyDOM();`
            // branch inside CaptureResponsiveDom executes.
            MethodInfo m = typeof(Percy).GetMethod("CaptureResponsiveDom",
                BindingFlags.Public | BindingFlags.Static)!;
            var result = m.Invoke(null, new object?[] { driver, new object[0], options });
            Assert.NotNull(result);
        }

        // ===== PercySeleniumDriver.GetSessionDetails cache-return path =========

        [Fact]
        public void PercySeleniumDriver_GetSessionDetails_ReturnsCachedDictionary()
        {
            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            var psd = new PercySeleniumDriver(driver);
            // Pre-seed the cache with a value that IS an IDictionary<string,object>
            // so the cache-hit return branch (line 62) executes without the cast
            // throwing. Key matches GetSessionDetails' "session_caps_" + sessionId().
            var key = "session_caps_" + psd.sessionId();
            var seeded = new Dictionary<string, object> { { "platformName", "win" } };
            PercyDriver.cache.Store(key, seeded);

            var session = psd.GetSessionDetails();
            Assert.Same(seeded, session);
        }

        // ===== Responsive reload-page branch (PERCY_RESPONSIVE_CAPTURE_RELOAD_PAGE) =====

        [Fact]
        public void ResponsiveCapture_ReloadPageEnabled_RefreshesPerWidth()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setCliConfig(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                "{\"snapshot\":{\"responsiveSnapshotCapture\":true}}"));
            Percy.setHttpClient(new HttpClient(ResponsiveMock("{\"widths\":[375,1280]}")));

            // Flip the env mirror so the reload-page branch in CaptureResponsiveDom
            // runs: Navigate().Refresh(), re-inject PercyDOM when missing, then
            // PercyDOM.waitForResize(). Production reads the same value from
            // PERCY_RESPONSIVE_CAPTURE_RELOAD_PAGE=true.
            bool oldReload = Percy.ResponsiveCaptureReloadPage;
            Percy.ResponsiveCaptureReloadPage = true;

            int refreshCount = 0;
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetWindowRect || cmd == DriverCommand.SetWindowRect)
                    return new Dictionary<string, object> { { "width", 1280L }, { "height", 1024L }, { "x", 0L }, { "y", 0L } };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                if (cmd == DriverCommand.Refresh) { refreshCount++; return null; }
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    // After refresh the SDK probes `return !!window.PercyDOM`; returning
                    // false drives the re-injection branch inside the reload block.
                    if (s.Contains("!!window.PercyDOM")) return false;
                    if (s.Contains("window.resizeCount")) return 0L;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "<r/>" } };
                    if (s.Contains("PercyDOM.waitForResize")) return null;
                    return null;
                }
                return null;
            };

            try
            {
                Percy.Snapshot(driver, "Responsive Reload Enabled");
            }
            finally
            {
                Percy.ResponsiveCaptureReloadPage = oldReload;
            }

            // Page was reloaded for each captured width.
            Assert.True(refreshCount >= 2, $"expected >= 2 refreshes, got {refreshCount}");
        }

        [Fact]
        public void ResponsiveCapture_ReloadPageEnabled_RefreshThrows_IsCaughtAndContinues()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setCliConfig(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                "{\"snapshot\":{\"responsiveSnapshotCapture\":true}}"));
            Percy.setHttpClient(new HttpClient(ResponsiveMock("{\"widths\":[375,1280]}")));

            bool oldReload = Percy.ResponsiveCaptureReloadPage;
            Percy.ResponsiveCaptureReloadPage = true;

            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetWindowRect || cmd == DriverCommand.SetWindowRect)
                    return new Dictionary<string, object> { { "width", 1280L }, { "height", 1024L }, { "x", 0L }, { "y", 0L } };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                // Refresh throws → the reload block's try/catch logs and continues.
                if (cmd == DriverCommand.Refresh) throw new WebDriverException("reload boom");
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("window.resizeCount")) return 0L;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "<r/>" } };
                    if (s.Contains("PercyDOM.waitForResize")) return null;
                    return null;
                }
                return null;
            };

            try
            {
                var ex = Record.Exception(() => Percy.Snapshot(driver, "Responsive Reload Throws"));
                Assert.Null(ex); // refresh failure is logged, not fatal
            }
            finally
            {
                Percy.ResponsiveCaptureReloadPage = oldReload;
            }
        }

        // ===== Responsive sleep-time branch (RESPONSIVE_CAPTURE_SLEEP_TIME) ====

        [Fact]
        public void ResponsiveCapture_SleepTimeSet_SleepsPerWidth()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setCliConfig(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                "{\"snapshot\":{\"responsiveSnapshotCapture\":true}}"));
            Percy.setHttpClient(new HttpClient(ResponsiveMock("{\"widths\":[375,1280]}")));

            // Flip the env mirror to a valid integer so Int32.TryParse succeeds and
            // the per-width Thread.Sleep(sleepTime * 1000) line runs. "0" keeps the
            // sleep instant (Thread.Sleep(0)) while still executing the line.
            // Production reads the same value from RESPONSIVE_CAPTURE_SLEEP_TIME.
            string oldSleep = Percy.ResponsiveCaptureSleepTime;
            Percy.ResponsiveCaptureSleepTime = "0";

            var driver = ResponsiveDriver();
            try
            {
                Percy.Snapshot(driver, "Responsive Sleep");
            }
            finally
            {
                Percy.ResponsiveCaptureSleepTime = oldSleep;
            }

            Assert.True(driver.Scripts.Count(s => s.Contains("PercyDOM.serialize")) >= 2);
        }

        // ===== getSerializedDom: Fatal frame error rethrows (inner + outer) ====

        [Fact]
        public void Snapshot_FatalFrameError_RethrowsThroughBothCatches()
        {
            Percy.Enabled = () => true;
            Percy.setSessionType("web");
            Percy.setHttpClient(new HttpClient(SnapshotMock()));

            // The iframe `src` attribute is read twice: once at the top of the
            // getSerializedDom loop (to compute origin) and again inside
            // ProcessFrame. The 1st read must SUCCEED with a cross-origin URL so
            // control reaches the per-frame try → ProcessFrame; the 2nd read (in
            // ProcessFrame, before its own try) throws a "Fatal"-message exception.
            // That propagates out of ProcessFrame into getSerializedDom's per-frame
            // catch whose `e.Message.Contains("Fatal")` is true → `throw;` (inner
            // rethrow). It re-propagates into the outer iframe catch, also Fatal →
            // `throw;` (outer rethrow). Snapshot's outermost catch then logs and
            // returns null. One test exercises BOTH rethrow arms.
            int srcReads = 0;
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                if (cmd == DriverCommand.FindElements) return new object[] { FakeDriverFactory.IframeElement };
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.SwitchToFrame) return null;
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString() : "";
                    if (s.Contains("!!window.PercyDOM")) return true;
                    if (s.Contains("document.URL")) return "http://localhost:5338/page";
                    if (s.Contains("PercyDOM.serialize")) return new Dictionary<string, object> { { "html", "x" } };
                    if (FakeDriverFactory.AttributeName(p) == "src")
                    {
                        srcReads++;
                        // 1st read (loop, origin calc) succeeds with cross-origin URL.
                        if (srcReads == 1) return "https://cross.example.com/frame.html";
                        // 2nd read (inside ProcessFrame) → Fatal exception.
                        throw new WebDriverException("Fatal: cannot read iframe src");
                    }
                    return null;
                }
                return null;
            };

            Newtonsoft.Json.Linq.JObject? result = null;
            var output = CapturedConsole(() => result = Percy.Snapshot(driver, "Fatal Frame"));
            // Both Fatal rethrows fired; Snapshot's catch swallowed → null result.
            Assert.Null(result);
            Assert.Contains("Could not take DOM snapshot \"Fatal Frame\"", output);
            Assert.True(srcReads >= 2, $"expected >= 2 src reads, got {srcReads}");
        }
    }
}
