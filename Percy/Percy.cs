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
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;
using Newtonsoft.Json.Linq;

namespace PercyIO.Selenium
{
    public static class Percy
    {
        public static readonly bool DEBUG =
            Environment.GetEnvironmentVariable("PERCY_LOGLEVEL") == "debug";
        public static readonly string CLI_API =
            Environment.GetEnvironmentVariable("PERCY_CLI_API") ?? "http://localhost:5338";
        public static readonly string CLIENT_INFO =
            typeof(Percy).Assembly.GetCustomAttribute<ClientInfoAttribute>().ClientInfo;
        public static readonly string ENVIRONMENT_INFO = Regex.Replace(
            Regex.Replace(RuntimeInformation.FrameworkDescription, @"\s+", "-"),
            @"-([\d\.]+).*$", "/$1").Trim().ToLower();

        public static readonly string ignoreElementKey = "ignore_region_selenium_elements";
        public static readonly string ignoreElementAltKey = "ignoreRegionSeleniumElements";
        public static readonly string considerElementKey = "consider_region_selenium_elements";
        public static readonly string considerElementAltKey = "considerRegionSeleniumElements";

        private static void Log<T>(T message)
        {
            string label = DEBUG ? "percy:dotnet" : "percy";
            Console.WriteLine($"[\u001b[35m{label}\u001b[39m] {message}");
        }

        private static HttpClient? _http;

        private static string? sessionType = null;

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
                        "https://docs.percy.io/docs/migrating-to-percy-cli");
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
                    setSessionType(type.ToString());
                    return (bool) (_enabled = true);
                }
            }
            catch (Exception error)
            {
                Log("Percy is not running, disabling snapshots");
                if (DEBUG) Log<Exception>(error);
                return (bool) (_enabled = false);
            }
        };

        public class Options : Dictionary<string, object> {}

        public static JObject? Snapshot(
            WebDriver driver, string name,
            IEnumerable<KeyValuePair<string, object>>? options = null)
        {
            if (!Enabled()) return null;
            if (sessionType == "automate")
                throw new Exception("Invalid function call - Snapshot(). Please use Screenshot() function while using Percy with Automate. For more information on usage of Screenshot, refer https://docs.percy.io/docs/integrate-functional-testing-with-visual-testing");

            try
            {
                if ((bool) driver.ExecuteScript("return !!window.PercyDOM") == false)
                    driver.ExecuteScript(GetPercyDOM());

                string opts = JsonSerializer.Serialize(options);
                var domSnapshot = driver.ExecuteScript($"return PercyDOM.serialize({opts})");

                Options snapshotOptions = new Options {
                    { "clientInfo", CLIENT_INFO },
                    { "environmentInfo", ENVIRONMENT_INFO },
                    { "domSnapshot", domSnapshot },
                    { "url", driver.Url },
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

        public static JObject? Screenshot(WebDriver driver, string name, IEnumerable<KeyValuePair<string, object>>? options = null)
        {
            PercyDriver percyDriver = new PercyDriver((RemoteWebDriver)driver);
            return percyDriver.Screenshot(name, options);
        }

        public static JObject? Screenshot(
            PercyDriver percyDriver, string name,
            IEnumerable<KeyValuePair<string, object>>? options = null)
        {
            if(!Enabled()) return null;
            if (sessionType == "web")
                throw new Exception("Invalid function call - Screenshot(). Please use Snapshot() function for taking screenshot. Screenshot() should be used only while using Percy with Automate. For more information on usage of PercySnapshot(), refer doc for your language https://docs.percy.io/docs/end-to-end-testing");
            try
            {
                Dictionary<string, object> receivedPayload = percyDriver.getPayload();
                Options screenshotOptions = new Options {};

                screenshotOptions.Add("snapshotName", name);
                foreach (KeyValuePair<string, object> o in receivedPayload)
                {
                    if(o.Key == "capabilities")
                    {
                        var dictionary = JObject.Parse(o.Value.ToString());
                        screenshotOptions.Add(o.Key, dictionary);
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
