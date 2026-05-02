using System.Text;
using HikVision.Models;
using HikVision.Services;
using Serilog;

namespace HikVision.Handlers;

public static class AttendanceCallbackReceiveHandler
{
    public static async Task<IResult> Handle(
        HttpRequest request,
        AttendanceCallbackProbeService attendanceCallbackProbeService,
        AttendanceForwardService attendanceForwardService)
    {
        var logger = Log.ForContext(typeof(AttendanceCallbackReceiveHandler));

        request.EnableBuffering();

        string body;

        using (var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync();
            request.Body.Position = 0;
        }

        var snapshot = new AttendanceCallbackSnapshot
        {
            Method = request.Method,
            Path = request.Path.Value ?? string.Empty,
            QueryString = request.QueryString.HasValue ? request.QueryString.Value : null,
            ContentType = request.ContentType,
            RemoteIpAddress = request.HttpContext.Connection.RemoteIpAddress?.ToString(),
            Headers = request.Headers.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToString(),
                StringComparer.OrdinalIgnoreCase),
            Body = body,
            ReceivedAt = DateTime.UtcNow.ToString("O")
        };

        snapshot.NormalizedEvent = attendanceForwardService.NormalizePayload(snapshot);
        snapshot.ForwardResult = await attendanceForwardService.ForwardAsync(snapshot.NormalizedEvent);
        snapshot = attendanceCallbackProbeService.Record(snapshot);

        logger.Information(
            "Received helper attendance callback #{CallbackCount} from {RemoteIpAddress} {Method} {Path}{QueryString} employee={EmployeeNo} serial={SerialNo} forwardStatus={ForwardStatus} body={Body}",
            snapshot.CallbackCount,
            snapshot.RemoteIpAddress ?? "unknown",
            snapshot.Method,
            snapshot.Path,
            snapshot.QueryString ?? string.Empty,
            snapshot.NormalizedEvent?.EmployeeNo ?? string.Empty,
            snapshot.NormalizedEvent?.EventSerialNo,
            snapshot.ForwardResult?.Status ?? "skipped",
            string.IsNullOrWhiteSpace(snapshot.Body) ? "<empty>" : snapshot.Body);

        return Results.Json(new AttendanceCallbackReceiveResponse
        {
            CallbackCount = snapshot.CallbackCount,
            Callback = snapshot
        });
    }
}
