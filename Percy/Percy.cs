using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Reflection;
using System.Diagnostics;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using Newtonsoft.Json.Linq;

namespace PercyIO.Selenium
{
    public static class Percy
    {
        public static readonly bool DEBUG =
            Environment.GetEnvironmentVariable("PERCY_LOGLEVEL") == "debug";
        public static readonly string CLI_API =
            Environment.GetEnvironmentVariable("PERCY_CLI_API") ?? "http://localhost:5338";
        
        public static readonly string RESPONSIVE_CAPTURE_SLEEP_TIME =
             Environment.GetEnvironmentVariable("RESPONSIVE_CAPTURE_SLEEP_TIME")
             ?? Environment.GetEnvironmentVariable("RESONSIVE_CAPTURE_SLEEP_TIME");
        public static readonly bool PERCY_RESPONSIVE_CAPTURE_RELOAD_PAGE =
            (Environment.GetEnvironmentVariable("PERCY_RESPONSIVE_CAPTURE_RELOAD_PAGE") ?? "")
                .Equals("true", StringComparison.OrdinalIgnoreCase);
        public static readonly bool PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT =
            (Environment.GetEnvironmentVariable("PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT") ?? "")
                .Equals("true", StringComparison.OrdinalIgnoreCase);
        public static readonly string CLIENT_INFO =
            typeof(Percy).Assembly.GetCustomAttribute<ClientInfoAttribute>().ClientInfo;
        
        public static readonly string ENVIRONMENT_INFO = Regex.Replace(
            Regex.Replace(RuntimeInformation.FrameworkDescription, @"\s+", "-"),
            @"-([\d\.]+).*$", "/$1").Trim().ToLower();

        public static readonly string ignoreElementKey = "ignore_region_selenium_elements";
        public static readonly string ignoreElementAltKey = "ignoreRegionSeleniumElements";
        public static readonly string considerElementKey = "consider_region_selenium_elements";
        public static readonly string considerElementAltKey = "considerRegionSeleniumElements";

        private static void Log<T>(T message, string lvl = "info")
        {
            string label = DEBUG ? "percy:dotnet" : "percy";
            string labeledMessage = $"[\u001b[35m{label}\u001b[39m] {message}";
            // Send log message to Percy CLI
            try
            {
                Dictionary<string, object> logPayload = new Dictionary<string, object> {
                    { "message", labeledMessage },
                    { "level", lvl }
                };
                Request("/percy/log", logPayload);
            }
            catch (Exception e)
            {
                if (DEBUG)
                    Console.WriteLine($"Sending log to CLI failed: {e.Message}");
            }
            finally
            {
                // Only log to console if lvl is not 'debug' or DEBUG is true
                if (lvl != "debug" || DEBUG)
                {
                    Console.WriteLine(labeledMessage);
                }
            }
        }

        private static HttpClient? _http;

        private static string? sessionType = null;
        private static object? eligibleWidths;
        private static object? cliConfig;

        private static string PayloadParser(object? payload = null, bool alreadyJson = false)
        {
            if (alreadyJson) 
            {
                return payload is null ? "" : payload.ToString();
            }
            return JsonSerializer.Serialize(payload).ToString();
        }

        internal static void setHttpClient(HttpClient client)
        {
            _http = client;
        }

        internal static HttpClient getHttpClient() {
            if (_http == null) {
                setHttpClient(new HttpClient());
                _http.Timeout = TimeSpan.FromMinutes(10);
            }
            
            return _http;
        }

        internal static void setSessionType(String? type)
        {
            sessionType = type;
        }

        internal static void setEligibleWidths(object widths)
        {
            eligibleWidths = widths;
        }

        internal static void setCliConfig(object config)
        {
            cliConfig = config;
        }

        // Added isJson since current JSON parsing doesn’t support nested objects and thats why we using different lib
        private static dynamic Request(string endpoint, object? payload = null, bool isJson = false)
        {
            StringContent? body = payload == null ? null : new StringContent(
                PayloadParser(payload, isJson), Encoding.UTF8, "application/json");
            
            HttpClient httpClient = getHttpClient();
            Task<HttpResponseMessage> apiTask = body != null
                ? httpClient.PostAsync($"{CLI_API}{endpoint}", body)
                : httpClient.GetAsync($"{CLI_API}{endpoint}");
            apiTask.Wait();

            HttpResponseMessage response = apiTask.Result;
            response.EnsureSuccessStatusCode();

            Task<string> contentTask = response.Content.ReadAsStringAsync();
            contentTask.Wait();

            IEnumerable<string>? version = null;
            response.Headers.TryGetValues("x-percy-core-version", out version);

            return new {
                version = version == null ? null : version.First(),
                content = contentTask.Result
            };
        }

        private static string? _dom = null;
        private static string GetPercyDOM()
        {
            if (_dom != null) return (string) _dom;
            _dom = Request("/percy/dom.js").content;
            return (string) _dom;
        }

        private static bool? _enabled = null;
        public static Func<bool> Enabled = () =>
        {
            if (_enabled != null) return (bool) _enabled;

            try
            {
                dynamic res = Request("/percy/healthcheck");
                dynamic data = JsonSerializer.Deserialize<dynamic>(res.content);

                if (data.GetProperty("success").GetBoolean() != true)
                {
                    throw new Exception(data.error);
                }
                else if (res.version == null)
                {
                    Log("You may be using @percy/agent " +
                        "which is no longer supported by this SDK. " +
                        "Please uninstall @percy/agent and install @percy/cli instead. " +
                        "https://www.browserstack.com/docs/percy/migration/migrate-to-cli");
                    return (bool) (_enabled = false);
                }
                else if (res.version[0] != '1')
                {
                    Log($"Unsupported Percy CLI version, {res.version}");
                    return (bool) (_enabled = false);
                }
                else
                {
                    data.TryGetProperty("type", out JsonElement type);
                    data.TryGetProperty("widths", out JsonElement widths);
                    data.TryGetProperty("config", out JsonElement config);
                    setEligibleWidths(widths);
                    setSessionType(type.ToString());
                    setCliConfig(config);
                    return (bool) (_enabled = true);
                }
            }
            catch (Exception error)
            {
                Log("Percy is not running, disabling snapshots");
                Log<Exception>(error, "debug");
                return (bool) (_enabled = false);
            }
        };
        public class Region 
        { 
            public RegionElementSelector elementSelector { get; set; } 
            public RegionPadding padding { get; set; } 
            public string algorithm { get; set; } 
            public RegionConfiguration configuration { get; set; } 
            public RegionAssertion assertion { get; set; } 

            // Rename the nested class to RegionElementSelector or another unique name
            public class RegionElementSelector 
            { 
                public RegionBoundingBox boundingBox { get; set; } 
                public string elementXpath { get; set; } 
                public string elementCSS { get; set; } 
            } 

            public class RegionBoundingBox 
            { 
                public int top { get; set; } 
                public int left { get; set; } 
                public int width { get; set; } 
                public int height { get; set; } 
            } 

            public class RegionPadding 
            { 
                public int top { get; set; } 
                public int left { get; set; } 
                public int right { get; set; } 
                public int bottom { get; set; } 
            } 

            public class RegionConfiguration 
            { 
                public int? diffSensitivity { get; set; } 
                public double? imageIgnoreThreshold { get; set; } 
                public bool? carouselsEnabled { get; set; } 
                public bool? bannersEnabled { get; set; } 
                public bool? adsEnabled { get; set; } 
            } 

            public class RegionAssertion 
            { 
                public double? diffIgnoreThreshold { get; set; } 
            }
        }


        public static Region CreateRegion(
            Region.RegionBoundingBox? boundingBox = null,
            string? elementXpath = null,
            string? elementCSS = null,
            Region.RegionPadding? padding = null,
            string algorithm = "ignore",
            int? diffSensitivity = null,
            double? imageIgnoreThreshold = null,
            bool? carouselsEnabled = null,
            bool? bannersEnabled = null,
            bool? adsEnabled = null,
            double? diffIgnoreThreshold = null)
        {
            var elementSelector = new Region.RegionElementSelector
            {
                boundingBox = boundingBox,
                elementXpath = elementXpath,
                elementCSS = elementCSS
            };

            var region = new Region
            {
                algorithm = algorithm,
                elementSelector = elementSelector,
                padding = padding
            };

            if (new[] { "standard", "intelliignore" }.Contains(algorithm))
            {
                var configuration = new Region.RegionConfiguration
                {
                    diffSensitivity = diffSensitivity,
                    imageIgnoreThreshold = imageIgnoreThreshold,
                    carouselsEnabled = carouselsEnabled,
                    bannersEnabled = bannersEnabled,
                    adsEnabled = adsEnabled
                };

                // Check if any configuration value is set and add it to the region
                if (configuration.diffSensitivity.HasValue || 
                    configuration.imageIgnoreThreshold.HasValue || 
                    configuration.carouselsEnabled.HasValue || 
                    configuration.bannersEnabled.HasValue || 
                    configuration.adsEnabled.HasValue)
                {
                    region.configuration = configuration;
                }
            }

            if (diffIgnoreThreshold.HasValue)
            {
                region.assertion = new Region.RegionAssertion
                {
                    diffIgnoreThreshold = diffIgnoreThreshold
                };
            }

            return region;
        }
        private static bool IsUnsupportedIframeSrc(string? src)
        {
            return string.IsNullOrEmpty(src) ||
                    src.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                    src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                    src.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetOrigin(string url)
        {
            try
            {
                Uri uri = new Uri(url);
                return $"{uri.Scheme}://{uri.Authority}";
            }
            catch
            {
                return "";
            }
        }

        private static Dictionary<string, object>? ProcessFrame(
            WebDriver driver,
            IWebElement frameElement,
            Dictionary<string, object>? options,
            string domJs)
        {
            // Read attributes while still in parent context — these calls will
            // fail if made after switchTo().frame().
            string? frameUrl = frameElement.GetAttribute("src") ?? "unknown-src";
            Log($"processFrame: checking iframe src=\"{frameUrl}\"", "debug");

            string? percyElementId = frameElement.GetAttribute("data-percy-element-id");
            Log($"processFrame: data-percy-element-id=\"{percyElementId}\" for src=\"{frameUrl}\"", "debug");
            if (string.IsNullOrEmpty(percyElementId))
            {
                Log($"Skipping frame {frameUrl}: no matching percyElementId found", "debug");
                return null;
            }

            Dictionary<string, object>? iframeSnapshot = null;
            try
            {
                driver.SwitchTo().Frame(frameElement);
                // Inject Percy DOM into the cross-origin frame context
                driver.ExecuteScript(domJs);
                // Serialize inside the frame; enableJavaScript=true is required for CORS iframes
                var iframeOptions = options != null
                    ? new Dictionary<string, object>(options)
                    : new Dictionary<string, object>();
                iframeOptions["enableJavaScript"] = true;
                var iframeOpts = JsonSerializer.Serialize(iframeOptions);
                iframeSnapshot = (Dictionary<string, object>)driver.ExecuteScript(
                    $"return PercyDOM.serialize({iframeOpts})");
            }
            catch (Exception e)
            {
                Log($"Failed to process cross-origin frame {frameUrl}: {e.Message}", "error");
                throw new Exception($"Failed to process cross-origin frame {frameUrl}", e);
            }
            finally
            {
                try
                {
                    driver.SwitchTo().DefaultContent();
                }
                catch (Exception err)
                {
                    throw new Exception(
                        $"Fatal: could not exit iframe context after processing \"{frameUrl}\". Driver may be unstable.", err);
                }
            }

            return new Dictionary<string, object>
            {
                { "iframeData", new Dictionary<string, object> { { "percyElementId", percyElementId } } },
                { "iframeSnapshot", iframeSnapshot },
                { "frameUrl", frameUrl }
            };
        }

        private static dynamic getSerializedDom(
            WebDriver driver,
            object cookies,
            Dictionary<string, object>? options,
            string? domJs = null)
        {
            var opts = JsonSerializer.Serialize(options);
            string script = $"return PercyDOM.serialize({opts})";
            var domSnapshot = (Dictionary<string, object>)driver.ExecuteScript(script);
            domSnapshot["cookies"] = cookies;

            // Process CORS iframes when DOM script is available
            if (!string.IsNullOrEmpty(domJs))
            {
                try
                {
                    string pageOrigin = GetOrigin(driver.Url);
                    var iframes = driver.FindElements(By.TagName("iframe"));
                    if (iframes.Count > 0)
                    {
                        var processedFrames = new List<Dictionary<string, object>>();
                        foreach (IWebElement frame in iframes)
                        {
                            string? frameSrc = frame.GetAttribute("src");
                            if (IsUnsupportedIframeSrc(frameSrc))
                                continue;

                            string frameOrigin;
                            try
                            {
                                Uri baseUri = new Uri(driver.Url);
                                Uri resolvedUri = new Uri(baseUri, frameSrc);
                                frameOrigin = GetOrigin(resolvedUri.ToString());
                            }
                            catch (Exception e)
                            {
                                Log($"Skipping iframe \"{frameSrc}\": {e.Message}", "debug");
                                continue;
                            }

                            if (frameOrigin == pageOrigin)
                                continue;

                            try
                            {
                                var result = ProcessFrame(driver, frame, options, domJs);
                                if (result != null)
                                    processedFrames.Add(result);
                            }
                            catch (Exception e)
                            {
                                Log($"Skipping frame \"{frameSrc}\" due to error: {e.Message}", "debug");
                                if (e.Message.Contains("Fatal"))
                                    throw;
                            }
                        }
                        if (processedFrames.Count > 0)
                            domSnapshot["corsIframes"] = processedFrames;
                    }
                }
                catch (Exception e)
                {
                    Log($"Failed to process cross-origin iframes: {e.Message}", "debug");
                    if (e.Message.Contains("Fatal"))
                        throw;
                }
            }

            return domSnapshot;
        }

        private static bool IsChromeBrowser(WebDriver driver)
        {
            if (driver is ChromeDriver)
            {
                return true;
            }

            if (driver is IHasCapabilities hasCapabilities)
            {
                object? browserName = hasCapabilities.Capabilities?.GetCapability("browserName");
                return browserName?.ToString()?.Equals("chrome", StringComparison.OrdinalIgnoreCase) == true;
            }

            return false;
        }

        private static bool TryResizeWithCdp(WebDriver driver, int width, int height)
        {
            if (!IsChromeBrowser(driver))
            {
                return false;
            }

            MethodInfo? executeCdpMethod = driver.GetType().GetMethod("ExecuteCdpCommand", new[] { typeof(string), typeof(Dictionary<string, object>) });
            if (executeCdpMethod == null)
            {
                return false;
            }

            var commandParams = new Dictionary<string, object>
            {
                { "width", width },
                { "height", height },
                { "deviceScaleFactor", 1 },
                { "mobile", false }
            };

            executeCdpMethod.Invoke(driver, new object[] { "Emulation.setDeviceMetricsOverride", commandParams });
            return true;
        }

        private static void ChangeWindowDimensionAndWait(WebDriver driver, int width, int height, int resizeCount)
        {
            try
            {
                if (TryResizeWithCdp(driver, width, height))
                {
                    Log($"Attempting to resize using CDP for width {width} and height {height}", "debug");
                }
                else
                {
                    driver.Manage().Window.Size = new System.Drawing.Size(width, height);
                }
            }
            catch (Exception e)
            {
                Log($"Resizing using CDP failed, falling back to driver for width {width}: {e.Message}", "debug");
                driver.Manage().Window.Size = new System.Drawing.Size(width, height);
            }
            // Wait for window resize event using WebDriverWait
            try
            {
                WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(1));
                wait.Until(d => (long)((IJavaScriptExecutor)d).ExecuteScript("return window.resizeCount") == resizeCount);
            }
            catch (WebDriverTimeoutException)
            {
                Log($"Timed out waiting for window resize event for width {width}", "debug");
            }
        }

        private class ResponsiveWidth
        {
            public int width { get; set; }
            public int? height { get; set; }
        }
        private static List<ResponsiveWidth> GetResponsiveWidths(List<int>? widths = null)
        {
            widths ??= new List<int>();

            try
            {
                string queryParam = widths.Count > 0 ? $"?widths={string.Join(",", widths)}" : "";
                dynamic res = Request($"/percy/widths-config{queryParam}");
                var data = JsonSerializer.Deserialize<JsonElement>(res.content);

                if (!data.TryGetProperty("widths", out JsonElement widthsElement) || widthsElement.ValueKind != JsonValueKind.Array)
                {
                    Log("Update Percy CLI to the latest version to use responsiveSnapshotCapture", "error");
                    throw new Exception("Update Percy CLI to the latest version to use responsiveSnapshotCapture");
                }

                return widthsElement.EnumerateArray().Select(widthItem =>
                {
                    if (widthItem.ValueKind == JsonValueKind.Number)
                    {
                        return new ResponsiveWidth { width = widthItem.GetInt32() };
                    }

                    int width = widthItem.GetProperty("width").GetInt32();
                    int? height = null;
                    if (widthItem.TryGetProperty("height", out JsonElement heightElement) && heightElement.ValueKind == JsonValueKind.Number)
                    {
                        height = heightElement.GetInt32();
                    }

                    return new ResponsiveWidth { width = width, height = height };
                }).ToList();
            }
            catch (Exception error)
            {
                Log($"Failed to get responsive widths: {error}", "debug");
                throw new Exception("Update Percy CLI to the latest version to use responsiveSnapshotCapture", error);
            }
        }
        private static int ResolveResponsiveTargetHeight(WebDriver driver, Dictionary<string, object> options, int currentHeight)
        {
            if (!PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT)
            {
                Log($"PERCY_RESPONSIVE_CAPTURE_MIN_HEIGHT is disabled, using current window height: {currentHeight}", "debug");
                return currentHeight;
            }

            int? minHeight = ResolveConfiguredMinHeight(options);
            if (minHeight == null)
            {
                Log($"minHeight not found in options or cliConfig, using current window height: {currentHeight}", "debug");
                return currentHeight;
            }

            return CalculateTargetHeight(driver, minHeight.Value, currentHeight);
        }

        private static int? ResolveConfiguredMinHeight(Dictionary<string, object> options)
        {
            object? minHeightObj = null;
            
            if (options != null && options.ContainsKey("minHeight"))
            {
                minHeightObj = options["minHeight"];
            }
            else if (cliConfig != null)
            {
                try
                {
                    JsonElement config = (JsonElement)cliConfig;
                    if (config.TryGetProperty("snapshot", out JsonElement snapshotElement))
                    {
                        if (snapshotElement.TryGetProperty("minHeight", out JsonElement minHeightElement))
                        {
                            minHeightObj = minHeightElement.GetInt32();
                        }
                    }
                }
                catch (Exception e)
                {
                    Log($"Error reading minHeight from cliConfig: {e.Message}", "debug");
                    return null;
                }
            }

            if (minHeightObj == null)
            {
                return null;
            }

            try
            {
                if (minHeightObj is int intValue)
                {
                    return intValue;
                }
                if (int.TryParse(minHeightObj.ToString(), out int parsedValue))
                {
                    return parsedValue;
                }
            }
            catch (FormatException e)
            {
                Log($"Invalid minHeight value {minHeightObj}; expected integer, using current window height instead. {e.Message}", "debug");
                return null;
            }

            return null;
        }

        private static int CalculateTargetHeight(WebDriver driver, int minHeight, int fallbackHeight)
        {
            try
            {
                object result = driver.ExecuteScript($"return window.outerHeight - window.innerHeight + {minHeight}");
                if (result is long longValue)
                {
                    return (int)longValue;
                }
                if (result is int intValue)
                {
                    return intValue;
                }
                if (result is double doubleValue)
                {
                    return (int)doubleValue;
                }
                return fallbackHeight;
            }
            catch (Exception e)
            {
                Log($"Error calculating target height: {e.Message}", "debug");
                return fallbackHeight;
            }
        }

        public static List<Dictionary<string, object>> CaptureResponsiveDom(WebDriver driver, object cookies, Dictionary<string, object> options)
        {
            List<int> widths = options != null && options.ContainsKey("widths") ? (List<int>)options["widths"] : new List<int>();
            List<ResponsiveWidth> widthHeights = GetResponsiveWidths(widths);

            var domSnapshots = new List<Dictionary<string, object>>();

            var windowSize = driver.Manage().Window.Size;
            int currentWidth = windowSize.Width;
            int currentHeight = windowSize.Height;
            int lastWindowWidth = currentWidth;
            int resizeCount = 0;
            int sleepTime = 0;
            driver.ExecuteScript("PercyDOM.waitForResize()");
            int targetHeight = ResolveResponsiveTargetHeight(driver, options, currentHeight);

             if (_dom == null)
             {
                 _dom = GetPercyDOM();
             }

            foreach (ResponsiveWidth widthHeight in widthHeights)
            {
                int width = widthHeight.width;
                int? height = widthHeight.height;
                int heightForWidth = height.HasValue ? height.Value : targetHeight;

                if (lastWindowWidth != width) {
                    resizeCount++;
                    ChangeWindowDimensionAndWait(driver, width, heightForWidth, resizeCount);
                    lastWindowWidth = width;
                }

                if (PERCY_RESPONSIVE_CAPTURE_RELOAD_PAGE)
                {
                    try
                    {
                        driver.Navigate().Refresh();
                        if ((bool)driver.ExecuteScript("return !!window.PercyDOM") == false)
                        {
                            driver.ExecuteScript(GetPercyDOM());
                        }
                        driver.ExecuteScript("PercyDOM.waitForResize()");
                        resizeCount = 0;
                    }
                    catch (Exception error)
                    {
                        Log($"Page reload failed during responsive capture for width {width}: {error.Message}", "debug");
                    }
                }
                if (Int32.TryParse(RESPONSIVE_CAPTURE_SLEEP_TIME, out sleepTime))
                    Thread.Sleep(sleepTime * 1000);

                var domSnapshot =  getSerializedDom(driver, cookies, options, _dom);
                domSnapshot["width"] = width;
                if (height.HasValue)
                    domSnapshot["height"] = height.Value;
                domSnapshots.Add(domSnapshot);
            }

            ChangeWindowDimensionAndWait(driver, currentWidth, currentHeight, resizeCount + 1);

            return domSnapshots;
        }

        private static bool isResponsiveSnapshotCapture(Dictionary<string, object>? options) 
        {
            JsonElement config = (JsonElement) cliConfig;
            if (config.GetProperty("percy").TryGetProperty("deferUploads", out JsonElement deferUploadsProperty)) {
                if (deferUploadsProperty.GetBoolean()) { return false; }
            }

            return (options != null && options.ContainsKey("responsiveSnapshotCapture") && (bool)options["responsiveSnapshotCapture"] ||
                    config.GetProperty("snapshot").GetProperty("responsiveSnapshotCapture").GetBoolean());
        }

        public class Options : Dictionary<string, object> {}

        public static JObject? Snapshot(
            WebDriver driver, string name,
            Dictionary<string, object>? options = null)
        {
            if (!Enabled()) return null;
            if (sessionType == "automate")
                throw new Exception("Invalid function call - Snapshot(). Please use Screenshot() function while using Percy with Automate. For more information on usage of Screenshot, refer https://www.browserstack.com/docs/percy/integrate/functional-and-visual");

            try
            {
                if ((bool) driver.ExecuteScript("return !!window.PercyDOM") == false)
                    driver.ExecuteScript(GetPercyDOM());

                var cookies = driver.Manage().Cookies.AllCookies;
                string opts = JsonSerializer.Serialize(options);
                dynamic domSnapshot = null;

                if (isResponsiveSnapshotCapture(options)) {
                    domSnapshot = CaptureResponsiveDom(driver, cookies, options);
                } else {
                    domSnapshot = getSerializedDom(driver, cookies, options, _dom);
                }

                Options snapshotOptions = new Options {
                    { "clientInfo", CLIENT_INFO },
                    { "environmentInfo", ENVIRONMENT_INFO },
                    { "domSnapshot", domSnapshot },
                    { "url", driver.ExecuteScript("return document.URL") },
                    { "name", name }
                };

                if (options != null)
                    foreach (KeyValuePair<string, object> o in options)
                        snapshotOptions.Add(o.Key, o.Value);

                dynamic res = Request("/percy/snapshot", snapshotOptions);
                dynamic data = JsonSerializer.Deserialize<object>(res.content);

                if (data.GetProperty("success").GetBoolean() != true)
                    throw new Exception(data.GetProperty("error").GetString());
                if (data.TryGetProperty("data", out JsonElement results)) {
                    return JObject.Parse(results.GetRawText());
                }
                return null;
            }
            catch(Exception error)
            {
                Log($"Could not take DOM snapshot \"{name}\"");
                Log(error);
                return null;
            }
        }

        public static JObject? Screenshot(WebDriver driver, string name, Dictionary<string, object>? options = null)
        {
            PercyDriver percyDriver = new PercyDriver((RemoteWebDriver)driver);
            return percyDriver.Screenshot(name, options);
        }

        public static JObject? Screenshot(
            PercyDriver percyDriver, string name,
            Dictionary<string, object>? options = null)
        {
            if(!Enabled()) return null;
            if (sessionType != "automate")
                throw new Exception("Invalid function call - Screenshot(). Please use Snapshot() function for taking screenshot. Screenshot() should be used only while using Percy with Automate. For more information on usage of PercySnapshot(), refer doc for your language https://www.browserstack.com/docs/percy/integrate/overview");
            try
            {
                Dictionary<string, object> receivedPayload = percyDriver.getPayload();
                Options screenshotOptions = new Options {};

                screenshotOptions.Add("snapshotName", name);
                foreach (KeyValuePair<string, object> o in receivedPayload)
                {
                    if(o.Key == "capabilities")
                    {
                        var capabilitiesString = JsonSerializer.Serialize(o.Value);
                        var capabilitiesJson = JObject.Parse(capabilitiesString);
                        screenshotOptions.Add(o.Key, capabilitiesJson);
                    } else
                    {
                        screenshotOptions.Add(o.Key, o.Value);
                    }
                }

                if(options != null) {
                    Dictionary<string, object> userOptions = options.ToDictionary(kv => kv.Key, kv => kv.Value);

                    if (userOptions.ContainsKey(ignoreElementAltKey)) {
                        userOptions[ignoreElementKey] = userOptions[ignoreElementAltKey];
                        userOptions.Remove(ignoreElementAltKey);
                    }

                    if (userOptions.ContainsKey(considerElementAltKey)) {
                        userOptions[considerElementKey] = userOptions[considerElementAltKey];
                        userOptions.Remove(considerElementAltKey);
                    }

                    if(userOptions.ContainsKey(ignoreElementKey)) {
                        List<IWebElement>? ignoreElements = userOptions[ignoreElementKey] as List<IWebElement>;

                        if(ignoreElements != null)
                        {
                            List<string> elementIds = percyDriver.GetElementIdFromElements(ignoreElements);
                            userOptions.Remove(ignoreElementKey);
                            userOptions["ignore_region_elements"] = elementIds;
                        }
                    }

                    if(userOptions.ContainsKey(considerElementKey)) {
                        List<IWebElement>? considerElements = userOptions[considerElementKey] as List<IWebElement>;

                        if(considerElements != null)
                        {
                            List<string> elementIds = percyDriver.GetElementIdFromElements(considerElements);
                            userOptions.Remove(considerElementKey);
                            userOptions["consider_region_elements"] = elementIds;
                        }
                    }
                    screenshotOptions.Add("options", userOptions);
                }

                dynamic res = Request("/percy/automateScreenshot", JObject.FromObject(screenshotOptions), true);
                dynamic data = JsonSerializer.Deserialize<object>(res.content);
                if (data.GetProperty("success").GetBoolean() != true)
                    throw new Exception(data.GetProperty("error").GetString());

                if (data.TryGetProperty("data", out JsonElement results)) {
                    return JObject.Parse(results.GetRawText());
                }
                return null;
            }
            catch(Exception error)
            {
                Log($"Could not take Percy Screenshot \"{name}\"");
                Log(error);
                return null;
            }
        }

        public static JObject? Snapshot(WebDriver driver, string name, object opts)
        {
            Options options = new Options();

            foreach (PropertyDescriptor prop in TypeDescriptor.GetProperties(opts))
                options.Add(prop.Name, prop.GetValue(opts));

            return Snapshot(driver, name, options);
        }

        public static JObject? Screenshot(WebDriver driver, string name, object opts) {
            Options options = new Options();
            
            foreach (PropertyDescriptor prop in TypeDescriptor.GetProperties(opts))
                options.Add(prop.Name, prop.GetValue(opts));
            
            return Screenshot(driver, name, options);
        }

        public static void ResetInternalCaches()
        {
            _enabled = null;
            _dom = null;
        }
    }

    [System.AttributeUsage(System.AttributeTargets.Assembly, Inherited = false, AllowMultiple = false)]
    sealed class ClientInfoAttribute : System.Attribute
    {
        public string ClientInfo { get; }
        public ClientInfoAttribute(string info)
        {
            this.ClientInfo = info;
        }
    }
}
