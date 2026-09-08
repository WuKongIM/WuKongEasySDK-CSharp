using System.Threading.Channels;
using WuKongEasySDK;

static string Required(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Missing {name}.");

var url = Required("WUKONGIM_WS_URL");
var aliceUid = Required("WUKONGIM_ALICE_UID");
var bobUid = Required("WUKONGIM_BOB_UID");
var options = new WKIMOptions { PingInterval = TimeSpan.FromMilliseconds(100), AutoReconnect = false };
await using var alice = new WKIM(url, new() { Uid = aliceUid, Token = Required("WUKONGIM_ALICE_TOKEN") }, options);
await using var bob = new WKIM(url, new() { Uid = bobUid, Token = Required("WUKONGIM_BOB_TOKEN") }, options);
var aliceInbox = Channel.CreateUnbounded<RecvMessage>();
var bobInbox = Channel.CreateUnbounded<RecvMessage>();
alice.Message += message => aliceInbox.Writer.TryWrite(message);
bob.Message += message => bobInbox.Writer.TryWrite(message);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    await Task.WhenAll(alice.ConnectAsync(deadline.Token), bob.ConnectAsync(deadline.Token));
    async Task Exchange(WKIM sender, string senderUid, string targetUid, Channel<RecvMessage> inbox)
    {
        var content = $"C# 验证 🌍 {Guid.NewGuid():N}";
        var ack = await sender.SendAsync(targetUid, ChannelType.Person, new { type = 1, content }, cancellationToken: deadline.Token);
        var received = await inbox.Reader.ReadAsync(deadline.Token);
        if (!ack.IsSuccess || received.MessageId != ack.MessageId || received.MessageSeq != ack.MessageSeq ||
            received.FromUid != senderUid || received.Payload.GetProperty("content").GetString() != content)
            throw new InvalidOperationException("Bidirectional message assertion failed.");
    }
    await Exchange(alice, aliceUid, bobUid, bobInbox);
    await Exchange(bob, bobUid, aliceUid, aliceInbox);
    await bob.DisconnectAsync();
    await bob.ConnectAsync(deadline.Token);
    await Exchange(alice, aliceUid, bobUid, bobInbox);
    // Let the short test heartbeat complete against the real Product Gateway.
    await Task.Delay(350, deadline.Token);
    if (!alice.IsConnected || !bob.IsConnected) throw new InvalidOperationException("Heartbeat connection lost.");
    await using var invalid = new WKIM(url, new() { Uid = aliceUid, Token = "deliberately-invalid-token" }, options);
    try { await invalid.ConnectAsync(deadline.Token); throw new InvalidOperationException("Invalid token was accepted."); }
    catch (Exception error) when (error is WKIMAuthenticationException or WKIMRpcException) { }
    await alice.DisconnectAsync();
    await bob.DisconnectAsync();
    Console.WriteLine("PASS: authenticated Alice/Bob, Unicode bidirectional SENDACK/RECV, reconnect, heartbeat, invalid-token rejection, cleanup.");
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL: {error.GetType().Name}; inspect bounded local server evidence.");
    Environment.ExitCode = 1;
}
