using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WuKongEasySDK.Tests;

/// <summary>Loopback WebSocket peer that controls protocol messages without mocking SDK internals.</summary>
internal sealed class TestServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Channel<Peer> _peers = Channel.CreateUnbounded<Peer>();
    private int _connections;
    public int Connections => Volatile.Read(ref _connections);
    public string Url { get; }

    private TestServer(WebApplication app, string url) { _app = app; Url = url; }

    public static async Task<TestServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.UseWebSockets();
        TestServer? server = null;
        app.Run(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var peer = new Peer(socket);
            Interlocked.Increment(ref server!._connections);
            server._peers.Writer.TryWrite(peer);
            await peer.PumpAsync();
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        server = new TestServer(app, address.Replace("http://", "ws://", StringComparison.Ordinal));
        return server;
    }

    public async Task<Peer> AcceptAsync() => await _peers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    public async Task<Peer> ConnectAsync(WKIM client)
    {
        var connect = client.ConnectAsync();
        var peer = await AcceptAsync();
        await peer.ReplyAsync(await peer.ReadAsync(), new { reasonCode = 1, nodeId = 1 });
        await connect;
        return peer;
    }
    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await _app.StopAsync(deadline.Token);
        await _app.DisposeAsync();
    }
}

internal sealed class Peer(WebSocket socket)
{
    private readonly Channel<JsonElement> _messages = Channel.CreateUnbounded<JsonElement>();
    private readonly SemaphoreSlim _write = new(1);
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<JsonElement> ReadAsync() => await _messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    public bool TryRead(out JsonElement value) => _messages.Reader.TryRead(out value);
    public Task ReplyAsync(JsonElement request, object? result) => SendAsync(new { id = request.GetProperty("id").GetString(), result });
    public Task NotifyAsync(string method, object parameters) => SendAsync(new { method, @params = parameters });
    public Task SendAsync(object packet) => RawAsync(JsonSerializer.Serialize(packet));
    public async Task RawAsync(string value, int fragment = int.MaxValue, WebSocketMessageType type = WebSocketMessageType.Text)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await _write.WaitAsync();
        try
        {
            for (var start = 0; start < bytes.Length; start += fragment)
            {
                var count = Math.Min(fragment, bytes.Length - start);
                await socket.SendAsync(bytes.AsMemory(start, count), type, start + count == bytes.Length, CancellationToken.None);
                if (fragment == int.MaxValue) break;
            }
        }
        finally { _write.Release(); }
    }
    public void Abort() => socket.Abort();
    public async Task PumpAsync()
    {
        try
        {
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream();
                ValueWebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    stream.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                using var document = JsonDocument.Parse(stream.ToArray());
                _messages.Writer.TryWrite(document.RootElement.Clone());
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { Closed.TrySetResult(); _messages.Writer.TryComplete(); }
    }
}
