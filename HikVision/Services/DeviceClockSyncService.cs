namespace HikVision.Services;

/// <summary>
/// Keeps the terminal clock on the PC's (Windows/NTP-synced) time: once at startup, then hourly.
/// The terminal has no NTP of its own on the gym LAN, so without this its RTC drifts by minutes.
/// </summary>
public class DeviceClockSyncService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(5);

    private readonly HikVisionIsapiClient _isapiClient;

    public DeviceClockSyncService(HikVisionIsapiClient isapiClient)
    {
        _isapiClient = isapiClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            await _isapiClient.SyncDeviceClockAsync(Tolerance);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
