using Serilog;
using HikVision.Config;
using HikVision.Models;
using HikVision.Services;

namespace HikVision.Handlers;

public static class IsapiFingerprintSetupHandler
{
    public static async Task<IsapiFingerprintSetupResponse> Handle(
        FingerprintPromptRequest request,
        HikVisionIsapiClient hikVisionIsapiClient,
        HikVisionConfig config)
    {
        var logger = Log.ForContext(typeof(IsapiFingerprintSetupHandler));
        var timeoutSeconds = request.TimeoutSeconds ?? HikVision.Config.HikVisionDefaults.RequestTimeoutSeconds;

        try
        {
            logger.Information(
                "Handling ISAPI fingerprint setup request for employee {EmployeeNo}, fingerprint {FingerprintId}, timeout {Timeout}s",
                request.EmployeeNo,
                request.FingerprintId,
                timeoutSeconds);

            var (status, message, httpStatusCode, rawStatus, rawResponse) =
                await hikVisionIsapiClient.SetupFingerprintAsync(
                    request.EmployeeNo,
                    request.FingerprintId,
                    request.CardReaders,
                    request.FingerprintData,
                    timeoutSeconds);

            return new IsapiFingerprintSetupResponse
            {
                Status = status,
                Message = message,
                HttpStatusCode = httpStatusCode,
                EmployeeNo = request.EmployeeNo,
                FingerprintId = request.FingerprintId,
                SetupStatus = rawStatus,
                RawResponse = rawResponse
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Unexpected error in ISAPI fingerprint setup handler");

            return new IsapiFingerprintSetupResponse
            {
                Status = "failed",
                Message = $"Unexpected error: {ex.Message}",
                EmployeeNo = request.EmployeeNo,
                FingerprintId = request.FingerprintId
            };
        }
    }
}
