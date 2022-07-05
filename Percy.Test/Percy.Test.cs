using Xunit;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenQA.Selenium;
using OpenQA.Selenium.Firefox;
using WebDriverManager;
using WebDriverManager.DriverConfigs.Impl;
using Percy.Selenium;

namespace Percy.Selenium.Tests
{
    public abstract class TestsBase : IDisposable
    {
        public readonly FirefoxDriver driver;
        private readonly StringWriter _stdout;

        public TestsBase()
        {
            new DriverManager().SetUpDriver(new FirefoxConfig());
            FirefoxOptions options = new FirefoxOptions();
            options.LogLevel = FirefoxDriverLogLevel.Fatal;
            options.AddArgument("--headless");

            driver = new FirefoxDriver(options);
            driver.Navigate().GoToUrl($"{Percy.CLI_API}/test/snapshot");

            _stdout = new StringWriter();
            Console.SetOut(_stdout);

            Percy.ResetInternalCaches();
            Request("/test/api/reset");
        }

        public void Dispose()
        {
            driver.Quit();
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
    }

    public class UnitTests : TestsBase
    {
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
                "https://docs.percy.io/docs/migrating-to-percy-cli\n",
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
                if (msg != null && (msg[0] == '-' || msg.StartsWith("Snapshot"))) logs.Add(msg);
            }

            Assert.Equal(new List<string> {
                "---------",
                "Snapshot found: Snapshot 1",
                "- url: http://localhost:5338/test/snapshot",
                "- widths: 375px, 1280px",
                "- minHeight: 1024px",
                "- discovery.allowedHostnames: localhost",
                "- clientInfo: percy-selenium-dotnet/1.0.0",
                $"- environmentInfo: dotnet/{Environment.Version}",
                "- domSnapshot: true",
                "---------",
                "Snapshot found: Snapshot 2",
                "- url: http://localhost:5338/test/snapshot",
                "- widths: 375px, 1280px",
                "- minHeight: 1024px",
                "- enableJavaScript: true",
                "- discovery.allowedHostnames: localhost",
                "- clientInfo: percy-selenium-dotnet/1.0.0",
                $"- environmentInfo: dotnet/{Environment.Version}",
                "- domSnapshot: true",
                "---------",
                "Snapshot found: Snapshot 3",
                "- url: http://localhost:5338/test/snapshot",
                "- widths: 375px, 1280px",
                "- minHeight: 1024px",
                "- enableJavaScript: true",
                "- discovery.allowedHostnames: localhost",
                "- clientInfo: percy-selenium-dotnet/1.0.0",
                $"- environmentInfo: dotnet/{Environment.Version}",
                "- domSnapshot: true"
            }, logs);
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
    }
}
