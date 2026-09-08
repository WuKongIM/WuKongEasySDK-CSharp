#!/usr/bin/env python3
"""Black-box C#/JS interoperability against one owned real WuKongIM cluster."""
import argparse
import asyncio
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import urllib.error
import urllib.request
import uuid

from smoke import port

ROOT = Path(__file__).resolve().parents[1]
PINS = json.loads((ROOT / "tests/interop/pins.json").read_text())


def verify_exchange(ack, message, sender, payload, client_msg_no):
    """Reject rounded IDs, mismatched ACK/RECV, and altered application payloads."""
    message_id = ack.get("messageId")
    if not isinstance(message_id, str) or not message_id.isdecimal() or int(message_id) <= 2**53:
        raise AssertionError("Expected a lossless string message ID beyond JS safe integer range")
    if ack.get("reasonCode") != 1 or message.get("messageId") != message_id:
        raise AssertionError("SENDACK/RECV identity mismatch or SEND rejection")
    if ack.get("messageSeq") != message.get("messageSeq") or int(ack["messageSeq"]) <= 0:
        raise AssertionError("SENDACK/RECV sequence mismatch")
    # v3.0.0-beta.9 SEND result omits clientMsgNo; RECV must retain the original value.
    if ack.get("clientMsgNo") not in (None, client_msg_no) or message.get("clientMsgNo") != client_msg_no:
        raise AssertionError("Client message correlation mismatch")
    if message.get("fromUid") != sender:
        raise AssertionError("Unexpected message sender")
    if json.dumps(message.get("payload"), sort_keys=True) != json.dumps(payload, sort_keys=True):
        raise AssertionError("Application payload changed")


class Proxy:
    """A bounded byte-transparent TCP relay; faults close sockets, never fake protocol data."""
    def __init__(self, target):
        self.target, self.enabled, self.attempts = target, True, 0
        self.writers, self.tasks = set(), set()

    async def start(self):
        self.server = await asyncio.start_server(self.handle, "127.0.0.1", 0)
        self.url = f"ws://127.0.0.1:{self.server.sockets[0].getsockname()[1]}"
        return self

    async def handle(self, reader, writer):
        self.attempts += 1
        task = asyncio.current_task()
        self.tasks.add(task)
        peers, pumps = [writer], []
        try:
            if not self.enabled:
                return
            upstream, remote = await asyncio.wait_for(asyncio.open_connection("127.0.0.1", self.target), 2)
            peers.append(remote)
            self.writers.update(peers)

            async def copy(source, destination):
                while data := await source.read(65536):
                    destination.write(data)
                    await destination.drain()

            pumps = [asyncio.create_task(copy(reader, remote)), asyncio.create_task(copy(upstream, writer))]
            await asyncio.wait(pumps, return_when=asyncio.FIRST_COMPLETED)
        except (OSError, TimeoutError, ConnectionError):
            pass
        finally:
            for pump in pumps:
                pump.cancel()
            await asyncio.gather(*pumps, return_exceptions=True)
            for peer in peers:
                peer.close()
                self.writers.discard(peer)
            await asyncio.gather(*(peer.wait_closed() for peer in peers), return_exceptions=True)
            self.tasks.discard(task)

    def drop(self):
        self.enabled = False
        for writer in tuple(self.writers):
            writer.close()

    async def close(self):
        self.drop()
        self.server.close()
        await self.server.wait_closed()
        for task in tuple(self.tasks):
            task.cancel()
        await asyncio.gather(*tuple(self.tasks), return_exceptions=True)


class Actor:
    """Correlate private JSON-line commands and bounded SDK event history."""
    def __init__(self, name, uid, process):
        self.name, self.uid, self.process = name, uid, process
        self.events, self.pending, self.serial = [], {}, 0
        self.condition, self.failure = asyncio.Condition(), None
        self.reader = asyncio.create_task(self.read())

    async def read(self):
        try:
            while line := await self.process.stdout.readline():
                event = json.loads(line)
                if event["kind"] == "result":
                    future = self.pending.pop(event["id"])
                    if not future.done():
                        future.set_result(event)
                else:
                    async with self.condition:
                        if len(self.events) >= 512:
                            raise AssertionError("Unexpected event flood")
                        self.events.append(event)
                        self.condition.notify_all()
        except Exception as error:
            self.failure = type(error).__name__
        finally:
            self.failure = self.failure or "Actor exited"
            for future in self.pending.values():
                if not future.done():
                    future.set_exception(RuntimeError(self.failure))
            async with self.condition:
                self.condition.notify_all()

    def count(self, kind):
        return sum(event["kind"] == kind for event in self.events)

    async def event(self, kind, *, after=0, client_msg_no=None):
        async with asyncio.timeout(40):
            async with self.condition:
                while True:
                    matches = [event for event in self.events if event["kind"] == kind and
                               (client_msg_no is None or event.get("clientMsgNo") == client_msg_no)]
                    if len(matches) > after:
                        return matches[-1]
                    if self.failure:
                        raise RuntimeError(f"{self.name}: {self.failure}")
                    await self.condition.wait()

    async def command(self, op, *, reject=False, **fields):
        self.serial += 1
        future = asyncio.get_running_loop().create_future()
        self.pending[self.serial] = future
        self.process.stdin.write((json.dumps({"id": self.serial, "op": op, **fields}) + "\n").encode())
        await self.process.stdin.drain()
        result = await asyncio.wait_for(future, 20)
        if result["ok"] == reject:
            raise AssertionError(f"{self.name}: unexpected {op} result")
        return result if reject else result.get("data")

    async def close(self):
        if self.process.returncode is None:
            try:
                await self.command("exit")
                await asyncio.wait_for(self.process.wait(), 5)
            except Exception:
                if self.process.returncode is None:
                    self.process.kill()
                    await self.process.wait()
        await self.reader


class Server:
    def __init__(self, binary, base):
        self.binary, self.base, self.process = binary, base, None
        cluster, self.api, manager, self.websocket = [port() for _ in range(4)]
        self.config = base / "wukongim.toml"
        self.config.write_text(f'''[node]
id = 1
data_dir = {json.dumps(str(base / 'data'))}
[cluster]
id = "csharp-js-interop"
listen_addr = "127.0.0.1:{cluster}"
nodes = [{{id = 1, addr = "127.0.0.1:{cluster}"}}]
initial_slot_count = 10
hash_slot_count = 256
slot_replica_n = 1
[api]
listen_addr = "127.0.0.1:{self.api}"
[manager]
listen_addr = "127.0.0.1:{manager}"
[gateway]
token_auth_on = true
listeners = [{{name = "ws", network = "websocket", address = "127.0.0.1:{self.websocket}", transport = "gnet", protocol = "wsmux"}}]
[plugin]
enable = false
[prometheus]
enable = false
[log]
level = "warn"
dir = {json.dumps(str(base / 'logs'))}
console = true
''')
        self.log = (base / "server.log").open("a")

    def request(self, route, body=None):
        request = urllib.request.Request(f"http://127.0.0.1:{self.api}{route}",
            data=None if body is None else json.dumps(body).encode(),
            headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=2) as response:
            if response.status != 200:
                raise RuntimeError("Fixture HTTP failure")

    async def start(self):
        env = {key: value for key, value in os.environ.items() if not key.startswith("WK_")}
        self.process = await asyncio.create_subprocess_exec(self.binary, "-config", str(self.config),
            env=env, stdout=self.log, stderr=self.log)
        async with asyncio.timeout(30):
            while True:
                if self.process.returncode is not None:
                    raise RuntimeError("Owned server exited before readiness")
                try:
                    await asyncio.to_thread(self.request, "/readyz")
                    return
                except (urllib.error.URLError, TimeoutError):
                    await asyncio.sleep(0.1)

    async def stop(self, *, crash=False):
        if self.process and self.process.returncode is None:
            if crash:
                self.process.kill()
            else:
                self.process.terminate()
            try:
                await asyncio.wait_for(self.process.wait(), 15)
            except TimeoutError:
                self.process.kill()
                await self.process.wait()


async def scenarios(binary, directory, dll, env, report):
    server, actors, proxies = Server(binary, directory), [], []
    dotnet, node = os.environ.get("DOTNET", "dotnet"), os.environ.get("NODE", "node")

    async def actor(name, language, uid, token):
        proxy = await Proxy(server.websocket).start()
        proxies.append(proxy)
        command = [dotnet, str(dll)] if language == "csharp" else [node, str(ROOT / "tests/interop/js/actor.cjs")]
        process = await asyncio.create_subprocess_exec(*command,
            env=dict(env, INTEROP_URL=proxy.url, INTEROP_UID=uid, INTEROP_TOKEN=token),
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL)
        client = Actor(name, uid, process)
        actors.append(client)
        return client, proxy

    async def exchange(sender, receiver, payload):
        correlation = uuid.uuid4().hex
        ack = await sender.command("send", target=receiver.uid, payload=payload, clientMsgNo=correlation)
        message = await receiver.event("message", client_msg_no=correlation)
        verify_exchange(ack, message, sender.uid, payload, correlation)

    async def pair(label):
        await asyncio.gather(exchange(csharp, js, {"type": 1, "content": f"C# → JS 中文 👩🏽‍💻 {label}"}),
                             exchange(js, csharp, {"type": 1, "content": f"JS → C# 你好 🌍 {label}"}))

    try:
        report["stage"] = "initial messaging"
        await server.start()
        tokens = {uid: os.urandom(24).hex() for uid in ("csharp-user", "javascript-user")}
        for uid, token in tokens.items():
            await asyncio.to_thread(server.request, "/user/token", {
                "uid": uid, "token": token, "device_flag": 2, "device_level": 1})
        csharp, cp = await actor("C#", "csharp", "csharp-user", tokens["csharp-user"])
        js, jp = await actor("JS", "js", "javascript-user", tokens["javascript-user"])
        await asyncio.gather(csharp.command("connect"), js.command("connect"))
        await pair("initial")
        custom = {"type": 9001, "schema": "fixture.v1", "nested": {"emoji": "🧪", "enabled": True,
                  "nullable": None, "preciseId": "9223372036854775807", "items": [1, "中文", False]}}
        await exchange(csharp, js, custom)
        await exchange(js, csharp, custom)
        report["cases"].append("bidirectional Unicode/custom payload and exact SENDACK/RECV correlation")

        for language, uid in (("csharp", "csharp-user"), ("js", "javascript-user")):
            report["stage"] = f"invalid Token ({language})"
            invalid, proxy = await actor(f"invalid-{language}", language, uid, "fixture-invalid-token")
            rejection = await invalid.command("connect", reject=True)
            if language == "csharp" and rejection["category"] not in ("WKIMAuthenticationException", "WKIMRpcException"):
                raise AssertionError("C# invalid Token failed without an authentication rejection")
            if language == "js" and not isinstance(rejection.get("code"), int):
                raise AssertionError("JS invalid Token failed without a server RPC rejection")
            await asyncio.sleep(1.3)
            if (await invalid.command("state"))["connected"] or invalid.count("reconnecting") or proxy.attempts != 1:
                raise AssertionError("Invalid-token client connected or retried")
            await invalid.close()
        report["cases"].append("both SDKs reject invalid Token without reconnect")

        baselines = [(a, a.count("connected"), a.count("disconnected"), a.count("reconnecting"), p.attempts)
                     for a, p in ((csharp, cp), (js, jp))]
        report["stage"] = "network recovery"
        cp.drop()
        jp.drop()
        for a, _, disconnected, reconnecting, _ in baselines:
            await a.event("disconnected", after=disconnected)
            await a.event("reconnecting", after=reconnecting)
        await asyncio.sleep(1.3)  # Force at least one retry while the fault is still present.
        for (_, _, _, _, attempts), proxy in zip(baselines, (cp, jp)):
            if proxy.attempts <= attempts:
                raise AssertionError("No connection retry crossed the dropped network")
            proxy.enabled = True
        for a, connected, _, _, _ in baselines:
            await a.event("connected", after=connected)
        await pair("after network recovery")
        report["cases"].append("automatic reconnect after interrupted network and one failed retry")

        report["stage"] = "server crash/restart"
        connected = [a.count("connected") for a in (csharp, js)]
        disconnected = [a.count("disconnected") for a in (csharp, js)]
        await server.stop(crash=True)
        for a, previous in zip((csharp, js), disconnected):
            await a.event("disconnected", after=previous)
        await server.start()  # Same config and storage, credentials must survive the restart.
        for a, previous in zip((csharp, js), connected):
            await a.event("connected", after=previous)
        await pair("after server restart")
        report["cases"].append("automatic reconnect and bidirectional messages after real server crash/restart")

        report["stage"] = "manual disconnect"
        await asyncio.gather(csharp.command("disconnect"), js.command("disconnect"))
        attempts = [p.attempts for p in (cp, jp)]
        reconnects = [a.count("reconnecting") for a in (csharp, js)]
        await asyncio.sleep(3.2)  # Beyond the default initial retry and second retry interval.
        for a, p, previous, retries in zip((csharp, js), (cp, jp), attempts, reconnects):
            if (await a.command("state"))["connected"] or p.attempts != previous or a.count("reconnecting") != retries:
                raise AssertionError("Manual disconnect did not stop reconnect")
            await a.command("send", target="offline-target", payload={"type": 1}, clientMsgNo=uuid.uuid4().hex, reject=True)
        await asyncio.gather(csharp.command("connect"), js.command("connect"))
        await pair("after explicit reconnect")
        report["cases"].append("manual disconnect stops reconnect, rejects offline SEND, and allows explicit reconnect")
        report["stage"] = "completed"
    finally:
        report["actors"] = [{"name": client.name, "events": {kind: client.count(kind)
            for kind in ("connected", "disconnected", "reconnecting", "error", "message")}}
            for client in actors]
        report["proxyAttempts"] = [proxy.attempts for proxy in proxies]
        for client in actors:
            await client.close()
        for proxy in proxies:
            await proxy.close()
        await server.stop()
        server.log.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", action="store_true", help="Test checkout C# source instead of public NuGet")
    args = parser.parse_args()
    binary = str(Path(os.environ["WUKONGIM_BINARY"]).resolve())
    metadata = subprocess.check_output(["go", "version", "-m", binary], text=True)
    if f"vcs.revision={PINS['serverCommit']}" not in metadata or "vcs.modified=false" not in metadata:
        raise RuntimeError("Build the pinned clean WuKongIM server commit before running this fixture")
    report = {"status": "failed", "pins": PINS, "clientMode": "candidate" if args.candidate else "released",
              "harnessCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
              "harnessDirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True)),
              "serverBinarySha256": hashlib.sha256(Path(binary).read_bytes()).hexdigest(), "cases": []}
    (ROOT / "artifacts").mkdir(exist_ok=True)
    output = ROOT / f"artifacts/interop-{report['clientMode']}.json"
    try:
        with tempfile.TemporaryDirectory(prefix="wukong-csharp-js-") as directory:
            base = Path(directory)
            env = dict(os.environ, NUGET_PACKAGES=str(base / "packages"))
            dotnet = os.environ.get("DOTNET", "dotnet")
            subprocess.run(["npm", "ci", "--prefix", str(ROOT / "tests/interop/js"), "--ignore-scripts",
                            "--no-audit", "--no-fund"], check=True, timeout=120)
            installed_js = json.loads((ROOT / "tests/interop/js/node_modules/easyjssdk/package.json").read_text())
            if installed_js["version"] != PINS["javascript"]:
                raise AssertionError("Unexpected installed JS version")
            report["jsTransport"] = "ws/" + json.loads((ROOT / "tests/interop/js/node_modules/ws/package.json").read_text())["version"]
            project = ROOT / "tests/interop/csharp/Interop.csproj"
            build = [dotnet, "build", str(project), "-c", "Release", "-t:Rebuild",
                     f"-p:InteropCandidate={str(args.candidate).lower()}", "--configfile", str(ROOT / "tests/interop/NuGet.Config")]
            subprocess.run(build, env=env, check=True, timeout=180)
            assets = json.loads((project.parent / "obj/project.assets.json").read_text())
            key = next(key for key in assets["libraries"] if key.startswith("WuKongEasySDK/"))
            library = assets["libraries"][key]
            if not args.candidate and key != f"WuKongEasySDK/{PINS['nuget']}":
                raise AssertionError("Unexpected installed NuGet version")
            report["csharpResolved"] = key
            if library["type"] != ("project" if args.candidate else "package"):
                raise AssertionError("Unexpected C# dependency source")
            dll = project.parent / "bin/Release/net8.0/Interop.dll"
            asyncio.run(asyncio.wait_for(scenarios(binary, base, dll, env, report), 150))
            report["status"] = "passed"
    except Exception as error:
        report["failure"] = type(error).__name__ + ": " + str(error) if isinstance(error, AssertionError) else type(error).__name__
        raise
    finally:
        output.write_text(json.dumps(report, indent=2) + "\n")
    print(f"PASS: {len(report['cases'])} real C#/JS interoperability scenarios ({report['clientMode']}); {output}")


if __name__ == "__main__":
    main()
