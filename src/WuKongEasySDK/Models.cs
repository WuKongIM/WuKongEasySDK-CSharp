using System.Text.Json;
using System.Text.Json.Serialization;

namespace WuKongEasySDK;

public enum ChannelType
{
    Person = 1, Group = 2, CustomerService = 3, Community = 4, CommunityTopic = 5,
    Info = 6, Data = 7, Temp = 8, Live = 9, Visitors = 10
}

/// <summary>Wire device categories; tokens must be issued for the same category.</summary>
public enum DeviceFlag { App = 0, Web = 1, Desktop = 2 }

/// <summary>Protocol values. Unknown and business-defined values remain representable.</summary>
public enum ReasonCode
{
    Unknown = 0, Success = 1, AuthFail = 2, SubscriberNotExist = 3, InBlacklist = 4,
    ChannelNotExist = 5, UserNotOnNode = 6, SenderOffline = 7, MsgKeyError = 8,
    PayloadDecodeError = 9, ForwardSendPacketError = 10, NotAllowSend = 11,
    ConnectKick = 12, NotInWhitelist = 13, QueryTokenError = 14, SystemError = 15,
    ChannelIDError = 16, NodeMatchError = 17, NodeNotMatch = 18, Ban = 19,
    NotSupportHeader = 20, ClientKeyIsEmpty = 21, RateLimit = 22,
    NotSupportChannelType = 23, Disband = 24, SendBan = 25
}

/// <summary>Credentials from a trusted backend. Never log this object.</summary>
public sealed class AuthOptions
{
    public required string Uid { get; init; }
    public required string Token { get; init; }
    public string? DeviceId { get; init; }
    public DeviceFlag DeviceFlag { get; init; } = DeviceFlag.Desktop;
}

/// <summary>Immutable connection policy; bounds cover queued work and complete wire messages.</summary>
public sealed class WKIMOptions
{
    /// <summary>Bounds WebSocket establishment plus authentication for each attempt.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Bounds send-lock admission, transport writing, and the correlated response.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(25);
    public TimeSpan PongTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public bool AutoReconnect { get; init; } = true;
    /// <summary>Maximum retries after an established connection is lost. Initial failures return immediately.</summary>
    public int MaxReconnectAttempts { get; init; } = 5;
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxPendingRequests { get; init; } = 256;
    public int MaxQueuedEvents { get; init; } = 128;
    public int MaxMessageBytes { get; init; } = 1024 * 1024;
    /// <summary>Optional diagnostics sink. Receives fixed operational strings, never credentials or content.</summary>
    public Action<string>? DebugLogger { get; init; }

    internal void Validate()
    {
        foreach (var duration in new[] { ConnectTimeout, RequestTimeout, PingInterval, PongTimeout,
                     InitialReconnectDelay, MaxReconnectDelay })
            if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(WKIMOptions), "Durations must be positive and at most Int32.MaxValue milliseconds.");
        if (MaxReconnectAttempts < 0 || MaxPendingRequests < 1 || MaxQueuedEvents < 1 || MaxMessageBytes < 256 ||
            MaxReconnectDelay < InitialReconnectDelay)
            throw new ArgumentOutOfRangeException(nameof(WKIMOptions), "Invalid request, event, message, or reconnect bounds.");
    }
}

public sealed record Header
{
    public bool NoPersist { get; init; }
    public bool RedDot { get; init; }
    public bool SyncOnce { get; init; }
    public bool Dup { get; init; }
}

public sealed record MessageSetting
{
    public bool Receipt { get; init; }
    public bool Signal { get; init; }
    public bool Stream { get; init; }
    public bool Topic { get; init; }
}

public sealed class SendOptions
{
    /// <summary>Stable business retry identifier. The SDK never automatically resends messages.</summary>
    public string? ClientMsgNo { get; init; }
    public Header Header { get; init; } = new() { RedDot = true };
    public MessageSetting? Setting { get; init; }
    public string? Topic { get; init; }
}

public sealed record ConnectResult
{
    public string? ServerKey { get; init; }
    public string? Salt { get; init; }
    public long TimeDiff { get; init; }
    public required ReasonCode ReasonCode { get; init; }
    public int? ServerVersion { get; init; }
    public ulong? NodeId { get; init; }
    public override string ToString() => $"ConnectResult {{ ReasonCode = {(int)ReasonCode} }}";
}

public sealed record SendResult
{
    [JsonConverter(typeof(MessageIdConverter))]
    public string MessageId { get; init; } = "";
    public ulong MessageSeq { get; init; }
    public string? ClientMsgNo { get; init; }
    public required ReasonCode ReasonCode { get; init; }
    public bool IsSuccess => ReasonCode == ReasonCode.Success;
    public override string ToString() => $"SendResult {{ ReasonCode = {(int)ReasonCode} }}";
}

public sealed record RecvMessage
{
    public Header Header { get; init; } = new();
    [JsonConverter(typeof(MessageIdConverter))]
    public required string MessageId { get; init; }
    public required ulong MessageSeq { get; init; }
    /// <summary>Message timestamp in Unix seconds, as supplied by the server.</summary>
    public long Timestamp { get; init; }
    public required string ChannelId { get; init; }
    public ChannelType ChannelType { get; init; }
    public required string FromUid { get; init; }
    /// <summary>Owned JSON value; object RECV and Base64-encoded JSON are normalized.</summary>
    public required JsonElement Payload { get; init; }
    public string? ClientMsgNo { get; init; }
    public MessageSetting? Setting { get; init; }
    public string? Topic { get; init; }
    public override string ToString() => "RecvMessage { Content = [redacted] }";
}

public sealed record EventNotification
{
    public Header? Header { get; init; }
    public required string Id { get; init; }
    public required string Type { get; init; }
    /// <summary>Event timestamp in Unix milliseconds; distinct from message timestamps.</summary>
    public required long Timestamp { get; init; }
    public required JsonElement Data { get; init; }
    public override string ToString() => "EventNotification { Content = [redacted] }";
}

public sealed record DisconnectInfo(bool IsManual, ReasonCode? ReasonCode = null, string? Reason = null);
public sealed record ReconnectingInfo(int Attempt, TimeSpan Delay);

/// <summary>A safe operational error. The SDK does not attach raw transport or server error objects.</summary>
public class WKIMException(string message) : Exception(message);
public sealed class WKIMRpcException(int code) : WKIMException($"JSON-RPC request failed (code {code}).")
{
    public int Code { get; } = code;
}
public sealed class WKIMAuthenticationException(ReasonCode reasonCode) : WKIMException($"Authentication rejected (reason {(int)reasonCode}).")
{
    public ReasonCode ReasonCode { get; } = reasonCode;
}
public sealed class WKIMDisconnectedException() : WKIMException("Connection closed; the outcome of an in-flight send may be unknown.");
public sealed class WKIMBackpressureException() : WKIMException("SDK capacity reached; slow down producers or event handlers.");

/// <summary>Accepts both server numeric IDs and legacy string IDs without floating-point conversion.</summary>
internal sealed class MessageIdConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString()!;
        if (reader.TokenType == JsonTokenType.Number)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.GetRawText();
        }
        throw new JsonException("Invalid message identifier.");
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
