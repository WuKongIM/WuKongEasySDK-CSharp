using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace WuKongEasySDK.Tests;

public sealed class ProtocolTests
{
    internal static AuthOptions Auth => new() { Uid = "alice", Token = "secret-token" };
    internal static WKIMOptions Options => new() { AutoReconnect = false, RequestTimeout = TimeSpan.FromSeconds(2) };
    internal static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public void ModelStringsRedactContentAndKeys()
    {
        var json = JsonSerializer.SerializeToElement(new { secret = "canary" });
        Assert.DoesNotContain("canary", new RecvMessage { MessageId = "1", MessageSeq = ulong.MaxValue,
            ChannelId = "alice", FromUid = "bob", Payload = json }.ToString());
        Assert.DoesNotContain("canary", new EventNotification { Id = "1", Type = "notice", Timestamp = 1, Data = json }.ToString());
        Assert.DoesNotContain("canary", new ConnectResult { ReasonCode = ReasonCode.Success, ServerKey = "canary", Salt = "canary" }.ToString());
    }

    [Fact]
    public async Task ReceivePreservesFullUnsignedSequenceAndUnknownPayload()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, Options);
        var message = Signal<RecvMessage>();
        client.Message += value => message.TrySetResult(value);
        var peer = await server.ConnectAsync(client);
        await peer.NotifyAsync("recv", new { messageId = "1", messageSeq = ulong.MaxValue, channelId = "alice", fromUid = "bob", payload = "not-base64" });
        var value = await Within(message.Task);
        Assert.Equal(ulong.MaxValue, value.MessageSeq);
        Assert.Equal("not-base64", value.Payload.GetString());
        Assert.Equal(ulong.MaxValue, (await peer.ReadAsync()).GetProperty("params").GetProperty("messageSeq").GetUInt64());
    }

    [Fact]
    public void InvalidResourceBoundsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WKIM("ws://localhost", Auth, new() { MaxPendingRequests = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WKIM("ws://localhost", Auth, new() { ConnectTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WKIM("ws://localhost", Auth, new() { MaxReconnectDelay = TimeSpan.FromMilliseconds(1) }));
    }

    [Theory]
    [InlineData("http://localhost:5200")]
    [InlineData("invalid")]
    [InlineData("ws://user:secret@localhost")]
    [InlineData("ws://localhost/#fragment")]
    public void RejectsInvalidEndpoints(string url) => Assert.Throws<ArgumentException>(() => new WKIM(url, Auth));

    [Fact]
    public async Task ConnectCoalescesAndSendsExactWireCredentials()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, Options);
        var connected = Signal<ConnectResult>();
        client.Connected += result => connected.TrySetResult(result);
        var waits = Enumerable.Range(0, 10).Select(_ => client.ConnectAsync()).ToArray();
        var peer = await server.AcceptAsync();
        var request = await peer.ReadAsync();
        Assert.Equal("2.0", request.GetProperty("jsonrpc").GetString());
        Assert.Equal("connect", request.GetProperty("method").GetString());
        var auth = request.GetProperty("params");
        Assert.Equal("alice", auth.GetProperty("uid").GetString());
        Assert.Equal("secret-token", auth.GetProperty("token").GetString());
        Assert.Equal(2, auth.GetProperty("deviceFlag").GetInt32());
        Assert.Equal(client.DeviceId, auth.GetProperty("deviceId").GetString());
        Assert.True(auth.GetProperty("clientTimestamp").GetInt64() > 0);
        await peer.ReplyAsync(request, new { reasonCode = 1, nodeId = 2, serverVersion = 5 });
        await Task.WhenAll(waits);
        Assert.Equal(2UL, (await Within(connected.Task)).NodeId);
        Assert.Equal(1, server.Connections);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task SendEncodesUnicodeAndPreservesOptionsAndBusinessRejection()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, Options);
        var peer = await server.ConnectAsync(client);
        var ackEvent = Signal<SendResult>();
        client.SendAck += ack => ackEvent.TrySetResult(ack);
        var send = client.SendAsync("group", ChannelType.Group, new { type = 1, content = "你好 🌍" },
            new() { ClientMsgNo = "stable-id", Header = new() { RedDot = false, NoPersist = true },
                Setting = new() { Receipt = true, Topic = true }, Topic = "news" });
        var request = await peer.ReadAsync();
        var parameters = request.GetProperty("params");
        Assert.Equal("stable-id", parameters.GetProperty("clientMsgNo").GetString());
        Assert.Equal(2, parameters.GetProperty("channelType").GetInt32());
        Assert.False(parameters.GetProperty("header").GetProperty("redDot").GetBoolean());
        Assert.True(parameters.GetProperty("header").GetProperty("noPersist").GetBoolean());
        Assert.Equal("news", parameters.GetProperty("topic").GetString());
        using var payload = JsonDocument.Parse(Convert.FromBase64String(parameters.GetProperty("payload").GetString()!));
        Assert.Equal("你好 🌍", payload.RootElement.GetProperty("content").GetString());
        await peer.ReplyAsync(request, new { messageId = 9223372036854775807L, messageSeq = 42, reasonCode = 128 });
        var result = await send;
        Assert.Equal("9223372036854775807", result.MessageId);
        Assert.Equal(42UL, result.MessageSeq);
        Assert.Equal(128, (int)result.ReasonCode);
        Assert.False(result.IsSuccess);
        Assert.Equal(result, await Within(ackEvent.Task));
    }

    [Fact]
    public async Task ConcurrentRequestsCorrelateOutOfOrderResults()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, Options);
        var peer = await server.ConnectAsync(client);
        var sends = Enumerable.Range(0, 32).Select(i => client.SendAsync("bob", ChannelType.Person, new { i }, new() { ClientMsgNo = i.ToString() })).ToArray();
        var requests = new List<JsonElement>();
        for (var i = 0; i < sends.Length; i++) requests.Add(await peer.ReadAsync());
        foreach (var request in requests.AsEnumerable().Reverse())
            await peer.ReplyAsync(request, new { reasonCode = 1, messageId = request.GetProperty("params").GetProperty("clientMsgNo").GetString() });
        var results = await Task.WhenAll(sends);
        Assert.Equal(Enumerable.Range(0, 32).Select(i => i.ToString()), results.Select(r => r.MessageId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceivesObjectOrBase64AcrossFragmentsAndAcknowledges(bool base64)
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, Options);
        var message = Signal<RecvMessage>();
        client.Message += value => message.TrySetResult(value);
        var peer = await server.ConnectAsync(client);
        var payload = new { type = 1, content = "你好 🌍" };
        var packet = JsonSerializer.Serialize(new { method = "recv", @params = new {
            header = new { redDot = true, syncOnce = true, dup = true }, messageId = 9223372036854775807L,
            messageSeq = 7, timestamp = 123, channelId = "alice", channelType = 1, fromUid = "bob",
            payload = base64 ? (object)Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload)) : payload } });
        await peer.RawAsync(packet, fragment: 7);
        var received = await Within(message.Task);
        Assert.Equal("你好 🌍", received.Payload.GetProperty("content").GetString());
        Assert.Equal("9223372036854775807", received.MessageId);
        var ack = await peer.ReadAsync();
        Assert.Equal("recvack", ack.GetProperty("method").GetString());
        Assert.False(ack.TryGetProperty("id", out _));
        Assert.Equal(7, ack.GetProperty("params").GetProperty("messageSeq").GetInt64());
        Assert.True(ack.GetProperty("params").GetProperty("header").GetProperty("syncOnce").GetBoolean());
    }

    [Theory]
    [InlineData("{\"online\":true}", JsonValueKind.Object)]
    [InlineData("plain-text", JsonValueKind.String)]
    public async Task CustomEventsDecodeJsonStrings(string data, JsonValueKind kind)
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, Options);
        var received = Signal<EventNotification>();
        client.CustomEvent += value => received.TrySetResult(value);
        var peer = await server.ConnectAsync(client);
        await peer.NotifyAsync("event", new { id = "e1", type = "user.status", timestamp = 1234567890123L, data });
        var value = await Within(received.Task);
        Assert.Equal(kind, value.Data.ValueKind);
        Assert.Equal(1234567890123L, value.Timestamp);
    }

    [Fact]
    public async Task CallbackFailureDoesNotBreakOtherListenersOrAcknowledgment()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, Options);
        var delivered = Signal<RecvMessage>();
        var error = Signal<Exception>();
        client.Message += _ => throw new Exception("secret-payload");
        client.Message += value => delivered.TrySetResult(value);
        client.Error += _ => throw new Exception("another-secret");
        client.Error += value => error.TrySetResult(value);
        var peer = await server.ConnectAsync(client);
        await peer.NotifyAsync("recv", new { messageId = "1", messageSeq = 1, channelId = "alice", fromUid = "bob", payload = new { type = 1 } });
        await Within(delivered.Task);
        Assert.DoesNotContain("secret", (await Within(error.Task)).ToString());
        Assert.Equal("recvack", (await peer.ReadAsync()).GetProperty("method").GetString());
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task RpcErrorAndDebugLoggingDoNotExposeRemoteData()
    {
        var logs = new ConcurrentQueue<string>();
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, new() { AutoReconnect = false, DebugLogger = logs.Enqueue });
        var peer = await server.ConnectAsync(client);
        var send = client.SendAsync("bob", ChannelType.Person, new { content = "secret-payload" });
        var request = await peer.ReadAsync();
        await peer.SendAsync(new { id = request.GetProperty("id").GetString(), error = new { code = 403, message = "secret-token", data = "secret-payload" } });
        var error = await Assert.ThrowsAsync<WKIMRpcException>(() => send);
        Assert.Equal(403, error.Code);
        Assert.DoesNotContain("secret", error.ToString());
        await client.DisconnectAsync();
        Assert.DoesNotContain("secret", string.Join(" ", logs));
    }

    [Fact]
    public async Task RequestCapacityRejectsExcessAndReturnsAfterCompletion()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, new() { MaxPendingRequests = 1, AutoReconnect = false });
        var peer = await server.ConnectAsync(client);
        var first = client.SendAsync("bob", ChannelType.Person, new { type = 1 });
        var request = await peer.ReadAsync();
        await Assert.ThrowsAsync<WKIMBackpressureException>(() => client.SendAsync("bob", ChannelType.Person, new { type = 1 }));
        await peer.ReplyAsync(request, new { reasonCode = 1 });
        await first;
        var second = client.SendAsync("bob", ChannelType.Person, new { type = 1 });
        await peer.ReplyAsync(await peer.ReadAsync(), new { reasonCode = 1 });
        Assert.True((await second).IsSuccess);
    }

    [Fact]
    public async Task LateResponseAfterTimeoutCannotCompleteAnotherRequest()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, new() { RequestTimeout = TimeSpan.FromMilliseconds(200), AutoReconnect = false });
        var peer = await server.ConnectAsync(client);
        var first = client.SendAsync("bob", ChannelType.Person, new { type = 1 });
        var old = await peer.ReadAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => first);
        var second = client.SendAsync("bob", ChannelType.Person, new { type = 1 });
        var current = await peer.ReadAsync();
        await peer.ReplyAsync(old, new { reasonCode = 1, messageId = "old" });
        await peer.ReplyAsync(current, new { reasonCode = 1, messageId = "new" });
        Assert.Equal("new", (await second).MessageId);
    }

    [Fact]
    public async Task SendCancellationRemovesPendingRequestWithoutReplaying()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, Options);
        var peer = await server.ConnectAsync(client);
        using var cancellation = new CancellationTokenSource();
        var send = client.SendAsync("bob", ChannelType.Person, new { type = 1 }, cancellationToken: cancellation.Token);
        var old = await peer.ReadAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        await peer.ReplyAsync(old, new { reasonCode = 1 });
        var next = client.SendAsync("bob", ChannelType.Person, new { type = 1 });
        await peer.ReplyAsync(await peer.ReadAsync(), new { reasonCode = 1 });
        Assert.True((await next).IsSuccess);
    }

    [Fact]
    public async Task InvalidPayloadAndOversizedSendAreRejectedWithoutWireTraffic()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = new WKIM(server.Url, Auth, new() { MaxMessageBytes = 512, AutoReconnect = false });
        var peer = await server.ConnectAsync(client);
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync("bob", ChannelType.Person, "text"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync("bob", ChannelType.Person, new { content = new string('x', 600) }));
        Assert.False(peer.TryRead(out _));
        Assert.True(client.IsConnected);
    }
}
