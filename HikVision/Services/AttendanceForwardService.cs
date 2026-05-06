using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using HikVision.Config;
using HikVision.Models;
using Serilog;

namespace HikVision.Services;

public class AttendanceForwardService
{
    private readonly HttpClient _httpClient;
    private readonly HikVisionConfig _config;
    private readonly HikVisionDeviceState _deviceState;

    public AttendanceForwardService(HttpClient httpClient, HikVisionConfig config, HikVisionDeviceState deviceState)
    {
        _httpClient = httpClient;
        _config = config;
        _deviceState = deviceState;
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

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _config.ForwardUrl)
            {
                Content = JsonContent.Create(payload)
            };

            if (!string.IsNullOrWhiteSpace(_config.ForwardToken))
            {
                request.Headers.Add("X-Biometric-Token", _config.ForwardToken);
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
                DeviceSerial = _deviceState.DeviceSerial,
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
                Raw = root.Clone(),
                RawBody = snapshot.Body
            };
        }
        catch (JsonException)
        {
            var xmlPayload = NormalizeXmlPayload(snapshot);
            if (xmlPayload is not null)
            {
                return xmlPayload;
            }

            Log.Warning("Unable to normalize attendance callback body as JSON or XML");
            return new AttendanceForwardPayload
            {
                DeviceIp = snapshot.RemoteIpAddress,
                DeviceSerial = _deviceState.DeviceSerial,
                EventType = "UnparsedAttendanceCallback",
                OccurredAt = snapshot.ReceivedAt,
                RawBody = snapshot.Body
            };
        }
    }

    private AttendanceForwardPayload? NormalizeXmlPayload(AttendanceCallbackSnapshot snapshot)
    {
        try
        {
            var document = XDocument.Parse(snapshot.Body!);
            var root = document.Root;
            if (root is null)
            {
                return null;
            }

            var accessControllerEvent = root
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "AccessControllerEvent");

            return new AttendanceForwardPayload
            {
                DeviceIp = ReadXmlString(root, "ipAddress") ?? snapshot.RemoteIpAddress,
                DeviceName = ReadXmlString(accessControllerEvent, "deviceName"),
                DeviceSerial = _deviceState.DeviceSerial,
                EmployeeNo = ReadXmlString(accessControllerEvent, "employeeNoString") ?? ReadXmlString(accessControllerEvent, "employeeNo"),
                EventSerialNo = ReadXmlInt(accessControllerEvent, "serialNo"),
                EventType = ReadXmlString(root, "eventType") ?? "AccessControllerEvent",
                EventSubType = ReadXmlInt(accessControllerEvent, "subEventType"),
                MajorEventType = ReadXmlInt(accessControllerEvent, "majorEventType"),
                OccurredAt = ReadXmlString(root, "dateTime") ?? snapshot.ReceivedAt,
                CardReaderNo = ReadXmlInt(accessControllerEvent, "cardReaderNo"),
                DoorNo = ReadXmlInt(accessControllerEvent, "doorNo"),
                UserType = ReadXmlString(accessControllerEvent, "userType"),
                AttendanceStatus = ReadXmlString(accessControllerEvent, "attendanceStatus"),
                RawBody = snapshot.Body
            };
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
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

    private static string? ReadXmlString(XElement? element, string localName)
    {
        return element?
            .Descendants()
            .FirstOrDefault(child => child.Name.LocalName == localName)?
            .Value
            .Trim();
    }

    private static int? ReadXmlInt(XElement? element, string localName)
    {
        var value = ReadXmlString(element, localName);
        return int.TryParse(value, out var parsed) ? parsed : null;
    }
}
