"""Bounded three-node acceptance through public SDK and HTTP interfaces."""


def verify_group(message, channel_id):
    """A matching payload from a different channel is not group-delivery evidence."""
    if message.get('channelId') != channel_id or type(message.get('channelType')) is not int or message['channelType'] != 2:
        raise AssertionError('Group channel identity mismatch')

def retry_activation(result, *, recovery):
    """Only the observed pinned-server SystemError is eligible for application login retry."""
    return recovery and result.get('ok') is False and type(result.get('code')) is int and result['code'] == 15


import asyncio
import json
import os
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

from interop import Actor, ROOT, Server, verify_exchange
from smoke import port


def get_json(address, route):
    with urllib.request.urlopen(f'http://127.0.0.1:{address}{route}', timeout=2) as response:
        return json.load(response)


async def stable_slots(nodes, alive):
    """Require actual Raft leaders and quorum on every surviving node for one second."""
    previous, since = None, 0
    async with asyncio.timeout(60):
        while True:
            try:
                snapshots = []
                for node_id in alive:
                    page = await asyncio.to_thread(get_json, nodes[node_id].manager,
                                                   f'/manager/slots?node_id={node_id}')
                    rows = sorted(page['items'], key=lambda row: row['slot_id'])
                    if page['total'] != 10 or len(rows) != 10:
                        raise ValueError('Incomplete Slot inventory')
                    fingerprint, hashes = [], []
                    for row in rows:
                        log, runtime = row['node_log'], row['runtime']
                        leader = log['leader_id']
                        if (row.get('task') or log['node_id'] != node_id or leader not in alive or
                            sorted(row['assignment']['desired_peers']) != [1, 2, 3] or
                            sorted(log['current_voters']) != [1, 2, 3] or
                            sorted(runtime['current_voters']) != [1, 2, 3] or not runtime['has_quorum'] or
                            log['role'] != ('leader' if leader == node_id else 'follower')):
                            raise ValueError('Slot election has not converged')
                        hashes.extend(row['hash_slots']['items'])
                        fingerprint.append((row['slot_id'], leader))
                    if sorted(hashes) != list(range(256)):
                        raise ValueError('Incomplete hash-slot coverage')
                    snapshots.append(fingerprint)
                if any(snapshot != snapshots[0] for snapshot in snapshots):
                    raise ValueError('Nodes disagree on actual Slot leaders')
                current = snapshots[0]
                if previous == current and time.monotonic() - since >= 1:
                    return {'nodes': alive, 'leaders': current, 'hashSlotCount': 256, 'replicas': 3}
                if previous != current:
                    previous, since = current, time.monotonic()
            except (urllib.error.URLError, TimeoutError, KeyError, ValueError):
                previous, since = None, 0
            await asyncio.sleep(.2)


async def node_health(observer, target, *, ready):
    """Wait for public failure detection or rejoin, separate from Slot leader election."""
    async with asyncio.timeout(60):
        while True:
            try:
                page = await asyncio.to_thread(get_json, observer.manager, '/manager/nodes')
                for row in page['items']:
                    if row['node_id'] != target:
                        continue
                    health = row['health']
                    healthy = health['fresh'] and health['runtime_ready'] and health['status'] == 'alive'
                    if bool(healthy) == ready:
                        return {key: health[key] for key in ('fresh', 'runtime_ready', 'status')}
            except (urllib.error.URLError, TimeoutError, KeyError):
                pass
            await asyncio.sleep(.2)


async def channel_ready(node, channel_id, alive, *, complete=False, channel_type=2):
    """Inspect the real group authority; never retry an ambiguous SEND to hide failure."""
    query = urllib.parse.urlencode({'exact': 1, 'channel_id': channel_id, 'channel_type': channel_type})
    async with asyncio.timeout(60):
        while True:
            try:
                page = await asyncio.to_thread(get_json, node.manager, '/manager/channel-runtime-meta?' + query)
                for row in page['items']:
                    if (row['channel_id'] == channel_id and row['channel_type'] == channel_type and
                        row['status'] == 'active' and row['min_isr'] == 2 and
                        row['leader'] in alive and not row.get('write_fence_token') and
                        sorted(row['replicas']) == [1, 2, 3] and
                        len(set(row['isr']) & set(alive)) >= 2 and
                        (not complete or sorted(row['isr']) == [1, 2, 3])):
                        return {key: row[key] for key in ('leader', 'replicas', 'isr', 'min_isr', 'status')}
            except (urllib.error.URLError, TimeoutError, KeyError):
                pass
            await asyncio.sleep(.2)


def stage(report, label):
    report["stage"] = label
    print(f"Cluster fixture: {label}", flush=True)


async def cluster_scenarios(binary, directory, dll, env, report):
    """Test SDK boundaries already approved: cross-node SEND/RECV and address replacement."""
    nodes, actors = {}, []
    ports = [port() for _ in range(3)]
    members = [{'id': i + 1, 'addr': f'127.0.0.1:{value}'} for i, value in enumerate(ports)]
    report['cluster'] = {'nodeCount': 3, 'hashSlotCount': 256, 'slotReplicas': 3,
                         'channelReplicas': 3, 'presenceRouteTTLSeconds': 90, 'migrationScanPages': 10, 'addressSwitch': 'application-created replacement client'}
    group = 'csharp-js-cluster-group'
    tokens = {uid: os.urandom(24).hex() for uid in ('cluster-csharp', 'cluster-js', 'cluster-observer')}
    identities = [('csharp', 'cluster-csharp', 1), ('js', 'cluster-js', 2), ('js', 'cluster-observer', 3)]

    async def actor(language, uid, node_id, *, recovery=False):
        # The trusted fixture backend obtains the selected live node's public route.
        route = await asyncio.to_thread(nodes[node_id].request, '/route')
        endpoint = route['ws_addr']
        parsed = urllib.parse.urlsplit(endpoint)
        if parsed.scheme != 'ws' or parsed.hostname != '127.0.0.1' or parsed.port != nodes[node_id].websocket:
            raise AssertionError('Route did not identify the selected owned node')
        command = ([os.environ.get('DOTNET', 'dotnet'), str(dll)] if language == 'csharp' else
                   [os.environ.get('NODE', 'node'), str(ROOT / 'tests/interop/js/actor.cjs')])
        started = time.monotonic()
        async with asyncio.timeout(110 if recovery else 25):
            while True:
                process = await asyncio.create_subprocess_exec(*command,
                    env=dict(env, INTEROP_URL=endpoint, INTEROP_UID=uid, INTEROP_TOKEN=tokens[uid], INTEROP_TRANSPORT='native'),
                    stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL)
                client = Actor(f'{language}@node{node_id}', uid, process)
                actors.append(client)
                result = await client.command('connect', reject=None)
                if result['ok']:
                    report.setdefault('connections', []).append({'language': language, 'uid': uid, 'nodeId': node_id,
                        'applicationConnectSeconds': round(time.monotonic() - started, 3)})
                    return client
                report.setdefault('connectRejections', []).append({'language': language, 'nodeId': node_id,
                    'category': result['category'], 'code': result.get('code')})
                await client.close()
                # This pinned server cannot notify a dead conflicting owner until its route expires.
                # Retry only this observed activation failure, never invalid auth or ambiguous SEND.
                if not retry_activation(result, recovery=recovery):
                    raise AssertionError(f'{language} connect rejected: {result["category"]}, code={result.get("code")}')
                await asyncio.sleep(2)

    async def exchange(sender, recipients, *, group_send=False):
        correlation = uuid.uuid4().hex
        payload = {'type': 9001, 'text': '三节点 中文 👩🏽‍💻',
                   'nested': {'enabled': True, 'id': '9223372036854775807', 'items': [None, 7, '群聊']}}
        pending = {'sender': sender.uid, 'recipients': [r.uid for r in recipients],
                   'channelType': 2 if group_send else 1, 'clientMsgNo': correlation, 'received': []}
        report['pendingExchange'] = pending
        ack = await sender.command('send', target=group if group_send else recipients[0].uid,
                                   channelType=2 if group_send else 1, payload=payload, clientMsgNo=correlation)
        pending['ack'] = ack
        for receiver in recipients:
            message = await receiver.event('message', client_msg_no=correlation)
            verify_exchange(ack, message, sender.uid, payload, correlation)
            pending['received'].append(receiver.uid)
            if group_send:
                verify_group(message, group)
        report.setdefault('exchanges', []).append({'sender': sender.uid, 'recipients': [r.uid for r in recipients],
            'channelType': 2 if group_send else 1, 'messageId': ack['messageId'], 'messageSeq': ack['messageSeq']})
        report.pop('pendingExchange', None)

    async def messaging(clients):
        # Slot convergence does not recreate every live UID route immediately.
        # Wait for public authority presence before sending, without replaying SEND.
        started, stable_since = time.monotonic(), None
        readiness = {'stage': report['stage'], 'initialMissing': None}
        report.setdefault('presenceReadiness', []).append(readiness)
        async with asyncio.timeout(45):
            while True:
                missing = {}
                for node_id, node in nodes.items():
                    if node.process.returncode is not None:
                        continue
                    rows = await asyncio.to_thread(node.request, '/user/onlinestatus', [c.uid for c in clients])
                    online = {row['uid'] for row in (rows or []) if row['online'] == 1 and row['device_flag'] == 2}
                    absent = sorted({c.uid for c in clients} - online)
                    if absent:
                        missing[str(node_id)] = absent
                if readiness['initialMissing'] is None:
                    readiness['initialMissing'] = missing
                readiness['lastMissing'] = missing
                if missing:
                    stable_since = None
                elif stable_since is None:
                    stable_since = time.monotonic()
                elif time.monotonic() - stable_since >= 1:
                    readiness['seconds'] = round(time.monotonic() - started, 3)
                    break
                await asyncio.sleep(.5)
        # Exercise every pair before faults so recovery never needs new placement with a replica absent.
        for sender in clients:
            for receiver in clients:
                if sender is not receiver:
                    await exchange(sender, [receiver])
        for sender in clients[:2]:
            await exchange(sender, [r for r in clients if r is not sender], group_send=True)

    try:
        stage(report, 'three-node bootstrap')
        for i in range(1, 4):
            base = directory / f'node-{i}'
            base.mkdir()
            node = Server(binary, base, node_id=i, members=members, cluster_port=ports[i - 1])
            # Accelerate only the migration scan; retain the server's health TTL.
            text = node.config.read_text() + '\n[observability]\nmetrics_enable = true\n' + '\n[presence]\nroute_ttl = "90s"\n[channel_migration]\nenable = true\nscan_interval = "100ms"\nmax_pages_per_tick = 10\nmax_tasks_per_tick = 4\ntask_limit = 4\n'
            text += '\n[diagnostics]\nenable = true\nsample_rate = 1.0\ndeep_sample_rate = 1.0\n'
            node.config.write_text(text)
            nodes[i] = node
        started = await asyncio.gather(*(node.start() for node in nodes.values()), return_exceptions=True)
        for result in started:
            if isinstance(result, BaseException):
                raise result
        report['initialSlots'] = await stable_slots(nodes, [1, 2, 3])
        for uid, token in tokens.items():
            await asyncio.to_thread(nodes[1].request, '/user/token',
                {'uid': uid, 'token': token, 'device_flag': 2, 'device_level': 1})
        await asyncio.to_thread(nodes[1].request, '/channel',
            {'channel_id': group, 'channel_type': 2, 'subscribers': list(tokens)})
        clients = [await actor(*identity) for identity in identities]
        stage(report, 'cross-node person and group delivery')
        await messaging(clients)
        inventory = await asyncio.to_thread(get_json, nodes[1].manager, '/manager/channel-runtime-meta?limit=100')
        person_channels = sorted(row['channel_id'] for row in inventory['items'] if row['channel_type'] == 1
            and sum(uid in row['channel_id'] for uid in tokens) == 2)
        if len(person_channels) != 3:
            raise AssertionError('Expected exactly three established person channels')
        report['personChannels'] = person_channels
        report['groupBeforeFault'] = await channel_ready(nodes[1], group, [1, 2, 3], complete=True)
        report['cases'].append('three-node cross-ingress person and group delivery with exact ACK/RECV and JSON types')
        for index, survivor in ((0, 3), (1, 1)):
            language, uid, node_id = identities[index]
            stage(report, f'{language} ingress node crash and application address switch')
            old = clients[index]
            disconnected, reconnecting = old.count('disconnected'), old.count('reconnecting')
            await nodes[node_id].stop(crash=True)
            await old.event('disconnected', after=disconnected)
            await old.event('reconnecting', after=reconnecting)
            if (await old.command('state'))['connected']:
                raise AssertionError('Client still reports connected to the killed node')
            rejection = await old.command('send', target='offline-target', payload={'type': 1},
                                          clientMsgNo=uuid.uuid4().hex, reject=True)
            await old.close()  # Retire retrying client before constructing its replacement.
            alive = [i for i in nodes if i != node_id]
            health = await node_health(nodes[survivor], node_id, ready=False)
            slots = await stable_slots(nodes, alive)
            meta = await channel_ready(nodes[survivor], group, alive)
            for channel in person_channels:
                await channel_ready(nodes[survivor], channel, alive, channel_type=1)
            clients[index] = await actor(language, uid, survivor, recovery=True)
            await messaging(clients)
            if nodes[node_id].process.returncode is None:
                raise AssertionError('Failed node unexpectedly restarted during failover proof')
            report.setdefault('failovers', []).append({'client': language, 'failedNode': node_id,
                'replacementNode': survivor, 'offlineSendRejection': rejection['category'],
                'slots': slots, 'group': meta, 'failedNodeHealth': health, 'failedNodeStayedStopped': True})
            report['cases'].append(f'{language} reports node loss and offline rejection; application switches address and resumes person/group delivery')
            stage(report, f'node {node_id} restart')
            await nodes[node_id].start()
            await node_health(nodes[survivor], node_id, ready=True)
            await stable_slots(nodes, [1, 2, 3])
            await channel_ready(nodes[survivor], group, [1, 2, 3], complete=True)
            # Return this identity to its original ingress; require persisted auth and memberships.
            await clients[index].close()
            clients[index] = await actor(language, uid, node_id)
            await messaging(clients)
            report['cases'].append(f'node {node_id} rejoins with persisted credentials and group membership')
        stage(report, 'completed')
    except Exception:
        # Preserve bounded public authority evidence before owned processes are stopped.
        diagnostics = []
        for node_id, node in nodes.items():
            if not node.process or node.process.returncode is not None:
                continue
            entry = {'nodeId': node_id}
            try:
                slots = await asyncio.to_thread(get_json, node.manager, f'/manager/slots?node_id={node_id}')
                pending = report.get('pendingExchange')
                if pending:
                    query = urllib.parse.urlencode({'node_id': node_id, 'client_msg_no': pending['clientMsgNo'], 'limit': 128})
                    trace = await asyncio.to_thread(get_json, node.manager, '/manager/diagnostics/message?' + query)
                    entry['messageTrace'] = {
                        'summary': trace.get('summary'),
                        'events': [{k: event.get(k) for k in ('stage', 'at', 'duration_ms', 'node_id', 'peer_node_id', 'message_seq', 'range_start', 'range_end', 'result', 'error_code', 'request_count', 'record_count', 'decision')}
                                   for event in trace.get('events', [])[:128]]}
                entry['online'] = await asyncio.to_thread(node.request, '/user/onlinestatus', list(tokens))
                entry['slots'] = [{key: row.get(key) for key in ('slot_id', 'runtime', 'node_log')}
                                  for row in slots['items'][:10]]
                channels = await asyncio.to_thread(get_json, node.manager, '/manager/channel-runtime-meta?limit=10')
                entry['channels'] = channels['items'][:10]
                entry['migrations'] = []
                for row in entry['channels']:
                    query = urllib.parse.urlencode({'channel_id': row['channel_id'], 'channel_type': row['channel_type'], 'limit': 4})
                    tasks = await asyncio.to_thread(get_json, node.manager, '/manager/channel-migrations/active?' + query)
                    entry['migrations'].append(tasks)
            except Exception as error:
                entry['readFailure'] = type(error).__name__
            diagnostics.append(entry)
        pending = report.get('pendingExchange', {})
        key = next((n['messageTrace']['summary'].get('channel_key') for n in diagnostics
                    if n.get('messageTrace', {}).get('summary', {}).get('channel_key')), None)
        for entry in diagnostics:
            node = nodes[entry['nodeId']]
            try:
                if key and pending.get('ack'):
                    query = urllib.parse.urlencode({'node_id': entry['nodeId'], 'channel_key': key,
                        'message_seq': pending['ack']['messageSeq'], 'limit': 128})
                    trace = await asyncio.to_thread(get_json, node.manager, '/manager/diagnostics/message?' + query)
                    entry['deliveryTrace'] = {'summary': trace.get('summary'),
                        'events': [{k: e.get(k) for k in ('stage', 'at', 'duration_ms', 'node_id', 'peer_node_id', 'message_seq', 'result', 'error_code', 'request_count', 'record_count', 'decision')}
                                   for e in trace.get('events', [])[:128]]}
                def metrics():
                    with urllib.request.urlopen(f'http://127.0.0.1:{node.api}/metrics', timeout=2) as response:
                        return [line for line in response.read(4 * 1024 * 1024).decode().splitlines()
                                if line.startswith(('wukongim_delivery_', 'wukongim_gateway_sendacks_total'))
                                and '_total{' in line][:128]
                entry['deliveryMetrics'] = await asyncio.to_thread(metrics)
                entry['planFailures'] = []
                # Plan terminal logs have no message correlation; retain only bounded
                # fixture-UID samples and do not present them as exact message proof.
                for line in (node.base / 'server.log').read_text(errors='replace').splitlines():
                    if 'online delivery plan incomplete' not in line:
                        continue
                    try:
                        row = json.loads(line[line.index('{'):])
                        if row.get('uid') not in ('', None, *tokens):
                            continue
                        item = {k: str(row.get(k, ''))[:512] for k in
                                ('phase', 'result', 'mode', 'recipients', 'uid', 'ownerNodeID', 'error')}
                        for k, value in item.items():
                            for token in tokens.values():
                                value = value.replace(token, '[redacted]')
                            item[k] = value
                        entry['planFailures'].append(item)
                        entry['planFailures'] = entry['planFailures'][-8:]
                    except (ValueError, KeyError):
                        continue
                entry['sendErrors'] = []
                for line in (node.base / 'server.log').read_text(errors='replace').splitlines():
                    if 'gateway send failed' not in line:
                        continue
                    try:
                        row = json.loads(line[line.index('{'):])
                        if row.get('clientMsgNo') != pending.get('clientMsgNo'):
                            continue
                        item = {k: str(row.get(k, ''))[:512] for k in ('errorClass', 'failedStage', 'error')}
                        for k, value in item.items():
                            for token in tokens.values():
                                value = value.replace(token, '[redacted]')
                            item[k] = value
                        entry['sendErrors'].append(item)
                        if len(entry['sendErrors']) == 4:
                            break
                    except (ValueError, KeyError):
                        continue
            except Exception as error:
                entry['deliveryReadFailure'] = type(error).__name__
        report['failureAuthority'] = diagnostics
        raise
    finally:
        report['actors'] = [{'name': a.name, 'events': {k: a.count(k) for k in
            ('connected', 'disconnected', 'reconnecting', 'error', 'message')}} for a in actors]
        await asyncio.gather(*(a.close() for a in actors), return_exceptions=True)
        await asyncio.gather(*(n.stop() for n in nodes.values()), return_exceptions=True)
        for node in nodes.values():
            node.log.close()
        report['ownedClientsStopped'] = all(a.process.returncode is not None for a in actors)
        report['ownedNodesStopped'] = all(n.process is None or n.process.returncode is not None for n in nodes.values())
