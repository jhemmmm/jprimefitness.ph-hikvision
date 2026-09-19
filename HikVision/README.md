# HikVision Helper

A Windows .NET 8 ASP.NET Core helper that connects a Hikvision access-control terminal to a web API.

The helper:

- discovers the Hikvision device on startup with SADP (multicast, the same way the official SADP tool works)
- can move an off-subnet device into the PC's subnet automatically (best effort — see Startup Behavior)
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
2. discovers Hikvision devices with SADP — an inquiry probe is sent to multicast group `239.255.255.250:37020` (joined on every IPv4 interface) plus each subnet's broadcast address, so devices reply even when they sit on a different IP subnet
3. requires exactly one discovered device
4. if that device's IP is outside every local subnet and unreachable, tries to move it into the PC's subnet with a SADP network update (auto-picked next free IP, the PC's mask/gateway, DHCP off, using the configured admin password), waits, and re-discovers to confirm — see the note below
5. stores the discovered IP in runtime state
6. fetches device serial/model/firmware with ISAPI
7. resolves the helper callback URL
8. registers the attendance callback on the device
9. starts listening on `http://0.0.0.0:5077`

If no device is found, or multiple devices are found, the helper does not guess. It exits with a clear log message.

### Off-subnet devices and the automatic network fix

If the device kept a static IP from a previous router/PC (e.g. it is on `192.168.1.x` while the PC is on `192.168.250.x`), SADP still finds it, and the helper attempts a SADP network update to move it into the PC's subnet.

This automatic update only works on devices that accept a plain-text SADP password. **Newer firmware advertises `Encrypt=true` and requires the encrypted password format that only the official Hikvision SADP tool sends** — those devices answer `PasswordError`, and repeated attempts make the device temporarily lock configuration changes. When the automatic fix cannot complete, the helper logs a clear, step-by-step message telling you to set the address once with the official SADP tool ("Modify Network Parameters"): pick the device by MAC, enter the target IP/mask/gateway and the admin password, and click Modify. If the device shows as locked, wait a few minutes or power-cycle it, then try again. After the device is on the same subnet as the PC, restart the helper — discovery then finds it directly and startup completes.

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

### SADP

- `GET /sadp/devices?timeoutMs=5000` — discover every Hikvision device on the LAN segment and return its full SADP details (IP, MAC, mask, gateway, ports, software version, DHCP, activated, `Encrypt`, …). `timeoutMs` is optional (1000–30000, default 5000).
- `POST /sadp/modify` — change a device's network parameters over SADP ("Modify Network Parameters"). Body:

  ```json
  {
    "mac": "a4-d5-c2-4b-74-b9",
    "ipAddress": "192.168.250.103",
    "subnetMask": "255.255.255.0",
    "gateway": "192.168.250.1",
    "commandPort": 8000,
    "httpPort": 80,
    "dhcp": false,
    "password": "your-device-password",
    "timeoutMs": 5000
  }
  ```

  `mac` defaults to the current device, and `password` defaults to the configured admin password; `commandPort`/`httpPort` default to `8000`/`80`. On success, if the modified device is the current one, the helper repoints itself at the new IP and re-registers the callback. On failure the response `message` explains how to complete the change manually (see the off-subnet note above — `Encrypt=true` firmware requires the official SADP tool).

### Device network (ISAPI — no SADP tool)

- `POST /device/network` — change the **currently reachable** device's IPv4 settings over ISAPI/HTTP with the admin password (no SADP encryption needed, so it works on `Encrypt=true` firmware). Body:

  ```json
  { "mode": "dhcp" }
  ```
  or
  ```json
  { "mode": "static", "ipAddress": "192.168.1.50", "subnetMask": "255.255.255.0", "gateway": "192.168.1.1", "primaryDns": "8.8.8.8", "secondaryDns": "8.8.4.4" }
  ```

  The device must be reachable by IP when you call this (i.e. already on the PC's subnet). It applies the change; when the IP moves, the HTTP connection drops mid-response — the helper reports `applied` and you re-discover with `GET /sadp/devices`. `dns` fields are optional.

  > Note on DHCP: this DS-K1A802AEF firmware performs **one-shot DHCP** — setting `mode:"dhcp"` makes it lease an address on the current network and then store that address as static. It does **not** stay in DHCP mode across a move to a different network. Plan redeployment around a static change per site (below) rather than relying on persistent DHCP.

## Redeploying to a new network (new router / PC) without the SADP tool

On a new network the device arrives with its old static IP, which is usually **off-subnet and unreachable**. SADP still *finds* it (`GET /sadp/devices`) but cannot *change* it on `Encrypt=true` firmware. To change it over ISAPI (which does work with the plain-text password) the PC must first have an address in the device's current subnet:

1. Find the device and its current IP/subnet: `GET /sadp/devices`.
2. Give the PC a temporary secondary IP in that subnet (elevated PowerShell; additive — your normal networking and internet are unaffected):

   ```bash
   netsh interface ipv4 add address "Ethernet 2" 192.168.1.50 255.255.255.0
   ```

   (Replace the adapter name and pick a free address in the device's subnet.)
3. Change the device to this network's addressing:

   ```bash
   curl -X POST http://127.0.0.1:5077/device/network -H "Content-Type: application/json" -d "{\"mode\":\"static\",\"ipAddress\":\"192.168.1.60\",\"subnetMask\":\"255.255.255.0\",\"gateway\":\"192.168.1.1\"}"
   ```
4. Remove the temporary alias:

   ```bash
   netsh interface ipv4 delete address "Ethernet 2" 192.168.1.50
   ```
5. Restart the helper — discovery now finds the device on the PC's subnet and startup completes.

## Troubleshooting

| Symptom | Check |
|---|---|
| Startup says no device found | Confirm the helper machine is on the same LAN segment. Close the official Hikvision SADP tool if it is open — it holds UDP `37020` and can receive the device's replies instead of the helper. Allow inbound UDP `37020` through Windows Firewall (`netsh advfirewall firewall add rule name="HikVision Helper SADP" dir=in action=allow protocol=UDP localport=37020`). On Wi-Fi, AP/client isolation or IGMP snooping can block the multicast probe. |
| Startup says multiple devices found | Keep only one Hikvision device reachable from this helper instance. |
| Startup says the device is on another subnet and could not be fixed | The device needs its IP changed. On `Encrypt=true` firmware, set it once with the official Hikvision SADP tool (Modify Network Parameters), then restart the helper. If the device shows as locked, wait a few minutes or reboot it first. |
| Forwarding is rejected by the web API | Confirm `ForwardToken` matches what the API expects in `X-Biometric-Token`. |
| SDK endpoints fail with DLL errors | Confirm the Hikvision SDK DLLs are in `bin/lib`, including `bin/lib/HCNetSDK.dll`, or add them to `PATH`. |
| No attendance callbacks arrive | Check logs for callback registration status and confirm the helper URL is reachable from the device. |

## Verification

```bash
dotnet build
dotnet test
```
