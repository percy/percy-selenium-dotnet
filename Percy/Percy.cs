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

        private static void Log<T>(T message)
        {
            string label = DEBUG ? "percy:dotnet" : "percy";
            Console.WriteLine($"[\u001b[35m{label}\u001b[39m] {message}");
        }

        private static HttpClient _http = new HttpClient();
        private static dynamic Request(string endpoint, object? payload = null)
        {
            StringContent? body = payload == null ? null : new StringContent(
                JsonSerializer.Serialize(payload).ToString(), Encoding.UTF8, "application/json");
            Task<HttpResponseMessage> apiTask = body != null
                ? _http.PostAsync($"{CLI_API}{endpoint}", body)
                : _http.GetAsync($"{CLI_API}{endpoint}");
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
        public static bool Enabled()
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
                    return (bool) (_enabled = true);
                }
            }
            catch (Exception error)
            {
                Log("Percy is not running, disabling snapshots");
                if (DEBUG) Log<Exception>(error);
                return (bool) (_enabled = false);
            }
        }

        public class Options : Dictionary<string, object> {}

        public static void Snapshot(
            WebDriver driver, string name,
            IEnumerable<KeyValuePair<string, object>>? options = null)
        {
            if (!Enabled()) return;

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
            }
            catch(Exception error)
            {
                Log($"Could not take DOM snapshot \"{name}\"");
                Log(error);
            }
        }

        public static void Screenshot(
            WebDriver driver, string name,
            IEnumerable<KeyValuePair<string, object>>? options = null)
        {
            if(!Enabled()) return;
            if(!(driver is RemoteWebDriver)) throw new Exception("Driver should be of type RemoteWebDriver");

            try
            {
                string sessionId = ((RemoteWebDriver)driver).SessionId.ToString();
                HttpCommandExecutor executor = (HttpCommandExecutor)((RemoteWebDriver)driver).CommandExecutor;
                FieldInfo remoteServerUriField = typeof(HttpCommandExecutor).GetField("remoteServerUri", BindingFlags.NonPublic | BindingFlags.Instance);
                string commandExecutorUrl = remoteServerUriField.GetValue(executor).ToString();
                ICapabilities capabilitiesX = ((RemoteWebDriver)driver).Capabilities;
                Dictionary<string, object> capabilities = new Dictionary<string, object>();

                List<string> properties = new List<string>()
                {
                    "browserName", "browserVersion", "platformName", "proxy"
                };

                properties.ForEach(property => capabilities.Add(property, capabilitiesX.GetCapability(property)));

                Options screenshotOptions = new Options {
                    { "sessionId", sessionId },
                    { "commandExecutorUrl", commandExecutorUrl },
                    { "capabilities", capabilities },
                    { "snapshotName", name },
                    { "clientInfo", CLIENT_INFO },
                    { "environmentinfo", ENVIRONMENT_INFO },
                };

                if (options != null)
                        screenshotOptions.Add( "options", options);

                dynamic res = Request("/percy/automateScreenshot", screenshotOptions);
                dynamic data = JsonSerializer.Deserialize<object>(res.content);
                if (data.GetProperty("success").GetBoolean() != true)
                    throw new Exception(data.GetProperty("error").GetString());
            }
            catch(Exception error)
            {
                Log($"Could not take Percy Screenshot \"{name}\"");
                Log(error);
            }
        }

        public static void Snapshot(WebDriver driver, string name, object opts)
        {
            Options options = new Options();

            foreach (PropertyDescriptor prop in TypeDescriptor.GetProperties(opts))
                options.Add(prop.Name, prop.GetValue(opts));

            Snapshot(driver, name, options);
        }

        public static void Screenshot(WebDriver driver, string name, object opts) {
            Options options = new Options();
            
            foreach (PropertyDescriptor prop in TypeDescriptor.GetProperties(opts))
                options.Add(prop.Name, prop.GetValue(opts));
            
            Screenshot(driver, name, options);
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
