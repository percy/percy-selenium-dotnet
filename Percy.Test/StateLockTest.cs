using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PercyIO.Selenium.Tests
{
    [Collection("HttpClientStateSerial")]
    public class StateLockTest : IDisposable
    {
        private const string Automate =
            "{\"success\":true,\"type\":\"automate\",\"widths\":[375],\"config\":{}}";
        private const string Web =
            "{\"success\":true,\"type\":\"web\",\"widths\":[375],\"config\":{}}";

        private readonly Func<bool> _oldEnabled;

        public StateLockTest()
        {
            _oldEnabled = Percy.Enabled;
            Percy.ResetInternalCaches();
        }

        public void Dispose()
        {
            Percy.Enabled = _oldEnabled;
            Percy.ResetInternalCaches();
            Percy.setHttpClient(new HttpClient());
        }

        // Serves queued responses; a gated one blocks until Release() so a fetch
        // can be held "in flight" deterministically.
        private sealed class GatedHandler : HttpMessageHandler
        {
            private readonly Queue<(string body, string? version, bool gated)> _responses;
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);
            public int Calls;

            public GatedHandler(params (string body, string? version, bool gated)[] responses)
            {
                _responses = new Queue<(string, string?, bool)>(responses);
            }

            public void Release() => _release.Set();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                var (body, version, gated) = _responses.Dequeue();
                return Task.Run(() =>
                {
                    if (gated)
                    {
                        Entered.Set();
                        _release.Wait(TimeSpan.FromSeconds(30));
                    }
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    };
                    if (version != null) resp.Headers.Add("x-percy-core-version", version);
                    return resp;
                });
            }
        }

        private static string GetPercyDOM() =>
            (string)typeof(Percy)
                .GetMethod("GetPercyDOM", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, null)!;

        [Fact]
        public void CurrentSessionType_ReflectsSetAndReset()
        {
            Assert.Null(Percy.CurrentSessionType());
            Percy.setSessionType("automate");
            Assert.Equal("automate", Percy.CurrentSessionType());
            Percy.ResetInternalCaches();
            Assert.Null(Percy.CurrentSessionType());
        }

        [Fact]
        public void ResetAndSessionTypeRead_DoNotWaitForInFlightHealthcheck()
        {
            var http = new GatedHandler((Automate, "1.0.0", true));
            Percy.setHttpClient(new HttpClient(http));

            var probe = Task.Run(() => Percy.Enabled());
            Assert.True(http.Entered.Wait(TimeSpan.FromSeconds(10)));

            var reset = Task.Run(() =>
            {
                Percy.ResetInternalCaches();
                return Percy.CurrentSessionType();
            });
            bool completed = reset.Wait(TimeSpan.FromSeconds(2));
            http.Release();
            Assert.True(probe.Wait(TimeSpan.FromSeconds(10)));

            Assert.True(completed, "reset/read blocked behind the healthcheck HTTP call");
        }

        [Fact]
        public void HealthcheckFinishingAfterReset_DoesNotRepopulateState()
        {
            var http = new GatedHandler((Automate, "1.0.0", true), (Web, "1.0.0", false));
            Percy.setHttpClient(new HttpClient(http));

            var probe = Task.Run(() => Percy.Enabled());
            Assert.True(http.Entered.Wait(TimeSpan.FromSeconds(10)));
            bool resetDone = Task.Run(Percy.ResetInternalCaches).Wait(TimeSpan.FromSeconds(2));
            http.Release();
            Assert.True(probe.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(resetDone);

            // Stale "automate" result must not be cached over the reset.
            Assert.Null(Percy.CurrentSessionType());
            Assert.True(Percy.Enabled());
            Assert.Equal(2, http.Calls);
            Assert.Equal("web", Percy.CurrentSessionType());
        }

        [Fact]
        public void DomFetchFinishingAfterReset_DoesNotRepopulateCache()
        {
            var http = new GatedHandler(("old-dom", null, true), ("new-dom", null, false));
            Percy.setHttpClient(new HttpClient(http));

            var fetch = Task.Run(GetPercyDOM);
            Assert.True(http.Entered.Wait(TimeSpan.FromSeconds(10)));
            Percy.ResetInternalCaches();
            http.Release();
            Assert.Equal("old-dom", fetch.Result);

            Assert.Equal("new-dom", GetPercyDOM());
            Assert.Equal(2, http.Calls);
        }
    }
}
