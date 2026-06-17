using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

    }
}
