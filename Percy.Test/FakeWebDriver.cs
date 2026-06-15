using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;

namespace PercyIO.Selenium.Tests
{
    // In-process fake of Selenium's abstract WebDriver. The real WebDriver routes
    // every command (ExecuteScript, FindElements, SwitchTo, Manage().Window,
    // Manage().Cookies, Manage().Timeouts, Navigate().Refresh, etc.) through the
    // single protected virtual ExecuteAsync(command, parameters) chokepoint. By
    // overriding ONLY that method we drive all of the real, sealed public Selenium
    // code paths (response decoding, element materialisation, option managers)
    // without launching a browser and without touching any production code.
    //
    // A pluggable Handler returns canned command results; commands issued are
    // recorded for assertions.
    internal class FakeWebDriver : WebDriver
    {
        public readonly List<string> Commands = new List<string>();
        public readonly List<string> Scripts = new List<string>();
        public Func<string, Dictionary<string, object>, object> Handler;

        private static ICommandExecutor DefaultExecutor() =>
            new HttpCommandExecutor(new Uri("http://hub-cloud.browserstack.com/wd/hub/"), TimeSpan.FromSeconds(60));

        public FakeWebDriver(ICapabilities capabilities)
            : base(DefaultExecutor(), capabilities) { }

        public FakeWebDriver(ICommandExecutor executor, ICapabilities capabilities)
            : base(executor, capabilities) { }

        protected override Task<Response> ExecuteAsync(string command, Dictionary<string, object> parameters)
        {
            Commands.Add(command);
            if (parameters != null && parameters.TryGetValue("script", out var s) && s != null)
                Scripts.Add(s.ToString());

            var response = new Response { Status = WebDriverResult.Success, SessionId = "sess-123" };

            if (command == DriverCommand.NewSession)
            {
                // Echo the requested firstMatch capabilities back as the session's
                // returned capabilities so the real Selenium Capabilities property
                // (and PercySeleniumDriver's reflection over it) sees browserName etc.
                var caps = new Dictionary<string, object>();
                if (parameters != null
                    && parameters.TryGetValue("capabilities", out var co) && co is Dictionary<string, object> cd
                    && cd.TryGetValue("firstMatch", out var fm) && fm is List<object> fml
                    && fml.Count > 0 && fml[0] is Dictionary<string, object> first)
                {
                    caps = first;
                }
                response.Value = caps;
                return Task.FromResult(response);
            }

            object value = Handler != null ? Handler(command, parameters) : DefaultResult(command, parameters);
            response.Value = value;
            return Task.FromResult(response);
        }

        private object DefaultResult(string command, Dictionary<string, object> parameters)
        {
            if (command == DriverCommand.GetWindowRect || command == DriverCommand.SetWindowRect)
                return new Dictionary<string, object> { { "width", 1280L }, { "height", 1024L }, { "x", 0L }, { "y", 0L } };
            if (command == DriverCommand.GetTimeouts)
                return new Dictionary<string, object> { { "implicit", 0L }, { "pageLoad", 300000L }, { "script", 30000L } };
            if (command == DriverCommand.GetAllCookies)
                return new object[0];
            if (command == DriverCommand.FindElements)
                return new object[0];
            if (command == DriverCommand.GetCurrentUrl)
                return "http://localhost:5338/page";
            return null;
        }

        protected override Dictionary<string, object> GetCapabilitiesDictionary(ICapabilities capabilitiesToConvert) =>
            capabilitiesToConvert is FakeCapabilities fc ? fc.Snapshot() : new Dictionary<string, object>();
    }

    // Chrome-flavoured fake: exposes an ExecuteCdpCommand method (discovered via
    // reflection by Percy.TryResizeWithCdp) so the CDP resize branch is exercised.
    internal class FakeChromeWebDriver : FakeWebDriver
    {
        public readonly List<string> CdpCommands = new List<string>();
        public bool CdpThrows = false;

        public FakeChromeWebDriver(ICapabilities capabilities) : base(capabilities) { }

        public object ExecuteCdpCommand(string commandName, Dictionary<string, object> commandParameters)
        {
            CdpCommands.Add(commandName);
            if (CdpThrows) throw new WebDriverException("cdp boom");
            return new Dictionary<string, object>();
        }
    }

    // Minimal ICapabilities backed by a dictionary. The capabilities object's
    // private "capabilities" field is read reflectively by PercySeleniumDriver;
    // here that field exists on Selenium's ReturnedCapabilities, which the real
    // IHasCapabilities.Capabilities returns, so PercySeleniumDriver works against
    // the actual production reflection path.
    internal class FakeCapabilities : ICapabilities
    {
        private readonly Dictionary<string, object> _caps;
        public FakeCapabilities(Dictionary<string, object> caps) { _caps = caps; }
        public object this[string capabilityName] =>
            _caps.TryGetValue(capabilityName, out var v) ? v : throw new ArgumentException(capabilityName);
        public bool HasCapability(string capability) => _caps.ContainsKey(capability);
        public object GetCapability(string capability) => _caps.TryGetValue(capability, out var v) ? v : null;
        public Dictionary<string, object> Snapshot() => new Dictionary<string, object>(_caps);
    }

    internal static class FakeDriverFactory
    {
        // WebElement.GetAttribute(name) runs the get-attribute atom via ExecuteScript;
        // the attribute name is the 2nd entry of parameters["args"]. Handlers use this
        // to distinguish e.g. "src" vs "data-percy-element-id" lookups.
        public static string AttributeName(Dictionary<string, object> parameters)
        {
            if (parameters != null && parameters.TryGetValue("args", out var a) && a is System.Collections.IEnumerable e)
            {
                var items = e.Cast<object>().ToList();
                if (items.Count >= 2 && items[1] is string s) return s;
            }
            return null;
        }

        public static readonly object IframeElement =
            new Dictionary<string, object> { { "element-6066-11e4-a52e-4f735466cecf", "iframe-element-1" } };

        public static object MakeElement(string id) =>
            new Dictionary<string, object> { { "element-6066-11e4-a52e-4f735466cecf", id } };

        public static FakeCapabilities FirefoxCaps() =>
            new FakeCapabilities(new Dictionary<string, object> { { "browserName", "firefox" } });

        public static FakeCapabilities ChromeCaps() =>
            new FakeCapabilities(new Dictionary<string, object> { { "browserName", "chrome" } });
    }
}
