using System.Net.Http.Json;
using System.Text.Json;
using HikVision.Config;
using HikVision.Models;
using Serilog;

namespace HikVision.Services;

public class AttendanceForwardService
{
    private readonly HttpClient _httpClient;
    private readonly HikVisionConfig _config;

    public AttendanceForwardService(HttpClient httpClient, HikVisionConfig config)
    {
        _httpClient = httpClient;
        _config = config;
    }

    public async Task<AttendanceForwardResult> ForwardAsync(AttendanceForwardPayload? payload)
    {
        if (payload is null)
        {
            return new AttendanceForwardResult
            {
                Attempted = false,
                Status = "skipped",
                Message = "No normalized attendance payload was available to forward."
            };
        }

        if (string.IsNullOrWhiteSpace(_config.ForwardUrl))
        {
            return new AttendanceForwardResult
            {
                Attempted = false,
                Status = "skipped",
                Message = "ForwardUrl is not configured."
            };
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _config.ForwardUrl)
            {
                Content = JsonContent.Create(payload)
            };

            if (!string.IsNullOrWhiteSpace(_config.ForwardToken))
            {
                request.Headers.Add("X-Hikvision-Token", _config.ForwardToken);
            }

            request.Headers.Add("X-Hikvision-Forwarded-By", "windows-helper");

            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            var status = response.IsSuccessStatusCode ? "forwarded" : "failed";
            var message = response.IsSuccessStatusCode
                ? "Attendance callback forwarded successfully."
                : $"ForwardUrl returned HTTP {(int) response.StatusCode}.";

            Log.Information(
                "Forwarded attendance callback to {ForwardUrl}. status={Status} httpStatusCode={HttpStatusCode}",
                _config.ForwardUrl,
                status,
                (int) response.StatusCode);

            return new AttendanceForwardResult
            {
                Attempted = true,
                Status = status,
                Message = message,
                HttpStatusCode = (int) response.StatusCode,
                ResponseBody = string.IsNullOrWhiteSpace(responseBody) ? null : responseBody
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to forward attendance callback to {ForwardUrl}", _config.ForwardUrl);

            return new AttendanceForwardResult
            {
                Attempted = true,
                Status = "failed",
                Message = ex.Message
            };
        }
    }

    public AttendanceForwardPayload? NormalizePayload(AttendanceCallbackSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(snapshot.Body);
            var root = document.RootElement;
            var accessControllerEvent = root.TryGetProperty("AccessControllerEvent", out var accessEvent)
                ? accessEvent
                : default;

            return new AttendanceForwardPayload
            {
                DeviceIp = ReadString(root, "ipAddress") ?? snapshot.RemoteIpAddress,
                DeviceName = ReadString(accessControllerEvent, "deviceName"),
                DeviceSerial = _config.DeviceSerial,
                EmployeeNo = ReadString(accessControllerEvent, "employeeNoString") ?? ReadString(accessControllerEvent, "employeeNo"),
                EventSerialNo = ReadInt(accessControllerEvent, "serialNo"),
                EventType = ReadString(root, "eventType") ?? "AccessControllerEvent",
                EventSubType = ReadInt(accessControllerEvent, "subEventType"),
                MajorEventType = ReadInt(accessControllerEvent, "majorEventType"),
                OccurredAt = ReadString(root, "dateTime") ?? snapshot.ReceivedAt,
                CardReaderNo = ReadInt(accessControllerEvent, "cardReaderNo"),
                DoorNo = ReadInt(accessControllerEvent, "doorNo"),
                UserType = ReadString(accessControllerEvent, "userType"),
                AttendanceStatus = ReadString(accessControllerEvent, "attendanceStatus"),
                Raw = root.Clone()
            };
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Unable to normalize attendance callback body as JSON");
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => value.ToString()
        };
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
