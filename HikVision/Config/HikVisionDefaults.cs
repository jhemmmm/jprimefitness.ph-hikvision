namespace HikVision.Config;

public static class HikVisionDefaults
{
    public const int HttpPort = 80;
    public const int SdkPort = 8000;
    public const int RequestTimeoutSeconds = 30;
    public const string BindUrl = "http://0.0.0.0:5077";
    public const string CallbackPath = "/attendance/callback";
    public const int CallbackHostId = 2;
}
