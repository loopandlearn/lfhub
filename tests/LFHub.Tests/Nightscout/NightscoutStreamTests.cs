using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LFHub.Nightscout;
using LFHub.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace LFHub.Tests.Nightscout;

/// The Socket.IO exchange with a fake Nightscout socket on loopback.
public sealed class NightscoutStreamTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplication _server = null!;
    private readonly List<string> _received = [];
    private string? _query;
    private readonly TaskCompletionSource _closeFromServer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _server = builder.Build();
        _server.UseWebSockets();
        _server.Map("/socket.io/", ServeAsync);
        await _server.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _closeFromServer.TrySetResult();
        await _server.DisposeAsync();
    }

    private string BaseUrl => _server.Urls.Single();

    /// Nightscout's side: the Engine.IO handshake, the namespace connect, `connected` once
    /// authorized, a ping, and one dataUpdate; then it closes the socket when told to.
    private async Task ServeAsync(HttpContext ctx)
    {
        _query = ctx.Request.QueryString.Value;
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        await SendAsync(ws, """0{"sid":"s1","pingInterval":25000,"pingTimeout":20000}""");
        await ReceiveAsync(ws);
        await SendAsync(ws, """40{"sid":"n1"}""");
        await ReceiveAsync(ws);
        await SendAsync(ws, """42["connected"]""");
        await SendAsync(ws, "2");
        await ReceiveAsync(ws);
        await SendAsync(ws, """42["dataUpdate",{"sgvs":[{"mgdl":120}]}]""");
        await _closeFromServer.Task;
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    private static Task SendAsync(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private async Task<string?> ReceiveAsync(WebSocket ws)
    {
        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close) return null;
        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        lock (_received) _received.Add(text);
        return text;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25, Ct);
        Assert.True(condition());
    }

    [Fact]
    public async Task Session_AuthorizesAnswersThePingDispatchesDataUpdatesAndReportsTheClose()
    {
        var authorized = 0;
        var closed = 0;
        JsonElement? update = null;
        var stream = new NightscoutStream(BaseUrl, "ns-token", "test", new ListLogger(),
            new HttpMessageInvoker(SourceAddress.Handler(true)),
            onDataUpdate: data => update = data,
            onAuthorized: () => Interlocked.Increment(ref authorized),
            onClosed: () => Interlocked.Increment(ref closed));
        await stream.StartAsync();
        await WaitForAsync(() => update is not null);

        Assert.True(stream.Connected);
        Assert.Equal(1, authorized);
        Assert.Equal(120, update!.Value.GetProperty("sgvs")[0].GetProperty("mgdl").GetInt32());
        Assert.Equal("?EIO=4&transport=websocket", _query);
        lock (_received)
        {
            Assert.Equal("40", _received[0]);
            var authorize = JsonDocument.Parse(_received[1][2..]).RootElement;
            Assert.StartsWith("42", _received[1], StringComparison.Ordinal);
            Assert.Equal("authorize", authorize[0].GetString());
            Assert.Equal("ns-token", authorize[1].GetProperty("secret").GetString());
            Assert.Equal(1, authorize[1].GetProperty("history").GetInt32());
            Assert.Equal("3", _received[2]);
        }

        Assert.Equal(0, closed);
        _closeFromServer.SetResult();
        await WaitForAsync(() => closed == 1);
        Assert.False(stream.Connected);
        await stream.DisposeAsync();
    }

    [Fact]
    public async Task Session_ToALoopbackHost_IsRefusedUnlessPrivateSourcesAreAllowed()
    {
        var log = new ListLogger();
        var authorized = false;
        var stream = new NightscoutStream(BaseUrl, "ns-token", "test", log,
            new HttpMessageInvoker(SourceAddress.Handler(false)),
            onDataUpdate: _ => { },
            onAuthorized: () => authorized = true,
            onClosed: () => { });
        await stream.StartAsync();
        await WaitForAsync(() => log.Lines.Any(l => l.Contains("socket session ended", StringComparison.Ordinal)));
        await stream.DisposeAsync();
        Assert.False(authorized);
        Assert.Empty(_received);
        Assert.Null(_query);
    }

    [Fact]
    public void KeepAlive_PingsEvery30SecondsAndWaits20ForThePong()
    {
        using var ws = new ClientWebSocket();
        NightscoutStream.ConfigureKeepAlive(ws.Options);
        Assert.Equal(TimeSpan.FromSeconds(30), ws.Options.KeepAliveInterval);
        Assert.Equal(TimeSpan.FromSeconds(20), ws.Options.KeepAliveTimeout);
    }
}
