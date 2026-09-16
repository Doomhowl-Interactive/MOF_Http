# Ministry of Flat HTTP Gateway

An ASP.NET Core C# HTTP gateway to the Ministry of Flat console executable, for private use only.

This project is for private use only. It is not a SaaS product and is not intended to offer a public or commercial UV-unwrapping service.

For native development, use Windows and the .NET 10 SDK. Run `dotnet run --project Mof.Http.csproj` to start on `http://localhost:5000`. Startup automatically downloads Ministry of Flat if `MOFBinary/UnWrapConsole3.exe` is missing. Existing binaries are reused.

## Docker (Linux + Wine)

The multi-stage image runs ASP.NET Core natively on Linux and launches only the MOF console executable through Wine. It includes 64-bit and 32-bit Wine support and runs as the non-root `app` user. Use Docker with Linux containers (Docker Desktop on Windows must be running in Linux-container mode).

```sh
docker compose up --build -d
docker compose logs -f mof
```

Open `http://localhost:5000`, with interactive Swagger UI documentation at `/swagger/` and the raw specification at `/openapi/v1.json`. Compose binds the port to the host loopback interface. First startup initializes a Wine prefix and downloads MOF over HTTPS; allow several minutes. The `mof-binaries` named volume retains the download across container replacement. `docker compose down` preserves it; `docker compose down -v` removes it. The Wine prefix is container-local and recreated when the container is replaced.

If port 5000 is occupied, set `MOF_HTTP_PORT` before starting Compose (or in a local `.env` file). For example, in PowerShell:

```powershell
$env:MOF_HTTP_PORT = "5001"
docker compose up -d --wait --wait-timeout 360
```

Then use `http://localhost:5001`. Keep the same port override for subsequent Compose `up` commands.

The image targets **linux/amd64**, matching the Windows executable. Compose specifies that platform; for a manual build use `docker build --platform linux/amd64 -t mof-http .`. ARM hosts require amd64 emulation and may be significantly slower. The local `MOFBinary` directory is not copied into the image. To supply an existing release instead of downloading, replace the named-volume mount with `./MOFBinary:/data/MOFBinary:ro` and ensure it contains `UnWrapConsole3.exe` and any companion files.

Settings can be overridden in Compose with `MinistryOfFlat__TimeoutSeconds`, `MinistryOfFlat__MaxConcurrentProcesses`, and the other .NET configuration keys. `MinistryOfFlat__WineExecutable` defaults to `wine` on Linux and can point to another Wine launcher. Request-local relative mesh paths avoid Linux/Windows path conversion. The image explicitly overrides `URLS` and `AllowedHosts` so Kestrel accepts container traffic. Wine initialization happens before Kestrel starts; the health check probes `/health`, which checks binary presence, **not** Wine compatibility or successful unwrapping.

Verify the actual executable after deployment using a known-good OBJ:

```sh
curl --fail http://localhost:5000/health
curl --fail-with-body -F "File=@mesh.obj" http://localhost:5000/api/unwrap -o unwrapped.obj
```

Check that the response contains `vt` records and UV-indexed faces. Wine compatibility must be verified with the MOF release in use, including timeouts and concurrent requests; the automated Windows integration tests do not establish Wine compatibility. To see Wine diagnostics, add `WINEDEBUG: "warn+all"` to the Compose environment and recreate the container.

### Repeatable container verification

With Python 3 installed and the Compose service running:

```sh
python tests/docker_smoke.py http://localhost:5000
python tests/docker_verify.py mof_http-mof-1
```

Adjust the URL for a custom port and the container name if the Compose project name differs (`docker compose ps`). The smoke test checks real MOF UV output, settings, UI/OpenAPI, invalid input, and concurrent requests with busy-response recovery. The lifecycle test creates temporary containers from the running service's image, mounts its binary volume read-only, and verifies real MOF timeouts, client-disconnect cancellation, process/file cleanup, subsequent successful requests, upload/output limits, non-root operation, and binary reuse with an unavailable download URL. Temporary containers are removed afterward.

Verified on Docker Desktop's Linux/amd64 engine with Wine 9.0 on September 17, 2026: image build, fresh download/startup, both scripts, successful 50-cube mesh unwrapping, Compose container replacement without redownloading, and clean shutdown/restart. All 22 Windows .NET tests also passed, including the fresh-download integration test. Test representative production meshes when changing MOF or Wine versions.

### Alternatives

- **Native Linux MOF executable/library:** no public Linux build was found when checking the [official site](https://www.quelsolaar.com/ministry_of_flat/) and its release ZIP on September 17, 2026. The ZIP contains only `UnWrap3.exe`, `UnWrapConsole3.exe`, and `documentation.txt`. The publisher advertises commercial source licensing; contact `theministry@quelsolaar.com` about a native build or port. A native version would avoid Wine and simplify process management and deployment.
- **Windows containers:** avoid Wine and run the existing executable natively, but require a compatible Windows container host and a much larger Windows Server Core ASP.NET image. Consider this if Wine fails your real-mesh tests.
- **Windows worker/VM:** keep MOF on a Windows host and deploy the gateway there, or introduce a remote job worker for a Linux-hosted API. A worker split needs additional queue/RPC and file-transfer implementation, but can be a better fit for existing Windows infrastructure.

API documentation is generated from ASP.NET Core: build with `dotnet build Mof.Http.slnx` to produce `openapi.json`, or read `/openapi/v1.json` on the running server. Settings are in `appsettings.json` and can be overridden with .NET configuration environment variables such as `MinistryOfFlat__TimeoutSeconds`.

Run `dotnet test Mof.Http.slnx` for automated tests. On Windows, integration tests use the real executable and download it if missing. Set `MOF_TEST_DOWNLOAD=1` to also test a fresh download from the official server into an isolated temporary directory.

The gateway source code is licensed under the [GNU General Public License version 3 (GPL-3.0-only)](LICENSE). The private-use description above states the project's intended use and does not impose additional restrictions on the rights granted by the GPL.

Ministry of Flat is third-party software and remains subject to its publisher's license terms. The gateway's GPL license does not apply to the Ministry of Flat executables.
