using Serilog;
using HikVision.Config;
using HikVision.Models;
using HikVision.Services;

namespace HikVision.Handlers;

public static class FingerprintDeleteHandler
{
    public static async Task<FingerprintDeleteResponse> Handle(
        FingerprintPromptRequest request,
        HikVisionIsapiClient hikVisionIsapiClient,
        HikVisionConfig config)
    {
        var logger = Log.ForContext(typeof(FingerprintDeleteHandler));

        try
        {
            logger.Information(
                "Handling fingerprint delete request for employee {EmployeeNo}, fingerprint {FingerprintId}",
                request.EmployeeNo,
                request.FingerprintId);

            var (status, message, httpStatusCode, rawStatus, rawResponse) =
                await hikVisionIsapiClient.DeleteFingerprintAsync(
                    request.EmployeeNo,
                    request.FingerprintId,
                    request.CardReaders);

            return new FingerprintDeleteResponse
            {
                Status = status,
                Message = message,
                HttpStatusCode = httpStatusCode,
                EmployeeNo = request.EmployeeNo,
                FingerprintId = request.FingerprintId,
                DeleteStatus = rawStatus,
                RawResponse = rawResponse
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Unexpected error in fingerprint delete handler");

            return new FingerprintDeleteResponse
            {
                Status = "failed",
                Message = $"Unexpected error: {ex.Message}",
                EmployeeNo = request.EmployeeNo,
                FingerprintId = request.FingerprintId
            };
        }
    }
}
