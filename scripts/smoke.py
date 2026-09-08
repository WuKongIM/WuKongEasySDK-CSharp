#!/usr/bin/env python3
"""Run the C# client against one owned, token-authenticated 256-hash-slot cluster."""
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request


def port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def main():
    binary = str(Path(os.environ["WUKONGIM_BINARY"]).resolve())
    root = Path(__file__).resolve().parents[1]
    dotnet = os.environ.get("DOTNET", "dotnet")
    subprocess.run([dotnet, "build", str(root / "tests/WuKongEasySDK.Smoke"), "-c", "Release"], check=True)
    with tempfile.TemporaryDirectory(prefix="wukong-csharp-") as directory:
        base = Path(directory)
        cluster, api, manager, websocket = [port() for _ in range(4)]
        config = base / "wukongim.toml"
        config.write_text(f'''[node]
id = 1
data_dir = "{base / 'data'}"
[cluster]
id = "csharp-smoke"
listen_addr = "127.0.0.1:{cluster}"
nodes = [{{id = 1, addr = "127.0.0.1:{cluster}"}}]
initial_slot_count = 10
hash_slot_count = 256
slot_replica_n = 1
[api]
listen_addr = "127.0.0.1:{api}"
[manager]
listen_addr = "127.0.0.1:{manager}"
[gateway]
token_auth_on = true
listeners = [{{name = "ws", network = "websocket", address = "127.0.0.1:{websocket}", transport = "gnet", protocol = "wsmux"}}]
[plugin]
enable = false
[prometheus]
enable = false
[log]
level = "warn"
dir = "{base / 'logs'}"
console = true
''')
        # Configuration environment belongs to this fixture; inherited WK_* overrides must not escape it.
        server_env = {key: value for key, value in os.environ.items() if not key.startswith("WK_")}
        with (base / "server.log").open("w") as log:
            process = subprocess.Popen([binary, "-config", str(config)], env=server_env, stdout=log, stderr=log)
            try:
                deadline = time.monotonic() + 30
                while True:
                    if process.poll() is not None:
                        raise RuntimeError("Owned server exited before readiness")
                    try:
                        with urllib.request.urlopen(f"http://127.0.0.1:{api}/readyz", timeout=1) as response:
                            if response.status == 200:
                                break
                    except (urllib.error.URLError, TimeoutError):
                        pass
                    if time.monotonic() > deadline:
                        raise TimeoutError("Owned server did not become ready")
                    time.sleep(0.1)
                env = os.environ.copy()
                env["WUKONGIM_WS_URL"] = f"ws://127.0.0.1:{websocket}"
                for name in ("alice", "bob"):
                    # These credentials are confined to this loopback fixture and are never logged.
                    token = os.urandom(24).hex()
                    request = urllib.request.Request(f"http://127.0.0.1:{api}/user/token", method="POST",
                        headers={"Content-Type": "application/json"}, data=json.dumps({
                            "uid": name, "token": token, "device_flag": 2, "device_level": 1}).encode())
                    with urllib.request.urlopen(request, timeout=5) as response:
                        if response.status != 200:
                            raise RuntimeError("Token preparation failed")
                    env[f"WUKONGIM_{name.upper()}_UID"] = name
                    env[f"WUKONGIM_{name.upper()}_TOKEN"] = token
                subprocess.run([dotnet, "run", "--no-build", "-c", "Release", "--project",
                    str(root / "tests/WuKongEasySDK.Smoke")], env=env, check=True, timeout=40)
            finally:
                if process.poll() is None:
                    process.send_signal(signal.SIGTERM)
                    try:
                        process.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)


if __name__ == "__main__":
    main()
