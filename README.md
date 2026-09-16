# Ministry of Flat HTTP Gateway

An ASP.NET Core C# HTTP gateway to the Ministry of Flat console executable, for private use only.

This project is for private use only. It is not a SaaS product and is not intended to offer a public or commercial UV-unwrapping service.

Requires Windows and the .NET 10 SDK. Run `dotnet run --project Mof.Http.csproj` to start on `http://localhost:5000`. Startup automatically downloads Ministry of Flat if `MOFBinary/UnWrapConsole3.exe` is missing. Existing binaries are reused. The `MOFBinary` folder is excluded from Git and publish output.

API documentation is generated from ASP.NET Core: build with `dotnet build Mof.Http.slnx` to produce `openapi.json`, or read `/openapi/v1.json` on the running server. Settings are in `appsettings.json` and can be overridden with .NET configuration environment variables such as `MinistryOfFlat__TimeoutSeconds`.

Run `dotnet test Mof.Http.slnx` for automated tests. On Windows, integration tests use the real executable and download it if missing. Set `MOF_TEST_DOWNLOAD=1` to also test a fresh download from the official server into an isolated temporary directory.

The gateway source code is licensed under the [GNU General Public License version 3 (GPL-3.0-only)](LICENSE). The private-use description above states the project's intended use and does not impose additional restrictions on the rights granted by the GPL.

Ministry of Flat is third-party software and remains subject to its publisher's license terms. The gateway's GPL license does not apply to the Ministry of Flat executable.
