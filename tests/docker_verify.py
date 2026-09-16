"""Lifecycle tests using temporary containers and the running Compose service's image/volume.

Usage: python tests/docker_verify.py mof_http-mof-1
Requires Python 3 and Docker. Temporary containers are removed even after failures.
"""

import contextlib
import json
from pathlib import Path
import socket
import subprocess
import sys
import time
import uuid

# Embedded Python distributions may omit the script directory from sys.path.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from docker_smoke import CUBE, assert_mesh, request, unwrap


def docker(*args):
    return subprocess.check_output(["docker", *args], text=True).strip()


def clean(container):
    # Inspect the container filesystem, including hidden Wine/MOF diagnostic files.
    docker("exec", container, "sh", "-c",
           'test ! -d /tmp/mof-requests || test -z "$(find /tmp/mof-requests -mindepth 1 -print -quit)"')
    processes = docker("top", container, "-eo", "pid,args")
    assert "UnWrapConsole3.exe" not in processes, processes


@contextlib.contextmanager
def instance(image, volume, **settings):
    name = "mof-verification-" + uuid.uuid4().hex[:12]
    args = ["run", "-d", "--name", name, "--platform", "linux/amd64",
            "-p", "127.0.0.1::8080", "-v", f"{volume}:/data/MOFBinary:ro"]
    for key, value in settings.items():
        args.extend(["-e", f"MinistryOfFlat__{key}={value}"])
    # Invalid URL ensures the existing volume is actually reused, not redownloaded.
    args.extend(["-e", "MinistryOfFlat__DownloadUrl=https://invalid.invalid/release.zip", image])
    docker(*args)
    try:
        port = json.loads(docker("inspect", name))[0]["NetworkSettings"]["Ports"]["8080/tcp"][0]["HostPort"]
        base = f"http://127.0.0.1:{port}"
        deadline = time.monotonic() + 120
        while True:
            try:
                status, _, body = request(base, "/health", timeout=2)
                if status == 200 and json.loads(body)["status"] == "ready":
                    break
            except (OSError, ValueError):
                pass
            if time.monotonic() > deadline:
                raise AssertionError(docker("logs", name))
            time.sleep(1)
        assert docker("exec", name, "id", "-u") != "0"
        yield name, base
    except BaseException:
        print(docker("logs", name), flush=True)
        raise
    finally:
        docker("rm", "-f", name)


def many_cubes(count=20000):
    lines = []
    source = CUBE.splitlines()
    for i in range(count):
        for line in source:
            values = line.split()
            if values[0] == "v":
                lines.append(f"v {float(values[1]) + (i % 200) * 4} {float(values[2]) + (i // 200) * 4} {values[3]}")
            else:
                lines.append("f " + " ".join(str(int(n) + i * 8) for n in values[1:]))
    return "\n".join(lines) + "\n"


def main(source):
    info = json.loads(docker("inspect", source))[0]
    image = info["Image"]
    volume = next(m["Name"] for m in info["Mounts"] if m["Destination"] == "/data/MOFBinary")
    clean(source)
    large = many_cubes()
    with instance(image, volume, TimeoutSeconds=5, MaxConcurrentProcesses=1) as (name, base):
        assert_mesh(unwrap(base))
        start = time.monotonic()
        result = unwrap(base, mesh=large, timeout=30)
        elapsed = time.monotonic() - start
        assert result[0] == 504, (result[0], result[2][:1000], elapsed)
        assert elapsed < 15, elapsed
        clean(name)
        assert_mesh(unwrap(base))
        clean(name)
        print(f"PASS real MOF timeout ({elapsed:.2f}s), process/file cleanup, subsequent unwrap", flush=True)

        try:
            unwrap(base, mesh=large, timeout=0.5)
            raise AssertionError("Large request unexpectedly completed before disconnect")
        except (TimeoutError, socket.timeout):
            pass
        deadline = time.monotonic() + 4
        while True:
            try:
                clean(name)
                break
            except (subprocess.CalledProcessError, AssertionError):
                if time.monotonic() > deadline:
                    raise
                time.sleep(0.1)
        assert_mesh(unwrap(base))
        clean(name)
        print("PASS client disconnect cleanup before timeout and subsequent unwrap", flush=True)
        assert "Downloading Ministry of Flat" not in docker("logs", name)
        print("PASS non-root operation and persisted binaries reused with unavailable download URL", flush=True)

    with instance(image, volume, MaxUploadBytes=1000, MaxOutputBytes=100) as (name, base):
        result = unwrap(base, mesh=CUBE + "#" * 2000)
        assert result[0] == 413, result
        result = unwrap(base)
        assert result[0] == 422 and b"output limit" in result[2], result
        clean(name)
        print("PASS upload/output limits and cleanup", flush=True)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "mof_http-mof-1")
