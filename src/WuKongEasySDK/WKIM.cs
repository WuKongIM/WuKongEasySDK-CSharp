using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace WuKongEasySDK;

/// <summary>
/// An application-owned identity and connection. Requests are correlated per socket generation;
/// event callbacks run in order on a bounded background dispatcher, outside the receive pump.
/// </summary>
public sealed class WKIM : IAsyncDisposable
{
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly object _gate = new();
    private readonly Uri _url;
    private readonly AuthOptions _auth;
    private readonly WKIMOptions _options;
    private readonly string _deviceId;
    private readonly Channel<Action> _events;
    private readonly Task _dispatcher;
    // Only the supervisor replaces sessions. Each session owns its own pending requests and write lock.
    private Session? _session;
    private CancellationTokenSource? _lifetime;
    private Task _run = Task.CompletedTask;
    private Task? _dispose;
    private TaskCompletionSource<ConnectResult> _ready = NewCompletion<ConnectResult>();
    private ConnectResult? _lastConnect;
    private bool _disposed;
    private bool _stopping;
    private int _connected;

    public event Action<ConnectResult>? Connected;
    public event Action<DisconnectInfo>? Disconnected;
    public event Action<RecvMessage>? Message;
    public event Action<SendResult>? SendAck;
    public event Action<EventNotification>? CustomEvent;
    public event Action<ReconnectingInfo>? Reconnecting;
    public event Action<Exception>? Error;

    public bool IsConnected => Volatile.Read(ref _connected) == 1;
    public string DeviceId => _deviceId;

    public WKIM(string url, AuthOptions auth, WKIMOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(auth);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("A ws:// or wss:// endpoint without user info or fragment is required.", nameof(url));
        ArgumentException.ThrowIfNullOrWhiteSpace(auth.Uid);
        ArgumentNullException.ThrowIfNull(auth.Token);
        if (!Enum.IsDefined(auth.DeviceFlag)) throw new ArgumentOutOfRangeException(nameof(auth));
        _url = uri;
        _auth = auth;
        _options = options ?? new();
        _options.Validate();
        _deviceId = string.IsNullOrWhiteSpace(auth.DeviceId) ? Guid.NewGuid().ToString("N") : auth.DeviceId;
        _events = Channel.CreateBounded<Action>(new BoundedChannelOptions(_options.MaxQueuedEvents)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
        });
        _dispatcher = Task.Run(DispatchAsync);
    }

    /// <summary>Creates an independent instance; unlike JS's default, this never replaces a global client.</summary>
    public static WKIM Init(string url, AuthOptions auth, WKIMOptions? options = null) => new(url, auth, options);

    /// <summary>
    /// Opens and authenticates, coalescing concurrent callers. Cancellation cancels only this caller's
    /// wait; DisconnectAsync cancels the shared attempt. An initial failure does not retry silently.
    /// </summary>
    public Task<ConnectResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping) throw new InvalidOperationException("Await DisconnectAsync before connecting again.");
            if (IsConnected) return Task.FromResult(_lastConnect!);
            if (_run.IsCompleted)
            {
                _lifetime?.Dispose();
                _lifetime = new();
                _ready = NewCompletion<ConnectResult>();
                var lifetime = _lifetime;
                _run = Task.Run(() => RunAsync(lifetime.Token));
            }
            return _ready.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Sends once and returns the server's reason code. Timeout, cancellation, or transport loss may
    /// leave delivery uncertain; no automatic replay or offline queue is provided.
    /// </summary>
    public async Task<SendResult> SendAsync(string channelId, ChannelType channelType, object payload,
        SendOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentNullException.ThrowIfNull(payload);
        if ((int)channelType is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(channelType));
        Session session;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsConnected || _session is null) throw new WKIMDisconnectedException();
            session = _session;
        }
        options ??= new();
        var result = Read<SendResult>(await RequestAsync(session, "send", () =>
        {
            var data = JsonSerializer.SerializeToElement(payload);
            if (data.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                throw new ArgumentException("Payload must be a non-null JSON object or array.", nameof(payload));
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(data);
            if (payloadBytes.Length > _options.MaxMessageBytes) throw new ArgumentException("Payload exceeds MaxMessageBytes.", nameof(payload));
            return new
            {
                channelId, channelType, payload = Convert.ToBase64String(payloadBytes),
                clientMsgNo = options.ClientMsgNo ?? Guid.NewGuid().ToString("N"),
                header = options.Header, setting = options.Setting, topic = options.Topic
            };
        }, _options.RequestTimeout, cancellationToken).ConfigureAwait(false));
        // A completed SEND must not become a failed task merely because an observer is overloaded.
        TryQueue(SendAck, result);
        return result;
    }

    /// <summary>Stops reconnect and aborts pending work, then waits for the connection supervisor to exit.</summary>
    public async Task DisconnectAsync()
    {
        Task run;
        lock (_gate)
        {
            _stopping = true;
            Volatile.Write(ref _connected, 0);
            _lifetime?.Cancel();
            _session?.Stop();
            run = _run;
        }
        await run.ConfigureAwait(false);
        lock (_gate) _stopping = false;
    }

    /// <summary>Stops the client and drains queued callbacks. Await from application lifecycle code, not a blocking callback.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            return new ValueTask(_dispose ??= Task.Run(async () =>
            {
                await DisconnectAsync().ConfigureAwait(false);
                _events.Writer.TryComplete();
                await _dispatcher.ConfigureAwait(false);
                Connected = null; Disconnected = null; Message = null; SendAck = null;
                CustomEvent = null; Reconnecting = null; Error = null;
                _lifetime?.Dispose();
                _lifetime = null;
            }));
        }
    }

    // The sole lifecycle loop serializes cleanup before a replacement socket is admitted.
    private async Task RunAsync(CancellationToken lifetime)
    {
        var everConnected = false;
        var attempt = 0;
        Exception terminal = new WKIMDisconnectedException();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var session = new Session(lifetime, _options.MaxPendingRequests);
                lock (_gate) _session = session;
                Task? receiver = null;
                Task? heartbeat = null;
                var authenticated = false;
                var permanent = false;
                DisconnectInfo disconnect = new(false);
                try
                {
                    using var opening = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    opening.CancelAfter(_options.ConnectTimeout);
                    Log("Connecting.");
                    await session.Socket.ConnectAsync(_url, opening.Token).ConfigureAwait(false);
                    receiver = ReceiveAndFailPendingAsync(session);
                    ConnectResult result;
                    try
                    {
                        result = Read<ConnectResult>(await RequestAsync(session, "connect", () => new
                        {
                            uid = _auth.Uid, token = _auth.Token, deviceId = _deviceId,
                            deviceFlag = _auth.DeviceFlag, clientTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }, _options.ConnectTimeout, opening.Token).ConfigureAwait(false));
                    }
                    catch (WKIMRpcException) { permanent = true; throw; }
                    if (result.ReasonCode != ReasonCode.Success) throw new WKIMAuthenticationException(result.ReasonCode);
                    opening.Token.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        lifetime.ThrowIfCancellationRequested();
                        _lastConnect = result;
                        Volatile.Write(ref _connected, 1);
                        Queue(Connected, result);
                        _ready.TrySetResult(result);
                        authenticated = everConnected = true;
                        attempt = 0;
                    }
                    Log("Authenticated.");
                    heartbeat = HeartbeatAsync(session);
                    await await Task.WhenAny(receiver, heartbeat).ConfigureAwait(false);
                    throw new WKIMDisconnectedException();
                }
                catch (ServerDisconnectException ex)
                {
                    terminal = ex;
                    permanent = true;
                    disconnect = ex.Info;
                }
                catch (WKIMAuthenticationException ex)
                {
                    terminal = ex;
                    permanent = true;
                    disconnect = new(false, ex.ReasonCode);
                }
                catch (Exception ex)
                {
                    terminal = ex is OperationCanceledException && !lifetime.IsCancellationRequested && !session.Token.IsCancellationRequested
                        ? new TimeoutException("WebSocket connection or authentication timed out.") : SafeError(ex);
                }
                finally
                {
                    lock (_gate)
                    {
                        Volatile.Write(ref _connected, 0);
                        if (authenticated && !lifetime.IsCancellationRequested) _ready = NewCompletion<ConnectResult>();
                    }
                    session.Stop();
                    await IgnoreFailure(receiver).ConfigureAwait(false);
                    await IgnoreFailure(heartbeat).ConfigureAwait(false);
                    lock (_gate) if (ReferenceEquals(_session, session)) _session = null;
                    session.Dispose();
                }
                TryQueue(Disconnected, lifetime.IsCancellationRequested ? new(true) : disconnect);
                Log("Disconnected.");
                if (lifetime.IsCancellationRequested) break;
                TryQueue(Error, terminal);
                if (!everConnected || permanent || !_options.AutoReconnect || attempt >= _options.MaxReconnectAttempts) break;
                var delay = TimeSpan.FromMilliseconds(Math.Min(_options.MaxReconnectDelay.TotalMilliseconds,
                    _options.InitialReconnectDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 30))));
                attempt++;
                TryQueue(Reconnecting, new(attempt, delay));
                await Task.Delay(delay, lifetime).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
            {
                Volatile.Write(ref _connected, 0);
                _ready.TrySetException(lifetime.IsCancellationRequested ? new WKIMDisconnectedException() : terminal);
                // Observe failures even when all ConnectAsync callers canceled their individual waits.
                _ = _ready.Task.Exception;
            }
        }
    }

    private async Task<JsonElement> RequestAsync(Session session, string method, Func<object> parameters,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!session.Capacity.Wait(0)) throw new WKIMBackpressureException();
        var id = Guid.NewGuid().ToString("N");
        var pending = new Pending(method);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token, cancellationToken);
        deadline.CancelAfter(timeout);
        session.Pending[id] = pending;
        try
        {
            await WriteAsync(session, new { jsonrpc = "2.0", method, @params = parameters(), id }, deadline.Token).ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.Token.IsCancellationRequested) throw new WKIMDisconnectedException();
            throw new TimeoutException($"{method} request timed out; send delivery may be uncertain.");
        }
        catch (WebSocketException) { throw new WKIMDisconnectedException(); }
        catch (ObjectDisposedException) { throw new WKIMDisconnectedException(); }
        finally
        {
            session.Pending.TryRemove(id, out _);
            _ = pending.Completion.Task.Exception;
            session.Capacity.Release();
        }
    }

    private async Task ReceiveAndFailPendingAsync(Session session)
    {
        try { await ReceiveAsync(session).ConfigureAwait(false); }
        catch (Exception ex)
        {
            var error = SafeError(ex);
            foreach (var pending in session.Pending.Values) pending.Completion.TrySetException(error);
            throw;
        }
    }

    // ClientWebSocket permits one writer at a time. The deadline includes waiting for this lock.
    private async Task WriteAsync(Session session, object value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, WireJson);
        if (bytes.Length > _options.MaxMessageBytes) throw new ArgumentException("Wire message exceeds MaxMessageBytes.");
        await session.WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await session.Socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally { session.WriteLock.Release(); }
    }

    private async Task ReceiveAsync(Session session)
    {
        var buffer = new byte[Math.Min(8192, _options.MaxMessageBytes)];
        while (!session.Token.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult part;
            do
            {
                part = await session.Socket.ReceiveAsync(buffer.AsMemory(), session.Token).ConfigureAwait(false);
                if (part.MessageType == WebSocketMessageType.Close) throw new WKIMDisconnectedException();
                if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > _options.MaxMessageBytes)
                    throw new WKIMException("Unsupported or oversized WebSocket message.");
                message.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            using var document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
            await HandleAsync(session, document.RootElement).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(Session session, JsonElement packet)
    {
        if (packet.ValueKind != JsonValueKind.Object) throw new WKIMException("Invalid JSON-RPC envelope.");
        if (packet.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
            (packet.TryGetProperty("result", out _) || packet.TryGetProperty("error", out _)))
        {
            if (!session.Pending.TryGetValue(id.GetString()!, out var pending)) return;
            if (packet.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                var code = error.GetProperty("code").GetInt32();
                pending.Completion.TrySetException(new WKIMRpcException(code));
            }
            else pending.Completion.TrySetResult(packet.GetProperty("result").Clone());
            return;
        }
        if (!packet.TryGetProperty("method", out var method)) throw new WKIMException("Invalid JSON-RPC envelope.");
        if (method.GetString() == "pong")
        {
            foreach (var pending in session.Pending.Values)
                if (pending.Method == "ping") pending.Completion.TrySetResult(JsonSerializer.SerializeToElement(new { }));
            return;
        }
        if (!packet.TryGetProperty("params", out var parameters)) return;
        switch (method.GetString())
        {
            case "recv":
                var received = Read<RecvMessage>(parameters);
                if (string.IsNullOrWhiteSpace(received.MessageId) ||
                    string.IsNullOrWhiteSpace(received.ChannelId) || string.IsNullOrWhiteSpace(received.FromUid))
                    throw new WKIMException("Invalid received message.");
                received = received with { Payload = DecodePayload(received.Payload) };
                Queue(Message, received);
                // ACK means receipt by the SDK, not UI rendering, persistence, or a user read receipt.
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
                {
                    deadline.CancelAfter(_options.RequestTimeout);
                    await WriteAsync(session, new
                    {
                        jsonrpc = "2.0", method = "recvack",
                        @params = new { header = received.Header, messageId = received.MessageId, messageSeq = received.MessageSeq }
                    }, deadline.Token).ConfigureAwait(false);
                }
                break;
            case "event":
                var notification = Read<EventNotification>(parameters);
                if (string.IsNullOrWhiteSpace(notification.Id) || string.IsNullOrWhiteSpace(notification.Type))
                    throw new WKIMException("Invalid event notification.");
                Queue(CustomEvent, notification with { Data = DecodeJsonString(notification.Data) });
                break;
            case "disconnect":
                var reasonCode = parameters.TryGetProperty("reasonCode", out var reason) ? (ReasonCode?)reason.GetInt32() : null;
                // Server text is deliberately excluded from operational errors and event metadata.
                throw new ServerDisconnectException(new(false, reasonCode));
            case "sendack":
                Queue(SendAck, Read<SendResult>(parameters));
                break;
        }
    }

    private async Task HeartbeatAsync(Session session)
    {
        using var timer = new PeriodicTimer(_options.PingInterval);
        while (await timer.WaitForNextTickAsync(session.Token).ConfigureAwait(false))
            await RequestAsync(session, "ping", () => new { }, _options.PongTimeout, session.Token).ConfigureAwait(false);
    }

    private static JsonElement DecodePayload(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return value;
        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(value.GetString()!));
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return value; }
    }

    private static JsonElement DecodeJsonString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return value;
        try
        {
            using var document = JsonDocument.Parse(value.GetString()!);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return value; }
    }

    private static T Read<T>(JsonElement value) => value.Deserialize<T>(WireJson) ?? throw new JsonException("Missing protocol result.");
    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task IgnoreFailure(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); } catch (Exception) { }
    }
    private static Exception SafeError(Exception error) => error switch
    {
        WKIMException or TimeoutException => error,
        JsonException or InvalidOperationException or FormatException => new WKIMException("Invalid protocol message."),
        _ => new WKIMDisconnectedException()
    };

    private void Queue<T>(Action<T>? handlers, T value)
    {
        if (!TryQueue(handlers, value)) throw new WKIMBackpressureException();
    }
    private bool TryQueue<T>(Action<T>? handlers, T value) => handlers is null || _events.Writer.TryWrite(() => Invoke(handlers, value));
    private void Invoke<T>(Action<T> handlers, T value)
    {
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try { handler(value); }
            catch (Exception)
            {
                Log("Event handler failed.");
                // Error handler exceptions are swallowed to prevent recursive error reporting.
                if (typeof(T) != typeof(Exception) && Error is { } errors)
                    Invoke(errors, (Exception)new WKIMException("An application event handler failed."));
            }
        }
    }
    private async Task DispatchAsync()
    {
        await foreach (var action in _events.Reader.ReadAllAsync().ConfigureAwait(false)) action();
    }
    private void Log(string text)
    {
        try { _options.DebugLogger?.Invoke(text); } catch (Exception) { }
    }

    private sealed class ServerDisconnectException(DisconnectInfo info) : WKIMException("Server ended the session.")
    {
        public DisconnectInfo Info { get; } = info;
    }
    private sealed class Pending(string method)
    {
        public string Method { get; } = method;
        public TaskCompletionSource<JsonElement> Completion { get; } = NewCompletion<JsonElement>();
    }
    private sealed class Session : IDisposable
    {
        private readonly CancellationTokenSource _stop;
        public ClientWebSocket Socket { get; } = new();
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public SemaphoreSlim Capacity { get; }
        public ConcurrentDictionary<string, Pending> Pending { get; } = new();
        public CancellationToken Token { get; }
        public Session(CancellationToken lifetime, int maxPending)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            Token = _stop.Token;
            Capacity = new(maxPending, maxPending);
            Socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
        }
        public void Stop()
        {
            _stop.Cancel();
            Socket.Abort();
        }
        public void Dispose()
        {
            Socket.Dispose();
            _stop.Dispose();
        }
    }
}
