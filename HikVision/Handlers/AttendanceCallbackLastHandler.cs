using HikVision.Models;
using HikVision.Services;

namespace HikVision.Handlers;

public static class AttendanceCallbackLastHandler
{
    public static AttendanceCallbackLastResponse Handle(AttendanceCallbackProbeService attendanceCallbackProbeService)
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
            Callback = callback
        };
    }
}
