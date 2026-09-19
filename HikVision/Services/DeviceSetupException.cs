namespace HikVision.Services;

/// <summary>
/// A startup problem the operator must fix (e.g. the device is on another subnet and needs its IP changed).
/// The message is actionable guidance; the top-level handler logs it without a stack trace.
/// </summary>
public sealed class DeviceSetupException : Exception
{
    public DeviceSetupException(string message) : base(message)
    {
    }
}
