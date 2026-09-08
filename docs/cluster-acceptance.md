# Three-node C# / JavaScript reproduction

**Blocked: full cluster acceptance has not passed.** Server migration, recovery,
and post-rejoin delivery findings are tracked in
[WuKongIM issue 927](https://github.com/WuKongIM/WuKongIM/issues/927).
This SDK task does not change the server. The fixture retains strict failure
reporting; an individual passing lane is not a complete acceptance receipt.

## Packages, server, and boundaries

The fixture uses public NuGet `WuKongEasySDK 1.0.0`, npm `easyjssdk 2.0.5`, and
Node 24.3.0 native WebSocket. A second lane builds the candidate C# source.
`tests/interop/pins.json` pins both single-node and cluster fixtures to released
server `v3.0.0-beta.9`, source `734166e0ec30fc0f6f10fef6f6d1889d079ab636`.
No unmerged server repair is a dependency of this SDK.

Three owned loopback processes use 256 hash slots, 10 logical Slots, three
Slot/Channel replicas, MinISR=2, Token authentication and online delivery.
Public Product HTTP creates fixture identities and membership and supplies the
selected node's route. Read-only Manager HTTP observes actual authority;
`/user/onlinestatus` observes recipient presence. Assertions use public SDK
connection, SEND/RECV, error, and disposal APIs, not SDK internals or database reads.

The intended five scenario groups are:

1. Every directed person-message pair and C#/JS group fanout. Check exact
   ACK/RECV IDs beyond JS's safe integer range, sequence, correlation, sender,
   channel identity, and Unicode/nested JSON values.
2. Kill node 1; observe C# disconnection/reconnect and offline SEND rejection.
   Dispose the old client, create a replacement on node 3, and require messaging
   while node 1 stays stopped.
3. Restart node 1 using its existing data; return C# to that ingress and require
   persisted authentication, group membership, and messaging.
4. Repeat the node-loss/address-replacement scenario for JS on node 2.
5. Restart node 2 and verify messaging again.

## Application behavior and known limits

Each SDK instance retries its fixed endpoint. To switch addresses, the trusted
application backend selects a live ingress and obtains its `/route`; the
application disposes the previous SDK and constructs a replacement. Do not replay
an interrupted SEND automatically: its durable outcome may be unknown.

The reproduction waits for observed Slot quorum, alive Channel authority without
a migration write fence, and all recipient Desktop routes visible from every
surviving ingress. Presence must remain visible for one second within 45 seconds.
Full ISR metadata and node readiness do not prove physical replica catch-up
before another fault; the server Issue records this evidence gap.

The fixture keeps the 90-second presence TTL. During replacement login it records
and retries only the observed activation SystemError 15, disposing each failed
client, with a 110-second bound. Other login errors stop the test. This is a
reproduction control, not a recommendation to retry arbitrary system errors.
Confirmed or ambiguous SENDs are never retried to obtain a passing result.

Migration scanning is explicitly set to 100 ms, 10 pages/tick, four tasks/tick,
and four executor tasks. This covers the tiny fixture; it does not establish
recovery under default scan budgets or provide production tuning guidance.

## Run and inspect

Build the exact clean pinned server in an independent clone as described in
[interoperability](interoperability.md), then run the lanes sequentially:

```sh
WUKONGIM_BINARY=/absolute/path/to/wukongim python3 scripts/interop.py --transport native --topology three-node
WUKONGIM_BINARY=/absolute/path/to/wukongim python3 scripts/interop.py --transport native --topology three-node --candidate
```

`DOTNET` and `NODE` may select executables. Setup is bounded; the cluster phase
has a 420-second deadline and stops all owned processes on success or failure.
Reports at `artifacts/interop-{released,candidate}-cluster.json` contain package
integrity, exact source/binary identities, ACK/RECV, connection errors, authority
and presence observations, bounded failure diagnostics, and cleanup status.
Plan terminal log samples lack exact message correlation and are not message
proof. Reports exclude fixture tokens and raw logs.

Regular push/PR CI runs four single-node jobs producing six transport reports.
Manual dispatch with `include_cluster=true` adds the two cluster reproduction
jobs. These jobs fail normally when a server blocker occurs; they do not use
`continue-on-error` or convert a failure into success.

Historical experiments used unmerged server source `c3dc526de3bc3f91461f32618ebdee7997984058`.
[Run 34208420961](https://github.com/WuKongIM/WuKongEasySDK-CSharp/actions/runs/34208420961)
and the diagnostic runs linked in issue 927 are investigation evidence only;
they must not be attributed to beta.9 or to a released server fix.

This bounded WS fixture does not establish multi-host partitions, multi-node WSS,
offline synchronization, large-group capacity, or long-duration stability.
The verified single-node/WSS matrix remains documented separately.
