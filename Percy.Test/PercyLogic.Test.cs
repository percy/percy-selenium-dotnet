using Xunit;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using RichardSzalay.MockHttp;
using OpenQA.Selenium.Chrome;

namespace PercyIO.Selenium.Tests
{
    // Non-driver unit tests for the pure-logic / HTTP-wrapper parts of the
    // Percy SDK. These exercise:
    //   * private static helpers via reflection (GetOrigin, IsUnsupportedIframeSrc,
    //     TryToLong, JsonElementToObject, ResolveReadinessConfig,
    //     ResolveConfiguredMinHeight, ResolveResponsiveTargetHeight,
    //     isResponsiveSnapshotCapture, GetPercyDOM, GetResponsiveWidths, Log,
    //     PayloadParser, IsChromeBrowser)
    //   * the public/internal surface that does not require a live WebDriver
    //     (Enabled, CreateRegion, ResetInternalCaches, setCliConfig,
    //     setEligibleWidths, getHttpClient, the object-overload Snapshot/Screenshot
    //     reflection mapping)
    //
    // None of these spin up Firefox/Selenium; HTTP is mocked with MockHttp and the
    // CLI config state is injected through Percy.setCliConfig.
    //
    // NOTE: kept in a class WITHOUT IClassFixture<TestsFixture> so the live-driver
    // filter (FullyQualifiedName!~UnitTests) does not exclude it and, conversely,
    // these run without a browser.
    [Collection("HttpClientStateSerial")]
    public class PercyLogicTest : IDisposable
    {
        private readonly Func<bool> _oldEnabled;

        public PercyLogicTest()
        {
            // Snapshot the Enabled func so per-test overrides cannot leak.
            _oldEnabled = Percy.Enabled;
            Percy.ResetInternalCaches();
        }

        public void Dispose()
        {
            Percy.Enabled = _oldEnabled;
            Percy.ResetInternalCaches();
            // Reset the static HTTP client to a clean default so a mocked handler
            // from one test does not leak into another / into driver tests.
            Percy.setHttpClient(new HttpClient());
            Percy.setSessionType(null);
        }

        // ---- reflection helpers ------------------------------------------------

        private static readonly Type PercyType = typeof(Percy);

        private static object? InvokePrivate(string name, params object?[] args)
        {
            MethodInfo? m = PercyType.GetMethod(name,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(m);
            return m!.Invoke(null, args);
        }

        // Log<T> is a generic method; close it over string for invocation.
        private static void InvokeLog(string message, string lvl)
        {
            MethodInfo open = PercyType.GetMethod("Log",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            MethodInfo closed = open.MakeGenericMethod(typeof(string));
            closed.Invoke(null, new object?[] { message, lvl });
        }

        private static void SetCliConfigJson(string json)
        {
            JsonElement el = JsonSerializer.Deserialize<JsonElement>(json);
            Percy.setCliConfig(el);
        }

        private static JsonElement ParseJson(string json) =>
            JsonSerializer.Deserialize<JsonElement>(json);

        // ===== GetOrigin =======================================================

        [Theory]
        [InlineData("https://example.com/foo/bar?x=1", "https://example.com")]
        [InlineData("http://localhost:5338/test", "http://localhost:5338")]
        [InlineData("https://user:pass@host.io:8443/p", "https://host.io:8443")]
        public void GetOrigin_ReturnsSchemeAndAuthority(string url, string expected)
        {
            var result = (string)InvokePrivate("GetOrigin", url)!;
            Assert.Equal(expected, result);
        }

        [Fact]
        public void GetOrigin_ReturnsEmptyOnInvalidUrl()
        {
            var result = (string)InvokePrivate("GetOrigin", "not a url")!;
            Assert.Equal("", result);
        }

        // ===== IsUnsupportedIframeSrc =========================================

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("javascript:void(0)", true)]
        [InlineData("JavaScript:alert(1)", true)]
        [InlineData("data:text/html,abc", true)]
        [InlineData("vbscript:msgbox", true)]
        [InlineData("https://example.com/frame.html", false)]
        [InlineData("/relative/path", false)]
        public void IsUnsupportedIframeSrc_DetectsUnsupportedSchemes(string? src, bool expected)
        {
            var result = (bool)InvokePrivate("IsUnsupportedIframeSrc", src)!;
            Assert.Equal(expected, result);
        }

        // ===== TryToLong =======================================================

        [Fact]
        public void TryToLong_ParsesNumericValues()
        {
            object?[] args = { (object)5000, 0L };
            MethodInfo m = PercyType.GetMethod("TryToLong",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            bool ok = (bool)m.Invoke(null, args)!;
            Assert.True(ok);
            Assert.Equal(5000L, (long)args[1]!);
        }

        [Fact]
        public void TryToLong_ParsesStringNumber()
        {
            object?[] args = { (object)"1234", 0L };
            MethodInfo m = PercyType.GetMethod("TryToLong",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            bool ok = (bool)m.Invoke(null, args)!;
            Assert.True(ok);
            Assert.Equal(1234L, (long)args[1]!);
        }

        [Fact]
        public void TryToLong_ReturnsFalseForNull()
        {
            object?[] args = { null, 0L };
            MethodInfo m = PercyType.GetMethod("TryToLong",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            bool ok = (bool)m.Invoke(null, args)!;
            Assert.False(ok);
            Assert.Equal(0L, (long)args[1]!);
        }

        [Fact]
        public void TryToLong_ReturnsFalseForUnconvertible()
        {
            object?[] args = { (object)"not-a-number", 0L };
            MethodInfo m = PercyType.GetMethod("TryToLong",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            bool ok = (bool)m.Invoke(null, args)!;
            Assert.False(ok);
        }

        // ===== JsonElementToObject ============================================

        [Fact]
        public void JsonElementToObject_MapsScalarKinds()
        {
            Assert.Equal("hello", InvokePrivate("JsonElementToObject", ParseJson("\"hello\"")));
            Assert.Equal(42L, InvokePrivate("JsonElementToObject", ParseJson("42")));
            Assert.Equal(3.14, InvokePrivate("JsonElementToObject", ParseJson("3.14")));
            Assert.Equal(true, InvokePrivate("JsonElementToObject", ParseJson("true")));
            Assert.Equal(false, InvokePrivate("JsonElementToObject", ParseJson("false")));
            Assert.Null(InvokePrivate("JsonElementToObject", ParseJson("null")));
        }

        [Fact]
        public void JsonElementToObject_MapsComplexKindsToRawText()
        {
            var arr = (string)InvokePrivate("JsonElementToObject", ParseJson("[1,2,3]"))!;
            Assert.Equal("[1,2,3]", arr);

            var obj = (string)InvokePrivate("JsonElementToObject", ParseJson("{\"a\":1}"))!;
            Assert.Contains("\"a\"", obj);
        }

        // ===== ResolveReadinessConfig =========================================

        [Fact]
        public void ResolveReadinessConfig_EmptyWhenNoConfigOrOptions()
        {
            var merged = (Dictionary<string, object?>)InvokePrivate(
                "ResolveReadinessConfig", new object?[] { null })!;
            Assert.Empty(merged);
        }

        [Fact]
        public void ResolveReadinessConfig_ReadsGlobalCliConfig()
        {
            SetCliConfigJson(
                "{\"snapshot\":{\"readiness\":{\"preset\":\"balanced\",\"timeoutMs\":1500}}}");

            var merged = (Dictionary<string, object?>)InvokePrivate(
                "ResolveReadinessConfig", new object?[] { null })!;

            Assert.Equal("balanced", merged["preset"]);
            Assert.Equal(1500L, merged["timeoutMs"]);
        }

        [Fact]
        public void ResolveReadinessConfig_PerSnapshotOverridesGlobal()
        {
            SetCliConfigJson(
                "{\"snapshot\":{\"readiness\":{\"preset\":\"balanced\",\"timeoutMs\":1500}}}");

            var perSnapshot = new Dictionary<string, object>
            {
                { "readiness", new Dictionary<string, object> { { "preset", "disabled" } } }
            };

            var merged = (Dictionary<string, object?>)InvokePrivate(
                "ResolveReadinessConfig", new object?[] { perSnapshot })!;

            // per-snapshot 'preset' wins; global 'timeoutMs' is still inherited
            Assert.Equal("disabled", merged["preset"]);
            Assert.Equal(1500L, merged["timeoutMs"]);
        }

        // ===== ResolveConfiguredMinHeight =====================================

        [Fact]
        public void ResolveConfiguredMinHeight_FromOptionsInt()
        {
            var options = new Dictionary<string, object> { { "minHeight", 2000 } };
            var result = (int?)InvokePrivate("ResolveConfiguredMinHeight", options);
            Assert.Equal(2000, result);
        }

        [Fact]
        public void ResolveConfiguredMinHeight_FromOptionsParsableString()
        {
            var options = new Dictionary<string, object> { { "minHeight", "1280" } };
            var result = (int?)InvokePrivate("ResolveConfiguredMinHeight", options);
            Assert.Equal(1280, result);
        }

        [Fact]
        public void ResolveConfiguredMinHeight_FromCliConfigWhenOptionsAbsent()
        {
            SetCliConfigJson("{\"snapshot\":{\"minHeight\":900}}");
            var options = new Dictionary<string, object>();
            var result = (int?)InvokePrivate("ResolveConfiguredMinHeight", options);
            Assert.Equal(900, result);
        }

        [Fact]
        public void ResolveConfiguredMinHeight_NullWhenNotConfigured()
        {
            var options = new Dictionary<string, object>();
            var result = (int?)InvokePrivate("ResolveConfiguredMinHeight", options);
            Assert.Null(result);
        }

        [Fact]
        public void ResolveConfiguredMinHeight_NullForUnparsableOptionString()
        {
            var options = new Dictionary<string, object> { { "minHeight", "abc" } };
            var result = (int?)InvokePrivate("ResolveConfiguredMinHeight", options);
            Assert.Null(result);
        }

        [Fact]
        public void ResolveConfiguredMinHeight_NullWhenCliConfigMinHeightNotInt()
        {
            // snapshot.minHeight is a string -> GetInt32() throws inside the try,
            // exercising the cliConfig read exception path which returns null.
            SetCliConfigJson("{\"snapshot\":{\"minHeight\":\"tall\"}}");
            var options = new Dictionary<string, object>();
            var result = (int?)InvokePrivate("ResolveConfiguredMinHeight", options);
            Assert.Null(result);
        }

        // ===== ResolveResponsiveTargetHeight (driver-free branches) ===========

        [Fact]
        public void ResolveResponsiveTargetHeight_ReturnsCurrentWhenEnvDisabled()
        {
            // PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT defaults to false in this env,
            // so the method returns the passed-in currentHeight without touching
            // the (null) driver.
            Assert.False(Percy.PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT);

            var options = new Dictionary<string, object>();
            var result = (int)InvokePrivate(
                "ResolveResponsiveTargetHeight", new object?[] { null, options, 768 })!;
            Assert.Equal(768, result);
        }

        // ===== isResponsiveSnapshotCapture ====================================

        [Fact]
        public void IsResponsiveSnapshotCapture_FalseWhenNoConfig()
        {
            var result = (bool)InvokePrivate(
                "isResponsiveSnapshotCapture", new object?[] { null })!;
            Assert.False(result);
        }

        [Fact]
        public void IsResponsiveSnapshotCapture_FalseWhenDeferUploads()
        {
            SetCliConfigJson(
                "{\"percy\":{\"deferUploads\":true},\"snapshot\":{\"responsiveSnapshotCapture\":true}}");
            var options = new Dictionary<string, object> { { "responsiveSnapshotCapture", true } };
            var result = (bool)InvokePrivate(
                "isResponsiveSnapshotCapture", new object?[] { options })!;
            Assert.False(result);
        }

        [Fact]
        public void IsResponsiveSnapshotCapture_TrueFromOptions()
        {
            SetCliConfigJson("{\"snapshot\":{}}");
            var options = new Dictionary<string, object> { { "responsiveSnapshotCapture", true } };
            var result = (bool)InvokePrivate(
                "isResponsiveSnapshotCapture", new object?[] { options })!;
            Assert.True(result);
        }

        [Fact]
        public void IsResponsiveSnapshotCapture_TrueFromSnapshotConfig()
        {
            SetCliConfigJson("{\"snapshot\":{\"responsiveSnapshotCapture\":true}}");
            var result = (bool)InvokePrivate(
                "isResponsiveSnapshotCapture", new object?[] { null })!;
            Assert.True(result);
        }

        [Fact]
        public void IsResponsiveSnapshotCapture_FalseFromSnapshotConfig()
        {
            SetCliConfigJson("{\"snapshot\":{\"responsiveSnapshotCapture\":false}}");
            var result = (bool)InvokePrivate(
                "isResponsiveSnapshotCapture", new object?[] { null })!;
            Assert.False(result);
        }

        [Fact]
        public void IsResponsiveSnapshotCapture_FalseWhenSnapshotKeyMissing()
        {
            SetCliConfigJson("{\"other\":{}}");
            var result = (bool)InvokePrivate(
                "isResponsiveSnapshotCapture", new object?[] { null })!;
            Assert.False(result);
        }

        [Fact]
        public void IsResponsiveSnapshotCapture_FalseWhenConfigMalformed_HitsCatch()
        {
            // responsiveSnapshotCapture is a string, not a bool -> GetBoolean()
            // throws and the method's catch returns false.
            SetCliConfigJson("{\"snapshot\":{\"responsiveSnapshotCapture\":\"yes\"}}");
            var result = (bool)InvokePrivate(
                "isResponsiveSnapshotCapture", new object?[] { null })!;
            Assert.False(result);
        }

        // ===== PayloadParser ===================================================

        [Fact]
        public void PayloadParser_SerializesObjectWhenNotJson()
        {
            var payload = new Dictionary<string, object> { { "a", 1 } };
            var result = (string)InvokePrivate("PayloadParser", payload, false)!;
            Assert.Equal("{\"a\":1}", result);
        }

        [Fact]
        public void PayloadParser_ReturnsToStringWhenAlreadyJson()
        {
            var jobj = JObject.Parse("{\"k\":\"v\"}");
            var result = (string)InvokePrivate("PayloadParser", jobj, true)!;
            Assert.Contains("\"k\"", result);
        }

        [Fact]
        public void PayloadParser_ReturnsEmptyForNullAlreadyJson()
        {
            var result = (string)InvokePrivate("PayloadParser", new object?[] { null, true })!;
            Assert.Equal("", result);
        }

        // ===== IsChromeBrowser (null branch) ==================================

        [Fact]
        public void IsChromeBrowser_FalseForNullDriver()
        {
            // null is neither ChromeDriver nor IHasCapabilities -> false.
            var result = (bool)InvokePrivate("IsChromeBrowser", new object?[] { null })!;
            Assert.False(result);
        }

        // ===== getHttpClient ===================================================

        [Fact]
        public void GetHttpClient_ReturnsSetClient()
        {
            var client = new HttpClient();
            Percy.setHttpClient(client);
            Assert.Same(client, Percy.getHttpClient());
        }

        [Fact]
        public void GetHttpClient_LazilyCreatesDefaultWhenUnset()
        {
            // Force the private static _http back to null so getHttpClient() takes
            // the double-checked-lock creation branch and builds a default client.
            FieldInfo httpField = PercyType.GetField("_http",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            httpField.SetValue(null, null);

            HttpClient created = Percy.getHttpClient();
            Assert.NotNull(created);
            // Default client is configured with a 10-minute timeout.
            Assert.Equal(TimeSpan.FromMinutes(10), created.Timeout);
            // Subsequent calls return the same lazily-created instance.
            Assert.Same(created, Percy.getHttpClient());
        }

        // ===== setEligibleWidths / setCliConfig =================================

        [Fact]
        public void SettersAndResetInternalCaches_DoNotThrow()
        {
            Percy.setEligibleWidths(new List<int> { 375, 1280 });
            Percy.setCliConfig(ParseJson("{\"snapshot\":{}}"));
            Percy.setSessionType("web");
            // ResetInternalCaches clears _enabled/_dom/sessionType/widths/config.
            Percy.ResetInternalCaches();
            // After reset, isResponsiveSnapshotCapture should treat config as null.
            var result = (bool)InvokePrivate(
                "isResponsiveSnapshotCapture", new object?[] { null })!;
            Assert.False(result);
        }

        // ===== CreateRegion extra branches ====================================

        [Fact]
        public void CreateRegion_StandardAlgorithm_WithoutConfigValues_LeavesConfigurationNull()
        {
            var region = Percy.CreateRegion(
                elementXpath: "//div",
                algorithm: "standard");

            Assert.Equal("standard", region.algorithm);
            Assert.Equal("//div", region.elementSelector.elementXpath);
            // No configuration values supplied -> configuration stays null.
            Assert.Null(region.configuration);
            Assert.Null(region.assertion);
        }

        [Fact]
        public void CreateRegion_IgnoreAlgorithm_DoesNotPopulateConfigurationEvenIfProvided()
        {
            // For the default "ignore" algorithm the configuration block is never
            // attached, even when sensitivity/threshold values are passed.
            var region = Percy.CreateRegion(
                elementCSS: ".sel",
                diffSensitivity: 4,
                imageIgnoreThreshold: 0.2);

            Assert.Equal("ignore", region.algorithm);
            Assert.Equal(".sel", region.elementSelector.elementCSS);
            Assert.Null(region.configuration);
        }

        [Fact]
        public void CreateRegion_StandardAlgorithm_WithAdsAndBanners_PopulatesConfiguration()
        {
            var region = Percy.CreateRegion(
                algorithm: "standard",
                adsEnabled: true,
                bannersEnabled: false);

            Assert.NotNull(region.configuration);
            Assert.True(region.configuration.adsEnabled);
            Assert.False(region.configuration.bannersEnabled);
        }

        // ===== GetPercyDOM (HTTP, cached) =====================================

        [Fact]
        public void GetPercyDOM_FetchesAndCachesDomScript()
        {
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.Expect(HttpMethod.Get, "http://localhost:5338/percy/dom.js")
                .Respond("application/javascript", "window.PercyDOM = {};");
            Percy.setHttpClient(new HttpClient(mockHttp));

            var first = (string)InvokePrivate("GetPercyDOM")!;
            Assert.Contains("PercyDOM", first);

            // Second call must hit the cache (_dom) and NOT make another request.
            var second = (string)InvokePrivate("GetPercyDOM")!;
            Assert.Equal(first, second);
            mockHttp.VerifyNoOutstandingExpectation();
        }

        // ===== GetResponsiveWidths (HTTP) =====================================

        [Fact]
        public void GetResponsiveWidths_ParsesNumberAndObjectWidths()
        {
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Get, "http://localhost:5338/percy/widths-config*")
                .Respond("application/json",
                    "{\"widths\":[375,{\"width\":1280,\"height\":900}]}");
            Percy.setHttpClient(new HttpClient(mockHttp));

            var result = InvokePrivate("GetResponsiveWidths", new object?[] { null });
            // returns List<ResponsiveWidth>; inspect via reflection on items.
            var list = (System.Collections.IEnumerable)result!;
            var items = list.Cast<object>().ToList();
            Assert.Equal(2, items.Count);

            Type rw = items[0].GetType();
            PropertyInfo widthProp = rw.GetProperty("width")!;
            PropertyInfo heightProp = rw.GetProperty("height")!;

            Assert.Equal(375, (int)widthProp.GetValue(items[0])!);
            Assert.Null(heightProp.GetValue(items[0]));

            Assert.Equal(1280, (int)widthProp.GetValue(items[1])!);
            Assert.Equal(900, (int)heightProp.GetValue(items[1])!);
        }

        [Fact]
        public void GetResponsiveWidths_ThrowsWhenWidthsMissing()
        {
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Get, "http://localhost:5338/percy/widths-config*")
                .Respond("application/json", "{\"notWidths\":true}");
            Percy.setHttpClient(new HttpClient(mockHttp));

            var ex = Assert.Throws<TargetInvocationException>(() =>
                InvokePrivate("GetResponsiveWidths", new object?[] { new List<int> { 100 } }));
            Assert.Contains("Update Percy CLI", ex.InnerException!.Message);
        }

        // ===== Log (HTTP best-effort + console) ===============================

        [Fact]
        public void Log_WritesLabeledMessageToConsole()
        {
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/log")
                .Respond("application/json", "{}");
            Percy.setHttpClient(new HttpClient(mockHttp));

            var original = Console.Out;
            var sw = new StringWriter();
            Console.SetOut(sw);
            try
            {
                InvokeLog("hello-log", "info");
            }
            finally
            {
                Console.SetOut(original);
            }

            var output = System.Text.RegularExpressions.Regex.Replace(
                sw.ToString(), @"\e\[(\d+;)*(\d+)?[ABCDHJKfmsu]", "");
            Assert.Contains("[percy] hello-log", output);
        }

        [Fact]
        public void Log_DebugLevelSuppressedFromConsoleWhenNotDebug()
        {
            // DEBUG is false in this env, so a 'debug' level message must NOT be
            // printed to the console.
            Assert.False(Percy.DEBUG);

            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/log")
                .Respond("application/json", "{}");
            Percy.setHttpClient(new HttpClient(mockHttp));

            var original = Console.Out;
            var sw = new StringWriter();
            Console.SetOut(sw);
            try
            {
                InvokeLog("debug-only-message", "debug");
            }
            finally
            {
                Console.SetOut(original);
            }

            Assert.DoesNotContain("debug-only-message", sw.ToString());
        }

        [Fact]
        public void Log_SwallowsHttpFailureButStillPrints()
        {
            // CLI log endpoint failing must not bubble; console output still happens.
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/log")
                .Respond(HttpStatusCode.InternalServerError);
            Percy.setHttpClient(new HttpClient(mockHttp));

            var original = Console.Out;
            var sw = new StringWriter();
            Console.SetOut(sw);
            try
            {
                InvokeLog("still-prints", "info");
            }
            finally
            {
                Console.SetOut(original);
            }

            var output = System.Text.RegularExpressions.Regex.Replace(
                sw.ToString(), @"\e\[(\d+;)*(\d+)?[ABCDHJKfmsu]", "");
            Assert.Contains("[percy] still-prints", output);
        }

        // ===== Enabled (healthcheck branches via MockHttp) =====================

        private static MockHttpMessageHandler HealthcheckHandler(
            string content, string? version)
        {
            var mockHttp = new MockHttpMessageHandler();
            var req = mockHttp.When(HttpMethod.Get, "http://localhost:5338/percy/healthcheck");
            var headers = new Dictionary<string, string>();
            if (version != null) headers["x-percy-core-version"] = version;
            req.Respond(headers, "application/json", content);
            return mockHttp;
        }

        [Fact]
        public void Enabled_TrueOnSuccessfulVersion1Healthcheck_SetsState()
        {
            var mockHttp = HealthcheckHandler(
                "{\"success\":true,\"type\":\"web\",\"widths\":[375,1280],\"config\":{\"snapshot\":{}}}",
                "1.28.0");
            Percy.setHttpClient(new HttpClient(mockHttp));

            Assert.True(Percy.Enabled());

            // Side effects: sessionType set to "web" -> a web Snapshot guard would
            // not throw the automate error. We assert via the readiness resolver
            // seeing the injected config (sessionType not directly readable).
            var merged = (Dictionary<string, object?>)InvokePrivate(
                "ResolveReadinessConfig", new object?[] { null })!;
            Assert.Empty(merged); // config.snapshot has no readiness key
        }

        [Fact]
        public void Enabled_FalseWhenVersionHeaderMissing()
        {
            var mockHttp = HealthcheckHandler("{\"success\":true}", null);
            Percy.setHttpClient(new HttpClient(mockHttp));

            var original = Console.Out;
            var sw = new StringWriter();
            Console.SetOut(sw);
            try { Assert.False(Percy.Enabled()); }
            finally { Console.SetOut(original); }

            Assert.Contains("no longer supported by this SDK", sw.ToString());
        }

        [Fact]
        public void Enabled_FalseWhenVersionNotOne()
        {
            var mockHttp = HealthcheckHandler("{\"success\":true}", "0.42.0");
            Percy.setHttpClient(new HttpClient(mockHttp));

            var original = Console.Out;
            var sw = new StringWriter();
            Console.SetOut(sw);
            try { Assert.False(Percy.Enabled()); }
            finally { Console.SetOut(original); }

            Assert.Contains("Unsupported Percy CLI version", sw.ToString());
        }

        [Fact]
        public void Enabled_FalseAndDisablesWhenHealthcheckThrows()
        {
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Get, "http://localhost:5338/percy/healthcheck")
                .Respond(HttpStatusCode.InternalServerError);
            Percy.setHttpClient(new HttpClient(mockHttp));

            var original = Console.Out;
            var sw = new StringWriter();
            Console.SetOut(sw);
            try { Assert.False(Percy.Enabled()); }
            finally { Console.SetOut(original); }

            Assert.Contains("Percy is not running, disabling snapshots", sw.ToString());
        }

        [Fact]
        public void Enabled_CachesResultAcrossCalls()
        {
            var mockHttp = HealthcheckHandler(
                "{\"success\":true,\"type\":\"web\",\"widths\":[375],\"config\":{}}",
                "1.0.0");
            Percy.setHttpClient(new HttpClient(mockHttp));

            Assert.True(Percy.Enabled());
            // Second invocation should return the cached _enabled without a second
            // request; swap in a handler that would fail if called.
            var failing = new MockHttpMessageHandler();
            failing.Fallback.Respond(HttpStatusCode.InternalServerError);
            Percy.setHttpClient(new HttpClient(failing));
            Assert.True(Percy.Enabled());
        }

        // ===== Log: DEBUG-true branches via DebugEnabled mirror ================

        [Fact]
        public void Log_DebugEnabled_PrintsCliFailureAndDebugLevelToConsole()
        {
            // Flip the env mirror so the DEBUG-gated lines in Log run:
            //   * the catch arm `if (DebugEnabled) Console.WriteLine("Sending log
            //     to CLI failed: ...")` (CLI POST must fail to reach the catch)
            //   * the finally arm that prints even a "debug"-level message
            // Production reads this exact same value from PERCY_LOGLEVEL=debug.
            bool oldDebug = Percy.DebugEnabled;
            Percy.DebugEnabled = true;

            // Make the /percy/log POST fail so Log's try-block throws and the
            // catch's DebugEnabled-gated console write executes.
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/log")
                .Respond(HttpStatusCode.InternalServerError);
            Percy.setHttpClient(new HttpClient(mockHttp));

            var original = Console.Out;
            var sw = new StringWriter();
            Console.SetOut(sw);
            try
            {
                InvokeLog("debug-when-enabled", "debug");
            }
            finally
            {
                Console.SetOut(original);
                Percy.DebugEnabled = oldDebug;
            }

            var output = System.Text.RegularExpressions.Regex.Replace(
                sw.ToString(), @"\e\[(\d+;)*(\d+)?[ABCDHJKfmsu]", "");
            // catch arm fired (CLI failure surfaced because DebugEnabled)
            Assert.Contains("Sending log to CLI failed", output);
            // finally arm fired: a debug-level message printed because DebugEnabled
            Assert.Contains("debug-when-enabled", output);
            // label switches to "percy:dotnet" when DebugEnabled
            Assert.Contains("[percy:dotnet]", output);
        }

        // ===== ResolveResponsiveTargetHeight: MinHeight-enabled branches =======

        [Fact]
        public void ResolveResponsiveTargetHeight_MinHeightEnabled_ReturnsConfiguredMinHeight()
        {
            // Flip the env mirror so the PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT-enabled
            // path runs and a configured minHeight (from options) is returned.
            bool old = Percy.ResponsiveCaptureMinHeight;
            Percy.ResponsiveCaptureMinHeight = true;
            try
            {
                var options = new Dictionary<string, object> { { "minHeight", 1500 } };
                var result = (int)InvokePrivate(
                    "ResolveResponsiveTargetHeight", new object?[] { null, options, 768 })!;
                Assert.Equal(1500, result); // uses minHeight, not currentHeight
            }
            finally
            {
                Percy.ResponsiveCaptureMinHeight = old;
            }
        }

        [Fact]
        public void ResolveResponsiveTargetHeight_MinHeightEnabled_FallsBackToCurrentWhenUnset()
        {
            // MinHeight enabled but neither options nor cliConfig provide a value →
            // the `minHeight == null` branch returns currentHeight.
            bool old = Percy.ResponsiveCaptureMinHeight;
            Percy.ResponsiveCaptureMinHeight = true;
            try
            {
                var options = new Dictionary<string, object>();
                var result = (int)InvokePrivate(
                    "ResolveResponsiveTargetHeight", new object?[] { null, options, 900 })!;
                Assert.Equal(900, result); // no configured minHeight → currentHeight
            }
            finally
            {
                Percy.ResponsiveCaptureMinHeight = old;
            }
        }

        // ===== ResolveConfiguredMinHeight: FormatException catch via seam ======

        [Fact]
        public void ResolveConfiguredMinHeight_ParserThrowsFormatException_HitsCatchReturnsNull()
        {
            // int.TryParse never throws FormatException, so the catch arm is
            // otherwise unreachable. Inject a parser that throws to drive it.
            var oldParser = Percy.MinHeightParser;
            Percy.MinHeightParser = _ => throw new FormatException("boom");
            try
            {
                var options = new Dictionary<string, object> { { "minHeight", "1280" } };
                var result = (int?)InvokePrivate("ResolveConfiguredMinHeight", options);
                Assert.Null(result); // catch logs and returns null
            }
            finally
            {
                Percy.MinHeightParser = oldParser;
            }
        }

        [Fact]
        public void ResolveConfiguredMinHeight_DefaultParser_ParsesAndReturnsValue()
        {
            // The default seam delegate is just int.TryParse — confirm it still
            // parses correctly (behavior-preserving) after the reroute.
            var options = new Dictionary<string, object> { { "minHeight", "640" } };
            var result = (int?)InvokePrivate("ResolveConfiguredMinHeight", options);
            Assert.Equal(640, result);
        }

        // ===== IsChromeBrowser: `driver is ChromeDriver` true branch ===========

        [Fact]
        public void IsChromeBrowser_TrueForChromeDriverInstance()
        {
            // The `driver is ChromeDriver` true branch (return true) needs a real
            // ChromeDriver runtime type. Construct one WITHOUT launching Chrome via
            // GetUninitializedObject so its ctor (which would start ChromeDriver
            // service) is bypassed; the runtime type is still ChromeDriver, so the
            // `is` pattern matches and the method short-circuits to true before any
            // executor/capability access.
            var chrome = (OpenQA.Selenium.WebDriver)
                RuntimeHelpers.GetUninitializedObject(typeof(ChromeDriver));
            var result = (bool)InvokePrivate("IsChromeBrowser", new object?[] { chrome })!;
            Assert.True(result);
        }

        // ===== ResetInternalCaches restores env mirrors ========================

        [Fact]
        public void ResetInternalCaches_RestoresFlippedMirrorsToEnvDefaults()
        {
            Percy.DebugEnabled = !Percy.DEBUG;
            Percy.ResponsiveCaptureMinHeight = !Percy.PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT;
            Percy.ResponsiveCaptureReloadPage = !Percy.PERCY_RESPONSIVE_CAPTURE_RELOAD_PAGE;
            Percy.MinHeightParser = _ => 42;

            Percy.ResetInternalCaches();

            Assert.Equal(Percy.DEBUG, Percy.DebugEnabled);
            Assert.Equal(Percy.PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT, Percy.ResponsiveCaptureMinHeight);
            Assert.Equal(Percy.PERCY_RESPONSIVE_CAPTURE_RELOAD_PAGE, Percy.ResponsiveCaptureReloadPage);
            Assert.Equal(Percy.RESPONSIVE_CAPTURE_SLEEP_TIME, Percy.ResponsiveCaptureSleepTime);
            // default parser parses again
            var options = new Dictionary<string, object> { { "minHeight", "55" } };
            Assert.Equal(55, (int?)InvokePrivate("ResolveConfiguredMinHeight", options));
        }
    }
}
