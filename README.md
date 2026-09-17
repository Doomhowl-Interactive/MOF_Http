# Ministry of Flat HTTP Gateway

An ASP.NET Core HTTP gateway for the Ministry of Flat console executable.

The gateway is licensed under [GPL-3.0-only](LICENSE). Ministry of Flat is third-party software and remains subject to its publisher's license; the gateway license does not apply to its executables.

## Run locally

Requires Windows and the .NET 10 SDK. Startup downloads `MOFBinary/UnWrapConsole3.exe` if it is missing.

```sh
dotnet run --project Mof.Http.csproj
```

The gateway listens on `http://localhost:5000`.

## Environment variables

Override `appsettings.json` values with the standard .NET `__` environment-variable syntax:

- `ApiPassword`: optional password for the UI and API.
- `MinistryOfFlat__BinaryDirectory`: binary directory, default `MOFBinary`.
- `MinistryOfFlat__WineExecutable`: Wine executable path; defaults to `wine` on Linux.
- `MinistryOfFlat__DownloadUrl`: HTTPS URL for the MOF release archive.
- `MinistryOfFlat__TimeoutSeconds`: process timeout, default `120`.
- `MinistryOfFlat__MaxUploadBytes`: upload limit, default `104857600`.
- `MinistryOfFlat__MaxOutputBytes`: output limit, default `209715200`.
- `MinistryOfFlat__MaxConcurrentProcesses`: process limit, default `2`.
- `MinistryOfFlat__TempDirectory`: temporary working directory.
- `MinistryOfFlat__ExpectedSha256`: optional SHA-256 checksum for `UnWrapConsole3.exe`.
- `MOF_TEST_DOWNLOAD`: set to `1` to test a fresh download in the Windows integration tests.
