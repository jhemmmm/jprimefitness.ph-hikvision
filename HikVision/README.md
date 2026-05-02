# Hikvision Remote Fingerprint Proof Helper
# HikVision Helper

A Windows .NET 8 ASP.NET Core service that bridges a Hikvision access control terminal to a Laravel backend. It handles fingerprint enrollment, person management, attendance event search, and real-time attendance callbacks — using both the Hikvision HCNetSDK and the ISAPI HTTP REST interface.

## Configuration

Edit `appsettings.json`. Only `Username` and `Password` are required — everything else has a sensible default or is auto-detected at runtime.

```json
{
  "HikVision": {
    "Username": "admin",
    "Password": "your-password",

    "DeviceIp": "",
    "DeviceSerial": "",

    "HttpPort": 80,
    "SdkPort": 8000,
    "TimeoutSeconds": 30,

    "BindUrl": "http://0.0.0.0:5077",
    "CallbackPath": "/attendance/callback",
    "CallbackToken": "your-secret-token",
    "CallbackHostId": 2,

    "ForwardUrl": "http://your-laravel-app/hikvision/callback",
    "ForwardToken": "your-laravel-token"
  }
}
```

### Auto-detection

`DeviceIp` and `DeviceSerial` do not need to be set manually:

| Field | How it is resolved |
|---|---|
| `DeviceIp` | SADP UDP broadcast on startup → learned from the first incoming callback if SADP finds nothing |
| `DeviceSerial` | Fetched from `/ISAPI/System/deviceInfo` once the IP is known → falls back to `deviceName` in the callback payload |

Once the device is found, the attendance callback URL is automatically registered on the device.

## Running

### Prerequisites

- Windows host on the same LAN as the Hikvision terminal
- `HCNetSDK.dll` present in the application directory (required for SDK endpoints only)
- .NET 8 runtime

### Start

```bash
dotnet run --project HikVision.csproj
```

The helper listens on `http://0.0.0.0:5077` and writes rolling logs to `logs/hikvision-helper-<date>.txt`.

## API Endpoints

### `GET /health`

```json
{ "status": "healthy", "timestamp": "..." }
```

---

### SDK Endpoints

#### `POST /sdk/test-login`

Verifies the HCNetSDK can authenticate to the device.

```json
{ "status": "success", "message": "Login successful", "sdkErrorCode": 0 }
```

#### `GET /sdk/acs-ability`

Returns the access control capabilities reported by the SDK.

---

### Fingerprint Endpoints

#### `POST /fingerprint/enroll`

Triggers a remote fingerprint prompt on the terminal via the HCNetSDK and waits for the finger to be placed.

**Request:**
```json
{ "employeeNo": "00000001", "fingerprintId": 1, "timeoutSeconds": 30 }
```

**Response statuses:** `success` · `unsupported` · `failed` · `timeout`

#### `POST /fingerprint/delete` · `POST /isapi/fingerprint/delete`

Deletes a stored fingerprint for an employee.

**Request:**
```json
{ "employeeNo": "00000001", "fingerprintId": 1 }
```

#### `POST /isapi/fingerprint/setup`

Starts a fingerprint capture session via ISAPI (alternative to SDK-based enroll).

---

### Person Endpoints

#### `POST /persons/upsert`

Creates or updates a person record on the device.

**Request:**
```json
{
  "employeeNo": "00000001",
  "name": "Jane Smith",
  "userType": "normal",
  "validityEnabled": false
}
```

#### `POST /persons/search`

Looks up a person by employee number.

**Request:**
```json
{ "employeeNo": "00000001" }
```

#### `POST /persons/delete`

Removes a person and their associated data from the device.

**Request:**
```json
{ "employeeNo": "00000001" }
```

---

### Attendance Endpoints

#### `POST /attendance/search`

Queries stored attendance events on the device.

**Request:**
```json
{
  "startTime": "2026-01-01T00:00:00",
  "endTime": "2026-12-31T23:59:59",
  "employeeNo": "00000001",
  "maxResults": 30
}
```

#### `POST /attendance/callback`

Receives real-time attendance events pushed by the device. The device posts here automatically once the callback URL has been registered. Received events are optionally forwarded to `ForwardUrl`.

#### `GET /attendance/callback/last`

Returns the most recently received callback payload.

#### `POST /attendance/callback/configure`

Manually re-registers the callback URL on the device.

**Request:**
```json
{ "callbackUrl": "http://192.168.250.102:5077/attendance/callback" }
```

---

## Architecture

```
HikVision terminal
  │  ISAPI HTTP (port 80)  ──→  HikVisionIsapiClient
  │  HCNetSDK (port 8000)  ──→  HikVisionSdkClient
  └─ Attendance callback   ──→  AttendanceCallbackReceiveHandler
                                  └─ AttendanceForwardService ──→ Laravel
```

| Component | Location |
|---|---|
| Configuration | `Config/HikVisionConfig.cs` |
| ISAPI client | `Services/HikVisionIsapiClient.cs` |
| SDK client | `Services/HikVisionSdkClient.cs` |
| SDK PInvoke bindings | `Sdk/HCNetSdkBindings.cs` |
| Device auto-discovery | `Services/HikVisionDeviceDiscovery.cs` (SADP) |
| Device auto-config | `Services/DeviceAutoConfigService.cs` (learns from first callback) |
| Endpoint handlers | `Handlers/` |
| Startup wiring | `Program.cs` |

## Troubleshooting

| Symptom | Check |
|---|---|
| `DllNotFoundException` on SDK endpoints | Copy `HCNetSDK.dll` to the application directory |
| Device not found at startup | Ensure the machine is on the same LAN; set `DeviceIp` manually if SADP is disabled on the device |
| `/sdk/test-login` fails | Verify `Username`, `Password`, `DeviceIp`, and that port 8000 is reachable |
| `/fingerprint/enroll` returns `unsupported` | This terminal model may not support remote capture via SDK; use `/isapi/fingerprint/setup` instead |
| No attendance callbacks arriving | Check `CallbackToken` matches what the device was configured with; trigger `POST /attendance/callback/configure` to re-register |
| Helper won't start | Verify .NET 8 runtime is installed; check `logs/` for startup errors |
## Next Steps (Phase 2+)

1. Once this proof confirms capability or limitation, file decision in the project
2. If successful, create `HikVisionProofHelperBridge.cs` in the Laravel codebase
3. Add `/api/biometric/fingerprint-remote` endpoint to Laravel that:
   - Calls POST `/fingerprint/prompt` on the Windows helper
   - Returns structured result to the frontend
4. Update UI to show a "Waiting for fingerprint..." modal during capture
5. Integrate with the existing biometric enrollment flow

---

**Phase 1 Outcome**: Proof-of-capability ✅  
**Success Criteria**: Decisive answer to "does the terminal support remote prompts?"  
**Deployment**: Local Windows machine, not production  
**Integration**: Intentionally decoupled from Laravel until phase 2
