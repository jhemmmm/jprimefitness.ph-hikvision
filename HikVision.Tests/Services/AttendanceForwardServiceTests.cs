using System.Net;
using System.Text.Json;
using HikVision.Config;
using HikVision.Models;
using HikVision.Services;

namespace HikVision.Tests.Services;

public class AttendanceForwardServiceTests
{
    [Fact]
    public async Task ForwardAsync_sends_forward_token_in_biometric_header_only()
    {
        var handler = new CapturingHandler();
        var config = TestConfig();
        var state = new HikVisionDeviceState();
        var service = new AttendanceForwardService(new HttpClient(handler), config, state);

        var result = await service.ForwardAsync(new AttendanceForwardPayload
        {
            EmployeeNo = "0001",
            EventType = "AccessControllerEvent"
        });

        Assert.Equal("forwarded", result.Status);
        Assert.NotNull(handler.Request);
        Assert.True(handler.Request!.Headers.TryGetValues("X-Biometric-Token", out var values));
        Assert.Equal("forward-token", Assert.Single(values));
        Assert.False(handler.Request.Headers.Contains("X-Hikvision-Token"));
    }

    [Fact]
    public void NormalizePayload_returns_raw_fallback_for_non_json_callback()
    {
        var config = TestConfig();
        var state = new HikVisionDeviceState();
        state.SetDiscoveredDevice("192.168.1.50", "SERIAL-1", "terminal");
        var service = new AttendanceForwardService(new HttpClient(new CapturingHandler()), config, state);

        var payload = service.NormalizePayload(new AttendanceCallbackSnapshot
        {
            RemoteIpAddress = "192.168.1.50",
            Body = "<EventNotificationAlert",
            ReceivedAt = "2026-05-06T00:00:00Z"
        });

        Assert.NotNull(payload);
        Assert.Equal("UnparsedAttendanceCallback", payload!.EventType);
        Assert.Equal("<EventNotificationAlert", payload.RawBody);
        Assert.Equal("SERIAL-1", payload.DeviceSerial);
    }

    [Fact]
    public void NormalizePayload_reads_xml_access_controller_event()
    {
        var config = TestConfig();
        var state = new HikVisionDeviceState();
        state.SetDiscoveredDevice("192.168.1.50", "SERIAL-1", "terminal");
        var service = new AttendanceForwardService(new HttpClient(new CapturingHandler()), config, state);

        const string body = """
            <?xml version="1.0" encoding="UTF-8"?>
            <EventNotificationAlert version="2.0" xmlns="http://www.isapi.org/ver20/XMLSchema">
              <ipAddress>192.168.1.50</ipAddress>
              <dateTime>2026-05-06T01:02:03+08:00</dateTime>
              <eventType>AccessControllerEvent</eventType>
              <AccessControllerEvent>
                <employeeNoString>0001</employeeNoString>
                <serialNo>99</serialNo>
                <deviceName>Door</deviceName>
                <majorEventType>5</majorEventType>
                <subEventType>75</subEventType>
                <attendanceStatus>checkIn</attendanceStatus>
              </AccessControllerEvent>
            </EventNotificationAlert>
            """;

        var payload = service.NormalizePayload(new AttendanceCallbackSnapshot
        {
            Body = body,
            ReceivedAt = "2026-05-06T00:00:00Z"
        });

        Assert.NotNull(payload);
        Assert.Equal("0001", payload!.EmployeeNo);
        Assert.Equal(99, payload.EventSerialNo);
        Assert.Equal(5, payload.MajorEventType);
        Assert.Equal(75, payload.EventSubType);
        Assert.Equal("checkIn", payload.AttendanceStatus);
        Assert.Equal("SERIAL-1", payload.DeviceSerial);
        Assert.Null(payload.Raw);
        Assert.Equal(body, payload.RawBody);
    }

    [Fact]
    public void NormalizePayload_reads_json_access_controller_event()
    {
        var config = TestConfig();
        var state = new HikVisionDeviceState();
        state.SetDiscoveredDevice("192.168.1.50", "SERIAL-1", "terminal");
        var service = new AttendanceForwardService(new HttpClient(new CapturingHandler()), config, state);

        var body = JsonSerializer.Serialize(new
        {
            ipAddress = "192.168.1.50",
            eventType = "AccessControllerEvent",
            dateTime = "2026-05-06T01:02:03+08:00",
            AccessControllerEvent = new
            {
                employeeNoString = "0001",
                serialNo = 99,
                deviceName = "Door"
            }
        });

        var payload = service.NormalizePayload(new AttendanceCallbackSnapshot
        {
            Body = body,
            ReceivedAt = "2026-05-06T00:00:00Z"
        });

        Assert.NotNull(payload);
        Assert.Equal("0001", payload!.EmployeeNo);
        Assert.Equal(99, payload.EventSerialNo);
        Assert.Equal("SERIAL-1", payload.DeviceSerial);
        Assert.NotNull(payload.Raw);
    }

    private static HikVisionConfig TestConfig()
    {
        return new HikVisionConfig
        {
            Username = "admin",
            Password = "password",
            ForwardUrl = "https://example.test/callback",
            ForwardToken = "forward-token"
        };
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}")
            });
        }
    }
}
