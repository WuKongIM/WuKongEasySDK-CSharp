"""Validate a release and install its exact package in an isolated consumer."""

import argparse
import io
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SOURCE = "https://api.nuget.org/v3/index.json"


def release_version():
    """Reject malformed versions, mismatched tags, and missing release notes."""
    project = ET.parse(ROOT / "src/WuKongEasySDK/WuKongEasySDK.csproj")
    version = project.findtext("PropertyGroup/Version")
    if not version or not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Release requires an exact stable three-part version")
    ref = os.environ.get("GITHUB_REF", "")
    if ref.startswith("refs/tags/") and ref != f"refs/tags/v{version}":
        raise ValueError("Tag does not match the package version")
    notes = (ROOT / "CHANGELOG.md").read_text()
    sections = re.findall(r"^## \[" + re.escape(version) + r"\][^\n]*\n(.*?)(?=^## |\Z)",
                          notes, re.M | re.S)
    if len(sections) != 1 or not re.search(r"^- \S", sections[0], re.M):
        raise ValueError("Release requires exactly one nonempty changelog section")
    return version, sections[0].strip()


def package_files(data, version, commit):
    """Verify identity and source revision; ignore only NuGet's added signature."""
    with zipfile.ZipFile(io.BytesIO(data)) as package:
        names = package.namelist()
        if len(names) != len(set(names)):
            raise ValueError("Package contains duplicate entries")
        specs = [name for name in names if name.endswith(".nuspec")]
        if len(specs) != 1:
            raise ValueError("Package must contain one manifest")
        metadata = ET.fromstring(package.read(specs[0])).find("{*}metadata")
        if metadata is None or metadata.findtext("{*}id") != "WuKongEasySDK":
            raise ValueError("Wrong package ID")
        if metadata.findtext("{*}version") != version:
            raise ValueError("Wrong package version")
        repository = metadata.find("{*}repository")
        if repository is None or repository.get("commit") != commit:
            raise ValueError("Package source commit mismatch")
        if repository.get("url") != "https://github.com/WuKongIM/WuKongEasySDK-CSharp":
            raise ValueError("Wrong source repository")
        if metadata.findall(".//{*}dependency"):
            raise ValueError("The SDK must have no runtime package dependencies")
        required = {"lib/net8.0/WuKongEasySDK.dll", "lib/net8.0/WuKongEasySDK.xml", "README.md"}
        if not required.issubset(names):
            raise ValueError("Package is missing its library, XML docs, or README")
        return {name: package.read(name) for name in names if name != ".signature.p7s"}


def public_package(version, seconds):
    """Wait a bounded time for the exact version, retrying only transient failures."""
    url = f"https://api.nuget.org/v3-flatcontainer/wukongeasysdk/{version}/wukongeasysdk.{version}.nupkg"
    deadline = time.monotonic() + seconds
    while True:
        try:
            with urllib.request.urlopen(url, timeout=30) as response:
                return response.read()
        except urllib.error.HTTPError as error:
            if error.code not in (404, 408, 429, 500, 502, 503, 504):
                raise
        except (urllib.error.URLError, TimeoutError):
            pass
        if time.monotonic() >= deadline:
            raise TimeoutError(f"NuGet version {version} did not become downloadable")
        print(f"Waiting for NuGet indexing: WuKongEasySDK {version}", flush=True)
        time.sleep(min(20, max(0, deadline - time.monotonic())))


def install(version, source):
    """Restore without cached packages or fallback sources, then compile and run."""
    dotnet = os.environ.get("DOTNET", "dotnet")
    with tempfile.TemporaryDirectory(prefix="wukong-nuget-consumer-") as directory:
        root = Path(directory)
        env = dict(os.environ, NUGET_PACKAGES=str(root / "packages"))
        config = ET.Element("configuration")
        sources = ET.SubElement(config, "packageSources")
        ET.SubElement(sources, "clear")
        ET.SubElement(sources, "add", key="release", value=source)
        ET.ElementTree(config).write(root / "NuGet.Config", encoding="utf-8")
        (root / "Consumer.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>'
            '<TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>'
            '<ItemGroup><PackageReference Include="WuKongEasySDK" Version="[' + version + ']" />'
            '</ItemGroup></Project>')
        (root / "Program.cs").write_text('''using WuKongEasySDK;
await using var im = new WKIM("ws://127.0.0.1:1", new AuthOptions
{
    Uid = "package-consumer", Token = "fixture-only", DeviceFlag = DeviceFlag.Desktop
});
im.Message += message => _ = message.Payload;
if (im.IsConnected) throw new System.Exception("A new client must be disconnected");
await im.DisconnectAsync();
System.Console.WriteLine("NuGet consumer compiled, loaded, and disposed successfully.");
''')
        subprocess.run([dotnet, "restore", "--configfile", "NuGet.Config", "--no-cache"],
                       cwd=root, env=env, check=True, timeout=180)
        assets = json.loads((root / "obj/project.assets.json").read_text())
        if f"WuKongEasySDK/{version}" not in assets["libraries"]:
            raise ValueError("Consumer did not resolve the exact package version")
        subprocess.run([dotnet, "run", "-c", "Release", "--no-restore"],
                       cwd=root, env=env, check=True, timeout=120)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("metadata", "local", "public"))
    parser.add_argument("--commit")
    parser.add_argument("--wait-seconds", type=int, default=600)
    args = parser.parse_args()
    version, notes = release_version()
    if args.mode == "metadata":
        print(version)
        (ROOT / "artifacts").mkdir(exist_ok=True)
        (ROOT / "artifacts/release-notes.md").write_text(notes + "\n")
        if output := os.environ.get("GITHUB_OUTPUT"):
            with open(output, "a") as file:
                file.write(f"version={version}\n")
        return
    if not args.commit or not re.fullmatch(r"[0-9a-f]{40}", args.commit):
        parser.error("--commit must be the full source SHA")
    package = ROOT / f"artifacts/WuKongEasySDK.{version}.nupkg"
    expected = package_files(package.read_bytes(), version, args.commit)
    if args.mode == "public":
        actual = package_files(public_package(version, args.wait_seconds), version, args.commit)
        if expected != actual:
            raise ValueError("Published package contents differ from the tested artifact")
    install(version, SOURCE if args.mode == "public" else str(package.parent))


if __name__ == "__main__":
    main()
