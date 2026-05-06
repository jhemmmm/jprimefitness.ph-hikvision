using Serilog;
using Serilog.Events;
using HikVision.Config;
using HikVision.Services;
using HikVision.Models;
using HikVision.Handlers;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddCommandLine(args, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["--username"] = "HikVision:Username",
    ["-u"] = "HikVision:Username",
    ["--password"] = "HikVision:Password",
    ["-p"] = "HikVision:Password",
    ["--forward-url"] = "HikVision:ForwardUrl",
    ["--forward-token"] = "HikVision:ForwardToken",
    ["--debug"] = "HikVision:Debug"
});

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var config = HikVisionConfigValidator.LoadAndValidate(builder.Configuration);
    ConfigureLogging(builder, config);

    RegisterServices(builder.Services, config);

    var app = builder.Build();
    MapEndpoints(app);

    await InitializeDeviceAsync(app.Services);

    Log.Information("Hikvision Helper starting on {BindUrl}", HikVisionDefaults.BindUrl);
    await app.RunAsync(HikVisionDefaults.BindUrl);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

static void ConfigureLogging(WebApplicationBuilder builder, HikVisionConfig config)
{
    var logsPath = Path.Combine(AppContext.BaseDirectory, "logs");
    Directory.CreateDirectory(logsPath);

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Is(config.Debug ? LogEventLevel.Debug : LogEventLevel.Information)
        .WriteTo.File(
            Path.Combine(logsPath, "hikvision-helper-.txt"),
            rollingInterval: RollingInterval.Day,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();

    builder.Logging.ClearProviders();
    builder.Host.UseSerilog();
}

static void RegisterServices(IServiceCollection services, HikVisionConfig config)
{
    services.AddSingleton(config);
    services.AddSingleton<HikVisionDeviceState>();
    services.AddSingleton<HikVisionSdkClient>();
    services.AddSingleton<HikVisionIsapiClient>();
    services.AddSingleton<AttendanceCallbackProbeService>();
    services.AddSingleton<DeviceAutoConfigService>();
    services.AddHttpClient<AttendanceForwardService>();
}

static void MapEndpoints(WebApplication app)
{
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
}

static async Task InitializeDeviceAsync(IServiceProvider services)
{
    var config = services.GetRequiredService<HikVisionConfig>();
    var deviceState = services.GetRequiredService<HikVisionDeviceState>();
    var isapiClient = services.GetRequiredService<HikVisionIsapiClient>();

    Log.Information("Discovering Hikvision device with SADP...");
    var discovered = await HikVisionDeviceDiscovery.DiscoverRequiredSingleDeviceAsync(
        config.Username,
        config.Password);
    deviceState.SetDiscoveredDevice(discovered.IpAddress, discovered.SerialNumber, discovered.DeviceType);

    Log.Information(
        "Discovery: Using Hikvision device IP {IP}, serial={Serial}, type={Type}",
        discovered.IpAddress,
        discovered.SerialNumber,
        discovered.DeviceType);

    var (success, serial, deviceType, firmware) = await isapiClient.GetDeviceInfoAsync();
    if (success)
    {
        deviceState.SetDeviceInfo(serial, deviceType, firmware);
        Log.Information(
            "ISAPI: Device info loaded. serial={Serial}, type={Type}, firmware={Firmware}",
            serial,
            deviceType,
            firmware);
    }
    else
    {
        Log.Warning("ISAPI: Device info could not be loaded after discovery. Continuing with SADP device data.");
    }

    var callbackUrl = AttendanceCallbackConfigHandler.ResolveCallbackUrl(deviceState)
        ?? throw new InvalidOperationException("Could not resolve a reachable helper callback URL for the Hikvision device.");

    Log.Information("Auto-configuring Hikvision attendance callback to {CallbackUrl}", callbackUrl);
    var (status, message, httpStatusCode, _, hostId, _) =
        await isapiClient.ConfigureAttendanceCallbackAsync(callbackUrl, HikVisionDefaults.CallbackHostId);

    if (status == "success")
    {
        deviceState.SetCallbackUrl(callbackUrl);
    }
    else
    {
        Log.Warning(
            "Attendance callback auto-configuration failed. status={Status}, hostId={HostId}, httpStatusCode={HttpStatusCode}, message={Message}",
            status,
            hostId,
            httpStatusCode,
            message);
    }
}
