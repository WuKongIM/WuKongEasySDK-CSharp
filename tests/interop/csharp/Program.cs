using System.Text.Json;
using WuKongEasySDK;

// JSON lines are private fixture IPC. They never carry production credentials.
var outputLock = new object();
void Emit(object value)
{
    lock (outputLock) Console.WriteLine(JsonSerializer.Serialize(value));
}
await using var im = new WKIM(Environment.GetEnvironmentVariable("INTEROP_URL")!, new AuthOptions
{
    Uid = Environment.GetEnvironmentVariable("INTEROP_UID")!,
    Token = Environment.GetEnvironmentVariable("INTEROP_TOKEN")!,
    DeviceFlag = DeviceFlag.Desktop
});
im.Connected += _ => Emit(new { kind = "connected" });
im.Disconnected += _ => Emit(new { kind = "disconnected" });
im.Reconnecting += _ => Emit(new { kind = "reconnecting" });
im.Error += _ => Emit(new { kind = "error" });
im.Message += message => Emit(new
{
    kind = "message", messageId = message.MessageId, messageSeq = message.MessageSeq.ToString(),
    clientMsgNo = message.ClientMsgNo, fromUid = message.FromUid, payload = message.Payload,
    channelId = message.ChannelId, channelType = (int)message.ChannelType
});
while (await Console.In.ReadLineAsync() is { } line)
{
    using var document = JsonDocument.Parse(line);
    var command = document.RootElement;
    var id = command.GetProperty("id").GetInt32();
    object? data = null;
    try
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        switch (command.GetProperty("op").GetString())
        {
            case "connect":
                await im.ConnectAsync(deadline.Token);
                break;
            case "send":
                var ack = await im.SendAsync(command.GetProperty("target").GetString()!,
                    command.TryGetProperty("channelType", out var channelType) ? (ChannelType)channelType.GetInt32() : ChannelType.Person,
                    command.GetProperty("payload"),
                    new SendOptions { ClientMsgNo = command.GetProperty("clientMsgNo").GetString()! }, deadline.Token);
                data = new { messageId = ack.MessageId, messageSeq = ack.MessageSeq.ToString(),
                    clientMsgNo = ack.ClientMsgNo, reasonCode = (int)ack.ReasonCode };
                break;
            case "disconnect": await im.DisconnectAsync(); break;
            case "state": data = new { connected = im.IsConnected }; break;
            case "exit": await im.DisconnectAsync(); Emit(new { kind = "result", id, ok = true }); return;
            default: throw new InvalidOperationException();
        }
        Emit(new { kind = "result", id, ok = true, data });
    }
    catch (Exception error)
    {
        Emit(new { kind = "result", id, ok = false, category = error.GetType().Name,
            code = error is WKIMRpcException rpc ? (int?)rpc.Code :
                error is WKIMAuthenticationException auth ? (int?)auth.ReasonCode : null });
    }
}
