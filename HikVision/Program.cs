using Serilog;
using HikVision.Config;
using HikVision.Services;
using HikVision.Models;
using HikVision.Handlers;

var builder = WebApplication.CreateBuilder(args);

// Map short CLI argument names to their full configuration keys.
// Usage examples:
//   --ip 192.168.250.201
//   --username admin --password secret
//   --bind http://0.0.0.0:5077
//   --forward-url http://laravel.local/hikvision/callback
builder.Configuration.AddCommandLine(args, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["--ip"]               = "HikVision:DeviceIp",
    ["--serial"]           = "HikVision:DeviceSerial",
    ["--username"]         = "HikVision:Username",
    ["-u"]                 = "HikVision:Username",
    ["--password"]         = "HikVision:Password",
    ["-p"]                 = "HikVision:Password",
    ["--http-port"]        = "HikVision:HttpPort",
    ["--sdk-port"]         = "HikVision:SdkPort",
    ["--timeout"]          = "HikVision:TimeoutSeconds",
    ["--bind"]             = "HikVision:BindUrl",
    ["--callback-path"]    = "HikVision:CallbackPath",
    ["--callback-token"]   = "HikVision:CallbackToken",
    ["--callback-host-id"] = "HikVision:CallbackHostId",
    ["--forward-url"]      = "HikVision:ForwardUrl",
    ["--forward-token"]    = "HikVision:ForwardToken",
});

var logsPath = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logsPath);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(
        Path.Combine(logsPath, "hikvision-helper-.txt"),
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    builder.Logging.ClearProviders();
    builder.Host.UseSerilog();

    var config = builder.Configuration.GetSection("HikVision").Get<HikVisionConfig>()
        ?? throw new InvalidOperationException("HikVision configuration not found in appsettings.json");

    // Auto-discover DeviceIp via SADP UDP broadcast if not set in configuration
    if (string.IsNullOrWhiteSpace(config.DeviceIp))
    {
        Log.Information("DeviceIp not configured — starting SADP auto-discovery...");
        var discovered = await HikVision.Services.HikVisionDeviceDiscovery.DiscoverDevicesAsync();

        if (discovered.Count == 0)
        {
            Log.Warning(
                "SADP: No Hikvision devices were found on the network. " +
                "Set DeviceIp in appsettings.json to connect to a specific device.");
        }
        else
        {
            if (discovered.Count > 1)
            {
                Log.Warning(
                    "SADP: Multiple devices found ({Count}). Using the first one: {IP}. " +
                    "Set DeviceIp in appsettings.json to choose a specific device.",
                    discovered.Count,
                    discovered[0].IpAddress);
            }

            config.DeviceIp = discovered[0].IpAddress;

            if (string.IsNullOrWhiteSpace(config.DeviceSerial) && discovered[0].SerialNumber is not null)
            {
                config.DeviceSerial = discovered[0].SerialNumber;
            }

            Log.Information("SADP: Using device IP: {IP}", config.DeviceIp);
        }
    }

    // Auto-fetch DeviceSerial from the device via ISAPI if not set (only if DeviceIp is known)
    if (string.IsNullOrWhiteSpace(config.DeviceSerial) && !string.IsNullOrWhiteSpace(config.DeviceIp))
    {
        Log.Information("DeviceSerial not configured — fetching from device at {IP}...", config.DeviceIp);
        try
        {
            using var tempClient = new HikVision.Services.HikVisionIsapiClient(config);
            var (success, serial, deviceType, firmware) = await tempClient.GetDeviceInfoAsync();

            if (success && !string.IsNullOrWhiteSpace(serial))
            {
                config.DeviceSerial = serial;
                Log.Information("Auto-detected device — Serial: {Serial}, Type: {Type}, Firmware: {Firmware}",
                    serial, deviceType, firmware);
            }
            else
            {
                Log.Warning("Could not auto-fetch DeviceSerial from device. Will retry after first callback.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch device info from {IP}", config.DeviceIp);
        }
    }

    Log.Information("Device configured — IP: {IP}, Serial: {Serial}", config.DeviceIp, config.DeviceSerial);

    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton<HikVisionSdkClient>();
    builder.Services.AddSingleton<HikVisionIsapiClient>();
    builder.Services.AddSingleton<AttendanceCallbackProbeService>();
    builder.Services.AddSingleton<DeviceAutoConfigService>();
    builder.Services.AddHttpClient<AttendanceForwardService>();

    var app = builder.Build();

    app.MapGet("/health", HealthHandler.Handle)
        .WithName("Health")
        .Produces<HealthResponse>();

    app.MapPost("/sdk/test-login", SdkTestLoginHandler.Handle)
        .WithName("SDK Test Login")
        .Produces<SdkTestLoginResponse>();

    app.MapGet("/sdk/acs-ability", SdkAcsAbilityHandler.Handle)
        .WithName("SDK ACS Ability")
        .Produces<AcsAbilityResponse>();

    app.MapPost("/fingerprint/enroll", FingerprintPromptHandler.Handle)
        .WithName("Fingerprint Enroll")
        .Produces<FingerprintPromptResponse>();

    app.MapPost("/fingerprint/delete", FingerprintDeleteHandler.Handle)
        .WithName("Fingerprint Delete")
        .Produces<FingerprintDeleteResponse>();

    app.MapPost("/isapi/fingerprint/delete", FingerprintDeleteHandler.Handle)
        .WithName("ISAPI Fingerprint Delete")
        .Produces<FingerprintDeleteResponse>();

    app.MapPost("/isapi/fingerprint/setup", IsapiFingerprintSetupHandler.Handle)
        .WithName("ISAPI Fingerprint Setup")
        .Produces<IsapiFingerprintSetupResponse>();

    app.MapPost("/persons/upsert", PersonUpsertHandler.Handle)
        .WithName("Person Upsert")
        .Produces<PersonResponse>();

    app.MapPost("/persons/search", PersonSearchHandler.Handle)
        .WithName("Person Search")
        .Produces<PersonResponse>();

    app.MapPost("/persons/delete", PersonDeleteHandler.Handle)
        .WithName("Person Delete")
        .Produces<PersonResponse>();

    app.MapPost("/attendance/search", AttendanceSearchHandler.Handle)
        .WithName("Attendance Search")
        .Produces<AttendanceSearchResponse>();

    app.MapPost("/attendance/callback", AttendanceCallbackReceiveHandler.Handle)
        .WithName("Attendance Callback Receive")
        .Produces<AttendanceCallbackReceiveResponse>();

    app.MapGet("/attendance/callback/last", AttendanceCallbackLastHandler.Handle)
        .WithName("Attendance Callback Last")
        .Produces<AttendanceCallbackLastResponse>();

    app.MapPost("/attendance/callback/configure", AttendanceCallbackConfigHandler.Handle)
        .WithName("Attendance Callback Configure")
        .Produces<AttendanceCallbackConfigResponse>();

    var hikVisionIsapiClient = app.Services.GetRequiredService<HikVisionIsapiClient>();
    var callbackUrl = AttendanceCallbackConfigHandler.ResolveCallbackUrl(config);

    if (!string.IsNullOrWhiteSpace(callbackUrl) && !string.IsNullOrWhiteSpace(config.DeviceIp))
    {
        try
        {
            Log.Information("Auto-configuring Hikvision attendance callback to {CallbackUrl}", callbackUrl);

            var (status, message, httpStatusCode, _, hostId, _) =
                await hikVisionIsapiClient.ConfigureAttendanceCallbackAsync(callbackUrl, config.CallbackHostId);

            Log.Information(
                "Auto-configure attendance callback result: status={Status}, hostId={HostId}, httpStatusCode={HttpStatusCode}, message={Message}",
                status,
                hostId,
                httpStatusCode,
                message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to auto-configure Hikvision attendance callback");
        }
    }
    else
    {
        if (string.IsNullOrWhiteSpace(config.DeviceIp))
            Log.Information("Skipping attendance callback auto-configuration — DeviceIp not yet known. Will register after first callback.");
        else
            Log.Information("Skipping attendance callback auto-configuration because no reachable callback URL could be resolved");
    }

    Log.Information("Hikvision Helper starting on {BindUrl}", config.BindUrl);
    await app.RunAsync(config.BindUrl);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
