using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PercyIO.Selenium.Tests
{
    // Minimal WebDriver BiDi endpoint so Selenium's real BiDi client (driven by
    // Percy through reflection) can be exercised without a browser. Responder
    // maps (method, params) to the raw JSON of the "result" member; returning
    // Error sends a BiDi error, Hang sends nothing.
    internal sealed class FakeBiDiServer : IDisposable
    {
        public const string Error = "__error__";
        public const string Hang = "__hang__";

        private readonly HttpListener _listener = new HttpListener();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        public readonly ConcurrentQueue<(string method, JsonElement parameters)> Received =
            new ConcurrentQueue<(string, JsonElement)>();
        public Func<string, JsonElement, string> Responder = (_, __) => "{}";
        public string Url { get; }

        public FakeBiDiServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Url = $"ws://127.0.0.1:{port}/session/fake";
            Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception) { return; }
                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }
                var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
                _ = Task.Run(() => Serve(ws));
            }
        }

        private async Task Serve(WebSocket ws)
        {
            var buffer = new byte[1 << 16];
            try
            {
                while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    var message = new StringBuilder();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                            return;
                        }
                        message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);

                    using var doc = JsonDocument.Parse(message.ToString());
                    long id = doc.RootElement.GetProperty("id").GetInt64();
                    string method = doc.RootElement.GetProperty("method").GetString()!;
                    JsonElement parameters = doc.RootElement.TryGetProperty("params", out var p) ? p.Clone() : default;
                    Received.Enqueue((method, parameters));

                    string response = Responder(method, parameters);
                    if (response == Hang) continue;
                    string payload = response == Error
                        ? $"{{\"type\":\"error\",\"id\":{id},\"error\":\"unknown error\",\"message\":\"fake failure\"}}"
                        : $"{{\"type\":\"success\",\"id\":{id},\"result\":{response}}}";
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload)),
                        WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch (Exception) { }
        }

        // script.callFunction success wrapper around a serialized remote value.
        public static string EvaluateSuccess(string remoteValueJson) =>
            $"{{\"type\":\"success\",\"realm\":\"realm-1\",\"result\":{remoteValueJson}}}";

        public static string EvaluateException() =>
            "{\"type\":\"exception\",\"realm\":\"realm-1\",\"exceptionDetails\":{\"columnNumber\":0,\"lineNumber\":0," +
            "\"exception\":{\"type\":\"error\"},\"stackTrace\":{\"callFrames\":[]},\"text\":\"boom\"}}";

        public static string Node(string sharedId, string? shadowRootSharedId = null, string mode = "closed") =>
            "{\"type\":\"node\",\"sharedId\":\"" + sharedId + "\",\"value\":{\"nodeType\":1,\"childNodeCount\":0,\"localName\":\"x-el\"" +
            (shadowRootSharedId == null ? "" :
                ",\"shadowRoot\":{\"type\":\"node\",\"sharedId\":\"" + shadowRootSharedId +
                "\",\"value\":{\"nodeType\":11,\"childNodeCount\":1,\"mode\":\"" + mode + "\"}}") +
            "}}";

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); _listener.Close(); } catch (Exception) { }
        }
    }
}
