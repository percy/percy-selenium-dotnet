using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace PercyIO.Selenium.Tests
{
    // Unit tests for the CORS iframe + closed shadow DOM helpers added to
    // Percy.cs. These don't require the Percy CLI or a real browser; they
    // exercise the pure-C# helpers via reflection where they are internal.
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

    }
}
