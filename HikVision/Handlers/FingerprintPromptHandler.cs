using Serilog;
using HikVision.Config;
using HikVision.Models;
using HikVision.Services;

namespace HikVision.Handlers;

public static class FingerprintPromptHandler
{
    public static async Task<FingerprintPromptResponse> Handle(
        FingerprintPromptRequest request,
        HikVisionSdkClient hikVisionSdkClient,
        HikVisionIsapiClient hikVisionIsapiClient,
        HikVisionConfig config)
    {
        var logger = Log.ForContext(typeof(FingerprintPromptHandler));
        var timeoutSeconds = request.TimeoutSeconds ?? config.TimeoutSeconds;
        var includeDebug = request.IncludeDebug ?? config.IncludeDebugInResponses;

        try
        {
            logger.Information(
                "Handling fingerprint prompt request for employee {EmployeeNo}, fingerprint {FingerprintId}, timeout {Timeout}s",
                request.EmployeeNo,
                request.FingerprintId,
                timeoutSeconds);

            var (loginSuccess, loginErrorCode, loginMessage) = hikVisionSdkClient.TestLogin();

            if (!loginSuccess)
            {
                return new FingerprintPromptResponse
                {
                    Status = "failed",
                    Message = loginMessage,
                    SdkErrorCode = (int)loginErrorCode,
                    EmployeeNo = request.EmployeeNo,
                    FingerprintId = request.FingerprintId
                };
            }

            var result =
                await hikVisionSdkClient.StartRemoteFingerprintCaptureAsync(
                    request.EmployeeNo,
                    request.FingerprintId,
                    timeoutSeconds);

            var fingerprintDataBase64 = result.Success && result.FingerprintData is not null
                ? Convert.ToBase64String(result.FingerprintData)
                : null;

            var response = new FingerprintPromptResponse
            {
                Status = result.Status,
                Message = result.Message,
                SdkErrorCode = (int)result.ErrorCode,
                EmployeeNo = request.EmployeeNo,
                FingerprintId = request.FingerprintId,
                FingerprintDataSize = result.Success ? result.FingerprintDataSize : null,
                FingerprintQuality = result.Success ? result.Quality : null,
                FingerprintDataBase64 = result.Success && request.IncludeFingerprintData
                    ? fingerprintDataBase64
                    : null,
                Debug = includeDebug ? result.Debug : null
            };

            if (!result.Success || !request.SaveToDevice)
            {
                return response;
            }

            if (string.IsNullOrWhiteSpace(fingerprintDataBase64))
            {
                response.Status = "save_failed";
                response.Message = "Fingerprint was captured, but the helper did not receive template data to save.";
                response.SaveStatus = "failed";
                response.SaveMessage = "Captured fingerprint did not include template data.";
                return response;
            }

            var (saveStatus, saveMessage, saveHttpStatusCode, rawStatus, rawResponse) =
                await hikVisionIsapiClient.SaveFingerprintAsync(
                    request.EmployeeNo,
                    request.FingerprintId,
                    request.CardReaders,
                    fingerprintDataBase64);

            response.SavedToDevice = saveStatus == "success";
            response.SaveStatus = rawStatus ?? saveStatus;
            response.SaveMessage = saveMessage;
            response.SaveHttpStatusCode = saveHttpStatusCode;
            response.SaveRawResponse = rawResponse;

            if (!response.SavedToDevice)
            {
                response.Status = "save_failed";
                response.Message = $"Fingerprint was captured, but saving to device failed: {saveMessage}";
            }
            else
            {
                response.Message = "Fingerprint captured and saved to device successfully.";
            }

            return response;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Unexpected error in fingerprint prompt handler");

            return new FingerprintPromptResponse
            {
                Status = "failed",
                Message = $"Unexpected error: {ex.Message}",
                SdkErrorCode = 0,
                EmployeeNo = request.EmployeeNo,
                FingerprintId = request.FingerprintId
            };
        }
        finally
        {
            hikVisionSdkClient.Cleanup();
        }
    }
}
