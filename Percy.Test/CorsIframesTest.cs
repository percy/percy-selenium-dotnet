using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;
using Xunit;

namespace PercyIO.Selenium.Tests
{
    // Collection definition used to serialize tests that mutate Percy's
    // static _http field. xunit.runner.json already disables parallelization
    // across collections, but the explicit Collection attribute pins the
    // invariant so a future flip back to parallel test collections doesn't
    // silently race GetHttpClient_* against any other suite that touches
    // setHttpClient / _http (Percy.Test.cs and PercyDriver.Test.cs both do).
    [CollectionDefinition("HttpClientStateSerial", DisableParallelization = true)]
    public class HttpClientStateSerialCollection { }

    // Unit tests for the CORS iframe + closed shadow DOM helpers added to
    // Percy.cs. These don't require the Percy CLI or a real browser; they
    // exercise the pure-C# helpers via reflection where they are internal.
    [Collection("HttpClientStateSerial")]
    public class CorsIframesTest
    {
        // -- GetOrigin -----------------------------------------------------------

        [Fact]
        public void GetOrigin_ExtractsSchemeAndAuthority()
        {
            Assert.Equal("https://example.com", Percy.GetOrigin("https://example.com/some/path?q=1"));
            Assert.Equal("http://localhost:8000", Percy.GetOrigin("http://localhost:8000/page"));
        }

        [Fact]
        public void GetOrigin_ReturnsEmptyForInvalidOrEmptyUrl()
        {
            Assert.Equal("", Percy.GetOrigin(""));
            Assert.Equal("", Percy.GetOrigin(null));
            Assert.Equal("", Percy.GetOrigin("not a url"));
        }

        // -- IsUnsupportedIframeSrc ---------------------------------------------

        [Theory]
        [InlineData("javascript:void(0)", true)]
        [InlineData("JAVASCRIPT:alert(1)", true)]
        [InlineData("data:text/html,<p/>", true)]
        [InlineData("vbscript:foo()", true)]
        [InlineData("about:blank", true)]
        [InlineData("About:Blank", true)]
        [InlineData("blob:https://example.com/abc", true)]
        [InlineData("file:///etc/passwd", true)]
        [InlineData("FILE:///C:/Users", true)]
        [InlineData("chrome://settings", true)]
        [InlineData("view-source:https://example.com", true)]
        [InlineData("", true)]
        [InlineData(null, true)]
        [InlineData("https://example.com/x", false)]
        [InlineData("http://localhost/page", false)]
        public void IsUnsupportedIframeSrc_RecognizesUnsupportedSchemes(string? src, bool expected)
        {
            Assert.Equal(expected, Percy.IsUnsupportedIframeSrc(src));
        }

        // -- ClampFrameDepth -----------------------------------------------------

        [Theory]
        [InlineData(0, Percy.DEFAULT_MAX_FRAME_DEPTH)]
        [InlineData(-5, Percy.DEFAULT_MAX_FRAME_DEPTH)]
        [InlineData(3, 3)]
        [InlineData(100, Percy.MAX_ALLOWED_FRAME_DEPTH)]
        public void ClampFrameDepth_AppliesBounds(int input, int expected)
        {
            Assert.Equal(expected, Percy.ClampFrameDepth(input));
        }

        // -- NormalizeIgnoreSelectors -------------------------------------------

        [Fact]
        public void NormalizeIgnoreSelectors_AcceptsSingleString()
        {
            var result = Percy.NormalizeIgnoreSelectors(".ad");
            Assert.Equal(new List<string> { ".ad" }, result);
        }

        [Fact]
        public void NormalizeIgnoreSelectors_AcceptsArrayAndDropsEmpties()
        {
            var result = Percy.NormalizeIgnoreSelectors(new List<string> { ".ad", "", null!, "iframe[data-ad]" });
            Assert.Equal(new List<string> { ".ad", "iframe[data-ad]" }, result);
        }

        [Fact]
        public void NormalizeIgnoreSelectors_ReturnsEmptyOnNull()
        {
            Assert.Empty(Percy.NormalizeIgnoreSelectors(null));
        }

        // -- ShouldSkipIframe ----------------------------------------------------
        //
        // The skip helper is internal — call via reflection so tests live in the
        // same project without changing visibility on production code.
        private static bool InvokeShouldSkipIframe(object iframeInfo, string parentOrigin)
        {
            MethodInfo method = typeof(Percy).GetMethod(
                "ShouldSkipIframe", BindingFlags.Static | BindingFlags.NonPublic)!;
            return (bool)method.Invoke(null, new[] { iframeInfo, parentOrigin })!;
        }

        private static object MakeIframeInfo(string src, string? percyElementId,
            bool dataPercyIgnore = false, bool matchesIgnoreSelector = false, string? srcdoc = null)
        {
            Type t = typeof(Percy).GetNestedType("IframeInfo", BindingFlags.NonPublic)!;
            object info = Activator.CreateInstance(t)!;
            t.GetField("Src")!.SetValue(info, src);
            t.GetField("PercyElementId")!.SetValue(info, percyElementId);
            t.GetField("DataPercyIgnore")!.SetValue(info, dataPercyIgnore);
            t.GetField("MatchesIgnoreSelector")!.SetValue(info, matchesIgnoreSelector);
            t.GetField("Srcdoc")!.SetValue(info, srcdoc);
            return info;
        }

        [Fact]
        public void ShouldSkipIframe_SkipsDataPercyIgnore()
        {
            var info = MakeIframeInfo("https://cross.example.com", "p-1", dataPercyIgnore: true);
            Assert.True(InvokeShouldSkipIframe(info, "https://parent.example.com"));
        }

        [Fact]
        public void ShouldSkipIframe_SkipsMatchesIgnoreSelector()
        {
            var info = MakeIframeInfo("https://ads.example.com", "p-2", matchesIgnoreSelector: true);
            Assert.True(InvokeShouldSkipIframe(info, "https://parent.example.com"));
        }

        [Fact]
        public void ShouldSkipIframe_SkipsUnsupportedSrc()
        {
            var info = MakeIframeInfo("javascript:void(0)", "p-3");
            Assert.True(InvokeShouldSkipIframe(info, "https://parent.example.com"));
        }

        [Fact]
        public void ShouldSkipIframe_SkipsSrcdoc()
        {
            var info = MakeIframeInfo("https://cross.example.com", "p-4", srcdoc: "<p>x</p>");
            Assert.True(InvokeShouldSkipIframe(info, "https://parent.example.com"));
        }

        [Fact]
        public void ShouldSkipIframe_SkipsSameOrigin()
        {
            var info = MakeIframeInfo("https://parent.example.com/iframe", "p-5");
            Assert.True(InvokeShouldSkipIframe(info, "https://parent.example.com"));
        }

        [Fact]
        public void ShouldSkipIframe_SkipsMissingPercyElementId()
        {
            var info = MakeIframeInfo("https://cross.example.com", percyElementId: null);
            Assert.True(InvokeShouldSkipIframe(info, "https://parent.example.com"));
        }

        [Fact]
        public void ShouldSkipIframe_AllowsCrossOriginWithPercyElementId()
        {
            var info = MakeIframeInfo("https://cross.example.com/x", "p-6");
            Assert.False(InvokeShouldSkipIframe(info, "https://parent.example.com"));
        }

        // Origin is compared to the IMMEDIATE parent, not the top-level page —
        // a frame whose origin matches an ancestor higher up the chain should
        // still be considered cross-origin from its parent.
        [Fact]
        public void ShouldSkipIframe_ComparesAgainstImmediateParentOrigin()
        {
            // Parent = http://b, child src points back to http://a (the top page).
            // From the parent's perspective the child is cross-origin and should
            // be captured.
            var info = MakeIframeInfo("http://a.example.com/page", "p-7");
            Assert.False(InvokeShouldSkipIframe(info, "http://b.example.com"));
        }

        // -- PercyContextLostException -------------------------------------------

        [Fact]
        public void PercyContextLostException_CarriesPartialCapture()
        {
            var ex = new Percy.PercyContextLostException("ctx lost");
            ex.PartialCapture.Add(new Dictionary<string, object> { ["frameUrl"] = "http://a/" });
            Assert.Single(ex.PartialCapture);
            Assert.Equal("http://a/", ex.PartialCapture[0]["frameUrl"]);
        }

        // -- HttpClient init invariant -------------------------------------------

        [Fact]
        public void GetHttpClient_AlwaysReturnsClientWithTenMinuteTimeout()
        {
            // The newly-volatile _http field exists so the unlocked outer
            // read in getHttpClient is guaranteed to see a fully-published
            // HttpClient with Timeout already set. Force the first-caller
            // path and confirm the invariant.
            var field = typeof(Percy).GetField("_http",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            field!.SetValue(null, null);

            var method = typeof(Percy).GetMethod("getHttpClient",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var client = (System.Net.Http.HttpClient)method!.Invoke(null, null)!;

            Assert.NotNull(client);
            Assert.Equal(TimeSpan.FromMinutes(10), client.Timeout);
        }

        [Fact]
        public void GetHttpClient_IsIdempotentAcrossCalls()
        {
            var field = typeof(Percy).GetField("_http",
                BindingFlags.NonPublic | BindingFlags.Static);
            field!.SetValue(null, null);

            var method = typeof(Percy).GetMethod("getHttpClient",
                BindingFlags.NonPublic | BindingFlags.Static);
            var first = method!.Invoke(null, null);
            var second = method.Invoke(null, null);

            Assert.Same(first, second);
        }

        // -- CollectClosedShadowRoots same-origin iframe recursion --------------
        //
        // The CDP DOM walker used to early-return on any node with a
        // contentDocument, silently skipping closed shadow DOM inside
        // same-origin iframes. The new behavior recurses into same-origin
        // contentDocument trees (same JS realm, same WeakMap) while still
        // skipping cross-origin frames.
        private static Dictionary<string, object> Node(long backendNodeId,
            List<Dictionary<string, object>>? children = null,
            List<Dictionary<string, object>>? shadowRoots = null,
            Dictionary<string, object>? contentDocument = null)
        {
            var n = new Dictionary<string, object>
            {
                ["backendNodeId"] = backendNodeId
            };
            if (children != null) n["children"] = children;
            if (shadowRoots != null) n["shadowRoots"] = shadowRoots;
            if (contentDocument != null) n["contentDocument"] = contentDocument;
            return n;
        }

        private static Dictionary<string, object> ClosedShadowRoot(long backendNodeId) =>
            new Dictionary<string, object>
            {
                ["backendNodeId"] = backendNodeId,
                ["shadowRootType"] = "closed"
            };

        [Fact]
        public void CollectClosedShadowRoots_RecursesIntoSameOriginContentDocument()
        {
            // host (id=10) hosts a closed shadow root (id=11), nested inside an
            // iframe whose contentDocument is same-origin with the page.
            var sameOriginIframe = Node(
                backendNodeId: 5,
                contentDocument: new Dictionary<string, object>
                {
                    ["backendNodeId"] = 6,
                    ["documentURL"] = "https://example.com/iframe-page",
                    ["children"] = new List<Dictionary<string, object>> {
                        Node(10, shadowRoots: new List<Dictionary<string, object>> { ClosedShadowRoot(11) })
                    }
                });
            var root = Node(1, children: new List<Dictionary<string, object>> { sameOriginIframe });

            var pairs = new List<(long, long)>();
            Percy.CollectClosedShadowRoots(root, pairs, "https://example.com");

            Assert.Single(pairs);
            Assert.Equal((10L, 11L), pairs[0]);
        }

        [Fact]
        public void CollectClosedShadowRoots_SkipsCrossOriginContentDocument()
        {
            var crossOriginIframe = Node(
                backendNodeId: 5,
                contentDocument: new Dictionary<string, object>
                {
                    ["backendNodeId"] = 6,
                    ["documentURL"] = "https://other.example.com/iframe-page",
                    ["children"] = new List<Dictionary<string, object>> {
                        Node(10, shadowRoots: new List<Dictionary<string, object>> { ClosedShadowRoot(11) })
                    }
                });
            var root = Node(1, children: new List<Dictionary<string, object>> { crossOriginIframe });

            var pairs = new List<(long, long)>();
            Percy.CollectClosedShadowRoots(root, pairs, "https://example.com");

            Assert.Empty(pairs);
        }

        [Fact]
        public void CollectClosedShadowRoots_SkipsContentDocumentWithMissingDocumentUrl()
        {
            // Defensive fallback: if documentURL is absent we can't prove
            // same-origin, so skip — matches pre-fix behavior.
            var iframe = Node(
                backendNodeId: 5,
                contentDocument: new Dictionary<string, object>
                {
                    ["backendNodeId"] = 6,
                    ["children"] = new List<Dictionary<string, object>> {
                        Node(10, shadowRoots: new List<Dictionary<string, object>> { ClosedShadowRoot(11) })
                    }
                });
            var root = Node(1, children: new List<Dictionary<string, object>> { iframe });

            var pairs = new List<(long, long)>();
            Percy.CollectClosedShadowRoots(root, pairs, "https://example.com");

            Assert.Empty(pairs);
        }

        [Fact]
        public void CollectClosedShadowRoots_TopLevelClosedRootStillCaptured()
        {
            // Sanity check: the same-origin-recursion fix mustn't regress the
            // baseline top-level closed shadow root capture.
            var host = Node(10, shadowRoots: new List<Dictionary<string, object>> { ClosedShadowRoot(11) });
            var root = Node(1, children: new List<Dictionary<string, object>> { host });

            var pairs = new List<(long, long)>();
            Percy.CollectClosedShadowRoots(root, pairs, "https://example.com");

            Assert.Single(pairs);
            Assert.Equal((10L, 11L), pairs[0]);
        }

        // -- RunClosedShadowRootExposure: DOM.enable / DOM.disable lifecycle ---
        //
        // Regression coverage for the new domEnabled finally path. The fake
        // CDP invoker records every command issued so the test can assert the
        // expected lifecycle, including the negative case where DOM.disable
        // must NOT be sent if DOM.enable itself failed.
        private sealed class FakeCdp
        {
            public readonly List<string> Commands = new List<string>();
            public Func<string, Dictionary<string, object>, object?> Handler { get; set; }
                = (_, __) => null;

            public object? Invoke(string command, Dictionary<string, object> args)
            {
                Commands.Add(command);
                return Handler(command, args);
            }
        }

        private static Dictionary<string, object> MinimalGetDocumentResponse() =>
            new Dictionary<string, object>
            {
                ["root"] = new Dictionary<string, object>
                {
                    ["backendNodeId"] = 1L,
                    // No shadowRoots and no children -> walker finds no pairs,
                    // so the resolveNode / Runtime.callFunctionOn branch is
                    // skipped and we get a clean DOM.enable / DOM.disable pair.
                }
            };

        [Fact]
        public void RunClosedShadowRootExposure_CallsDomDisableAfterSuccess()
        {
            var fake = new FakeCdp();
            fake.Handler = (cmd, args) => cmd == "DOM.getDocument"
                ? MinimalGetDocumentResponse()
                : (object?)null;

            Percy.RunClosedShadowRootExposure(
                fake.Invoke,
                _ => { /* scriptRunner no-op */ },
                () => "https://example.com/");

            Assert.Contains("DOM.enable", fake.Commands);
            Assert.Contains("DOM.disable", fake.Commands);
            // DOM.disable must be the last CDP command sent.
            Assert.Equal("DOM.disable", fake.Commands[fake.Commands.Count - 1]);
        }

        [Fact]
        public void RunClosedShadowRootExposure_CallsDomDisableAfterGetDocumentThrows()
        {
            var fake = new FakeCdp();
            fake.Handler = (cmd, args) =>
            {
                if (cmd == "DOM.getDocument") throw new InvalidOperationException("boom");
                return null;
            };

            // Should swallow the exception and still issue DOM.disable.
            Percy.RunClosedShadowRootExposure(
                fake.Invoke,
                _ => { },
                () => "https://example.com/");

            Assert.Contains("DOM.enable", fake.Commands);
            Assert.Contains("DOM.getDocument", fake.Commands);
            Assert.Contains("DOM.disable", fake.Commands);
        }

        [Fact]
        public void RunClosedShadowRootExposure_DoesNotCallDomDisableWhenDomEnableFails()
        {
            // Critical invariant: if DOM.enable itself threw, domEnabled stays
            // false and the finally block must NOT issue a spurious DOM.disable
            // (which would be sent on a session that never enabled the DOM
            // domain in the first place).
            var fake = new FakeCdp();
            fake.Handler = (cmd, args) =>
            {
                if (cmd == "DOM.enable") throw new InvalidOperationException("session closed");
                return null;
            };

            Percy.RunClosedShadowRootExposure(
                fake.Invoke,
                _ => { },
                () => "https://example.com/");

            Assert.Contains("DOM.enable", fake.Commands);
            Assert.DoesNotContain("DOM.disable", fake.Commands);
            Assert.DoesNotContain("DOM.getDocument", fake.Commands);
        }

        // ===================================================================
        //  Reflection plumbing for the remaining private helpers.
        // ===================================================================

        private static object? InvokePrivateStatic(string name, params object?[] args)
        {
            MethodInfo m = typeof(Percy).GetMethod(
                name, BindingFlags.Static | BindingFlags.NonPublic)!;
            return m.Invoke(null, args);
        }

        private static Type IframeInfoType =>
            typeof(Percy).GetNestedType("IframeInfo", BindingFlags.NonPublic)!;
        private static Type FrameTreeContextType =>
            typeof(Percy).GetNestedType("FrameTreeContext", BindingFlags.NonPublic)!;

        private static object MakeIframeInfoObj(string src, string? percyElementId,
            bool dataPercyIgnore = false, bool matchesIgnoreSelector = false,
            string? srcdoc = null, int index = 0)
        {
            object info = Activator.CreateInstance(IframeInfoType)!;
            IframeInfoType.GetField("Src")!.SetValue(info, src);
            IframeInfoType.GetField("PercyElementId")!.SetValue(info, percyElementId);
            IframeInfoType.GetField("DataPercyIgnore")!.SetValue(info, dataPercyIgnore);
            IframeInfoType.GetField("MatchesIgnoreSelector")!.SetValue(info, matchesIgnoreSelector);
            IframeInfoType.GetField("Srcdoc")!.SetValue(info, srcdoc);
            IframeInfoType.GetField("Index")!.SetValue(info, index);
            return info;
        }

        private static object MakeFrameTreeContext(int maxDepth, List<string>? ignore = null,
            string domJs = "window.__dom='x';")
        {
            object ctx = Activator.CreateInstance(FrameTreeContextType)!;
            FrameTreeContextType.GetField("MaxFrameDepth")!.SetValue(ctx, maxDepth);
            FrameTreeContextType.GetField("IgnoreSelectors")!.SetValue(ctx, ignore ?? new List<string>());
            FrameTreeContextType.GetField("SerializeOptions")!.SetValue(ctx, new Dictionary<string, object>());
            FrameTreeContextType.GetField("DomJs")!.SetValue(ctx, domJs);
            return ctx;
        }

        private static List<Dictionary<string, object>> InvokeProcessFrameTree(
            FakeWebDriver driver, object info, int depth, HashSet<string> ancestors, object ctx)
        {
            MethodInfo m = typeof(Percy).GetMethod(
                "ProcessFrameTree", BindingFlags.Static | BindingFlags.NonPublic)!;
            try
            {
                return (List<Dictionary<string, object>>)m.Invoke(
                    null, new object[] { driver, info, depth, ancestors, ctx })!;
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException!;
            }
        }

        private static List<Dictionary<string, object>> InvokeCaptureCorsIframes(
            FakeWebDriver driver, string pageUrl, object ctx)
        {
            MethodInfo m = typeof(Percy).GetMethod(
                "CaptureCorsIframes", BindingFlags.Static | BindingFlags.NonPublic)!;
            return (List<Dictionary<string, object>>)m.Invoke(
                null, new object[] { driver, pageUrl, ctx })!;
        }

        // A FakeWebDriver that speaks the ENUMERATE_IFRAMES_SCRIPT / querySelector /
        // document.URL / PercyDOM.serialize protocol. `enumResults` is consulted in
        // order: each successive enumerate call pops the next entry (or [] when
        // exhausted). frameUrl is returned by document.URL when switched in.
        private static FakeWebDriver FrameDriver(
            Func<string, Dictionary<string, object>, object?>? scriptOverride = null)
        {
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetCurrentUrl) return "http://localhost:5338/page";
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                if (cmd == DriverCommand.SwitchToFrame) return null;
                if (cmd == DriverCommand.SwitchToParentFrame) return null;
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                    return scriptOverride?.Invoke(cmd, p ?? new Dictionary<string, object>());
                return null;
            };
            return driver;
        }

        private static Dictionary<string, object> EnumEntry(string src, string? pid,
            bool dataPercyIgnore = false, bool matchesIgnoreSelector = false, string? srcdoc = null) =>
            new Dictionary<string, object>
            {
                ["src"] = src,
                ["srcdoc"] = srcdoc!,
                ["percyElementId"] = pid!,
                ["dataPercyIgnore"] = dataPercyIgnore,
                ["matchesIgnoreSelector"] = matchesIgnoreSelector,
                ["index"] = 0L
            };

        // ===================================================================
        //  ResolveMaxFrameDepth
        // ===================================================================

        [Fact]
        public void ResolveMaxFrameDepth_OptionsInt_IsClamped()
        {
            Percy.ResetInternalCaches();
            var opts = new Dictionary<string, object> { ["maxIframeDepth"] = 3 };
            Assert.Equal(3, (int)InvokePrivateStatic("ResolveMaxFrameDepth", opts)!);
        }

        [Fact]
        public void ResolveMaxFrameDepth_OptionsLong_IsClampedToCeiling()
        {
            Percy.ResetInternalCaches();
            var opts = new Dictionary<string, object> { ["maxIframeDepth"] = 100L };
            // 100 > MAX_ALLOWED_FRAME_DEPTH (10) → capped at 10.
            Assert.Equal(Percy.MAX_ALLOWED_FRAME_DEPTH, (int)InvokePrivateStatic("ResolveMaxFrameDepth", opts)!);
        }

        [Fact]
        public void ResolveMaxFrameDepth_OptionsParsableString_IsUsed()
        {
            Percy.ResetInternalCaches();
            var opts = new Dictionary<string, object> { ["maxIframeDepth"] = "4" };
            Assert.Equal(4, (int)InvokePrivateStatic("ResolveMaxFrameDepth", opts)!);
        }

        [Fact]
        public void ResolveMaxFrameDepth_FromCliConfigNumber()
        {
            Percy.ResetInternalCaches();
            Percy.setCliConfig(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                "{\"snapshot\":{\"maxIframeDepth\":2}}"));
            Assert.Equal(2, (int)InvokePrivateStatic("ResolveMaxFrameDepth", new object?[] { null })!);
            Percy.ResetInternalCaches();
        }

        [Fact]
        public void ResolveMaxFrameDepth_DefaultsWhenNoneConfigured()
        {
            Percy.ResetInternalCaches();
            Assert.Equal(Percy.DEFAULT_MAX_FRAME_DEPTH,
                (int)InvokePrivateStatic("ResolveMaxFrameDepth", new object?[] { null })!);
        }

        [Fact]
        public void ResolveMaxFrameDepth_CliConfigWrongType_FallsThroughCatch()
        {
            Percy.ResetInternalCaches();
            // cliConfig is a non-JsonElement object so the (JsonElement) cast throws
            // → caught → falls through to the DEFAULT.
            Percy.setCliConfig(new object());
            Assert.Equal(Percy.DEFAULT_MAX_FRAME_DEPTH,
                (int)InvokePrivateStatic("ResolveMaxFrameDepth", new object?[] { null })!);
            Percy.ResetInternalCaches();
        }

        // ===================================================================
        //  ResolveIgnoreSelectors
        // ===================================================================

        [Fact]
        public void ResolveIgnoreSelectors_FromOptions()
        {
            Percy.ResetInternalCaches();
            var opts = new Dictionary<string, object> { ["ignoreIframeSelectors"] = ".ad" };
            var result = (List<string>)InvokePrivateStatic("ResolveIgnoreSelectors", opts)!;
            Assert.Equal(new List<string> { ".ad" }, result);
        }

        [Fact]
        public void ResolveIgnoreSelectors_FromCliConfigString()
        {
            Percy.ResetInternalCaches();
            Percy.setCliConfig(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                "{\"snapshot\":{\"ignoreIframeSelectors\":\".banner\"}}"));
            var result = (List<string>)InvokePrivateStatic("ResolveIgnoreSelectors", new object?[] { null })!;
            Assert.Equal(new List<string> { ".banner" }, result);
            Percy.ResetInternalCaches();
        }

        [Fact]
        public void ResolveIgnoreSelectors_FromCliConfigArray()
        {
            Percy.ResetInternalCaches();
            Percy.setCliConfig(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                "{\"snapshot\":{\"ignoreIframeSelectors\":[\".x\",\".y\"]}}"));
            var result = (List<string>)InvokePrivateStatic("ResolveIgnoreSelectors", new object?[] { null })!;
            Assert.Equal(new List<string> { ".x", ".y" }, result);
            Percy.ResetInternalCaches();
        }

        [Fact]
        public void ResolveIgnoreSelectors_CliConfigWrongType_FallsThroughCatch()
        {
            Percy.ResetInternalCaches();
            Percy.setCliConfig(new object()); // cast throws → caught → empty
            var result = (List<string>)InvokePrivateStatic("ResolveIgnoreSelectors", new object?[] { null })!;
            Assert.Empty(result);
            Percy.ResetInternalCaches();
        }

        [Fact]
        public void ResolveIgnoreSelectors_NoneConfigured_IsEmpty()
        {
            Percy.ResetInternalCaches();
            var result = (List<string>)InvokePrivateStatic("ResolveIgnoreSelectors", new object?[] { null })!;
            Assert.Empty(result);
        }

        // ===================================================================
        //  EnumerateIframes
        // ===================================================================

        [Fact]
        public void EnumerateIframes_CoercesDictionaryItems()
        {
            Percy.ResetInternalCaches();
            // Handler returns Dictionary<string,object> entries (the shape Selenium
            // actually decodes a JS object literal into) → exercises the
            // Dictionary<string,object> coercion branch in EnumerateIframes.
            var driver = FrameDriver((cmd, p) =>
            {
                string s = p.ContainsKey("script") ? p["script"].ToString()! : "";
                if (s.Contains("document.querySelectorAll('iframe')"))
                    return new object[] { EnumEntry("https://cross.example.com/x", "pid-1") };
                return null;
            });

            MethodInfo m = typeof(Percy).GetMethod(
                "EnumerateIframes", BindingFlags.Static | BindingFlags.NonPublic)!;
            var list = (System.Collections.IList)m.Invoke(null, new object[] { driver, new List<string>() })!;
            Assert.Single(list);
            object info = list[0]!;
            Assert.Equal("https://cross.example.com/x", IframeInfoType.GetField("Src")!.GetValue(info));
            Assert.Equal("pid-1", IframeInfoType.GetField("PercyElementId")!.GetValue(info));
        }

        [Fact]
        public void EnumerateIframes_NonEnumerableResult_ReturnsEmpty()
        {
            Percy.ResetInternalCaches();
            var driver = FrameDriver((cmd, p) =>
            {
                string s = p.ContainsKey("script") ? p["script"].ToString()! : "";
                if (s.Contains("document.querySelectorAll('iframe')")) return 42L; // not enumerable
                return null;
            });

            MethodInfo m = typeof(Percy).GetMethod(
                "EnumerateIframes", BindingFlags.Static | BindingFlags.NonPublic)!;
            var list = (System.Collections.IList)m.Invoke(null, new object[] { driver, new List<string>() })!;
            Assert.Empty(list);
        }

        // ===================================================================
        //  ProcessFrameTree
        // ===================================================================

        [Fact]
        public void ProcessFrameTree_DepthBeyondMax_StopsImmediately()
        {
            Percy.ResetInternalCaches();
            var driver = FrameDriver((cmd, p) => null);
            var ctx = MakeFrameTreeContext(maxDepth: 2);
            var info = MakeIframeInfoObj("https://cross.example.com/x", "pid-1");
            // depth 3 > maxDepth 2 → returns empty, never switches in.
            var result = InvokeProcessFrameTree(driver, info, 3, new HashSet<string>(), ctx);
            Assert.Empty(result);
            Assert.DoesNotContain(DriverCommand.SwitchToFrame, driver.Commands);
        }

        [Fact]
        public void ProcessFrameTree_CyclicAncestor_IsSkipped()
        {
            Percy.ResetInternalCaches();
            var driver = FrameDriver((cmd, p) => null);
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var info = MakeIframeInfoObj("https://cross.example.com/loop", "pid-1");
            var ancestors = new HashSet<string> { "https://cross.example.com/loop" };
            var result = InvokeProcessFrameTree(driver, info, 1, ancestors, ctx);
            Assert.Empty(result);
            Assert.DoesNotContain(DriverCommand.SwitchToFrame, driver.Commands);
        }

        [Fact]
        public void ProcessFrameTree_ElementResolvesNull_LogsAndSkips()
        {
            Percy.ResetInternalCaches();
            var driver = FrameDriver((cmd, p) =>
            {
                string s = p.ContainsKey("script") ? p["script"].ToString()! : "";
                if (s.Contains("querySelector('iframe[data-percy-element-id")) return null; // not found
                return null;
            });
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var info = MakeIframeInfoObj("https://cross.example.com/x", "pid-missing");
            var result = InvokeProcessFrameTree(driver, info, 1, new HashSet<string>(), ctx);
            Assert.Empty(result);
            Assert.DoesNotContain(DriverCommand.SwitchToFrame, driver.Commands);
        }

        [Fact]
        public void ProcessFrameTree_ElementResolveThrows_LogsAndSkips()
        {
            Percy.ResetInternalCaches();
            var driver = FrameDriver((cmd, p) =>
            {
                string s = p.ContainsKey("script") ? p["script"].ToString()! : "";
                if (s.Contains("querySelector('iframe[data-percy-element-id"))
                    throw new WebDriverException("resolve boom");
                return null;
            });
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var info = MakeIframeInfoObj("https://cross.example.com/x", "pid-1");
            var result = InvokeProcessFrameTree(driver, info, 1, new HashSet<string>(), ctx);
            Assert.Empty(result); // resolve threw → element null → skip
            Assert.DoesNotContain(DriverCommand.SwitchToFrame, driver.Commands);
        }

        [Fact]
        public void ProcessFrameTree_HappyPath_CapturesFrameAndRecurses()
        {
            Percy.ResetInternalCaches();
            int enumCalls = 0;
            var driver = FrameDriver((cmd, p) =>
            {
                string s = p.ContainsKey("script") ? p["script"].ToString()! : "";
                if (s.Contains("querySelector('iframe[data-percy-element-id"))
                    return FakeDriverFactory.IframeElement;
                if (s.Contains("document.URL")) return "https://cross.example.com/frame.html";
                if (s.Contains("document.querySelectorAll('iframe')"))
                {
                    enumCalls++;
                    // Inside the frame, one same-origin child → recursion runs but the
                    // child is skipped (same origin as the frame), so we still record
                    // exactly one captured frame. This exercises the recurse branch.
                    return enumCalls >= 1
                        ? new object[] { EnumEntry("https://cross.example.com/child", "pid-child") }
                        : new object[0];
                }
                if (s.Contains("PercyDOM.serialize"))
                    return new Dictionary<string, object> { ["html"] = "<f/>" };
                return null;
            });
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var info = MakeIframeInfoObj("https://cross.example.com/frame.html", "pid-1");
            var result = InvokeProcessFrameTree(driver, info, 1, new HashSet<string> { "http://localhost:5338/page" }, ctx);

            Assert.Single(result);
            Assert.Equal("https://cross.example.com/frame.html", result[0]["frameUrl"]);
            var iframeData = (Dictionary<string, object>)result[0]["iframeData"];
            Assert.Equal("pid-1", iframeData["percyElementId"]);
            Assert.Contains(DriverCommand.SwitchToFrame, driver.Commands);
            Assert.Contains(DriverCommand.SwitchToParentFrame, driver.Commands);
            // The DOM-injection script ran inside the frame.
            Assert.Contains(driver.Scripts, x => x.Contains("window.__dom"));
        }

        // Nested CROSS-origin child is captured and rolled up into the parent's
        // collected list (exercises the `nested.Count > 0 -> AddRange` recurse arm
        // and the post-recursion `return collected`).
        [Fact]
        public void ProcessFrameTree_NestedCrossOriginChild_IsCapturedAndRolledUp()
        {
            Percy.ResetInternalCaches();
            int framesEntered = 0;   // incremented on each SwitchToFrame
            int enumCalls = 0;
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                if (cmd == DriverCommand.SwitchToFrame) { framesEntered++; return null; }
                if (cmd == DriverCommand.SwitchToParentFrame) { framesEntered--; return null; }
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString()! : "";
                    if (s.Contains("querySelector('iframe[data-percy-element-id"))
                        return FakeDriverFactory.IframeElement;
                    if (s.Contains("document.URL"))
                        // Report the URL of the frame we are currently inside: A at
                        // depth 1, B at depth 2. This makes B cross-origin to A so the
                        // nested recursion captures it.
                        return framesEntered >= 2 ? "https://b.example.com/inner.html"
                                                  : "https://a.example.com/frame.html";
                    if (s.Contains("document.querySelectorAll('iframe')"))
                    {
                        enumCalls++;
                        // 1st enum (inside A) → cross-origin child B; deeper → none.
                        return enumCalls == 1
                            ? new object[] { EnumEntry("https://b.example.com/inner.html", "pid-b") }
                            : new object[0];
                    }
                    if (s.Contains("PercyDOM.serialize"))
                        return new Dictionary<string, object> { ["html"] = "<f/>" };
                    return null;
                }
                return null;
            };
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var info = MakeIframeInfoObj("https://a.example.com/frame.html", "pid-a");
            // Parent page (localhost) is the ancestor; A is captured, then B.
            var result = InvokeProcessFrameTree(driver, info, 1,
                new HashSet<string> { "http://localhost:5338/page" }, ctx);

            // Both A and the nested B were captured.
            Assert.Equal(2, result.Count);
            Assert.Equal("https://a.example.com/frame.html", result[0]["frameUrl"]);
            Assert.Equal("https://b.example.com/inner.html", result[1]["frameUrl"]);
        }

        // A non-context-lost exception raised inside the per-frame try (here: the
        // nested EnumerateIframes call throws) is caught by ProcessFrameTree's outer
        // catch, recorded as capturedError, and the partial result returned.
        [Fact]
        public void ProcessFrameTree_NestedEnumerateThrows_OuterCatchReturnsPartial()
        {
            Percy.ResetInternalCaches();
            int enumCalls = 0;
            var driver = FrameDriver((cmd, p) =>
            {
                string s = p.ContainsKey("script") ? p["script"].ToString()! : "";
                if (s.Contains("querySelector('iframe[data-percy-element-id"))
                    return FakeDriverFactory.IframeElement;
                if (s.Contains("document.URL")) return "https://a.example.com/frame.html";
                if (s.Contains("document.querySelectorAll('iframe')"))
                {
                    enumCalls++;
                    // Top-level serialize of A succeeds, then the NESTED enumeration
                    // throws → escapes to ProcessFrameTree's outer catch.
                    if (enumCalls >= 1) throw new WebDriverException("nested enumerate boom");
                    return new object[0];
                }
                if (s.Contains("PercyDOM.serialize"))
                    return new Dictionary<string, object> { ["html"] = "<f/>" };
                return null;
            });
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var info = MakeIframeInfoObj("https://a.example.com/frame.html", "pid-a");
            // The nested enumerate throw is swallowed by the outer catch; the frame
            // captured BEFORE the throw is returned, and we still exit cleanly.
            var result = InvokeProcessFrameTree(driver, info, 1, new HashSet<string>(), ctx);
            Assert.Single(result);
            Assert.Equal("https://a.example.com/frame.html", result[0]["frameUrl"]);
            Assert.Contains(DriverCommand.SwitchToParentFrame, driver.Commands);
        }

        // At depth 1, a ParentFrame restore failure is logged and recovered via
        // DefaultContent — it must NOT escalate to PercyContextLostException
        // (that only happens for depth > 1).
        [Fact]
        public void ProcessFrameTree_ParentFrameFailsAtDepth1_RecoversWithoutThrowing()
        {
            Percy.ResetInternalCaches();
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                if (cmd == DriverCommand.SwitchToFrame) return null;            // also DefaultContent (null frame id)
                if (cmd == DriverCommand.SwitchToParentFrame)
                    throw new WebDriverException("ctx detached");
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString()! : "";
                    if (s.Contains("querySelector('iframe[data-percy-element-id"))
                        return FakeDriverFactory.IframeElement;
                    if (s.Contains("document.URL")) return "https://cross.example.com/frame.html";
                    if (s.Contains("document.querySelectorAll('iframe')")) return new object[0];
                    if (s.Contains("PercyDOM.serialize"))
                        return new Dictionary<string, object> { ["html"] = "<f/>" };
                    return null;
                }
                return null;
            };
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var info = MakeIframeInfoObj("https://cross.example.com/frame.html", "pid-1");

            // depth 1 → ParentFrame throw is recovered (DefaultContent fallback), no
            // PercyContextLostException; the frame captured before the restore stands.
            var result = InvokeProcessFrameTree(driver, info, 1, new HashSet<string>(), ctx);
            Assert.Single(result);
            Assert.Equal("https://cross.example.com/frame.html", result[0]["frameUrl"]);
        }

        [Fact]
        public void ProcessFrameTree_FrameNavigatedToUnsupportedUrl_IsSkipped()
        {
            Percy.ResetInternalCaches();
            var driver = FrameDriver((cmd, p) =>
            {
                string s = p.ContainsKey("script") ? p["script"].ToString()! : "";
                if (s.Contains("querySelector('iframe[data-percy-element-id"))
                    return FakeDriverFactory.IframeElement;
                // After switching in, the document URL is about:blank (unsupported).
                if (s.Contains("document.URL")) return "about:blank";
                if (s.Contains("PercyDOM.serialize"))
                    return new Dictionary<string, object> { ["html"] = "<f/>" };
                return null;
            });
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var info = MakeIframeInfoObj("https://cross.example.com/x", "pid-1");
            var result = InvokeProcessFrameTree(driver, info, 1, new HashSet<string>(), ctx);
            Assert.Empty(result); // unsupported post-switch URL → skip
            Assert.Contains(DriverCommand.SwitchToFrame, driver.Commands);   // switched in
            Assert.Contains(DriverCommand.SwitchToParentFrame, driver.Commands); // and back out
        }

        [Fact]
        public void ProcessFrameTree_UrlReadThrows_FallsBackToSrc()
        {
            Percy.ResetInternalCaches();
            var driver = FrameDriver((cmd, p) =>
            {
                string s = p.ContainsKey("script") ? p["script"].ToString()! : "";
                if (s.Contains("querySelector('iframe[data-percy-element-id"))
                    return FakeDriverFactory.IframeElement;
                if (s.Contains("document.URL")) throw new WebDriverException("no url"); // read throws
                if (s.Contains("document.querySelectorAll('iframe')")) return new object[0];
                if (s.Contains("PercyDOM.serialize"))
                    return new Dictionary<string, object> { ["html"] = "<f/>" };
                return null;
            });
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var info = MakeIframeInfoObj("https://cross.example.com/frame.html", "pid-1");
            var result = InvokeProcessFrameTree(driver, info, 1, new HashSet<string>(), ctx);
            // URL read threw → frameUrl falls back to info.Src; frame still captured.
            Assert.Single(result);
            Assert.Equal("https://cross.example.com/frame.html", result[0]["frameUrl"]);
        }

        [Fact]
        public void ProcessFrameTree_SerializeThrows_LogsAndSkips()
        {
            Percy.ResetInternalCaches();
            var driver = FrameDriver((cmd, p) =>
            {
                string s = p.ContainsKey("script") ? p["script"].ToString()! : "";
                if (s.Contains("querySelector('iframe[data-percy-element-id"))
                    return FakeDriverFactory.IframeElement;
                if (s.Contains("document.URL")) return "https://cross.example.com/frame.html";
                if (s.Contains("PercyDOM.serialize")) throw new WebDriverException("serialize boom");
                return null;
            });
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var info = MakeIframeInfoObj("https://cross.example.com/frame.html", "pid-1");
            var result = InvokeProcessFrameTree(driver, info, 1, new HashSet<string>(), ctx);
            Assert.Empty(result);
            Assert.Contains(DriverCommand.SwitchToParentFrame, driver.Commands);
        }

        [Fact]
        public void ProcessFrameTree_SerializeReturnsNull_LogsAndSkips()
        {
            Percy.ResetInternalCaches();
            var driver = FrameDriver((cmd, p) =>
            {
                string s = p.ContainsKey("script") ? p["script"].ToString()! : "";
                if (s.Contains("querySelector('iframe[data-percy-element-id"))
                    return FakeDriverFactory.IframeElement;
                if (s.Contains("document.URL")) return "https://cross.example.com/frame.html";
                if (s.Contains("PercyDOM.serialize")) return null; // empty result
                return null;
            });
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var info = MakeIframeInfoObj("https://cross.example.com/frame.html", "pid-1");
            var result = InvokeProcessFrameTree(driver, info, 1, new HashSet<string>(), ctx);
            Assert.Empty(result);
        }

        [Fact]
        public void ProcessFrameTree_ParentFrameFailsAtDepth2_RaisesContextLost()
        {
            Percy.ResetInternalCaches();
            // depth 2 > 1 → ParentFrame failure escalates to PercyContextLostException.
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                if (cmd == DriverCommand.SwitchToFrame) return null;
                if (cmd == DriverCommand.SwitchToParentFrame) throw new WebDriverException("ctx detached");
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString()! : "";
                    if (s.Contains("querySelector('iframe[data-percy-element-id"))
                        return FakeDriverFactory.IframeElement;
                    if (s.Contains("document.URL")) return "https://cross.example.com/frame.html";
                    if (s.Contains("document.querySelectorAll('iframe')")) return new object[0];
                    if (s.Contains("PercyDOM.serialize"))
                        return new Dictionary<string, object> { ["html"] = "<f/>" };
                    return null;
                }
                return null;
            };
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var info = MakeIframeInfoObj("https://cross.example.com/frame.html", "pid-1");

            var ex = Assert.Throws<Percy.PercyContextLostException>(() =>
                InvokeProcessFrameTree(driver, info, 2, new HashSet<string>(), ctx));
            // The captured frame is carried in the exception's partial payload.
            Assert.Single(ex.PartialCapture);
            Assert.Equal("https://cross.example.com/frame.html", ex.PartialCapture[0]["frameUrl"]);
        }

        // ===================================================================
        //  CaptureCorsIframes
        // ===================================================================

        [Fact]
        public void CaptureCorsIframes_NoIframes_ReturnsEmpty()
        {
            Percy.ResetInternalCaches();
            var driver = FrameDriver((cmd, p) =>
            {
                string s = p.ContainsKey("script") ? p["script"].ToString()! : "";
                if (s.Contains("document.querySelectorAll('iframe')")) return new object[0];
                return null;
            });
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var result = InvokeCaptureCorsIframes(driver, "http://localhost:5338/page", ctx);
            Assert.Empty(result);
        }

        [Fact]
        public void CaptureCorsIframes_TopLevelCrossOrigin_IsCaptured()
        {
            Percy.ResetInternalCaches();
            int enumCalls = 0;
            var driver = FrameDriver((cmd, p) =>
            {
                string s = p.ContainsKey("script") ? p["script"].ToString()! : "";
                if (s.Contains("document.querySelectorAll('iframe')"))
                {
                    enumCalls++;
                    return enumCalls == 1
                        ? new object[] { EnumEntry("https://cross.example.com/x", "pid-1") }
                        : new object[0];
                }
                if (s.Contains("querySelector('iframe[data-percy-element-id"))
                    return FakeDriverFactory.IframeElement;
                if (s.Contains("document.URL")) return "https://cross.example.com/x";
                if (s.Contains("PercyDOM.serialize"))
                    return new Dictionary<string, object> { ["html"] = "<f/>" };
                return null;
            });
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var result = InvokeCaptureCorsIframes(driver, "http://localhost:5338/page", ctx);
            Assert.Single(result);
        }

        [Fact]
        public void CaptureCorsIframes_EnumerateThrows_OuterCatchSwallows()
        {
            Percy.ResetInternalCaches();
            var driver = FrameDriver((cmd, p) =>
            {
                string s = p.ContainsKey("script") ? p["script"].ToString()! : "";
                if (s.Contains("document.querySelectorAll('iframe')"))
                    throw new WebDriverException("enumerate boom");
                return null;
            });
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            // Outer catch logs and returns whatever was collected (empty) — no throw.
            var result = InvokeCaptureCorsIframes(driver, "http://localhost:5338/page", ctx);
            Assert.Empty(result);
        }

        [Fact]
        public void CaptureCorsIframes_ContextLostFromChild_AbortsWithPartial()
        {
            Percy.ResetInternalCaches();
            // page → frame A → frame B; B's ParentFrame (depth 2) throws → context
            // lost bubbles to CaptureCorsIframes which logs, merges partial, breaks.
            int enumCalls = 0;
            int parentCalls = 0;
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                if (cmd == DriverCommand.SwitchToFrame) return null;
                if (cmd == DriverCommand.SwitchToParentFrame)
                {
                    parentCalls++;
                    if (parentCalls == 1) throw new WebDriverException("ctx detached");
                    return null;
                }
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString()! : "";
                    if (s.Contains("querySelector('iframe[data-percy-element-id"))
                        return FakeDriverFactory.IframeElement;
                    if (s.Contains("document.querySelectorAll('iframe')"))
                    {
                        enumCalls++;
                        if (enumCalls == 1) return new object[] { EnumEntry("https://a.example.com/f", "id-a") };
                        if (enumCalls == 2) return new object[] { EnumEntry("https://b.example.com/g", "id-b") };
                        return new object[0];
                    }
                    if (s.Contains("document.URL"))
                        return enumCalls >= 2 ? "https://a.example.com/f" : "https://a.example.com/f";
                    if (s.Contains("PercyDOM.serialize"))
                        return new Dictionary<string, object> { ["html"] = "<f/>" };
                    return null;
                }
                return null;
            };
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var result = InvokeCaptureCorsIframes(driver, "http://localhost:5338/page", ctx);
            // Partial capture (frame A + frame B before the loss) is returned, no throw.
            Assert.True(result.Count >= 1, $"expected >= 1 partial frame, got {result.Count}");
        }

        // ShouldSkipIframe: a non-empty, supported, non-srcdoc src that nonetheless
        // produces an empty origin (malformed URL) is skipped via the invalid-URL arm.
        [Fact]
        public void ShouldSkipIframe_InvalidUrlOrigin_IsSkipped()
        {
            Percy.ResetInternalCaches();
            var info = MakeIframeInfoObj("http:///nohost", "pid-1"); // GetOrigin → "" (no authority)
            bool skipped = InvokeShouldSkipIframe(info, "https://parent.example.com");
            Assert.True(skipped);
        }

        // ProcessFrameTree: a genuinely-captured nested grandchild is appended to the
        // parent's collected list (exercises `if (nested.Count > 0) AddRange`).
        [Fact]
        public void ProcessFrameTree_NestedGrandchildCaptured_IsAppended()
        {
            Percy.ResetInternalCaches();
            int enumCalls = 0;
            var driver = FrameDriver((cmd, p) =>
            {
                string s = p.ContainsKey("script") ? p["script"].ToString()! : "";
                if (s.Contains("querySelector('iframe[data-percy-element-id"))
                    return FakeDriverFactory.IframeElement;
                if (s.Contains("document.URL"))
                {
                    // 1st switch (parent frame A) → a.example.com; deeper switch
                    // (child B) → b.example.com so B is cross-origin to A.
                    return enumCalls <= 1 ? "https://a.example.com/f" : "https://b.example.com/g";
                }
                if (s.Contains("document.querySelectorAll('iframe')"))
                {
                    enumCalls++;
                    // Inside A (enum #1): yield child B (cross-origin to A).
                    if (enumCalls == 1)
                        return new object[] { EnumEntry("https://b.example.com/g", "pid-b") };
                    // Inside B (enum #2) and beyond: no further frames.
                    return new object[0];
                }
                if (s.Contains("PercyDOM.serialize"))
                    return new Dictionary<string, object> { ["html"] = "<f/>" };
                return null;
            });
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var info = MakeIframeInfoObj("https://a.example.com/f", "pid-a");
            var result = InvokeProcessFrameTree(
                driver, info, 1, new HashSet<string> { "http://localhost:5338/page" }, ctx);

            // Both A (parent) and B (nested grandchild) captured → AddRange ran.
            Assert.Equal(2, result.Count);
        }

        // ProcessFrameTree: an exception thrown AFTER we switched in (here, the nested
        // child enumeration) that is NOT a PercyContextLostException is caught by the
        // generic catch, logged, and the frame's partial collected list returned.
        [Fact]
        public void ProcessFrameTree_GenericExceptionAfterSwitchIn_IsCaught()
        {
            Percy.ResetInternalCaches();
            int enumCalls = 0;
            var driver = FrameDriver((cmd, p) =>
            {
                string s = p.ContainsKey("script") ? p["script"].ToString()! : "";
                if (s.Contains("querySelector('iframe[data-percy-element-id"))
                    return FakeDriverFactory.IframeElement;
                if (s.Contains("document.URL")) return "https://cross.example.com/f";
                if (s.Contains("document.querySelectorAll('iframe')"))
                {
                    enumCalls++;
                    // The child enumeration (inside the frame, after capture) throws a
                    // plain exception → hits ProcessFrameTree's generic catch.
                    throw new InvalidOperationException("child enumerate boom");
                }
                if (s.Contains("PercyDOM.serialize"))
                    return new Dictionary<string, object> { ["html"] = "<f/>" };
                return null;
            });
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var info = MakeIframeInfoObj("https://cross.example.com/f", "pid-1");
            // depth 1 so the generic catch returns collected (the captured frame) and
            // no PercyContextLostException is raised.
            var result = InvokeProcessFrameTree(driver, info, 1, new HashSet<string>(), ctx);
            // The frame itself was captured before the child enumeration threw.
            Assert.Single(result);
            Assert.Contains(DriverCommand.SwitchToParentFrame, driver.Commands);
        }

        // ProcessFrameTree at depth 1: ParentFrame failure does NOT escalate to a
        // PercyContextLostException (depth is not > 1); DefaultContent fallback runs
        // and the failure is swallowed.
        [Fact]
        public void ProcessFrameTree_ParentFrameFailsAtDepth1_SwallowedNoEscalation()
        {
            Percy.ResetInternalCaches();
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            driver.Handler = (cmd, p) =>
            {
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                if (cmd == DriverCommand.SwitchToFrame) return null;          // Frame() AND DefaultContent()
                if (cmd == DriverCommand.SwitchToParentFrame)
                    throw new WebDriverException("parent frame gone");        // ParentFrame() throws
                if (cmd == DriverCommand.ExecuteScript || cmd == DriverCommand.ExecuteAsyncScript)
                {
                    string s = p != null && p.ContainsKey("script") ? p["script"].ToString()! : "";
                    if (s.Contains("querySelector('iframe[data-percy-element-id"))
                        return FakeDriverFactory.IframeElement;
                    if (s.Contains("document.URL")) return "https://cross.example.com/f";
                    if (s.Contains("document.querySelectorAll('iframe')")) return new object[0];
                    if (s.Contains("PercyDOM.serialize"))
                        return new Dictionary<string, object> { ["html"] = "<f/>" };
                    return null;
                }
                return null;
            };
            var ctx = MakeFrameTreeContext(maxDepth: 5);
            var info = MakeIframeInfoObj("https://cross.example.com/f", "pid-1");

            // No exception escapes (depth 1 → no PercyContextLostException); the
            // captured frame is still returned.
            List<Dictionary<string, object>>? result = null;
            var ex = Record.Exception(() =>
                result = InvokeProcessFrameTree(driver, info, 1, new HashSet<string>(), ctx));
            Assert.Null(ex);
            Assert.Single(result!);
            // DefaultContent() fallback (a SwitchToFrame with null id) was attempted.
            Assert.True(driver.Commands.Count(c => c == DriverCommand.SwitchToFrame) >= 2);
        }

        // RunClosedShadowRootExposure: DOM.disable itself throwing in the finally is
        // swallowed by the defensive inner catch.
        [Fact]
        public void RunClosedShadowRootExposure_DomDisableThrows_IsSwallowed()
        {
            Percy.ResetInternalCaches();
            var fake = new FakeCdp();
            fake.Handler = (cmd, args) =>
            {
                if (cmd == "DOM.getDocument") return MinimalGetDocumentResponse();
                if (cmd == "DOM.disable") throw new InvalidOperationException("disable boom");
                return null;
            };
            // The DOM.disable failure in the finally must not surface.
            var ex = Record.Exception(() =>
                Percy.RunClosedShadowRootExposure(fake.Invoke, _ => { }, () => "https://example.com/"));
            Assert.Null(ex);
            Assert.Contains("DOM.disable", fake.Commands);
        }

        // ===================================================================
        //  ExposeClosedShadowRoots (driver-level dispatch)
        // ===================================================================

        [Fact]
        public void ExposeClosedShadowRoots_NonChrome_IsNoOp()
        {
            Percy.ResetInternalCaches();
            var driver = new FakeWebDriver(FakeDriverFactory.FirefoxCaps());
            // Firefox → IsChromeBrowser false → returns before any CDP/script work.
            Percy.ExposeClosedShadowRoots(driver);
            // No scripts and no CDP-related commands were issued (only the implicit
            // NewSession from driver construction may appear).
            Assert.Empty(driver.Scripts);
            Assert.DoesNotContain(DriverCommand.ExecuteScript, driver.Commands);
        }

        [Fact]
        public void ExposeClosedShadowRoots_ChromeWithoutCdpMethod_LogsAndReturns()
        {
            Percy.ResetInternalCaches();
            // Plain FakeWebDriver with chrome caps has NO ExecuteCdpCommand method →
            // reflection lookup yields null → logs + returns, no scripts run.
            var driver = new FakeWebDriver(FakeDriverFactory.ChromeCaps());
            Percy.ExposeClosedShadowRoots(driver);
            Assert.Empty(driver.Scripts);
        }

        [Fact]
        public void ExposeClosedShadowRoots_ChromeWithCdp_DrivesExposurePipeline()
        {
            Percy.ResetInternalCaches();
            // FakeChromeWebDriver exposes ExecuteCdpCommand → reflection finds it →
            // RunClosedShadowRootExposure runs. DOM.getDocument returns a doc with no
            // closed roots, so we get a clean DOM.enable/DOM.disable cycle.
            var driver = new FakeChromeWebDriver(FakeDriverFactory.ChromeCaps());
            Percy.ExposeClosedShadowRoots(driver);
            Assert.Contains("DOM.enable", driver.CdpCommands);
            // getDocResult is an empty dictionary from FakeChromeWebDriver → root
            // missing → RunClosedShadowRootExposure returns after DOM.getDocument,
            // still issuing DOM.disable in the finally.
            Assert.Contains("DOM.getDocument", driver.CdpCommands);
            Assert.Contains("DOM.disable", driver.CdpCommands);
        }

        [Fact]
        public void ExposeClosedShadowRoots_PageUrlGetterThrows_IsSwallowed()
        {
            Percy.ResetInternalCaches();
            // Drive ExposeClosedShadowRoots with a chrome+CDP driver whose Url getter
            // throws. The pageUrlGetter lambda's catch logs "" and the walk proceeds.
            var driver = new FakeChromeWebDriver(FakeDriverFactory.ChromeCaps());
            driver.Handler = (cmd, p) =>
            {
                // driver.Url issues GetCurrentUrl under the hood → make it throw.
                if (cmd == DriverCommand.GetCurrentUrl) throw new WebDriverException("no url");
                if (cmd == DriverCommand.GetAllCookies) return new object[0];
                if (cmd == DriverCommand.FindElements) return new object[0];
                return null;
            };
            // FakeChromeWebDriver.ExecuteCdpCommand returns an empty dict for
            // DOM.getDocument → no root → exposure short-circuits, but the
            // pageUrlGetter is only invoked when a root exists, so to exercise its
            // catch we make CDP return a real root with no closed shadow roots.
            driver.CdpResult = (cmd, args) =>
            {
                if (cmd == "DOM.getDocument")
                    return new Dictionary<string, object>
                    {
                        ["root"] = new Dictionary<string, object> { ["backendNodeId"] = 1L }
                    };
                return new Dictionary<string, object>();
            };
            Percy.ExposeClosedShadowRoots(driver);
            Assert.Contains("DOM.getDocument", driver.CdpCommands);
            Assert.Contains("DOM.disable", driver.CdpCommands);
        }

        // ===================================================================
        //  RunClosedShadowRootExposure: full resolveNode → objectId → callFunctionOn
        // ===================================================================

        [Fact]
        public void RunClosedShadowRootExposure_ResolvesAndExposesClosedRoot()
        {
            Percy.ResetInternalCaches();
            var scripts = new List<string>();
            var fake = new FakeCdp();
            fake.Handler = (cmd, args) =>
            {
                if (cmd == "DOM.getDocument")
                    return new Dictionary<string, object>
                    {
                        ["root"] = new Dictionary<string, object>
                        {
                            ["backendNodeId"] = 1L,
                            ["children"] = new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    ["backendNodeId"] = 10L,
                                    ["shadowRoots"] = new List<object>
                                    {
                                        new Dictionary<string, object>
                                        {
                                            ["backendNodeId"] = 11L,
                                            ["shadowRootType"] = "closed"
                                        }
                                    }
                                }
                            }
                        }
                    };
                if (cmd == "DOM.resolveNode")
                {
                    long id = Convert.ToInt64(args["backendNodeId"]);
                    return new Dictionary<string, object>
                    {
                        ["object"] = new Dictionary<string, object> { ["objectId"] = $"obj-{id}" }
                    };
                }
                return new Dictionary<string, object>();
            };

            Percy.RunClosedShadowRootExposure(
                fake.Invoke,
                s => scripts.Add(s),
                () => "https://example.com/");

            // The closed root was resolved (both host + shadow) and bound via
            // Runtime.callFunctionOn — the happy path master's tests skipped.
            Assert.Equal(2, fake.Commands.Count(c => c == "DOM.resolveNode"));
            Assert.Contains("Runtime.callFunctionOn", fake.Commands);
            Assert.Contains("DOM.disable", fake.Commands);
            // The WeakMap was primed on the page.
            Assert.Contains(scripts, x => x.Contains("__percyClosedShadowRoots"));
        }

        [Fact]
        public void RunClosedShadowRootExposure_ResolveNodeMissingObjectId_SkipsPair()
        {
            Percy.ResetInternalCaches();
            var fake = new FakeCdp();
            fake.Handler = (cmd, args) =>
            {
                if (cmd == "DOM.getDocument")
                    return new Dictionary<string, object>
                    {
                        ["root"] = new Dictionary<string, object>
                        {
                            ["backendNodeId"] = 1L,
                            ["children"] = new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    ["backendNodeId"] = 10L,
                                    ["shadowRoots"] = new List<object>
                                    {
                                        new Dictionary<string, object>
                                        {
                                            ["backendNodeId"] = 11L,
                                            ["shadowRootType"] = "closed"
                                        }
                                    }
                                }
                            }
                        }
                    };
                // resolveNode returns no "object" → ExtractObjectId null → pair skipped.
                if (cmd == "DOM.resolveNode") return new Dictionary<string, object>();
                return new Dictionary<string, object>();
            };

            Percy.RunClosedShadowRootExposure(fake.Invoke, _ => { }, () => "https://example.com/");
            // Resolve was attempted but no Runtime.callFunctionOn fired (objectId null).
            Assert.Contains("DOM.resolveNode", fake.Commands);
            Assert.DoesNotContain("Runtime.callFunctionOn", fake.Commands);
            Assert.Contains("DOM.disable", fake.Commands);
        }

        [Fact]
        public void RunClosedShadowRootExposure_ResolveNodeThrows_PerPairCatchSwallows()
        {
            Percy.ResetInternalCaches();
            var fake = new FakeCdp();
            fake.Handler = (cmd, args) =>
            {
                if (cmd == "DOM.getDocument")
                    return new Dictionary<string, object>
                    {
                        ["root"] = new Dictionary<string, object>
                        {
                            ["backendNodeId"] = 1L,
                            ["children"] = new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    ["backendNodeId"] = 10L,
                                    ["shadowRoots"] = new List<object>
                                    {
                                        new Dictionary<string, object>
                                        {
                                            ["backendNodeId"] = 11L,
                                            ["shadowRootType"] = "closed"
                                        }
                                    }
                                }
                            }
                        }
                    };
                if (cmd == "DOM.resolveNode") throw new InvalidOperationException("resolve boom");
                return new Dictionary<string, object>();
            };

            // Per-pair catch swallows the resolveNode failure; DOM.disable still runs.
            Percy.RunClosedShadowRootExposure(fake.Invoke, _ => { }, () => "https://example.com/");
            Assert.Contains("DOM.resolveNode", fake.Commands);
            Assert.Contains("DOM.disable", fake.Commands);
        }

        [Fact]
        public void RunClosedShadowRootExposure_GetDocumentReturnsNull_ReturnsEarly()
        {
            Percy.ResetInternalCaches();
            var fake = new FakeCdp();
            fake.Handler = (cmd, args) => cmd == "DOM.getDocument" ? null : (object?)null;
            Percy.RunClosedShadowRootExposure(fake.Invoke, _ => { }, () => "https://example.com/");
            // getDocResult null → returns; DOM.disable still runs in finally.
            Assert.Contains("DOM.disable", fake.Commands);
        }

        [Fact]
        public void RunClosedShadowRootExposure_RootMissing_ReturnsEarly()
        {
            Percy.ResetInternalCaches();
            var fake = new FakeCdp();
            // getDocResult has no "root" key → docDict.TryGetValue fails → returns.
            fake.Handler = (cmd, args) => cmd == "DOM.getDocument"
                ? new Dictionary<string, object> { ["notRoot"] = 1L }
                : (object?)null;
            Percy.RunClosedShadowRootExposure(fake.Invoke, _ => { }, () => "https://example.com/");
            Assert.Contains("DOM.disable", fake.Commands);
        }

        // ===================================================================
        //  CollectClosedShadowRoots: remaining branches
        // ===================================================================

        [Fact]
        public void CollectClosedShadowRoots_ContentDocumentNotADict_ReturnsEarly()
        {
            Percy.ResetInternalCaches();
            var node = new Dictionary<string, object>
            {
                ["backendNodeId"] = 1L,
                ["contentDocument"] = "not-a-dict" // contentDoc cast → null → return
            };
            var pairs = new List<(long, long)>();
            Percy.CollectClosedShadowRoots(node, pairs, "https://example.com");
            Assert.Empty(pairs);
        }

        [Fact]
        public void CollectClosedShadowRoots_NonClosedShadowRoot_IsRecursedNotPaired()
        {
            Percy.ResetInternalCaches();
            // An OPEN shadow root is walked into but never added as a closed pair.
            var node = new Dictionary<string, object>
            {
                ["backendNodeId"] = 10L,
                ["shadowRoots"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["backendNodeId"] = 11L,
                        ["shadowRootType"] = "open"
                    }
                }
            };
            var pairs = new List<(long, long)>();
            Percy.CollectClosedShadowRoots(node, pairs, "https://example.com");
            Assert.Empty(pairs);
        }

        [Fact]
        public void CollectClosedShadowRoots_ShadowRootItemNotADict_IsSkipped()
        {
            Percy.ResetInternalCaches();
            var node = new Dictionary<string, object>
            {
                ["backendNodeId"] = 10L,
                ["shadowRoots"] = new List<object> { "garbage" } // sr cast → null → continue
            };
            var pairs = new List<(long, long)>();
            Percy.CollectClosedShadowRoots(node, pairs, "https://example.com");
            Assert.Empty(pairs);
        }

        [Fact]
        public void CollectClosedShadowRoots_NonDictNode_ReturnsEarly()
        {
            Percy.ResetInternalCaches();
            var pairs = new List<(long, long)>();
            Percy.CollectClosedShadowRoots("not-a-node", pairs, "https://example.com");
            Assert.Empty(pairs);
        }

        // ===================================================================
        //  TryGetLong / ExtractObjectId (private — via reflection)
        // ===================================================================

        private static long? InvokeTryGetLong(IDictionary<string, object> dict, string key)
        {
            MethodInfo m = typeof(Percy).GetMethod(
                "TryGetLong", BindingFlags.Static | BindingFlags.NonPublic)!;
            return (long?)m.Invoke(null, new object[] { dict, key });
        }

        [Fact]
        public void TryGetLong_HandlesLongIntStringAndNull()
        {
            Percy.ResetInternalCaches();
            Assert.Equal(7L, InvokeTryGetLong(new Dictionary<string, object> { ["k"] = 7L }, "k"));
            Assert.Equal(5L, InvokeTryGetLong(new Dictionary<string, object> { ["k"] = 5 }, "k"));
            Assert.Equal(9L, InvokeTryGetLong(new Dictionary<string, object> { ["k"] = "9" }, "k"));
            // unparsable string → null
            Assert.Null(InvokeTryGetLong(new Dictionary<string, object> { ["k"] = "nope" }, "k"));
            // missing key → null
            Assert.Null(InvokeTryGetLong(new Dictionary<string, object>(), "k"));
            // present but null value → null
            Assert.Null(InvokeTryGetLong(new Dictionary<string, object> { ["k"] = null! }, "k"));
        }

        private static string? InvokeExtractObjectId(IDictionary<string, object>? resolveResult)
        {
            MethodInfo m = typeof(Percy).GetMethod(
                "ExtractObjectId", BindingFlags.Static | BindingFlags.NonPublic)!;
            return (string?)m.Invoke(null, new object?[] { resolveResult });
        }

        [Fact]
        public void ExtractObjectId_AllBranches()
        {
            Percy.ResetInternalCaches();
            // null result → null
            Assert.Null(InvokeExtractObjectId(null));
            // no "object" key → null
            Assert.Null(InvokeExtractObjectId(new Dictionary<string, object> { ["x"] = 1 }));
            // "object" not a dict → null
            Assert.Null(InvokeExtractObjectId(new Dictionary<string, object> { ["object"] = "str" }));
            // object dict without objectId → null
            Assert.Null(InvokeExtractObjectId(new Dictionary<string, object>
            {
                ["object"] = new Dictionary<string, object> { ["other"] = 1 }
            }));
            // happy path → objectId string
            Assert.Equal("abc", InvokeExtractObjectId(new Dictionary<string, object>
            {
                ["object"] = new Dictionary<string, object> { ["objectId"] = "abc" }
            }));
        }
    }
}
