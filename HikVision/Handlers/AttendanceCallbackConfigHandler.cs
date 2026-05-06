using HikVision.Config;
using HikVision.Models;
using HikVision.Services;
using Serilog;

namespace HikVision.Handlers;

public static class AttendanceCallbackConfigHandler
{
    public static string? ResolveCallbackUrl(HikVisionDeviceState deviceState, string? callbackUrl = null)
    {
        var rawCallbackUrl = string.IsNullOrWhiteSpace(callbackUrl)
            ? HelperPublicUrlResolver.ResolveCallbackUrl(deviceState.DeviceIp)
            : callbackUrl;

        if (string.IsNullOrWhiteSpace(rawCallbackUrl))
        {
            return null;
        }

        return NormalizeAbsoluteUrl(rawCallbackUrl);
    }

    private static string NormalizeAbsoluteUrl(string callbackUrl)
    {
        var uri = new Uri(callbackUrl);
        var normalizedPath = uri.AbsolutePath.Replace("//", "/");
        var builder = new UriBuilder(uri)
        {
            Path = normalizedPath
        };

        return builder.Uri.ToString().TrimEnd('/');
    }

    public static async Task<AttendanceCallbackConfigResponse> Handle(
        AttendanceCallbackConfigRequest request,
        HikVisionIsapiClient hikVisionIsapiClient,
        HikVisionDeviceState deviceState)
    {
        var logger = Log.ForContext(typeof(AttendanceCallbackConfigHandler));
        var callbackUrl = ResolveCallbackUrl(deviceState, request.CallbackUrl);

        if (string.IsNullOrWhiteSpace(callbackUrl))
        {
            return new AttendanceCallbackConfigResponse
            {
                Status = "failed",
                Message = "Callback URL is required. Set HikVision:CallbackPublicUrl or pass callbackUrl in the request."
            };
        }

        try
        {
            logger.Information("Configuring attendance callback to {CallbackUrl}", callbackUrl);

            var (status, message, httpStatusCode, _, hostId, rawResponse) =
                await hikVisionIsapiClient.ConfigureAttendanceCallbackAsync(callbackUrl, request.HostId);

            if (status == "success")
            {
                deviceState.SetCallbackUrl(callbackUrl);
            }

            return new AttendanceCallbackConfigResponse
            {
                Status = status,
                Message = message,
                HttpStatusCode = httpStatusCode,
                CallbackUrl = callbackUrl,
                HostId = hostId,
                RawResponse = rawResponse
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Unexpected error in attendance callback config handler");

            return new AttendanceCallbackConfigResponse
            {
                Status = "failed",
                Message = $"Unexpected error: {ex.Message}",
                CallbackUrl = callbackUrl
            };
        }
    }
}
