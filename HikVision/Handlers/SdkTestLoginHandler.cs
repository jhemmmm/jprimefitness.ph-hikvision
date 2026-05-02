using Serilog;
using HikVision.Models;
using HikVision.Services;

namespace HikVision.Handlers;

public static class SdkTestLoginHandler
{
    public static SdkTestLoginResponse Handle(HikVisionSdkClient client)
    {
        var logger = Log.ForContext(typeof(SdkTestLoginHandler));

        try
        {
            logger.Information("Handling SDK test login request");
            var (success, errorCode, message) = client.TestLogin();

            return new SdkTestLoginResponse
            {
                Status = success ? "success" : "failed",
                Message = message,
                SdkErrorCode = (int)errorCode
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Unexpected error in test login handler");

            return new SdkTestLoginResponse
            {
                Status = "failed",
                Message = $"Unexpected error: {ex.Message}",
                SdkErrorCode = 0
            };
        }
        finally
        {
            client.Cleanup();
        }
    }
}
