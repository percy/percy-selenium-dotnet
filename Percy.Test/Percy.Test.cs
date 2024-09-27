using Xunit;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenQA.Selenium.Firefox;
using WebDriverManager;
using WebDriverManager.DriverConfigs.Impl;

namespace PercyIO.Selenium.Tests
{
    public class TestsFixture : IDisposable
    {
        public readonly FirefoxDriver driver;

        public TestsFixture ()
        {
            new DriverManager().SetUpDriver(new FirefoxConfig());
            FirefoxOptions options = new FirefoxOptions();
            options.LogLevel = FirefoxDriverLogLevel.Fatal;
            options.AddArgument("--headless");

            driver = new FirefoxDriver(options);
            Percy.setHttpClient(new HttpClient());
        }

        public void Dispose()
        {
            driver.Quit();
        }
    }

    public class UnitTests : IClassFixture<TestsFixture>
    {
        public readonly FirefoxDriver driver;
        private readonly StringWriter _stdout;

        public UnitTests(TestsFixture fixture)
        {
            _stdout = new StringWriter();
            Console.SetOut(_stdout);

            driver = fixture.driver;
            driver.Navigate().GoToUrl($"{Percy.CLI_API}/test/snapshot");

            Percy.ResetInternalCaches();
            Request("/test/api/widths", new { config = new List<int> {375, 1280}, mobile = new List<int> {} });
            Request("/test/api/reset");
        }

        public string Stdout()
        {
            return Regex.Replace(_stdout.ToString(), @"\e\[(\d+;)*(\d+)?[ABCDHJKfmsu]", "");
        }

        private static HttpClient _http = new HttpClient();
        public static JsonElement Request(string endpoint, object? payload = null)
        {
            StringContent? body = payload == null ? null : new StringContent(
                JsonSerializer.Serialize(payload).ToString(), Encoding.UTF8, "application/json");
            Task<HttpResponseMessage> apiTask = body != null
                ? _http.PostAsync($"{Percy.CLI_API}{endpoint}", body)
                : _http.GetAsync($"{Percy.CLI_API}{endpoint}");
            apiTask.Wait();

            HttpResponseMessage response = apiTask.Result;
            response.EnsureSuccessStatusCode();

            Task<string> contentTask = response.Content.ReadAsStringAsync();
            contentTask.Wait();

            return JsonSerializer.Deserialize<JsonElement>(contentTask.Result);
        }

        private static void AssertLogs(List<string> expected, List<string> logs) {
            foreach (int i in expected.Select((v, i) => i)) {
                if (Regex.IsMatch(expected[i], @".*userAgent.*")) {
                    Assert.Matches(expected[i], logs[i]);
                    continue;    
                }
                Assert.Equal(expected[i], logs[i]);
            }
        }

        [Fact]
        public void DisablesSnapshotsWhenHealthcheckFails()
        {
            Request("/test/api/disconnect", "/percy/healthcheck");

            Percy.Snapshot(driver, "Snapshot 1");
            Percy.Snapshot(driver, "Snapshot 2");

            Assert.Equal("[percy] Percy is not running, disabling snapshots\n", Stdout());
        }

        [Fact]
        public void DisablesSnapshotsWhenHealthcheckVersionIsMissing()
        {
            Request("/test/api/version", false);

            Percy.Snapshot(driver, "Snapshot 1");
            Percy.Snapshot(driver, "Snapshot 2");

            Assert.Equal(
                "[percy] You may be using @percy/agent " +
                "which is no longer supported by this SDK. " +
                "Please uninstall @percy/agent and install @percy/cli instead. " +
                "https://www.browserstack.com/docs/percy/migration/migrate-to-cli\n",
                Stdout()
            );
        }

        [Fact]
        public void DisablesSnapshotsWhenHealthcheckVersionIsUnsupported()
        {
            Request("/test/api/version", "0.0.1");

            Percy.Snapshot(driver, "Snapshot 1");
            Percy.Snapshot(driver, "Snapshot 2");

            Assert.Equal("[percy] Unsupported Percy CLI version, 0.0.1\n", Stdout());
        }

        [Fact]
        public void PostsSnapshotsToLocalPercyServer()
        {
            Percy.Snapshot(driver, "Snapshot 1");
            Percy.Snapshot(driver, "Snapshot 2", new {
                    enableJavaScript = true
                });
            Percy.Snapshot(driver, "Snapshot 3", new Percy.Options {
                { "enableJavaScript", true }
                });

            JsonElement data = Request("/test/logs");
            List<string> logs = new List<string>();

            foreach (JsonElement log in data.GetProperty("logs").EnumerateArray())
            {
                string? msg = log.GetProperty("message").GetString();
                if (msg != null) logs.Add(msg);
            }

            List<string> expected = new List<string> {
                "---------",
                "Received snapshot: Snapshot 1",
                "- url: http://localhost:5338/test/snapshot",
                "- widths: 375px, 1280px",
                "- minHeight: 1024px",
                "- enableJavaScript: false",
                "- cliEnableJavaScript: true",
                "- disableShadowDOM: false",
                "- discovery.allowedHostnames: localhost",
                "- discovery.captureMockedServiceWorker: false",
                $"- clientInfo: {Percy.CLIENT_INFO}",
                $"- environmentInfo: {Percy.ENVIRONMENT_INFO}",
                "- domSnapshot: true",
                @"- domSnapshot\.userAgent: Mozilla\/5\.0 \(.*\) Gecko\/\d{8} Firefox\/\d+\.\d+",
                "Snapshot found: Snapshot 1",
                "---------",
                "Received snapshot: Snapshot 2",
                "- url: http://localhost:5338/test/snapshot",
                "- widths: 375px, 1280px",
                "- minHeight: 1024px",
                "- enableJavaScript: true",
                "- cliEnableJavaScript: true",
                "- disableShadowDOM: false",
                "- discovery.allowedHostnames: localhost",
                "- discovery.captureMockedServiceWorker: false",
                $"- clientInfo: {Percy.CLIENT_INFO}",
                $"- environmentInfo: {Percy.ENVIRONMENT_INFO}",
                "- domSnapshot: true",
                @"- domSnapshot\.userAgent: Mozilla\/5\.0 \(.*\) Gecko\/\d{8} Firefox\/\d+\.\d+",
                "Snapshot found: Snapshot 2",
                "---------",
                "Received snapshot: Snapshot 3",
                "- url: http://localhost:5338/test/snapshot",
                "- widths: 375px, 1280px",
                "- minHeight: 1024px",
                "- enableJavaScript: true",
                "- cliEnableJavaScript: true",
                "- disableShadowDOM: false",
                "- discovery.allowedHostnames: localhost",
                "- discovery.captureMockedServiceWorker: false",
                $"- clientInfo: {Percy.CLIENT_INFO}",
                $"- environmentInfo: {Percy.ENVIRONMENT_INFO}",
                "- domSnapshot: true",
                @"- domSnapshot\.userAgent: Mozilla\/5\.0 \(.*\) Gecko\/\d{8} Firefox\/\d+\.\d+",
                "Snapshot found: Snapshot 3",
            };

            AssertLogs(expected, logs);
        }

        [Fact]
        public void PostsSnapshotWithSync()
        {
            Percy.Snapshot(driver, "Snapshot 1", new {
                    sync = true
                });

            JsonElement data = Request("/test/logs");
            List<string> logs = new List<string>();

            foreach (JsonElement log in data.GetProperty("logs").EnumerateArray())
            {
                string? msg = log.GetProperty("message").GetString();
                if (msg != null) logs.Add(msg);
            }
            List<string> expected = new List<string> {
                "---------",
                "Received snapshot: Snapshot 1",
                "- url: http://localhost:5338/test/snapshot",
                "- widths: 375px, 1280px",
                "- minHeight: 1024px",
                "- enableJavaScript: false",
                "- cliEnableJavaScript: true",
                "- disableShadowDOM: false",
                "- discovery.allowedHostnames: localhost",
                "- discovery.captureMockedServiceWorker: false",
                $"- clientInfo: {Percy.CLIENT_INFO}",
                $"- environmentInfo: {Percy.ENVIRONMENT_INFO}",
                "- domSnapshot: true",
                @"- domSnapshot\.userAgent: Mozilla\/5\.0 \(.*\) Gecko\/\d{8} Firefox\/\d+\.\d+",
                "The Synchronous CLI functionality is not compatible with skipUploads option.",
                "Snapshot found: Snapshot 1",
            };

            AssertLogs(expected, logs);
        }

        [Fact]
        public void PostsSnapshotWithResponsiveSnapshotCapture()
        {
            Request("/test/api/widths", new { config = new List<int> {375, 800}, mobile = new List<int> {390} });
            Percy.Snapshot(driver, "Snapshot 1", new {
                    responsiveSnapshotCapture = true
            });

            JsonElement data = Request("/test/logs");
            List<string> logs = new List<string>();

            foreach (JsonElement log in data.GetProperty("logs").EnumerateArray())
            {
                string? msg = log.GetProperty("message").GetString();
                if (msg != null) logs.Add(msg);
            }
            List<string> expected = new List<string> {
                // This happens because of firefox limitation to resize below 450px.
                "[\u001b[35mpercy\u001b[39m] Timed out waiting for window resize event for width 375",
                "[\u001b[35mpercy\u001b[39m] Timed out waiting for window resize event for width 800",
                "[\u001b[35mpercy\u001b[39m] Timed out waiting for window resize event for width 1366",
                "---------",
                "Received snapshot: Snapshot 1",
                "- url: http://localhost:5338/test/snapshot",
                "- widths: 375px, 800px",
                "- minHeight: 1024px",
                "- enableJavaScript: false",
                "- cliEnableJavaScript: true",
                "- disableShadowDOM: false",
                "- discovery.allowedHostnames: localhost",
                "- discovery.captureMockedServiceWorker: false",
                $"- clientInfo: {Percy.CLIENT_INFO}",
                $"- environmentInfo: {Percy.ENVIRONMENT_INFO}",
                "- domSnapshot: true, true, true",
                @"- domSnapshot\.0\.userAgent: Mozilla\/5\.0 \(.*\) Gecko\/\d{8} Firefox\/\d+\.\d+",
                "Snapshot found: Snapshot 1",
            };

            AssertLogs(expected, logs);
        }

        [Fact]
        public void PostsSnapshotWithResponsiveSnapshotCapturWithCLIOptions()
        {
            Request("/test/api/widths", new { responsive = true, config = new List<int> {375, 800} });
            Percy.Snapshot(driver, "Snapshot 2", new {
                widths = new List<int> {500, 900, 1200}
            });

            JsonElement data = Request("/test/logs");
            List<string> logs = new List<string>();

            foreach (JsonElement log in data.GetProperty("logs").EnumerateArray())
            {
                string? msg = log.GetProperty("message").GetString();
                if (msg != null) logs.Add(msg);
            }
            List<string> expected = new List<string> {
                "---------",
                "Received snapshot: Snapshot 2",
                "- url: http://localhost:5338/test/snapshot",
                "- widths: 500px, 900px, 1200px",
                "- minHeight: 1024px",
                "- enableJavaScript: false",
                "- cliEnableJavaScript: true",
                "- disableShadowDOM: false",
                "- discovery.allowedHostnames: localhost",
                "- discovery.captureMockedServiceWorker: false",
                $"- clientInfo: {Percy.CLIENT_INFO}",
                $"- environmentInfo: {Percy.ENVIRONMENT_INFO}",
                "- domSnapshot: true, true, true",
                @"- domSnapshot\.0\.userAgent: Mozilla\/5\.0 \(.*\) Gecko\/\d{8} Firefox\/\d+\.\d+",
                "Snapshot found: Snapshot 2",
            };

            AssertLogs(expected, logs);
        }

        [Fact]
        public void HandlesExceptionsDuringSnapshot()
        {
            Request("/test/api/error", "/percy/snapshot");

            Percy.Snapshot(driver, "Snapshot 1");

            Assert.Contains(
                "[percy] Could not take DOM snapshot \"Snapshot 1\"\n" +
                "[percy] System.Net.Http.HttpRequestException:",
                Stdout()
            );
        }

        [Fact]
        public void PostSnapshotThrowExceptionWithAutomate()
        {
            Func<bool> oldEnabledFn = Percy.Enabled;
            Percy.Enabled = () => true;
            Percy.setSessionType("automate");
            try {
                Percy.Snapshot(driver, "Snapshot 1");
                Assert.Fail("Exception not raised");
            } catch (Exception error) {
                Assert.Equal("Invalid function call - Snapshot(). Please use Screenshot() function while using Percy with Automate. For more information on usage of Screenshot, refer https://www.browserstack.com/docs/percy/integrate/functional-and-visual", error.Message);
            }
            Percy.Enabled = oldEnabledFn;
        }
    }
}
