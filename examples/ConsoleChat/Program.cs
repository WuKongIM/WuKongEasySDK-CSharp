using WuKongEasySDK;

static string Required(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Set {name} before running this example.");

if (args.Length != 1)
{
    Console.WriteLine("Usage: dotnet run --project examples/ConsoleChat -- <peer-uid>");
    return;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
await using var im = new WKIM(Required("WUKONGIM_WS_URL"), new AuthOptions
{
    Uid = Required("WUKONGIM_UID"), Token = Required("WUKONGIM_TOKEN"), DeviceFlag = DeviceFlag.Desktop
});
im.Connected += _ => Console.WriteLine("Connected. Type text and press Enter; /quit exits.");
im.Disconnected += _ => Console.WriteLine("Disconnected.");
im.Reconnecting += state => Console.WriteLine($"Reconnecting, attempt {state.Attempt}.");
im.Error += _ => Console.WriteLine("SDK operation failed.");
im.Message += message =>
{
    // This is the chat UI, not an operational log. Only display the intended text field.
    if (message.Payload.ValueKind == System.Text.Json.JsonValueKind.Object &&
        message.Payload.TryGetProperty("content", out var content) &&
        content.ValueKind == System.Text.Json.JsonValueKind.String)
        Console.WriteLine($"Received: {content.GetString()}");
};

try
{
    await im.ConnectAsync(shutdown.Token);
    while (!shutdown.IsCancellationRequested)
    {
        var text = await Console.In.ReadLineAsync(shutdown.Token);
        if (text is null or "/quit") break;
        if (string.IsNullOrWhiteSpace(text)) continue;
        try
        {
            var result = await im.SendAsync(args[0], ChannelType.Person, new { type = 1, content = text }, cancellationToken: shutdown.Token);
            Console.WriteLine(result.IsSuccess ? "Server accepted SEND." : $"SEND rejected: {(int)result.ReasonCode}.");
        }
        catch (WKIMException) { Console.WriteLine("Send failed; check connection before retrying."); }
        catch (TimeoutException) { Console.WriteLine("Send timed out; delivery may be uncertain."); }
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
catch (Exception) { Console.Error.WriteLine("Connection failed. Check the endpoint and backend-issued credentials."); Environment.ExitCode = 1; }
