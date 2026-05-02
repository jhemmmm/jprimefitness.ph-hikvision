# Hikvision Remote Fingerprint Proof Helper

This is a Windows-side C#/.NET 8 ASP.NET Core proof-of-concept that tests whether the Hikvision terminal supports remote fingerprint prompts via the Windows HCNetSDK.

## Phase 1 Scope

**This is a capability spike, not production deployment.** The goal is to answer one question:

> Can the Hikvision terminal show a remote fingerprint prompt when driven from the Windows HCNetSDK?

Possible outcomes:
- ✅ **Success**: Terminal shows a prompt and the helper receives capture data → Phase 2 will build a Laravel bridge
- ❌ **Unsupported**: Terminal doesn't support this flow even through the SDK → Stop pursuing remote prompt, keep manual enrollment
- ❌ **Failed**: SDK error prevents capture (setup issue, credentials, offline) → Debug and retry

## Configuration

Edit `appsettings.json` with your device details:

```json
{
  "HikVision": {
    "DeviceIp": "192.168.250.201",
    "SdkPort": 8000,
    "Username": "admin",
    "Password": "your-password",
    "DeviceSerial": "DS-K1T342E",
    "TimeoutSeconds": 30
  }
}
```

## Running the Helper

### Prerequisites

- Windows host on the same LAN as the Hikvision terminal
- HCNetSDK.dll must be installed or in the application directory
- .NET 8 runtime

### Start the Helper

```bash
dotnet run --project HikVision.csproj
```

The helper listens on `http://127.0.0.1:5077` and logs to `logs/hikvision-proof-*.txt`.

## API Endpoints

### `GET /health`

Proves the helper is running.

**Response:**
```json
{
  "status": "healthy",
  "timestamp": "2026-04-13T12:34:56.789Z",
  "version": "1.0.0-proof"
}
```

### `POST /sdk/test-login`

Proves the Windows SDK can log into the device.

**Response:**
```json
{
  "status": "success",
  "message": "Login successful",
  "sdkErrorCode": 0,
  "timestamp": "2026-04-13T12:34:56.789Z"
}
```

Possible statuses:
- `success`: SDK login worked
- `failed`: SDK login failed (check credentials, device IP, firewall)

**Common errors:**
- `DllNotFoundException`: HCNetSDK.dll not found → install SDK on Windows
- Error code 1-2: Device offline or no SDK support

### `POST /fingerprint/prompt`

The decisive test: attempt to trigger a remote fingerprint prompt on the terminal.

**Request:**
```json
{
  "employeeNo": "00000001",
  "fingerprintId": "1",
  "timeoutSeconds": 30
}
```

**Response:**
```json
{
  "status": "success",
  "message": "Remote fingerprint capture completed successfully",
  "sdkErrorCode": 0,
  "employeeNo": "00000001",
  "fingerprintId": "1",
  "timestamp": "2026-04-13T12:34:56.789Z"
}
```

Possible statuses:
- `success`: Terminal showed prompt and capture succeeded
- `unsupported`: SDK function not supported by this device model
- `failed`: Capture was attempted but failed (timeout, wrong state, etc.)
- `timeout`: No finger placed within the timeout window

## Test Plan

Manual sequence on the Windows machine:

1. **Start the helper:**
   ```bash
   dotnet run
   ```

2. **Check health:**
   ```bash
   curl http://127.0.0.1:5077/health
   ```

3. **Test SDK login:**
   ```bash
   curl -X POST http://127.0.0.1:5077/sdk/test-login
   ```
   - If this fails, verify device IP, credentials, and network connectivity.

4. **Trigger remote fingerprint prompt:**
   ```bash
   curl -X POST http://127.0.0.1:5077/fingerprint/prompt \
     -H "Content-Type: application/json" \
     -d '{"employeeNo":"00000001","fingerprintId":"1","timeoutSeconds":30}'
   ```
   - Watch the Hikvision terminal screen
   - Place your finger on the terminal's scanner
   - The helper will log the outcome

5. **Review logs:**
   ```bash
   type logs\hikvision-proof-*.txt
   ```

## Logging

All SDK operations are logged to `logs/hikvision-proof-*.txt` with timestamps and levels:

```
[2026-04-13 12:34:56.123 +00:00] [INF] Initializing HCNetSDK...
[2026-04-13 12:34:56.456 +00:00] [INF] Attempting SDK login to device 192.168.250.201:8000
[2026-04-13 12:34:56.789 +00:00] [INF] SDK login successful. Login ID: 0
[2026-04-13 12:34:56.890 +00:00] [INF] Starting remote fingerprint capture for employee 00000001, fingerprint 1
[2026-04-13 12:35:26.900 +00:00] [INF] Remote capture stopped. Result: True
```

Use these logs to diagnose failures:
- Missing DLL → "HCNetSDK.dll not found"
- Wrong credentials → "SDK login failed with error code X"
- Unsupported → "SDK function not supported by device"

## Phase 2 (If Successful)

If the proof shows `status: success`, phase 2 will:
1. Create a new Laravel endpoint that calls this helper
2. Wire a button in the biometric UI to trigger `/fingerprint/prompt`
3. Replace the fake remote-prompt UX with a real Windows-backed flow
4. Retire the helper once flow is stable

If the proof shows `unsupported` or persistent `failed`, we will:
1. Stop pursuing remote prompt on this terminal model
2. Keep manual enrollment + attendance sync as the primary enrollment method
3. Document this terminal's limitation for future reference

## Architecture Notes

- **PInvoke bindings** in `Sdk/HCNetSdkBindings.cs` define the SDK function signatures
- **SDK client** in `Services/HikVisionSdkClient.cs` wraps init, login, capture, logout, cleanup
- **Handlers** in `Handlers/` expose the three endpoints
- **Configuration** loads from `appsettings.json` and is injected via DI
- **Logging** uses Serilog with rolling file sinks

The helper maintains a persistent SDK session across calls (login once, reuse for all requests, logout on shutdown).

## Troubleshooting

| Symptom | Check |
|---------|-------|
| `DllNotFoundException` | Install HCNetSDK on Windows, or copy HCNetSDK.dll to the helper directory |
| `/sdk/test-login` fails | Verify device IP, credentials, firewall, device is online |
| `/fingerprint/prompt` returns `unsupported` | This terminal model may not support remote capture via SDK |
| `/fingerprint/prompt` times out | Place finger on scanner during the 30-second window |
| Helper won't start | Verify .NET 8 runtime is installed, check logs/ directory permissions |

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
