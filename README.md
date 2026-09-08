# WuKongEasySDK-CSharp

[中文](README_zh.md) · [C# quickstart](https://docs.githubim.com/en/sdk/easy/csharp/getting-started)

A lightweight C# communication SDK for WuKongIM, following
[WuKongEasySDK-JS](https://github.com/WuKongIM/WuKongEasySDK-JS)'s WebSocket JSON-RPC protocol.
It manages authentication, online messaging, automatic receive acknowledgments,
custom events, heartbeats, and reconnects. Your application owns UI, persistence,
history synchronization, unread counts, media, push, and business receipts.

## Requirements and installation

.NET 8 or later on Windows, Linux, or macOS. The library targets `net8.0` and uses
only the .NET base class library (`ClientWebSocket`, `System.Text.Json`).
Unity, .NET Framework, and browser WebAssembly are not currently supported targets.

Install [WuKongEasySDK 1.0.0](https://www.nuget.org/packages/WuKongEasySDK/1.0.0)
from nuget.org:

```bash
dotnet add package WuKongEasySDK --version 1.0.0
```

For a source build, use a project reference to the release commit:

```bash
git clone https://github.com/WuKongIM/WuKongEasySDK-CSharp.git
git -C WuKongEasySDK-CSharp checkout 02ea7d60cd94feef1996f41bca35ffc3b8e18ea6
dotnet new console -n MyChat
dotnet add MyChat/MyChat.csproj reference WuKongEasySDK-CSharp/src/WuKongEasySDK/WuKongEasySDK.csproj
```

For a local NuGet feed, build the package from your pinned checkout:

```bash
cd WuKongEasySDK-CSharp
dotnet pack src/WuKongEasySDK -c Release -o artifacts
dotnet add ../MyChat/MyChat.csproj package WuKongEasySDK --version 1.0.0 --source ./artifacts
```

Record the exact Git commit in your dependency setup. A local `.nupkg` is not a
public registry release.

## Quickstart

Your trusted backend supplies the WebSocket endpoint, UID, and token after product
login. It must issue the token for `DeviceFlag.Desktop` (`2`) unless you explicitly
select `App` (`0`) or `Web` (`1`). Never embed Product HTTP management credentials
in the client. Production endpoints use `wss://` with normal certificate validation.

```csharp
using WuKongEasySDK;

await using var im = new WKIM(
    Environment.GetEnvironmentVariable("WUKONGIM_WS_URL")!,
    new AuthOptions
    {
        Uid = Environment.GetEnvironmentVariable("WUKONGIM_UID")!,
        Token = Environment.GetEnvironmentVariable("WUKONGIM_TOKEN")!,
        DeviceFlag = DeviceFlag.Desktop
    });

Action<RecvMessage> onMessage = message =>
{
    // Hand message.Payload to application state; deduplicate by MessageId.
    Console.WriteLine("Message received.");
};
im.Message += onMessage;
im.Error += _ => Console.WriteLine("SDK operation failed.");
im.CustomEvent += notification =>
{
    // Route notification.Type and notification.Data to application state.
};

await im.ConnectAsync();
var result = await im.SendAsync("bob", ChannelType.Person,
    new { type = 1, content = "Hello from C# 👋" });
if (!result.IsSuccess)
    Console.WriteLine($"SEND rejected: {(int)result.ReasonCode}");

im.Message -= onMessage;
await im.DisconnectAsync();
// await using also stops reconnect, drains queued callbacks, and releases resources.
```

`ReasonCode.Success` is **1**. An accepted SEND does not prove recipient receipt
or reading. Business rejection codes, including `128–255`, remain in `SendResult`;
JSON-RPC errors throw `WKIMRpcException` with its numeric `Code`.

## API and lifecycle

| C# API | Purpose / JS equivalent |
| --- | --- |
| `new WKIM(url, auth, options)` / `WKIM.Init(...)` | Create an independent instance / `WKIM.init` |
| `ConnectAsync(cancellationToken)` | Establish and authenticate / `connect()` |
| `SendAsync(channelId, channelType, payload, options, cancellationToken)` | One SEND request / `send()` |
| `IsConnected`, `DeviceId` | Current authenticated state and stable instance device ID |
| `DisconnectAsync()` | Stop, fail pending work, and cancel reconnect / `disconnect()` |
| `DisposeAsync()` / `await using` | Final cleanup / `destroy()` |
| `Connected`, `Disconnected`, `Message`, `Error`, `SendAck`, `Reconnecting`, `CustomEvent` | Typed C# events; use `+=` and `-=` / `on` and `off` |

Instances never replace each other through a global singleton. Keep one client per
identity; when switching accounts or rotating credentials, dispose the old client
and construct one with new backend-issued credentials. Store a stable `DeviceId`
in application settings if it should persist across processes.

Concurrent `ConnectAsync` callers share one attempt. Canceling one caller cancels
only its wait; call `DisconnectAsync` to stop the shared attempt. An initial
connection/authentication failure returns immediately after cleanup. After an
established connection drops, the SDK retries at most five times with delays of
1, 2, 4, 8, and 16 seconds, capped by `MaxReconnectDelay`. A successful connection
resets this budget. Authentication rejection, server `disconnect` (including a
kick), manual disconnect, and disposal stop automatic retry.

Events run serially on a background dispatcher. Keep handlers short and marshal
UI updates to your UI dispatcher. Handlers must not synchronously block on async
SDK operations or await disposal from inside the callback. Exceptions in one
handler do not prevent other listeners or automatic ACK. A receive ACK means
admission into the SDK event queue, not successful application processing.
Already queued callbacks may finish after disconnect or listener removal;
await `DisposeAsync` from lifecycle code to drain them.

The SDK never queues offline sends or automatically replays uncertain sends.
When cancellation, timeout, or connection loss interrupts a send, delivery may
already have happened. Reconcile with your backend and reuse an application-owned
`SendOptions.ClientMsgNo` when your retry policy calls for the same logical send.

## Options and message data

| `WKIMOptions` | Default |
| --- | --- |
| `ConnectTimeout` | 10 seconds, covering WebSocket open and authentication |
| `RequestTimeout` | 15 seconds, covering write-lock wait, write, and response |
| `PingInterval` / `PongTimeout` | 25 / 10 seconds |
| `AutoReconnect` / `MaxReconnectAttempts` | `true` / `5` |
| `InitialReconnectDelay` / `MaxReconnectDelay` | 1 / 30 seconds |
| `MaxPendingRequests` | 256; excess callers fail with `WKIMBackpressureException` |
| `MaxQueuedEvents` | 128; overflow closes the connection without ACKing the unaccepted message |
| `MaxMessageBytes` | 1 MiB, including the complete Base64 JSON-RPC envelope |
| `DebugLogger` | `null`; optional sink for fixed operational strings only |

SEND accepts a JSON object or array and preserves its property names. The wire
payload is Base64-encoded UTF-8 JSON. RECV accepts both JSON objects and legacy
Base64 JSON; an undecodable string is retained as a JSON string. `Payload` and event
`Data` are owned `JsonElement` values that remain usable after callbacks return.
Custom events accept object data or JSON text; plain text stays a string.

`SendOptions` exposes `ClientMsgNo`, `Header`, `Setting`, and `Topic`. The default
header sets `RedDot = true`; an explicitly supplied header is respected. This
intentionally avoids the JS implementation's forced override of `redDot`.
`ChannelType` carries all JS wire values (1–10), but actual channel support and
membership remain server policy. Subscription, stream publishing, and history
APIs are outside this lightweight client.

Message IDs are strings, including when a server emits a JSON number, so there is
no floating-point precision loss. Message sequences and node IDs are `ulong`.
Message timestamps are Unix **seconds**; custom event timestamps are Unix
**milliseconds**. Do not confuse these units.

SDK diagnostics and exception messages never include tokens, payloads, raw
frames, server error text, or transport exception objects. Message/event model
`ToString()` redacts content. Application serializers and callbacks still own
their logging choices; do not log complete received models or auth objects.

## Run the console example

Prepare Alice and Bob's backend-issued desktop tokens, then run in two terminals:

```bash
# Terminal A; values shown are for an isolated development environment only.
export WUKONGIM_WS_URL=ws://127.0.0.1:5200
export WUKONGIM_UID=alice
export WUKONGIM_TOKEN=alice-development-token
dotnet run --project examples/ConsoleChat -- bob

# Terminal B: use bob's own UID and token, then target alice.
```

The example displays the intended chat text in its UI; it does not print tokens
or complete protocol objects. `/quit` or Ctrl+C exits.

## Build and verify

```bash
dotnet build -c Release
dotnet test -c Release
dotnet pack src/WuKongEasySDK -c Release -o artifacts

# Supply an already-built WuKongIM binary. This fixture starts and cleans up only
# its own loopback single-node cluster, with token auth and 256 hash slots.
WUKONGIM_BINARY=/absolute/path/to/wukongim python3 scripts/smoke.py
```

See [validation evidence](docs/validation.md) for exact source revisions and
scope. CI builds and tests on Windows, Linux, and macOS. The separate
[C#/JS interoperability workflow](docs/interoperability.md) tests both the public
NuGet release and candidate source against pinned JS/server versions, including
network interruption and server restart. Separate lanes cover Node/`ws`, repaired
JS source with native Node WebSocket, and real Chromium over normally validated WSS.
The separate
[release workflow](docs/releasing.md) publishes only after three-platform package
validation, then verifies the exact public package in a fresh consumer.

Protocol reference: JS `v2.0.4`, source
`9c03c98c725982fac224cd1d3b52456eae983975`. This is a C# implementation with explicit
async lifecycle, bounds, typed events, and rejection handling, not a claim that
JavaScript implementation bugs are part of the compatibility contract.
