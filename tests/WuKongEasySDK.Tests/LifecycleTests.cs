using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using static WuKongEasySDK.Tests.ProtocolTests;

namespace WuKongEasySDK.Tests;

public sealed class LifecycleTests
{
    private static WKIMOptions ReconnectOptions => new()
    {
        InitialReconnectDelay = TimeSpan.FromMilliseconds(30), MaxReconnectDelay = TimeSpan.FromMilliseconds(100),
        // Reconnect failures are driven by peer aborts, not tiny wall-clock deadlines.
        // Keep the normal opening budget so cold hosted HTTP initialization can finish.
        ConnectTimeout = TimeSpan.FromSeconds(10), MaxReconnectAttempts = 2
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticationFailureIsTerminal(bool rpc)
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, ReconnectOptions);
        var retries = 0;
        client.Reconnecting += _ => Interlocked.Increment(ref retries);
        var connect = client.ConnectAsync();
        var peer = await server.AcceptAsync();
        var request = await peer.ReadAsync();
        if (rpc)
        {
            await peer.SendAsync(new { id = request.GetProperty("id").GetString(), error = new { code = 401, message = "secret" } });
            await Assert.ThrowsAsync<WKIMRpcException>(() => connect);
        }
        else
        {
            await peer.ReplyAsync(request, new { reasonCode = 2 });
            await Assert.ThrowsAsync<WKIMAuthenticationException>(() => connect);
        }
        await client.DisconnectAsync();
        Assert.False(client.IsConnected);
        Assert.Equal(0, retries);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task CompleteConnectionAttemptHasDeadlineIncludingColdHandshake()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, new() { ConnectTimeout = TimeSpan.FromMilliseconds(150) });
        var connect = client.ConnectAsync();
        // Cold TLS/HTTP/WebSocket initialization on hosted runners may consume the whole
        // budget before CONNECT is sent. That is precisely the complete-attempt contract.
        await Assert.ThrowsAsync<TimeoutException>(() => connect.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(connect.IsCompleted);
        if (server.Connections > 0)
        {
            var peer = await server.AcceptAsync();
            await peer.Closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task DisconnectDuringAuthenticationCompletesAllWaiters()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth);
        var first = client.ConnectAsync();
        var second = client.ConnectAsync();
        var peer = await server.AcceptAsync();
        await peer.ReadAsync();
        await client.DisconnectAsync();
        await Assert.ThrowsAsync<WKIMDisconnectedException>(() => first);
        await Assert.ThrowsAsync<WKIMDisconnectedException>(() => second);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task CancelingOneConnectWaiterDoesNotCancelOtherWaiters()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth);
        using var cancellation = new CancellationTokenSource();
        var first = client.ConnectAsync(cancellation.Token);
        var second = client.ConnectAsync();
        var peer = await server.AcceptAsync();
        var request = await peer.ReadAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await peer.ReplyAsync(request, new { reasonCode = 1 });
        await second;
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task LostSocketFailsPendingRequestsAndReauthenticatesWithoutReplay()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, ReconnectOptions);
        var connects = Channel.CreateUnbounded<ConnectResult>();
        client.Connected += result => connects.Writer.TryWrite(result);
        var peer = await server.ConnectAsync(client);
        await connects.Reader.ReadAsync();
        var send = client.SendAsync("bob", ChannelType.Person, new { type = 1 });
        var oldRequest = await peer.ReadAsync();
        peer.Abort();
        await Assert.ThrowsAsync<WKIMDisconnectedException>(() => send);
        var replacement = await server.AcceptAsync();
        var request = await replacement.ReadAsync();
        Assert.Equal("connect", request.GetProperty("method").GetString());
        Assert.Equal(client.DeviceId, request.GetProperty("params").GetProperty("deviceId").GetString());
        await replacement.ReplyAsync(request, new { reasonCode = 1 });
        await connects.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        var next = client.SendAsync("bob", ChannelType.Person, new { type = 1 });
        var current = await replacement.ReadAsync();
        await replacement.ReplyAsync(oldRequest, new { reasonCode = 1, messageId = "stale" });
        await replacement.ReplyAsync(current, new { reasonCode = 1, messageId = "current" });
        Assert.Equal("current", (await next).MessageId);
        Assert.Equal(2, server.Connections);
    }

    [Fact]
    public async Task ReconnectBudgetStopsAfterExactAttemptLimit()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, ReconnectOptions);
        var errors = Channel.CreateUnbounded<Exception>();
        client.Error += error => errors.Writer.TryWrite(error);
        var peer = await server.ConnectAsync(client);
        peer.Abort();
        await errors.Reader.ReadAsync();
        var waiting = client.ConnectAsync();
        for (var i = 0; i < 2; i++)
        {
            var retry = await server.AcceptAsync();
            await retry.ReadAsync();
            retry.Abort();
        }
        await Assert.ThrowsAsync<WKIMDisconnectedException>(() => waiting);
        Assert.Equal(3, server.Connections);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ManualDisconnectCancelsReconnectBackoff()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, new() { InitialReconnectDelay = TimeSpan.FromSeconds(10) });
        var retry = Signal<ReconnectingInfo>();
        client.Reconnecting += value => retry.TrySetResult(value);
        var peer = await server.ConnectAsync(client);
        peer.Abort();
        await Within(retry.Task);
        await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task ServerKickDoesNotReconnectAndEmitsOneDisconnect()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, ReconnectOptions);
        var disconnect = Signal<DisconnectInfo>();
        var count = 0;
        client.Disconnected += value => { Interlocked.Increment(ref count); disconnect.TrySetResult(value); };
        var peer = await server.ConnectAsync(client);
        await peer.NotifyAsync("disconnect", new { reasonCode = 12, reason = "secret" });
        var result = await Within(disconnect.Task);
        Assert.Equal(ReasonCode.ConnectKick, result.ReasonCode);
        Assert.False(result.IsManual);
        Assert.Null(result.Reason);
        await client.DisconnectAsync();
        Assert.Equal(1, count);
        Assert.Equal(1, server.Connections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeartbeatAcceptsCorrelatedNullOrPongNotification(bool notification)
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, new()
        {
            PingInterval = TimeSpan.FromMilliseconds(60), PongTimeout = TimeSpan.FromMilliseconds(250), AutoReconnect = false
        });
        var peer = await server.ConnectAsync(client);
        for (var i = 0; i < 3; i++)
        {
            var ping = await peer.ReadAsync();
            Assert.Equal("ping", ping.GetProperty("method").GetString());
            if (notification) await peer.NotifyAsync("pong", new { });
            else await peer.ReplyAsync(ping, null);
        }
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task MissingPongTriggersReconnect()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, new()
        {
            PingInterval = TimeSpan.FromMilliseconds(30), PongTimeout = TimeSpan.FromMilliseconds(80),
            InitialReconnectDelay = TimeSpan.FromMilliseconds(10)
        });
        var retry = Signal<ReconnectingInfo>();
        client.Reconnecting += value => retry.TrySetResult(value);
        var peer = await server.ConnectAsync(client);
        Assert.Equal("ping", (await peer.ReadAsync()).GetProperty("method").GetString());
        Assert.Equal(1, (await Within(retry.Task)).Attempt);
        await client.DisconnectAsync();
    }

    [Fact]
    public async Task DisconnectFailsInflightSendAndAllowsFreshConnect()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, Options);
        var peer = await server.ConnectAsync(client);
        var send = client.SendAsync("bob", ChannelType.Person, new { type = 1 });
        await peer.ReadAsync();
        await client.DisconnectAsync();
        await Assert.ThrowsAsync<WKIMDisconnectedException>(() => send);
        await server.ConnectAsync(client);
        Assert.True(client.IsConnected);
        await client.DisposeAsync();
        await client.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendAsync("bob", ChannelType.Person, new { type = 1 }));
    }

    [Fact]
    public async Task OversizedFragmentedReceiveClosesWithSafeError()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, new() { AutoReconnect = false, MaxMessageBytes = 512 });
        var error = Signal<Exception>();
        client.Error += value => error.TrySetResult(value);
        var peer = await server.ConnectAsync(client);
        try { await peer.RawAsync(JsonSerializer.Serialize(new { secret = new string('x', 1000) }), fragment: 256); }
        catch (WebSocketException) { }
        Assert.DoesNotContain("secret", (await Within(error.Task)).ToString());
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task EventOverflowClosesWithoutAcknowledgingUnacceptedMessage()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, new() { AutoReconnect = false, MaxQueuedEvents = 1 });
        using var release = new ManualResetEventSlim();
        var entered = Signal<bool>();
        client.Message += _ => { entered.TrySetResult(true); release.Wait(TimeSpan.FromSeconds(3)); };
        var peer = await server.ConnectAsync(client);
        object Message(int sequence) => new { messageId = sequence.ToString(), messageSeq = sequence, channelId = "alice", fromUid = "bob", payload = new { type = 1 } };
        try
        {
            await peer.NotifyAsync("recv", Message(1));
            await Within(entered.Task);
            await peer.ReadAsync();
            await peer.NotifyAsync("recv", Message(2));
            await peer.ReadAsync();
            await peer.NotifyAsync("recv", Message(3));
            await peer.Closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(peer.TryRead(out _));
            Assert.False(client.IsConnected);
        }
        finally { release.Set(); }
    }
}
