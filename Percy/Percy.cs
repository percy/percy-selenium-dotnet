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
        
        public static readonly string RESONSIVE_CAPTURE_SLEEP_TIME =
             Environment.GetEnvironmentVariable("RESONSIVE_CAPTURE_SLEEP_TIME");
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

        private static dynamic getSerializedDom(WebDriver driver, object cookies, Dictionary<string, object>? options) {
            var opts = JsonSerializer.Serialize(options);
            string script = $"return PercyDOM.serialize({opts})";
            var domSnapshot = (Dictionary<string, object>)driver.ExecuteScript(script);
            domSnapshot["cookies"] = cookies;
            return domSnapshot;
        }

        private static List<int> GetWidthsForMultiDom(int[] widths)
        {
            var fetchedWidthsElement = (JsonElement)eligibleWidths;
            var allWidths = fetchedWidthsElement.GetProperty("mobile")
                                            .EnumerateArray()
                                            .Select(x => x.GetInt32())
                                            .ToList();

            if (widths.Length != 0)
            {
                allWidths.AddRange(widths);
            }
            else
            {
                allWidths.AddRange(fetchedWidthsElement.GetProperty("config")
                                                .EnumerateArray()
                                                .Select(x => x.GetInt32()));
            }

            return allWidths.Distinct().ToList();
        }

        // Method to check if ChromeDriver supports CDP by checking the existence of ExecuteCdpCommand
        private static bool IsCdpSupported(ChromeDriver chromeDriver)
        {
            return chromeDriver.GetType().GetMethod("ExecuteCdpCommand") != null;
        }

        private static void ChangeWindowDimensionAndWait(WebDriver driver, int width, int height, int resizeCount)
        {
            try
            {
                // Check if the driver is ChromeDriver and supports CDP
                if (driver is ChromeDriver chromeDriver && IsCdpSupported(chromeDriver))
                {
                    var commandParams = new Dictionary<string, object>
                    {
                        { "width", width },
                        { "height", height },
                        { "deviceScaleFactor", 1 },
                        { "mobile", false }
                    };

                    chromeDriver.ExecuteCdpCommand("Emulation.setDeviceMetricsOverride", commandParams);
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

        public static List<Dictionary<string, object>> CaptureResponsiveDom(WebDriver driver, object cookies, Dictionary<string, object> options)
        {
            List<int> widths = options != null && options.ContainsKey("widths") ? (List<int>)options["widths"] : new List<int>();
            widths = GetWidthsForMultiDom(widths.ToArray());
            var domSnapshots = new List<Dictionary<string, object>>();

            var windowSize = driver.Manage().Window.Size;
            int currentWidth = windowSize.Width;
            int currentHeight = windowSize.Height;
            int lastWindowWidth = currentWidth;
            int resizeCount = 0;
            int sleepTime = 0;
            driver.ExecuteScript("PercyDOM.waitForResize()");

            foreach (int width in widths)
            {
                if (lastWindowWidth != width) {
                    resizeCount++;
                    ChangeWindowDimensionAndWait(driver, width, currentHeight, resizeCount);
                    lastWindowWidth = width;
                }
                if (Int32.TryParse(RESONSIVE_CAPTURE_SLEEP_TIME, out sleepTime))
                    Thread.Sleep(sleepTime * 1000);

                var domSnapshot =  getSerializedDom(driver, cookies, options);
                domSnapshot["width"] = width;
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
                    domSnapshot = getSerializedDom(driver, cookies, options);
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
                        var capabilitiesJson = JsonSerializer.Serialize(o.Value);
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
