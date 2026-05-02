using Serilog;
using HikVision.Models;
using HikVision.Services;

namespace HikVision.Handlers;

public static class SdkAcsAbilityHandler
{
    public static async Task<AcsAbilityResponse> Handle(
        HikVisionSdkClient client,
        HikVisionIsapiClient hikVisionIsapiClient)
    {
        var logger = Log.ForContext(typeof(SdkAcsAbilityHandler));

        try
        {
            logger.Information("Handling SDK ACS ability request");
            var (success, errorCode, message, abilityXml, supportsCaptureFingerprint) = client.GetAccessControlAbility();

            if (!success)
            {
                logger.Warning("SDK ACS ability request failed with error {ErrorCode}. Falling back to ISAPI /ISAPI/AccessControl/capabilities.", errorCode);

                var isapiXml = await hikVisionIsapiClient.GetAccessControlCapabilitiesXmlAsync();
                var isapiSupportsCaptureFingerprint =
                    isapiXml.Contains("<isSupportCaptureFingerPrint>true</isSupportCaptureFingerPrint>", StringComparison.OrdinalIgnoreCase);

                return new AcsAbilityResponse
                {
                    Status = "success",
                    Message = $"SDK ability failed with error {errorCode}; returned ISAPI access-control capabilities instead.",
                    SdkErrorCode = (int)errorCode,
                    AbilityXml = isapiXml,
                    SupportsCaptureFingerPrint = isapiSupportsCaptureFingerprint,
                    Source = "isapi-fallback"
                };
            }

            return new AcsAbilityResponse
            {
                Status = success ? "success" : "failed",
                Message = message,
                SdkErrorCode = (int)errorCode,
                AbilityXml = abilityXml,
                SupportsCaptureFingerPrint = supportsCaptureFingerprint,
                Source = "sdk"
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Unexpected error in ACS ability handler");

            return new AcsAbilityResponse
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
