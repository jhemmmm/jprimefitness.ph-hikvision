using System.Net;
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
catch (HikVision.Services.DeviceSetupException ex)
{
    Log.Fatal("Cannot start the helper. {Guidance}", ex.Message);
    Environment.ExitCode = 1;
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
    services.AddHostedService<DeviceClockSyncService>();
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

    app.MapGet("/sadp/devices", SadpDevicesHandler.Handle)
        .WithName("SADP Devices")
        .Produces<SadpDevicesResponse>();

    app.MapPost("/sadp/modify", SadpModifyHandler.Handle)
        .WithName("SADP Modify Network")
        .Produces<SadpModifyResponse>();

    app.MapPost("/device/network", DeviceNetworkHandler.Handle)
        .WithName("Device Network Configure")
        .Produces<DeviceNetworkResponse>();
}

static async Task InitializeDeviceAsync(IServiceProvider services)
{
    var config = services.GetRequiredService<HikVisionConfig>();
    var deviceState = services.GetRequiredService<HikVisionDeviceState>();
    var isapiClient = services.GetRequiredService<HikVisionIsapiClient>();

    Log.Information("Discovering Hikvision device with SADP...");
    var discovered = await HikVisionDeviceDiscovery.DiscoverRequiredSingleDeviceAsync(attempts: 2);
    discovered = await EnsureDeviceOnLocalSubnetAsync(discovered, config);
    deviceState.SetDiscoveredDevice(discovered.IpAddress, discovered.SerialNumber, discovered.DeviceType, discovered.MacAddress, discovered.Encrypt);

    Log.Information(
        "Discovery: Using Hikvision device IP {IP}, mac={Mac}, serial={Serial}, type={Type}",
        discovered.IpAddress,
        discovered.MacAddress,
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

/// <summary>
/// The helper can only talk ISAPI/SDK to a device it can route to. When SADP finds the device on a foreign subnet
/// (e.g. it kept a static IP from an old router), move it into the helper's subnet with a SADP network update,
/// exactly like "Modify Network Parameters" in the official SADP tool, then continue with the new address.
/// </summary>
static async Task<HikVisionDeviceDiscovery.DiscoveredDevice> EnsureDeviceOnLocalSubnetAsync(
    HikVisionDeviceDiscovery.DiscoveredDevice discovered,
    HikVisionConfig config)
{
    // Enumerate the local interfaces once and reuse the list for the subnet check, the plan, and logging.
    var interfaces = LocalNetwork.GetIpv4Interfaces(includeVirtualInterfaces: false);
    var subnets = interfaces
        .Select(i => (Network: LocalNetwork.ToUInt32(i.Address) & LocalNetwork.ToUInt32(i.Mask), Mask: LocalNetwork.ToUInt32(i.Mask)))
        .Distinct()
        .ToList();

    if (IPAddress.TryParse(discovered.IpAddress, out var discoveredAddress)
        && LocalNetwork.IsInAnyLocalSubnet(discoveredAddress, subnets))
    {
        return discovered;
    }

    var localSubnets = string.Join(", ", interfaces.Select(i => $"{i.Address}/{i.Mask} ({i.Name})"));

    Log.Warning(
        "SADP: Device {IP}/{Mask} gw {Gateway} is not in any local subnet [{LocalSubnets}]. The helper cannot reach it by IP.",
        discovered.IpAddress,
        discovered.SubnetMask ?? "?",
        discovered.Gateway ?? "?",
        localSubnets);

    var httpPort = discovered.HttpPort ?? HikVisionDefaults.HttpPort;
    if (discoveredAddress is not null
        && await LocalNetwork.IsTcpPortOpenAsync(discoveredAddress, httpPort, timeoutMs: 1500))
    {
        Log.Warning("SADP: Device {IP}:{Port} is reachable through routing anyway; skipping the automatic network fix.", discovered.IpAddress, httpPort);
        return discovered;
    }

    if (SadpProtocol.NormalizeMac(discovered.MacAddress) is null)
    {
        throw new HikVision.Services.DeviceSetupException(
            $"Device {discovered.IpAddress} is on another subnet and reported no MAC address, so its network settings cannot be changed automatically. " +
            "Use the Hikvision SADP tool to move it into the helper's subnet, then restart the helper.");
    }

    var plan = SadpNetworkPlanner.PlanOffSubnetFix(
        discovered,
        interfaces,
        ip => LocalNetwork.IsHostRespondingAsync(ip, 500).GetAwaiter().GetResult());

    if (plan is null)
    {
        throw new HikVision.Services.DeviceSetupException(
            $"Device {discovered.IpAddress} is on another subnet and no free address could be chosen in the helper's subnet [{localSubnets}]. " +
            "Use the Hikvision SADP tool to move it into the helper's subnet, then restart the helper.");
    }

    // Encrypt=true firmware rejects a plain-text SADP password and locks itself after repeated failed attempts, so do
    // not send a doomed update on every startup — show the manual-fix guidance straight away.
    if (string.Equals(discovered.Encrypt, "true", StringComparison.OrdinalIgnoreCase))
    {
        throw new HikVision.Services.DeviceSetupException(
            HikVisionSadpClient.BuildManualFixGuidance(HikVisionSadpClient.EncryptedPasswordReason, plan.Mac, plan));
    }

    var iface = SadpNetworkPlanner.SelectInterface(discovered, interfaces);
    Log.Warning(
        "SADP AUTO-FIX: Changing device {Mac} from {OldIp} to {NewIp}/{Mask} gw {Gateway} (cmd {CommandPort}, http {HttpPort}) via {Interface}",
        plan.Mac,
        discovered.IpAddress,
        plan.IpAddress,
        plan.SubnetMask,
        plan.Gateway,
        plan.CommandPort,
        plan.HttpPort,
        iface?.Name ?? "?");

    var result = await HikVisionSadpClient.ModifyNetworkAndConfirmAsync(plan, config.Password);

    if (!result.Success)
    {
        throw new HikVision.Services.DeviceSetupException(HikVisionSadpClient.BuildManualFixGuidance(result, plan));
    }

    Log.Warning(
        "SADP AUTO-FIX: Device {Mac} is now at {NewIp} ({Detail})",
        plan.Mac,
        plan.IpAddress,
        result.Confirmed is not null ? "confirmed by re-discovery" : result.Result);

    return result.Confirmed ?? discovered with
    {
        IpAddress = plan.IpAddress,
        SubnetMask = plan.SubnetMask,
        Gateway = plan.Gateway
    };
}
