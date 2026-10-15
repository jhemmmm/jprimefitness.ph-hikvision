using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Serilog;
using HikVision.Config;

namespace HikVision.Services;

/// <summary>
/// Hikvision ISAPI HTTP REST client for fingerprint capture.
/// Uses Digest auth on port 80 — independent of the SDK binary session on port 8000.
/// </summary>
public class HikVisionIsapiClient : IDisposable
{
    private readonly HikVisionConfig _config;
    private readonly HikVisionDeviceState _deviceState;
    private readonly HttpClient _httpClient;

    public HikVisionIsapiClient(HikVisionConfig config, HikVisionDeviceState deviceState)
    {
        _config = config;
        _deviceState = deviceState;

        // HttpClientHandler with NetworkCredential for automatic Digest auth
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(_config.Username, _config.Password),
            PreAuthenticate = false  // let device challenge first, then reply with Digest
        };

        // BaseAddress is intentionally NOT set — DeviceIp may be null at construction time
        // and cannot be changed on HttpClient after the first request. Use DeviceUrl() instead.
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(HikVisionDefaults.RequestTimeoutSeconds)
        };
    }

    /// <summary>
    /// Builds a full absolute URI for the given ISAPI path using the current DeviceIp.
    /// Throws <see cref="InvalidOperationException"/> if DeviceIp is not yet configured.
    /// </summary>
    private string DeviceUrl(string path)
    {
        var base_ = $"http://{_deviceState.RequireDeviceIp()}:{HikVisionDefaults.HttpPort}";
        return path.StartsWith('/') ? base_ + path : base_ + "/" + path;
    }

    /// <summary>
    /// Fetches device information (serial number, model, firmware) from /ISAPI/System/deviceInfo.
    /// </summary>
    public async Task<(bool Success, string? SerialNumber, string? DeviceType, string? FirmwareVersion)> GetDeviceInfoAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync(DeviceUrl("/ISAPI/System/deviceInfo?format=json"));
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                // Some devices ignore ?format=json and always return XML — handle both
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    var info = root.TryGetProperty("DeviceInfo", out var di) ? di : root;

                    var serial = ReadJsonString(info, "serialNumber");
                    var deviceType = ReadJsonString(info, "deviceType") ?? ReadJsonString(info, "model");
                    var firmware = ReadJsonString(info, "firmwareVersion");

                    return (true, serial, deviceType, firmware);
                }
                catch (JsonException)
                {
                    // Device returned XML despite ?format=json — fall through to XML parsing below
                    return ParseDeviceInfoXml(body);
                }
            }

            // Fallback: try plain XML endpoint
            response = await _httpClient.GetAsync(DeviceUrl("/ISAPI/System/deviceInfo"));
            body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("ISAPI: Could not fetch device info. HTTP {StatusCode}", response.StatusCode);
                return (false, null, null, null);
            }

            return ParseDeviceInfoXml(body);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ISAPI: Failed to retrieve device info");
            return (false, null, null, null);
        }
    }

    private static (bool Success, string? SerialNumber, string? DeviceType, string? FirmwareVersion) ParseDeviceInfoXml(string xml)
    {
        try
        {
            var xmlDoc = System.Xml.Linq.XDocument.Parse(xml);
            var ns = xmlDoc.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;

            string? GetXmlValue(string name) =>
                xmlDoc.Descendants(ns + name).FirstOrDefault()?.Value?.Trim()
                ?? xmlDoc.Descendants(name).FirstOrDefault()?.Value?.Trim();

            return (true, GetXmlValue("serialNumber"), GetXmlValue("deviceType") ?? GetXmlValue("model"), GetXmlValue("firmwareVersion"));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ISAPI: Failed to parse device info XML");
            return (false, null, null, null);
        }
    }

    public async Task<(string Status, string Message, int HttpStatusCode, string? RawStatus, string? RawResponse)> SetupFingerprintAsync(
        string employeeNo,
        int fingerprintId,
        int[]? cardReaders,
        string? fingerprintData,
        int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        try
        {
            Log.Information(
                "ISAPI: Starting fingerprint capture for employee {EmployeeNo}, finger {FingerNo}",
                employeeNo, fingerprintId);

            var startResult = await StartFingerprintCaptureAsync(employeeNo, fingerprintId, cardReaders, fingerprintData);

            if (!startResult.Success)
            {
                return (startResult.Status, startResult.Message, startResult.HttpStatusCode, startResult.RawStatus, startResult.RawResponse);
            }

            Log.Information("ISAPI: Fingerprint capture started. Polling for completion (timeout: {Timeout}s)", timeoutSeconds);

            int pollCount = 0;

            while (DateTime.UtcNow < deadline)
            {
                pollCount++;
                var pollResult = await PollFingerprintStatusAsync();

                if (pollCount <= 3 || pollCount % 10 == 0)
                {
                    Log.Information("ISAPI: Poll #{Count}: status={Status}, errorCode={ErrorCode}",
                        pollCount, pollResult.status, pollResult.errorCode);
                }

                switch (pollResult.status)
                {
                    case "success":
                        Log.Information("✓ ISAPI: Fingerprint captured successfully");
                        return ("success", "Fingerprint captured via ISAPI", 200, pollResult.status, pollResult.rawResponse);

                    case "failed":
                        Log.Warning("✗ ISAPI: Fingerprint capture failed. ErrorCode: {ErrorCode}",
                            pollResult.errorCode);
                        return (MapHttpStatusToStatusCode(200, pollResult.rawResponse), $"Device rejected capture. Error code: {pollResult.errorCode}", 200, pollResult.status, pollResult.rawResponse);

                    case "collecting":
                        await Task.Delay(500);
                        continue;

                    case "error_polling":
                        return ("failed", "Failed to poll capture status from device", pollResult.errorCode, pollResult.status, pollResult.rawResponse);

                    default:
                        Log.Warning("ISAPI: Unexpected status: {Status}", pollResult.status);
                        await Task.Delay(500);
                        continue;
                }
            }

            Log.Warning("✗ ISAPI: Fingerprint capture timed out after {Timeout}s ({PollCount} polls)",
                timeoutSeconds, pollCount);
            return ("timeout", $"No finger placed within {timeoutSeconds} seconds", 200, "timeout", null);
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "ISAPI: HTTP request error");
            return ("failed", $"HTTP error: {ex.Message}", 0, null, null);
        }
        catch (TaskCanceledException ex)
        {
            Log.Error(ex, "ISAPI: Request timeout");
            return ("failed", "ISAPI request timeout", 0, null, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ISAPI: Unexpected error");
            return ("failed", $"Error: {ex.Message}", 0, null, null);
        }
        finally
        {
            try
            {
                await CancelFingerprintCaptureAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ISAPI: Error during cleanup");
            }
        }
    }

    public async Task<(string Status, string Message, int HttpStatusCode, string? RawStatus, string? RawResponse)> DeleteFingerprintAsync(
        string employeeNo,
        int fingerprintId,
        int[]? cardReaders)
    {
        try
        {
            Log.Information(
                "ISAPI: Deleting fingerprint for employee {EmployeeNo}, finger {FingerNo}",
                employeeNo,
                fingerprintId);

            var deleteResult = await SendFingerprintSetupRequestAsync(
                employeeNo,
                fingerprintId,
                cardReaders,
                fingerprintData: null,
                deleteFingerPrint: true);

            if (!deleteResult.Success)
            {
                return (
                    deleteResult.Status,
                    deleteResult.Message,
                    deleteResult.HttpStatusCode,
                    deleteResult.RawStatus,
                    deleteResult.RawResponse);
            }

            return (
                "success",
                "Fingerprint deleted successfully",
                deleteResult.HttpStatusCode,
                deleteResult.RawStatus,
                deleteResult.RawResponse);
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "ISAPI: HTTP request error during fingerprint delete");
            return ("failed", $"HTTP error: {ex.Message}", 0, null, null);
        }
        catch (TaskCanceledException ex)
        {
            Log.Error(ex, "ISAPI: Request timeout during fingerprint delete");
            return ("failed", "ISAPI request timeout", 0, null, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ISAPI: Unexpected error during fingerprint delete");
            return ("failed", $"Error: {ex.Message}", 0, null, null);
        }
    }

    public async Task<(string Status, string Message, int HttpStatusCode, string? RawStatus, string? RawResponse)> SaveFingerprintAsync(
        string employeeNo,
        int fingerprintId,
        int[]? cardReaders,
        string fingerprintData)
    {
        try
        {
            Log.Information(
                "ISAPI: Saving fingerprint for employee {EmployeeNo}, finger {FingerNo}",
                employeeNo,
                fingerprintId);

            var saveResult = await SendFingerprintSetupRequestAsync(
                employeeNo,
                fingerprintId,
                cardReaders,
                fingerprintData,
                deleteFingerPrint: false);

            if (!saveResult.Success)
            {
                return (
                    saveResult.Status,
                    saveResult.Message,
                    saveResult.HttpStatusCode,
                    saveResult.RawStatus,
                    saveResult.RawResponse);
            }

            return (
                "success",
                "Fingerprint saved successfully",
                saveResult.HttpStatusCode,
                saveResult.RawStatus,
                saveResult.RawResponse);
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "ISAPI: HTTP request error during fingerprint save");
            return ("failed", $"HTTP error: {ex.Message}", 0, null, null);
        }
        catch (TaskCanceledException ex)
        {
            Log.Error(ex, "ISAPI: Request timeout during fingerprint save");
            return ("failed", "ISAPI request timeout", 0, null, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ISAPI: Unexpected error during fingerprint save");
            return ("failed", $"Error: {ex.Message}", 0, null, null);
        }
    }

    /// <summary>
    /// Send PUT /ISAPI/AccessControl/FingerPrint/SetUp to start fingerprint capture.
    /// </summary>
    public async Task<string> GetAccessControlCapabilitiesXmlAsync()
    {
        var response = await _httpClient.GetAsync(DeviceUrl("/ISAPI/AccessControl/capabilities"));
        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync();
        Log.Information("ISAPI: Retrieved /ISAPI/AccessControl/capabilities");

        return xml;
    }

    public async Task<(string Status, string Message, int HttpStatusCode, string? RawResponse)> UpsertPersonAsync(
        string employeeNo,
        string name,
        string userType,
        bool validityEnabled,
        string? validFrom,
        string? validTo,
        bool checkUser,
        bool closeDelayEnabled,
        int numberOfFingerprints)
    {
        try
        {
            var requestBody = new
            {
                UserInfo = new
                {
                    employeeNo,
                    name,
                    userType,
                    closeDelayEnabled,
                    Valid = new
                    {
                        enable = validityEnabled,
                        beginTime = validityEnabled ? FormatDeviceDateTime(validFrom) : null,
                        endTime = validityEnabled ? FormatDeviceDateTime(validTo) : null
                    },
                    checkUser,
                    numOfFP = Math.Max(0, numberOfFingerprints)
                }
            };

            return await SendJsonRequestAsync(
                HttpMethod.Put,
                "/ISAPI/AccessControl/UserInfo/SetUp?format=json",
                requestBody,
                "ISAPI: User upsert");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ISAPI: Unexpected error during user upsert");
            return ("failed", $"Error: {ex.Message}", 0, null);
        }
    }

    public async Task<(string Status, string Message, int HttpStatusCode, string? RawResponse)> DeletePersonAsync(string employeeNo)
    {
        try
        {
            var requestBody = new
            {
                UserInfoDetail = new
                {
                    mode = "byEmployeeNo",
                    EmployeeNoList = new[]
                    {
                        new { employeeNo }
                    }
                }
            };

            var deleteResponse = await SendJsonRequestAsync(
                HttpMethod.Put,
                "/ISAPI/AccessControl/UserInfoDetail/Delete?format=json",
                requestBody,
                "ISAPI: User delete");

            if (deleteResponse.Status != "success")
            {
                return deleteResponse;
            }

            var processResponse = await PollDeleteProcessAsync();

            return processResponse.Status == "success"
                ? ("success", "Person deleted successfully", deleteResponse.HttpStatusCode, processResponse.RawResponse ?? deleteResponse.RawResponse)
                : processResponse;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ISAPI: Unexpected error during user delete");
            return ("failed", $"Error: {ex.Message}", 0, null);
        }
    }

    public async Task<(string Status, string Message, int HttpStatusCode, Dictionary<string, object?>? UserInfo, string? RawResponse)> SearchPersonAsync(string employeeNo)
    {
        try
        {
            var searchId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var requestBody = new
            {
                UserInfoSearchCond = new
                {
                    searchID = searchId,
                    searchResultPosition = 0,
                    maxResults = 1,
                    EmployeeNoList = new[]
                    {
                        new { employeeNo }
                    }
                }
            };

            var json = JsonSerializer.Serialize(requestBody);
            var response = await _httpClient.PostAsync(
                DeviceUrl("/ISAPI/AccessControl/UserInfo/Search?format=json"),
                new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return (
                    MapHttpStatusToStatusCode((int)response.StatusCode, responseBody),
                    $"Person search failed. HTTP {response.StatusCode}. Response: {responseBody}",
                    (int)response.StatusCode,
                    null,
                    responseBody);
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("UserInfoSearch", out var searchNode) &&
                searchNode.TryGetProperty("UserInfo", out var userInfoNode) &&
                userInfoNode.ValueKind == JsonValueKind.Array &&
                userInfoNode.GetArrayLength() > 0)
            {
                var userInfo = userInfoNode[0];
                return ("success", "Person found", 200, ConvertJsonObject(userInfo), responseBody);
            }

            return ("not_found", "Person not found", 404, null, responseBody);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ISAPI: Unexpected error during person search");
            return ("failed", $"Error: {ex.Message}", 0, null, null);
        }
    }

    public async Task<(string Status, string Message, int HttpStatusCode, List<Models.AttendanceEventRecord> Events, int NumOfMatches, int TotalMatches, string? RawResponse)> SearchAttendanceEventsAsync(
        string startTime,
        string endTime,
        int position,
        int maxResults,
        string? employeeNo,
        int major,
        int minor,
        string eventAttribute)
    {
        try
        {
            var requestBody = new
            {
                AcsEventCond = new
                {
                    searchID = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                    searchResultPosition = Math.Max(0, position),
                    maxResults = Math.Clamp(maxResults, 1, 30),
                    major,
                    minor,
                    startTime = FormatDeviceDateTime(startTime),
                    endTime = FormatDeviceDateTime(endTime),
                    eventAttribute,
                    employeeNoString = string.IsNullOrWhiteSpace(employeeNo) ? null : employeeNo
                }
            };

            var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });

            var response = await _httpClient.PostAsync(
                DeviceUrl("/ISAPI/AccessControl/AcsEvent?format=json"),
                new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return (
                    MapHttpStatusToStatusCode((int)response.StatusCode, responseBody),
                    $"Attendance search failed. HTTP {response.StatusCode}. Response: {responseBody}",
                    (int)response.StatusCode,
                    [],
                    0,
                    0,
                    responseBody);
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            var eventRoot = root.TryGetProperty("AcsEvent", out var acsEventNode) ? acsEventNode : default;
            var events = new List<Models.AttendanceEventRecord>();

            if (eventRoot.ValueKind == JsonValueKind.Object &&
                eventRoot.TryGetProperty("InfoList", out var infoListNode) &&
                infoListNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in infoListNode.EnumerateArray())
                {
                    events.Add(new Models.AttendanceEventRecord
                    {
                        EmployeeNo = ReadJsonString(item, "employeeNoString"),
                        Name = ReadJsonString(item, "name"),
                        Time = ReadJsonString(item, "time"),
                        VerifyMode = ReadJsonString(item, "currentVerifyMode"),
                        AttendanceStatus = ReadJsonString(item, "attendanceStatus"),
                        SerialNo = ReadJsonInt(item, "serialNo"),
                        Major = ReadJsonInt(item, "major"),
                        Minor = ReadJsonInt(item, "minor"),
                        DeviceSerial = _deviceState.DeviceSerial,
                        RawPayload = item.GetRawText()
                    });
                }
            }

            return (
                "success",
                "Attendance events retrieved successfully",
                200,
                events,
                ReadJsonInt(eventRoot, "numOfMatches") ?? events.Count,
                ReadJsonInt(eventRoot, "totalMatches") ?? events.Count,
                responseBody);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ISAPI: Unexpected error during attendance search");
            return ("failed", $"Error: {ex.Message}", 0, [], 0, 0, null);
        }
    }

    /// <summary>
    /// Changes the device's IPv4 network settings over ISAPI (Digest auth, plain-text password — no SADP encryption
    /// needed). The device must currently be reachable by IP. <paramref name="mode"/> is "dhcp" or "static".
    /// A dropped connection right after the PUT is treated as "likely applied", because the device switches to its new
    /// address (or subnet) mid-response; callers should re-discover by MAC to confirm.
    /// </summary>
    public async Task<(string Status, string Message, int HttpStatusCode, string? RawResponse)> SetNetworkAsync(
        string mode,
        string? ipAddress = null,
        string? subnetMask = null,
        string? gateway = null,
        string? primaryDns = null,
        string? secondaryDns = null)
    {
        const string path = "/ISAPI/System/Network/interfaces/1/ipAddress";
        var isDhcp = string.Equals(mode, "dhcp", StringComparison.OrdinalIgnoreCase);

        if (!isDhcp && (ipAddress is null || subnetMask is null || gateway is null))
        {
            return ("failed", "ipAddress, subnetMask and gateway are required for a static network change.", 0, null);
        }

        // Read the current config first. A failure here means the device is NOT reachable, which must be reported as a
        // failure — it is only the PUT that may legitimately drop the connection (the device moves mid-response).
        string currentXml;
        try
        {
            var getResponse = await _httpClient.GetAsync(DeviceUrl(path));
            currentXml = await getResponse.Content.ReadAsStringAsync();
            if (!getResponse.IsSuccessStatusCode)
            {
                return (
                    MapHttpStatusToStatusCode((int)getResponse.StatusCode, currentXml),
                    $"Could not read current network settings. HTTP {getResponse.StatusCode}.",
                    (int)getResponse.StatusCode,
                    currentXml);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ISAPI: Could not read current network settings before the change");
            return ("failed", $"Could not reach the device to read its current network settings: {ex.Message}", 0, null);
        }

        var body = isDhcp
            ? IsapiNetworkXml.ToDynamic(currentXml)
            : IsapiNetworkXml.ToStatic(currentXml, ipAddress!, subnetMask!, gateway!, primaryDns, secondaryDns);

        Log.Information("ISAPI: Setting network to {Mode} (ip={Ip} mask={Mask} gw={Gateway})", mode, ipAddress ?? "-", subnetMask ?? "-", gateway ?? "-");
        Log.Debug("ISAPI: Network PUT body={Body}", body);

        try
        {
            var putResponse = await _httpClient.PutAsync(
                DeviceUrl(path),
                new StringContent(body, System.Text.Encoding.UTF8, "application/xml"));

            var responseBody = await putResponse.Content.ReadAsStringAsync();
            if (putResponse.IsSuccessStatusCode)
            {
                return ("success", "Network settings applied. The device may change its IP; re-discover by MAC to confirm.", (int)putResponse.StatusCode, responseBody);
            }

            return (
                MapHttpStatusToStatusCode((int)putResponse.StatusCode, responseBody),
                $"Network change was rejected. HTTP {putResponse.StatusCode}. Response: {responseBody}",
                (int)putResponse.StatusCode,
                responseBody);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The device applied the change and dropped the connection because its address/subnet moved.
            Log.Warning(ex, "ISAPI: Connection dropped during network change; this is expected when the device's IP moves.");
            return ("applied", "The network change was sent and the device connection dropped, which is expected when its IP changes. Re-discover by MAC to confirm.", 0, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ISAPI: Unexpected error during network change");
            return ("failed", $"Unexpected error: {ex.Message}", 0, null);
        }
    }

    /// <summary>
    /// Sets the device clock from this PC when it has drifted more than <paramref name="tolerance"/>.
    /// Attendance is recorded with the device's event time, so a drifting terminal clock shifts every time-in/out.
    /// Returns the drift measured before any correction (positive = device ahead), or null when it could not be read.
    /// </summary>
    public async Task<TimeSpan?> SyncDeviceClockAsync(TimeSpan tolerance)
    {
        const string path = "/ISAPI/System/time";

        try
        {
            var getResponse = await _httpClient.GetAsync(DeviceUrl(path));
            var currentXml = await getResponse.Content.ReadAsStringAsync();
            if (!getResponse.IsSuccessStatusCode)
            {
                Log.Warning("ISAPI: Could not read device clock. HTTP {StatusCode}", getResponse.StatusCode);
                return null;
            }

            var deviceTime = IsapiTimeXml.ReadLocalTime(currentXml);
            if (deviceTime is null)
            {
                Log.Warning("ISAPI: Device clock response had no readable localTime. Body={Body}", currentXml);
                return null;
            }

            var now = DateTimeOffset.Now;
            var drift = deviceTime.Value - now;
            if (drift.Duration() <= tolerance)
            {
                Log.Information("ISAPI: Device clock is within tolerance. drift={DriftSeconds:F0}s", drift.TotalSeconds);
                return drift;
            }

            var putResponse = await _httpClient.PutAsync(
                DeviceUrl(path),
                new StringContent(IsapiTimeXml.ToManual(currentXml, now), System.Text.Encoding.UTF8, "application/xml"));
            var responseBody = await putResponse.Content.ReadAsStringAsync();

            if (putResponse.IsSuccessStatusCode)
            {
                Log.Warning("ISAPI: Device clock was off by {DriftSeconds:F0}s; set it to the PC time {Now}", drift.TotalSeconds, now);
            }
            else
            {
                Log.Warning(
                    "ISAPI: Device clock is off by {DriftSeconds:F0}s but setting it failed. HTTP {StatusCode}. Response: {Body}",
                    drift.TotalSeconds,
                    putResponse.StatusCode,
                    responseBody);
            }

            return drift;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ISAPI: Device clock sync failed");
            return null;
        }
    }

    public async Task<(string Status, string Message, int HttpStatusCode, string? CallbackUrl, int HostId, string? RawResponse)> ConfigureAttendanceCallbackAsync(string callbackUrl, int? hostId)
    {
        try
        {
            var resolvedHostId = hostId ?? HikVisionDefaults.CallbackHostId;
            var preferredEventFormat = await GetConfiguredCallbackEventFormatAsync(resolvedHostId);

            if (string.Equals(preferredEventFormat, "XML", StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("ISAPI: Callback host {HostId} prefers XML event format", resolvedHostId);
                var xmlResult = await RegisterXmlCallbackAsync(callbackUrl, resolvedHostId);
                if (xmlResult.Status == "success")
                {
                    return xmlResult;
                }

                Log.Information("ISAPI: XML callback registration was not accepted. Trying JSON event format.");
                return await RegisterJsonCallbackAsync(callbackUrl, resolvedHostId, logFailureAsWarning: true);
            }

            var jsonResult = await RegisterJsonCallbackAsync(callbackUrl, resolvedHostId, logFailureAsWarning: false);
            if (jsonResult.Status == "success")
            {
                return jsonResult;
            }

            Log.Information("ISAPI: JSON callback registration was not accepted. Trying XML event format.");
            return await RegisterXmlCallbackAsync(callbackUrl, resolvedHostId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ISAPI: Unexpected error during callback configuration");
            return ("failed", $"Error: {ex.Message}", 0, callbackUrl, hostId ?? HikVisionDefaults.CallbackHostId, null);
        }
    }

    private async Task<(string Status, string Message, int HttpStatusCode, string? CallbackUrl, int HostId, string? RawResponse)> RegisterJsonCallbackAsync(
        string callbackUrl,
        int hostId,
        bool logFailureAsWarning)
    {
        var requestBody = new
        {
            HttpHostNotification = new
            {
                id = hostId,
                url = callbackUrl,
                protocolType = callbackUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "HTTPS" : "HTTP",
                parameterFormatType = "JSON",
                addressingFormatType = "ipaddress",
                ipAddress = ExtractHost(callbackUrl),
                portNo = ExtractPort(callbackUrl),
                httpAuthenticationMethod = "none",
                SubscribeEvent = new
                {
                    eventMode = "all"
                }
            }
        };

        var result = await SendJsonRequestAsync(
            HttpMethod.Put,
            $"/ISAPI/Event/notification/httpHosts/{hostId}?format=json",
            requestBody,
            "ISAPI: Callback registration",
            logFailureAsWarning);

        if (result.Status == "success")
        {
            Log.Information("ISAPI: Callback registration succeeded using JSON event format");
        }

        return (result.Status, result.Message, result.HttpStatusCode, callbackUrl, hostId, result.RawResponse);
    }

    private async Task<(string Status, string Message, int HttpStatusCode, string? CallbackUrl, int HostId, string? RawResponse)> RegisterXmlCallbackAsync(
        string callbackUrl,
        int hostId)
    {
        Log.Information("ISAPI: Callback registration: PUT /ISAPI/Event/notification/httpHosts/{HostId} using XML event format", hostId);
        Log.Debug("ISAPI: Callback registration XML body={Body}", BuildCallbackXml(callbackUrl, hostId, "XML"));

        var response = await _httpClient.PutAsync(
            DeviceUrl($"/ISAPI/Event/notification/httpHosts/{hostId}"),
            new StringContent(BuildCallbackXml(callbackUrl, hostId, "XML"), System.Text.Encoding.UTF8, "application/xml"));

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("ISAPI: XML callback registration failed. HTTP {StatusCode}", response.StatusCode);
            Log.Debug("ISAPI: XML callback registration response body: {Body}", body);

            return (
                MapHttpStatusToStatusCode((int)response.StatusCode, body),
                $"Callback registration failed. HTTP {response.StatusCode}. Response: {body}",
                (int)response.StatusCode,
                callbackUrl,
                hostId,
                body);
        }

        Log.Information("ISAPI: Callback registration succeeded using XML event format");
        return ("success", "Attendance callback configured successfully using XML event format", (int)response.StatusCode, callbackUrl, hostId, body);
    }

    private async Task<string?> GetConfiguredCallbackEventFormatAsync(int hostId)
    {
        try
        {
            var response = await _httpClient.GetAsync(DeviceUrl($"/ISAPI/Event/notification/httpHosts/{hostId}"));
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            var document = XDocument.Parse(body);
            var parameterFormatType = document
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "parameterFormatType")
                ?.Value
                ?.Trim();

            if (string.Equals(parameterFormatType, "XML", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parameterFormatType, "JSON", StringComparison.OrdinalIgnoreCase))
            {
                return parameterFormatType?.ToUpperInvariant();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or System.Xml.XmlException)
        {
            Log.Debug(ex, "ISAPI: Could not read current callback host format");
        }

        return null;
    }

    private Task<(bool Success, string Status, string Message, int HttpStatusCode, string? RawStatus, string? RawResponse)> StartFingerprintCaptureAsync(
        string employeeNo,
        int fingerprintId,
        int[]? cardReaders,
        string? fingerprintData)
    {
        return SendFingerprintSetupRequestAsync(
            employeeNo,
            fingerprintId,
            cardReaders,
            fingerprintData,
            deleteFingerPrint: false);
    }

    private async Task<(bool Success, string Status, string Message, int HttpStatusCode, string? RawStatus, string? RawResponse)> SendFingerprintSetupRequestAsync(
        string employeeNo,
        int fingerprintId,
        int[]? cardReaders,
        string? fingerprintData,
        bool deleteFingerPrint)
    {
        try
        {
            var enableCardReader = (cardReaders is { Length: > 0 } ? cardReaders : [1])
                .Where(readerNo => readerNo > 0)
                .Distinct()
                .ToArray();

            var requestBody = new
            {
                FingerPrintCfg = new
                {
                    employeeNo,
                    enableCardReader,
                    fingerPrintID = fingerprintId,
                    fingerType = "normalFP",
                    fingerData = string.IsNullOrWhiteSpace(fingerprintData) ? null : fingerprintData,
                    checkEmployeeNo = true,
                    deleteFingerPrint = deleteFingerPrint
                }
            };

            var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            Log.Debug(
                "ISAPI: POST /ISAPI/AccessControl/FingerPrint/SetUp ({Mode}) with body: {Body}",
                deleteFingerPrint ? "delete" : "setup",
                json);

            var response = await _httpClient.PostAsync(
                DeviceUrl("/ISAPI/AccessControl/FingerPrint/SetUp?format=json"),
                content);

            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                Log.Warning("ISAPI: Fingerprint endpoint not supported on this device. HTTP {StatusCode}", response.StatusCode);
                return (false, "unsupported", "ISAPI fingerprint endpoint not supported by device", (int)response.StatusCode, null, null);
            }

            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log.Error("ISAPI: PUT failed. HTTP {StatusCode}", response.StatusCode);
                Log.Debug("ISAPI: Response body: {Body}", responseBody);
                Log.Debug("ISAPI: Request was: {Json}", json);
                return (
                    false,
                    MapHttpStatusToStatusCode((int)response.StatusCode, responseBody),
                    $"Failed to start capture. HTTP {response.StatusCode}. Response: {responseBody}",
                    (int)response.StatusCode,
                    null,
                    responseBody
                );
            }

            Log.Information("ISAPI: SetUp succeeded (HTTP {StatusCode})", response.StatusCode);
            Log.Debug("ISAPI: SetUp response body: {Body}", responseBody);

            var responseStatus = ExtractFingerprintStatus(responseBody);
            var successState = deleteFingerPrint ? "success" : "collecting";
            var successMessage = deleteFingerPrint
                ? "Fingerprint delete request accepted"
                : "Capture started, waiting for finger";

            return (true, successState, successMessage, 200, responseStatus, responseBody);
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "ISAPI: PUT request error");
            return (false, "failed", $"HTTP error: {ex.Message}", 0, null, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ISAPI: Unexpected error in StartCapture");
            return (false, "failed", $"Error: {ex.Message}", 0, null, null);
        }
    }

    /// <summary>
    /// Poll GET /ISAPI/AccessControl/FingerPrint/SetUpStatus to check capture status.
    /// </summary>
    private async Task<(string status, int errorCode, string? rawResponse)> PollFingerprintStatusAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync(
                DeviceUrl("/ISAPI/AccessControl/FingerPrint/SetUpStatus?format=json"));

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Log.Warning("ISAPI: GET status failed. HTTP {StatusCode}", response.StatusCode);
                Log.Debug("ISAPI: GET status response body: {Body}", body);
                return ("error_polling", (int)response.StatusCode, body);
            }

            var json = await response.Content.ReadAsStringAsync();
            Log.Debug("ISAPI: Status response: {Json}", json);

            // Parse response to extract status
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("FingerPrintSetUpStatus", out var status))
            {
                var statusStr = status.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown";
                var errorCode = status.TryGetProperty("errorCode", out var e) ? e.GetInt32() : 0;
                return (statusStr, errorCode, json);
            }

            Log.Warning("ISAPI: Unexpected response structure: {Json}", json);
            return ("error_polling", 0, json);
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "ISAPI: GET request error");
            return ("error_polling", 0, null);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "ISAPI: Failed to parse response");
            return ("error_polling", 0, null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ISAPI: Unexpected error in PollStatus");
            return ("error_polling", 0, null);
        }
    }

    /// <summary>
    /// Send DELETE /ISAPI/AccessControl/FingerPrint/SetUp to cancel capture and cleanup.
    /// </summary>
    private async Task CancelFingerprintCaptureAsync()
    {
        try
        {
            Log.Information("ISAPI: POST /ISAPI/AccessControl/FingerPrint/Cancel (cleanup)");
            var response = await _httpClient.PostAsync(
                DeviceUrl("/ISAPI/AccessControl/FingerPrint/Cancel?format=json"),
                new StringContent("", System.Text.Encoding.UTF8, "application/json"));

            if (response.IsSuccessStatusCode)
            {
                Log.Information("ISAPI: Cleanup successful (HTTP {StatusCode})", response.StatusCode);
            }
            else
            {
                Log.Warning("ISAPI: Cleanup returned HTTP {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ISAPI: Error during cleanup DELETE");
        }
    }

    private async Task<(string Status, string Message, int HttpStatusCode, string? RawResponse)> PollDeleteProcessAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            var response = await _httpClient.GetAsync(DeviceUrl("/ISAPI/AccessControl/UserInfoDetail/DeleteProcess?format=json"));
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return (
                    MapHttpStatusToStatusCode((int)response.StatusCode, responseBody),
                    $"Delete process check failed. HTTP {response.StatusCode}. Response: {responseBody}",
                    (int)response.StatusCode,
                    responseBody);
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            var status = root.TryGetProperty("UserInfoDetailDeleteProcess", out var processNode) &&
                         processNode.TryGetProperty("status", out var statusNode)
                ? statusNode.GetString() ?? "unknown"
                : "unknown";

            if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                return ("success", "Person delete completed", 200, responseBody);
            }

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return ("failed", "Person delete failed on device", 200, responseBody);
            }

            await Task.Delay(300);
        }

        return ("timeout", "Timed out waiting for person delete completion", 200, null);
    }

    private async Task<(string Status, string Message, int HttpStatusCode, string? RawResponse)> SendJsonRequestAsync(
        HttpMethod method,
        string path,
        object requestBody,
        string logLabel,
        bool logFailureAsWarning = true)
    {
        var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        var request = new HttpRequestMessage(method, new Uri(DeviceUrl(path)))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

        Log.Information("{LogLabel}: {Method} {Path}", logLabel, method, path);
        Log.Debug("{LogLabel}: {Method} {Path} body={Body}", logLabel, method, path, json);

        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            if (logFailureAsWarning)
            {
                Log.Warning("{LogLabel} failed. HTTP {StatusCode}", logLabel, response.StatusCode);
            }
            else
            {
                Log.Debug("{LogLabel} failed. HTTP {StatusCode}", logLabel, response.StatusCode);
            }

            Log.Debug("{LogLabel} response body: {Body}", logLabel, responseBody);

            return (
                MapHttpStatusToStatusCode((int)response.StatusCode, responseBody),
                $"{logLabel} failed. HTTP {response.StatusCode}. Response: {responseBody}",
                (int)response.StatusCode,
                responseBody);
        }

        return ("success", $"{logLabel} succeeded", (int)response.StatusCode, responseBody);
    }

    private static string BuildCallbackXml(string callbackUrl, int hostId, string parameterFormatType)
    {
        var protocol = callbackUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "HTTPS" : "HTTP";
        var host = ExtractHost(callbackUrl);
        var port = ExtractPort(callbackUrl);

        return $$"""
<?xml version="1.0" encoding="UTF-8"?>
<HttpHostNotification version="2.0" xmlns="http://www.isapi.org/ver20/XMLSchema">
  <id>{{hostId}}</id>
  <url>{{System.Security.SecurityElement.Escape(callbackUrl)}}</url>
  <protocolType>{{protocol}}</protocolType>
  <parameterFormatType>{{System.Security.SecurityElement.Escape(parameterFormatType)}}</parameterFormatType>
  <addressingFormatType>{{(System.Net.IPAddress.TryParse(host, out _) ? "ipaddress" : "hostname")}}</addressingFormatType>
  {{(System.Net.IPAddress.TryParse(host, out _) ? $"<ipAddress>{System.Security.SecurityElement.Escape(host)}</ipAddress>" : $"<hostName>{System.Security.SecurityElement.Escape(host)}</hostName>")}}
<portNo>{{port}}</portNo>
<userName></userName>
<httpAuthenticationMethod>none</httpAuthenticationMethod>
  <SubscribeEvent>
    <eventMode>all</eventMode>
  </SubscribeEvent>
</HttpHostNotification>
""";
    }

    private static string ExtractHost(string callbackUrl)
    {
        var uri = new Uri(callbackUrl);
        return uri.Host;
    }

    private static int ExtractPort(string callbackUrl)
    {
        var uri = new Uri(callbackUrl);
        return uri.IsDefaultPort
            ? (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
            : uri.Port;
    }

    private static string FormatDeviceDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz");
        }

        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToString("yyyy-MM-ddTHH:mm:sszzz")
            : value;
    }

    private static Dictionary<string, object?> ConvertJsonObject(JsonElement element)
    {
        return element.EnumerateObject()
            .ToDictionary(property => property.Name, property => ConvertJsonValue(property.Value));
    }

    private static object? ConvertJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ConvertJsonObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonValue).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue,
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? ReadJsonString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property)
            ? property.ToString()
            : null;
    }

    private static int? ReadJsonInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var intValue) => intValue,
            JsonValueKind.String when int.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }

    ~HikVisionIsapiClient()
    {
        Dispose();
    }

    private static string MapHttpStatusToStatusCode(int httpStatusCode, string? responseBody)
    {
        if (httpStatusCode == 400 || (responseBody?.Contains("parameter", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return "parameter_error";
        }

        return "failed";
    }

    private static string? ExtractFingerprintStatus(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("FingerPrintStatus", out var fingerPrintStatus) &&
                fingerPrintStatus.TryGetProperty("status", out var statusValue))
            {
                return statusValue.GetString();
            }

            if (root.TryGetProperty("FingerPrintSetUpStatus", out var fingerPrintSetupStatus) &&
                fingerPrintSetupStatus.TryGetProperty("status", out statusValue))
            {
                return statusValue.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
