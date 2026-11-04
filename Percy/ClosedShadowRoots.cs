using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;

namespace PercyIO.Selenium
{
    public static partial class Percy
    {
        internal static string? ClosedShadowMaxHostsSetting = Environment.GetEnvironmentVariable("PERCY_CLOSED_SHADOW_MAX_HOSTS");
        internal static string? ClosedShadowClassicSetting = Environment.GetEnvironmentVariable("PERCY_CLOSED_SHADOW_CLASSIC");
        private static bool _remoteClassicSkipLogged = false;
        private const int DefaultClosedShadowMaxHosts = 200;
        private const int MaxClosedShadowNesting = 10;

        private static void ResetClosedShadowState()
        {
            _remoteClassicSkipLogged = false;
        }

        // Custom elements whose shadowRoot reads as null (closed, or none at all)
        // under the given scopes, descending into open shadow roots.
        private const string ClosedShadowCandidatesFn =
            "function percyClosedShadowCandidates(scopes) {" +
            "  var out = [], stack = scopes.slice();" +
            "  while (stack.length) {" +
            "    var els = stack.pop().querySelectorAll('*');" +
            "    for (var i = 0; i < els.length; i++) {" +
            "      if (els[i].shadowRoot) stack.push(els[i].shadowRoot);" +
            "      else if (els[i].localName.indexOf('-') !== -1) out.push(els[i]);" +
            "    }" +
            "  }" +
            "  return out;" +
            "}";

        private const string SetClosedShadowRootFn =
            "var percyClosedShadowRoots = window.__percyClosedShadowRoots = window.__percyClosedShadowRoots || new WeakMap();";

        // Two script.callFunction calls per nesting level: one returns candidate
        // hosts serialized with their (closed) shadow roots, one stores the pairs.
        private static bool ExposeClosedShadowRootsViaBiDi(WebDriver driver)
        {
            try
            {
                using var bidi = BiDiSession.Open(driver);
                string context = driver.CurrentWindowHandle;
                var scopes = new List<string>();
                int exposed = 0;
                for (int level = 0; level < MaxClosedShadowNesting; level++)
                {
                    object? candidates = bidi.CallFunction(context,
                        "function() { var scopes = Array.prototype.slice.call(arguments);" +
                        " return (" + ClosedShadowCandidatesFn + ")(scopes.length ? scopes : [document]); }",
                        scopes, includeShadowTree: true);
                    var pairs = BiDiSession.ClosedShadowRootPairs(candidates);
                    if (pairs.Count == 0) break;

                    bidi.CallFunction(context,
                        "function() { " + SetClosedShadowRootFn +
                        " for (var i = 0; i < arguments.length; i += 2) percyClosedShadowRoots.set(arguments[i], arguments[i + 1]); }",
                        pairs.SelectMany(p => new[] { p.host, p.root }), includeShadowTree: false);
                    exposed += pairs.Count;
                    scopes = pairs.Select(p => p.root).ToList();
                }
                if (exposed > 0) Log($"Exposed {exposed} closed shadow root(s) via BiDi", "debug");
                return true;
            }
            catch (Exception e)
            {
                Log($"Could not expose closed shadow roots via BiDi: {UnwrapMessage(e)}", "debug");
                return false;
            }
        }

        // One WebDriver round trip per candidate host, so this only runs against a
        // local driver unless the user opts in; on a remote grid it can take minutes.
        private static void ExposeClosedShadowRootsViaClassic(WebDriver driver)
        {
            if (!IsLocalDriver(driver) &&
                !"true".Equals(ClosedShadowClassicSetting, StringComparison.OrdinalIgnoreCase))
            {
                if (!_remoteClassicSkipLogged)
                {
                    _remoteClassicSkipLogged = true;
                    Log("closed shadow roots skipped on remote session; start the session with webSocketUrl: true or set PERCY_CLOSED_SHADOW_CLASSIC=true");
                }
                return;
            }

            try
            {
                int maxHosts = int.TryParse(ClosedShadowMaxHostsSetting, out int configured) && configured >= 0
                    ? configured
                    : DefaultClosedShadowMaxHosts;
                var queue = new Queue<IWebElement>(AsElements(driver.ExecuteScript(
                    ClosedShadowCandidatesFn + " return percyClosedShadowCandidates([document]);")));
                int checkedHosts = 0, exposed = 0;
                while (queue.Count > 0)
                {
                    if (checkedHosts >= maxHosts)
                    {
                        Log($"Closed shadow root host limit {maxHosts} reached; {queue.Count} candidate(s) not checked", "debug");
                        break;
                    }
                    IWebElement host = queue.Dequeue();
                    checkedHosts++;

                    ISearchContext? root;
                    // NoSuchShadowRoot for elements without a root; Safari returns
                    // null instead of a closed root, which Selenium also rejects.
                    try { root = host.GetShadowRoot(); }
                    catch (WebDriverException) { continue; }

                    foreach (IWebElement nested in AsElements(driver.ExecuteScript(
                        ClosedShadowCandidatesFn + SetClosedShadowRootFn +
                        " percyClosedShadowRoots.set(arguments[0], arguments[1]);" +
                        " return percyClosedShadowCandidates([arguments[1]]);", host, root)))
                        queue.Enqueue(nested);
                    exposed++;
                }
                if (exposed > 0) Log($"Exposed {exposed} closed shadow root(s) via WebDriver classic ({checkedHosts} host(s) checked)", "debug");
            }
            catch (Exception e)
            {
                Log($"Could not expose closed shadow roots via WebDriver classic: {e.Message}", "warn");
            }
        }

        private static IEnumerable<IWebElement> AsElements(object? scriptResult) =>
            (scriptResult as IEnumerable)?.OfType<IWebElement>() ?? Enumerable.Empty<IWebElement>();

        internal static bool IsLocalDriver(WebDriver driver)
        {
            ICommandExecutor executor = driver.CommandExecutor;
            if (executor is DriverServiceCommandExecutor) return true;
            Uri? uri = executor is HttpCommandExecutor http
                ? typeof(HttpCommandExecutor).GetField("remoteServerUri", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(http) as Uri
                : null;
            string host = uri?.Host.Trim('[', ']') ?? "";
            return host == "localhost" || host == "127.0.0.1" || host == "::1";
        }
    }
}
