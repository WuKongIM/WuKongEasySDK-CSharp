# Three-node C# / JavaScript acceptance

The approved test boundary is the public C#/JS SDK connection, SEND/RECV, error,
and disposal API against real WuKongIM processes. Public Product HTTP sets up
fixture identities/membership and supplies a selected node's `/route`; read-only
Manager HTTP proves cluster authority; public `/user/onlinestatus` verifies
that every surviving ingress sees all three device routes before each send phase. No SDK internals or database reads are
used to establish success.

The matrix fixes NuGet `WuKongEasySDK 1.0.0` and npm `easyjssdk 2.0.5`, restored
into empty caches, and also checks the candidate C# project. It uses the same
candidate server repair source `c3dc526de3bc3f91461f32618ebdee7997984058`, with
Node 24.3.0 native WebSocket. This is a source receipt, not a new server release.
The [single-node/WSS matrix](interoperability.md) retains its original beta.9 pin.
The cluster candidate loads cold local replicas before migration probes and
completes fenced leader metadata application with writes closed. Full quorum
recovery remains mandatory after fence removal. Three processes share 256 hash slots, 10 logical Slots, three Slot
voters, and three Channel replicas. Token authentication and delivery are enabled.

Before messaging, every node must agree on actual Raft leaders for one second,
report three voters and quorum, and cover every hash slot exactly once.
Recovery requires the Channel leader to be alive and its migration write fence cleared.
After a Slot authority change, live UID routes may need a heartbeat/touch to
reappear. Before each message phase, require every live ingress to report all
three Desktop routes online for one second, with a 45-second bound; preserve
missing-route observations and elapsed time. Do not retry an already-ACKed SEND
that was attempted before its recipient became visible. The group
must expose three replicas and ISR members through Manager. C# initially connects
to node 1, JS to node 2, and an independent JS group recipient to node 3.

The five scenario groups require:

1. Bidirectional person messages for every client pair, plus C#/JS group fanout
   to both other members. Match SENDACK/RECV message IDs beyond JS's safe integer
   range, sequence, client correlation, sender, group identity, and nested JSON.
2. Kill node 1. C# reports disconnect/retry and rejects an offline SEND. Dispose
   that client, obtain node 3's route, and create a replacement with the same
   credentials. Require person and group delivery while node 1 remains stopped.
3. Restart node 1 with its existing data, require cluster/group convergence, then
   reconnect the original identity there and verify persisted auth/membership.
4. Repeat node loss/address replacement for JS on node 2, switching to node 1.
5. Restart node 2, return its identity, and verify communication again.

Address replacement belongs to the application. Each SDK instance has one fixed
endpoint and retries that endpoint; neither SDK discovers a surviving node here.
The fixture's trusted backend explicitly selects a surviving owned node and calls
its `/route`. Retire the old client and its retries before creating a replacement.
The pinned server may reject replacement login with SystemError `15` while its
old owner route remains active: conflict handling still tries to contact the
stopped owner. The default 90-second presence route TTL is explicitly retained.
The application fixture records these rejections and retries only this observed
activation error, disposing each failed client, for at most 110 seconds. Invalid
credentials and other failures stop the test. This is bounded eventual recovery,
not immediate failover or a general recommendation to retry every system error.

An in-flight SEND interrupted by a failure may have an unknown outcome; this test
checks sends after observed disconnection and does not claim exactly-once delivery
or automatic replay. Confirmed business sends are not broadly retried to hide
failures. All person channels are established before fault injection, so this
receipt does not imply new three-replica placement while one node is absent.

## Run and inspect

Build the exact clean cluster server commit from `tests/interop/pins.json` in
an independent clone, using the build method in [interoperability](interoperability.md),
then run these lanes sequentially because they share the consumer build output:

```sh
WUKONGIM_BINARY=/absolute/path/to/wukongim python3 scripts/interop.py --transport native --topology three-node
WUKONGIM_BINARY=/absolute/path/to/wukongim python3 scripts/interop.py --transport native --topology three-node --candidate
```

`DOTNET` and `NODE` may select exact executables. Setup has bounded deadlines;
cluster scenarios have a 420-second deadline and stop all owned nodes and clients.
The fixture keeps the server health defaults and uses a 100 ms Channel migration
scan, at most 10 pages and four tasks per tick, with four executor tasks. The
pinned scanner starts each tick from the first local Slot; its default one-page
budget can starve later Slots. This fixture's explicit ten-page budget covers
all ten logical Slots and its four established channels. It is a configuration
precondition of this receipt, not proof that the server default repairs every
Slot or that this budget is appropriate at production scale. It does not change machine trust or buy cloud resources.

CI adds two Linux jobs to the existing six single-node transport reports. The
`artifacts/interop-{released,candidate}-cluster.json` reports retain package
integrity, server binary hash, actual harness revision, observed Slot/group
replicas, explicit ingress replacements, offline and activation rejection categories, application connection time, ACK/RECV
receipts, presence convergence observations, failed-exchange ACK/recipient details,
and owned-node cleanup status. Only sanitized JSON is uploaded.

This bounded same-host three-node WS test does not establish multi-host network
partitions, C# browser WebAssembly, multi-node WSS, large-group capacity, offline
synchronization, or long-duration stability. Existing WSS certificate controls
remain in the separate single-node matrix.
