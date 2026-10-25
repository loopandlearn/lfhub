using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LFHub.Nightscout;

/// <summary>
/// A Socket.IO v4 subscription to one Nightscout instance, carrying the four
/// exchanges a live feed needs: the Engine.IO handshake, the namespace connect,
/// one authorize emit, and the dataUpdate event stream.
/// </summary>
/// <remarks>
/// Frames are Engine.IO packets. The leading digit is the Engine.IO type
/// (0 open, 1 close, 2 ping, 3 pong, 4 message); a message frame carries a
/// second digit for the Socket.IO type (0 connect, 2 event, 4 connect_error),
/// and an event body is the JSON array [name, payload]. The server pings on the
/// interval it announces in the handshake and drops the connection unless each
/// ping is answered with a pong.
/// </remarks>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "A Socket.IO event stream, not a System.IO.Stream; the hub reports these as streams.")]
public sealed class NightscoutStream : IAsyncDisposable
{
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);
    private const int ReceiveBufferSize = 64 * 1024;

    private readonly Uri _wsUri;
    private readonly string _token;
    private readonly string _name;
    private readonly ILogger _log;
    private readonly HttpMessageInvoker? _invoker;
    private readonly Action<JsonElement> _onDataUpdate;
    private readonly Action _onAuthorized;
    private readonly Action _onClosed;

    private readonly CancellationTokenSource _cts = new();
    private ClientWebSocket? _ws;
    private Task? _supervisor;

    /// <summary>True while a websocket session is established.</summary>
    public bool Connected => _ws is { State: WebSocketState.Open };

    /// <param name="invoker">What the WebSocket connects with; null connects directly.</param>
    public NightscoutStream(
        string baseUrl,
        string? token,
        string name,
        ILogger log,
        HttpMessageInvoker? invoker,
        Action<JsonElement> onDataUpdate,
        Action onAuthorized,
        Action onClosed)
    {
        var builder = new UriBuilder(baseUrl.TrimEnd('/'));
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        builder.Path = builder.Path.TrimEnd('/') + "/socket.io/";
        builder.Query = "EIO=4&transport=websocket";
        _wsUri = builder.Uri;
        _token = token ?? string.Empty;
        _name = name;
        _log = log;
        _invoker = invoker;
        _onDataUpdate = onDataUpdate;
        _onAuthorized = onAuthorized;
        _onClosed = onClosed;
    }

    /// <summary>
    /// Starts the connection and keeps it up, reconnecting with a capped
    /// backoff until disposed.
    /// </summary>
    public Task StartAsync()
    {
        _supervisor = Task.Run(() => SuperviseAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        var delay = InitialReconnectDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(ct);
                delay = InitialReconnectDelay;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning("[{Name}] socket session ended: {Message}", _name, ex.Message);
            }

            _onClosed();
            if (ct.IsCancellationRequested) break;

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }

            var next = TimeSpan.FromTicks(delay.Ticks * 2);
            delay = next > MaxReconnectDelay ? MaxReconnectDelay : next;
        }
    }

    /// <summary>
    /// A WebSocket ping every 30 seconds, and the connection is aborted when its pong does
    /// not arrive within 20, so a half-open socket drops and the supervisor reconnects.
    /// </summary>
    internal static void ConfigureKeepAlive(ClientWebSocketOptions options)
    {
        options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ConfigureKeepAlive(ws.Options);
        _ws = ws;
        await ws.ConnectAsync(_wsUri, _invoker, ct);

        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        try
        {
            using var frame = new MemoryStream();
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                frame.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _log.LogInformation("[{Name}] server closed the socket: {Status}", _name, result.CloseStatus);
                        return;
                    }
                    frame.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                await HandleFrameAsync(
                    ws, Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleFrameAsync(ClientWebSocket ws, string frame, CancellationToken ct)
    {
        if (frame.Length == 0) return;
        switch (frame[0])
        {
            case '0':
                LogHandshake(frame[1..]);
                await SendAsync(ws, "40", ct);
                break;
            case '2':
                await SendAsync(ws, "3", ct);
                break;
            case '4':
                await HandleMessageAsync(ws, frame[1..], ct);
                break;
            case '1':
                _log.LogInformation("[{Name}] server sent Engine.IO close", _name);
                break;
        }
    }

    private async Task HandleMessageAsync(ClientWebSocket ws, string body, CancellationToken ct)
    {
        if (body.Length == 0) return;
        switch (body[0])
        {
            case '0':
                await EmitAuthorizeAsync(ws, ct);
                break;
            case '2':
                DispatchEvent(body[1..]);
                break;
            case '4':
                _log.LogWarning("[{Name}] connect_error: {Error}", _name, body[1..]);
                break;
        }
    }

    private async Task EmitAuthorizeAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["client"] = "lfhub",
            ["secret"] = _token,
            ["history"] = 1,
        }.ToJsonString();
        await SendAsync(ws, $"42[\"authorize\",{payload}]", ct);
    }

    private void DispatchEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return;
            switch (root[0].GetString())
            {
                case "connected":
                    _onAuthorized();
                    break;
                case "dataUpdate" when root.GetArrayLength() > 1:
                    // Cloned because a handler may keep reading the payload after
                    // this returns, outliving the document backing it.
                    _onDataUpdate(root[1].Clone());
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("[{Name}] event parse failed: {Message}", _name, ex.Message);
        }
    }

    private void LogHandshake(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var interval = doc.RootElement.TryGetProperty("pingInterval", out var pi)
                ? pi.GetInt32() : -1;
            _log.LogInformation("[{Name}] connected, authorizing (server ping every {Interval}ms)", _name, interval);
        }
        catch (Exception ex)
        {
            _log.LogWarning("[{Name}] handshake parse failed: {Message}", _name, ex.Message);
        }
    }

    private static async Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;
        await _cts.CancelAsync();

        var ws = _ws;
        if (ws is { State: WebSocketState.Open })
        {
            // A clean close is a courtesy to the server; the session ends either way.
            try
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutting down", closing.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }

        if (_supervisor is not null)
        {
            try
            {
                await _supervisor;
            }
            catch (Exception ex)
            {
                _log.LogWarning("[{Name}] socket supervisor ended with an error: {Message}", _name, ex.Message);
            }
        }
        _cts.Dispose();
    }
}
