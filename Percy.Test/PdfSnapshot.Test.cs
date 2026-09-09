using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using RichardSzalay.MockHttp;

namespace PercyIO.Selenium.Tests
{
    // Percy.PdfSnapshot takes no WebDriver -- a PDF is bytes, not a rendered
    // page -- so these run against the CLI's testing-mode API without a browser.
    [Collection("HttpClientStateSerial")]
    public class PdfSnapshotTests
    {
        // A valid 446-byte single-page PDF: one filled rectangle, no fonts.
        // Built literally so the CLI genuinely rasterizes it rather than
        // rejecting a truncated header.
        private const string MinimalPdfBase64 =
            "JVBERi0xLjQKMSAwIG9iago8PCAvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgMiAwIFIgPj4KZW5kb2JqCjIgMCBvYmoK" +
            "PDwgL1R5cGUgL1BhZ2VzIC9LaWRzIFszIDAgUl0gL0NvdW50IDEgPj4KZW5kb2JqCjMgMCBvYmoKPDwgL1R5cGUg" +
            "L1BhZ2UgL1BhcmVudCAyIDAgUiAvTWVkaWFCb3ggWzAgMCA2MCA2MF0gL0NvbnRlbnRzIDQgMCBSIC9SZXNvdXJj" +
            "ZXMgPDwgPj4gPj4KZW5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCAxNiA+PgpzdHJlYW0KMTAgMTAgNDAgNDAgcmUg" +
            "ZgplbmRzdHJlYW0KZW5kb2JqCnhyZWYKMCA1CjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAwMDAwMDAwOSAwMDAwMCBu" +
            "IAowMDAwMDAwMDU4IDAwMDAwIG4gCjAwMDAwMDAxMTUgMDAwMDAgbiAKMDAwMDAwMDIxNyAwMDAwMCBuIAp0cmFp" +
            "bGVyCjw8IC9TaXplIDUgL1Jvb3QgMSAwIFIgPj4Kc3RhcnR4cmVmCjI4MwolJUVPRgo=";

        private static byte[] Pdf => Convert.FromBase64String(MinimalPdfBase64);

        public PdfSnapshotTests()
        {
            Percy.setHttpClient(new HttpClient());
            Percy.ResetInternalCaches();
            UnitTests.Request("/test/api/reset");
        }

        [Fact]
        public void ReturnsNullWhenPercyIsDisabled()
        {
            Percy.ResetInternalCaches();
            UnitTests.Request("/test/api/disconnect", "/percy/healthcheck");

            Assert.Null(Percy.PdfSnapshot("Policy", Pdf));
        }

        [Fact]
        public void RequiresASnapshotName()
        {
            Assert.Throws<ArgumentException>(() => Percy.PdfSnapshot("", Pdf));
            Assert.Throws<ArgumentException>(() => Percy.PdfSnapshot("   ", Pdf));
        }

        [Fact]
        public void RequiresPdfData()
        {
            Assert.Throws<ArgumentException>(() => Percy.PdfSnapshot("Policy", null));
            Assert.Throws<ArgumentException>(() => Percy.PdfSnapshot("Policy", new byte[0]));
        }

        [Fact]
        public void PostsTheDocumentAsBase64InAJsonBody()
        {
            Percy.PdfSnapshot("Policy", Pdf);

            JsonElement requests = UnitTests.Request("/test/requests");
            JsonElement request = requests.GetProperty("requests").EnumerateArray()
                .Single(r => r.GetProperty("url").GetString() == "/percy/pdf/snapshot");

            Assert.Equal("POST", request.GetProperty("method").GetString());

            JsonElement body = request.GetProperty("body");
            Assert.Equal("Policy", body.GetProperty("name").GetString());
            // base64 in an ordinary JSON body -- this is what lets the .NET
            // wrapper reuse the existing Request() helper with no multipart
            // or streaming code of its own.
            Assert.Equal(MinimalPdfBase64, body.GetProperty("pdf").GetProperty("content").GetString());
            Assert.False(string.IsNullOrEmpty(body.GetProperty("clientInfo").GetString()));
            Assert.False(string.IsNullOrEmpty(body.GetProperty("environmentInfo").GetString()));
        }

        [Fact]
        public void ForwardsSnapshotOptions()
        {
            Percy.PdfSnapshot("Policy", Pdf, new Dictionary<string, object> {
                { "pages", "1" },
                { "scale", 2 },
                { "labels", "regression" }
            });

            JsonElement body = UnitTests.Request("/test/requests")
                .GetProperty("requests").EnumerateArray()
                .Single(r => r.GetProperty("url").GetString() == "/percy/pdf/snapshot")
                .GetProperty("body");

            Assert.Equal("1", body.GetProperty("pages").GetString());
            Assert.Equal(2, body.GetProperty("scale").GetInt32());
            Assert.Equal("regression", body.GetProperty("labels").GetString());
        }

        [Fact]
        public void AcceptsAnAnonymousObjectForOptions()
        {
            Percy.PdfSnapshot("Policy", Pdf, new { pages = "1" });

            JsonElement body = UnitTests.Request("/test/requests")
                .GetProperty("requests").EnumerateArray()
                .Single(r => r.GetProperty("url").GetString() == "/percy/pdf/snapshot")
                .GetProperty("body");

            Assert.Equal("1", body.GetProperty("pages").GetString());
        }

        [Fact]
        public void IgnoresACallerSuppliedPdfKey()
        {
            // The byte[] argument is the source of truth; an options-bag `pdf`
            // must not be able to override it.
            Percy.PdfSnapshot("Policy", Pdf, new Dictionary<string, object> {
                { "pdf", new Dictionary<string, object> { { "content", "bm90LWEtcGRm" } } }
            });

            JsonElement body = UnitTests.Request("/test/requests")
                .GetProperty("requests").EnumerateArray()
                .Single(r => r.GetProperty("url").GetString() == "/percy/pdf/snapshot")
                .GetProperty("body");

            Assert.Equal(MinimalPdfBase64, body.GetProperty("pdf").GetProperty("content").GetString());
        }

        // Response parsing is asserted against a stubbed CLI rather than the one
        // testing mode boots: /percy/pdf/snapshot is not in a published
        // @percy/cli yet, so the real server 404s it and there is no aggregate
        // to parse. Stubbing also lets this cover the sync shape, which is the
        // one carrying per-page comparison results.
        private const string SyncAggregateResponse = @"{
          ""success"": true,
          ""data"": {
            ""pdf-name"": ""Policy"",
            ""page-count"": 2,
            ""pages-snapshotted"": 2,
            ""status"": ""success"",
            ""pages"": [
              { ""page"": 1, ""snapshot-name"": ""Policy | Page 1"", ""status"": ""success"",
                ""screenshots"": [ { ""diff-info"": { ""diff-ratio"": 0 } } ] },
              { ""page"": 2, ""snapshot-name"": ""Policy | Page 2"", ""status"": ""success"",
                ""screenshots"": [ { ""diff-info"": { ""diff-ratio"": 0.0142 } } ] }
            ]
          }
        }";

        [Fact]
        public void ParsesTheSyncAggregateResponseAsAJObject()
        {
            var mockHttp = new MockHttpMessageHandler();
            mockHttp.When(HttpMethod.Get, "http://localhost:5338/percy/healthcheck")
                .Respond(new Dictionary<string, string> { { "x-percy-core-version", "1.0.0" } },
                         "application/json", "{\"success\":true}");
            mockHttp.When(HttpMethod.Post, "http://localhost:5338/percy/pdf/snapshot")
                .Respond("application/json", SyncAggregateResponse);
            mockHttp.Fallback.Respond("application/json", "{}");
            Percy.setHttpClient(new HttpClient(mockHttp));
            Percy.ResetInternalCaches();

            JObject? result = Percy.PdfSnapshot("Policy", Pdf, new { sync = true });

            // A JSON object, never a bare array -- that is what lets JObject.Parse work.
            Assert.NotNull(result);
            Assert.Equal("Policy", (string?)result!["pdf-name"]);
            Assert.Equal(2, (int)result["page-count"]!);
            Assert.Equal("success", (string?)result["status"]);

            JArray pages = Assert.IsType<JArray>(result["pages"]);
            Assert.Equal(2, pages.Count);
            Assert.Equal("Policy | Page 1", (string?)pages[0]["snapshot-name"]);
            Assert.Equal("Policy | Page 2", (string?)pages[1]["snapshot-name"]);

            // diff-info hangs off each screenshot, not off the page itself.
            Assert.Equal(0d, (double)pages[0]["screenshots"]![0]!["diff-info"]!["diff-ratio"]!, 4);
            Assert.Equal(0.0142d, (double)pages[1]["screenshots"]![0]!["diff-info"]!["diff-ratio"]!, 4);
        }

        [Fact]
        public void ReturnsNullAndLogsWhenTheEndpointFails()
        {
            UnitTests.Request("/test/api/error", "/percy/pdf/snapshot");

            Assert.Null(Percy.PdfSnapshot("Policy", Pdf));
        }
    }
}
