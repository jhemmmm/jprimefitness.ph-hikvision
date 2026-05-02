using HikVision.Models;
using HikVision.Services;
using Serilog;

namespace HikVision.Handlers;

public static class AttendanceSearchHandler
{
    public static async Task<AttendanceSearchResponse> Handle(
        AttendanceSearchRequest request,
        HikVisionIsapiClient hikVisionIsapiClient)
    {
        var logger = Log.ForContext(typeof(AttendanceSearchHandler));

        try
        {
            var endTime = request.EndTime ?? DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
            var startTime = request.StartTime ?? DateTimeOffset.Now.AddHours(-12).ToString("yyyy-MM-ddTHH:mm:sszzz");

            logger.Information(
                "Handling attendance search from {StartTime} to {EndTime} for employee {EmployeeNo}",
                startTime,
                endTime,
                request.EmployeeNo ?? "*");

            var (status, message, httpStatusCode, events, numOfMatches, totalMatches, rawResponse) =
                await hikVisionIsapiClient.SearchAttendanceEventsAsync(
                    startTime,
                    endTime,
                    request.Position,
                    request.MaxResults,
                    request.EmployeeNo,
                    request.Major,
                    request.Minor,
                    request.EventAttribute);

            return new AttendanceSearchResponse
            {
                Status = status,
                Message = message,
                HttpStatusCode = httpStatusCode,
                NumOfMatches = numOfMatches,
                TotalMatches = totalMatches,
                Events = events,
                RawResponse = rawResponse
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Unexpected error in attendance search handler");

            return new AttendanceSearchResponse
            {
                Status = "failed",
                Message = $"Unexpected error: {ex.Message}"
            };
        }
    }
}
