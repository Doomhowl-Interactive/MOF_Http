"""Real MOF HTTP smoke tests. Usage: python tests/docker_smoke.py http://localhost:5001"""

import concurrent.futures
import json
import math
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid

CUBE = """v -1 -1 -1
v 1 -1 -1
v 1 1 -1
v -1 1 -1
v -1 -1 1
v 1 -1 1
v 1 1 1
v -1 1 1
f 1 4 3 2
f 5 6 7 8
f 1 2 6 5
f 2 3 7 6
f 3 4 8 7
f 4 1 5 8
"""


def request(base, path, data=None, headers=None, timeout=150):
    req = urllib.request.Request(base + path, data=data, headers=headers or {})
    try:
        response = urllib.request.urlopen(req, timeout=timeout)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        return response.status, response.headers, response.read()


def unwrap(base, mesh=CUBE, filename="cube.obj", fields=None, timeout=150):
    boundary = uuid.uuid4().hex
    parts = []
    if mesh is not None:
        parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="File"; '
                     f'filename="{filename}"\r\nContent-Type: application/octet-stream\r\n\r\n{mesh}\r\n')
    for key, value in (fields or {}).items():
        parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{key}"\r\n\r\n{value}\r\n')
    parts.append(f"--{boundary}--\r\n")
    return request(base, "/api/unwrap", "".join(parts).encode(),
                   {"Content-Type": f"multipart/form-data; boundary={boundary}"}, timeout)


def assert_mesh(result):
    status, headers, body = result
    assert status == 200, (status, body.decode(errors="replace"))
    assert headers.get_content_type() == "application/octet-stream"
    assert "attachment" in headers["Content-Disposition"]
    lines = body.decode().splitlines()
    vertices = [line for line in lines if line.startswith("v ")]
    uv = [line for line in lines if line.startswith("vt ")]
    faces = [line for line in lines if line.startswith("f ")]
    assert vertices and uv and faces
    assert all(math.isfinite(float(n)) for line in uv for n in line.split()[1:])
    for face in faces:
        for item in face.split()[1:]:
            indices = item.split("/")
            assert len(indices) >= 2 and 1 <= int(indices[1]) <= len(uv), item
    return len(vertices), len(uv), len(faces)


def main(base):
    status, _, body = request(base, "/health")
    assert status == 200 and json.loads(body)["status"] == "ready"
    status, _, body = request(base, "/")
    assert status == 200 and b'id="drop-zone"' in body
    status, _, body = request(base, "/openapi/v1.json")
    assert status == 200
    operation = json.loads(body)["paths"]["/api/unwrap"]["post"]
    assert "504" in operation["responses"]
    print("PASS health, UI, OpenAPI", flush=True)

    started = time.monotonic()
    print("PASS real MOF cube (vertices, UVs, faces):", assert_mesh(unwrap(base)),
          f"in {time.monotonic() - started:.2f}s", flush=True)
    assert_mesh(unwrap(base, filename="mesh with spaces.obj", fields={"Resolution": "512", "Separate": "true", "Aspect": "1.5"}))
    print("PASS settings and filename with spaces", flush=True)

    for kwargs, expected in [
        ({"mesh": "not a mesh\n"}, 422),
        ({"mesh": None, "fields": {"Resolution": "512"}}, 400),
        ({"mesh": ""}, 400),
        ({"filename": "bad.txt"}, 400),
        ({"fields": {"Resolution": "0"}}, 400),
        ({"fields": {"Aspect": "NaN"}}, 400),
        ({"fields": {"CenterX": "Infinity"}}, 400),
        ({"fields": {"Udims": "-1"}}, 400),
        ({"fields": {"Separate": "maybe"}}, 400),
    ]:
        result = unwrap(base, **kwargs)
        assert result[0] == expected, (kwargs, result)
    print("PASS invalid geometry and input validation", flush=True)

    barrier = threading.Barrier(8)

    def concurrent_request(_):
        barrier.wait()
        return unwrap(base)

    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        results = list(pool.map(concurrent_request, range(8)))
    codes = [result[0] for result in results]
    assert 200 in codes and 503 in codes, codes
    for result in results:
        if result[0] == 200:
            assert_mesh(result)
        else:
            assert result[0] == 503 and result[1]["Retry-After"] == "1", result
    assert_mesh(unwrap(base))
    print("PASS concurrent requests, capacity rejection, recovery:", codes, flush=True)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5000")
