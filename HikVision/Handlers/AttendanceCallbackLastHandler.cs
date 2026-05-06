using HikVision.Models;
using HikVision.Services;
using HikVision.Config;

namespace HikVision.Handlers;

public static class AttendanceCallbackLastHandler
{
    public static AttendanceCallbackLastResponse Handle(
        AttendanceCallbackProbeService attendanceCallbackProbeService,
        HikVisionConfig config)
    {
        var callback = attendanceCallbackProbeService.GetLastCallback();

        if (callback is null)
        {
            return new AttendanceCallbackLastResponse();
        }

        return new AttendanceCallbackLastResponse
        {
            Status = "success",
            Message = "Last attendance callback retrieved successfully.",
            Callback = config.Debug ? callback : RedactDebugFields(callback)
        };
    }

    private static AttendanceCallbackSnapshot RedactDebugFields(AttendanceCallbackSnapshot callback)
    {
        return new AttendanceCallbackSnapshot
        {
            CallbackCount = callback.CallbackCount,
            Method = callback.Method,
            Path = callback.Path,
            QueryString = null,
            ContentType = callback.ContentType,
            RemoteIpAddress = callback.RemoteIpAddress,
            Headers = [],
            Body = null,
            ReceivedAt = callback.ReceivedAt,
            NormalizedEvent = callback.NormalizedEvent is null
                ? null
                : new AttendanceForwardPayload
                {
                    DeviceIp = callback.NormalizedEvent.DeviceIp,
                    DeviceName = callback.NormalizedEvent.DeviceName,
                    DeviceSerial = callback.NormalizedEvent.DeviceSerial,
                    EmployeeNo = callback.NormalizedEvent.EmployeeNo,
                    EventSerialNo = callback.NormalizedEvent.EventSerialNo,
                    EventType = callback.NormalizedEvent.EventType,
                    EventSubType = callback.NormalizedEvent.EventSubType,
                    MajorEventType = callback.NormalizedEvent.MajorEventType,
                    OccurredAt = callback.NormalizedEvent.OccurredAt,
                    CardReaderNo = callback.NormalizedEvent.CardReaderNo,
                    DoorNo = callback.NormalizedEvent.DoorNo,
                    UserType = callback.NormalizedEvent.UserType,
                    AttendanceStatus = callback.NormalizedEvent.AttendanceStatus
                },
            ForwardResult = callback.ForwardResult is null
                ? null
                : new AttendanceForwardResult
                {
                    Attempted = callback.ForwardResult.Attempted,
                    Status = callback.ForwardResult.Status,
                    Message = callback.ForwardResult.Message,
                    HttpStatusCode = callback.ForwardResult.HttpStatusCode
                }
        };
    }
}
