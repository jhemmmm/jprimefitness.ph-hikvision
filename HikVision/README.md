# HikVision Helper

A Windows .NET 8 ASP.NET Core helper that connects a Hikvision access-control terminal to a web API.

The helper:

- discovers the Hikvision device on startup with SADP
- registers `/attendance/callback` on the device automatically
- receives real-time attendance callbacks
- forwards attendance callbacks to the configured web API
- keeps the existing SDK, fingerprint, person, and attendance search endpoints used by the web system

## Configuration

Only five values are configurable in `appsettings.json`:

```json
{
  "HikVision": {
    "ForwardUrl": "https://your-web-api.example/api/biometric/hikvision/callback",
    "ForwardToken": "your-forward-token",
    "Username": "admin",
    "Password": "your-device-password",
    "Debug": false
  }
}
```

All other helper settings are fixed internal defaults:

| Setting | Value |
|---|---|
| Helper bind URL | `http://0.0.0.0:5077` |
| Device HTTP port | `80` |
| Device SDK port | `8000` |
| Callback path | `/attendance/callback` |
| Callback host id | `2` |
| Default request timeout | `30s` |

Startup fails if unsupported `HikVision` keys are present. This keeps deployment predictable: users configure only the web API forward target, token, device credentials, and debug mode.

## Startup Behavior

On startup, the helper:

1. validates the five supported configuration keys
2. discovers Hikvision devices with SADP
3. requires exactly one discovered device
4. stores the discovered IP in runtime state
5. fetches device serial/model/firmware with ISAPI
6. resolves the helper callback URL
7. registers the attendance callback on the device
8. starts listening on `http://0.0.0.0:5077`

If no device is found, or multiple devices are found, the helper does not guess. It exits with a clear log message.

## Forwarding

Attendance callbacks received at `/attendance/callback` are normalized and forwarded to `ForwardUrl`.

The forwarding request includes:

```http
Content-Type: application/json
X-Biometric-Token: {ForwardToken}
```

The forwarded payload includes normalized attendance fields plus the raw Hikvision callback payload when available.

## Running

```bash
dotnet run --project HikVision.csproj
```

Command-line overrides are available for the five supported settings:

```bash
dotnet run --project HikVision.csproj -- --username admin --password secret --forward-url https://your-web-api.example/api/biometric/hikvision/callback --forward-token token --debug false
```

## Release Build

```bash
dotnet build HikVision.sln -c Release
```

The application release output is written to the repository-level `bin` folder:

```text
bin/
  HikVision.exe
  HikVision.dll
  appsettings.json
  lib/
    HCNetSDK.dll
```

The SDK loader checks `lib/HCNetSDK.dll` relative to the executable before falling back to the normal Windows DLL search path.

## API Endpoints

### Health

- `GET /health`

### SDK

- `POST /sdk/test-login`
- `GET /sdk/acs-ability`

### Fingerprint

- `POST /fingerprint/enroll`
- `POST /fingerprint/delete`
- `POST /isapi/fingerprint/delete`
- `POST /isapi/fingerprint/setup`

### Person

- `POST /persons/upsert`
- `POST /persons/search`
- `POST /persons/delete`

### Attendance

- `POST /attendance/search`
- `POST /attendance/callback`
- `GET /attendance/callback/last`
- `POST /attendance/callback/configure`

## Troubleshooting

| Symptom | Check |
|---|---|
| Startup says no device found | Confirm the helper machine is on the same LAN and UDP port `37020` is reachable. |
| Startup says multiple devices found | Keep only one Hikvision device reachable from this helper instance. |
| Forwarding is rejected by the web API | Confirm `ForwardToken` matches what the API expects in `X-Biometric-Token`. |
| SDK endpoints fail with DLL errors | Confirm the Hikvision SDK DLLs are in `bin/lib`, including `bin/lib/HCNetSDK.dll`, or add them to `PATH`. |
| No attendance callbacks arrive | Check logs for callback registration status and confirm the helper URL is reachable from the device. |

## Verification

```bash
dotnet build
dotnet test
```
