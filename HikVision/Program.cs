using Serilog;
using HikVision.Config;
using HikVision.Services;
using HikVision.Models;
using HikVision.Handlers;

var builder = WebApplication.CreateBuilder(args);

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

    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton<HikVisionSdkClient>();
    builder.Services.AddSingleton<HikVisionIsapiClient>();
    builder.Services.AddSingleton<AttendanceCallbackProbeService>();
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

    if (!string.IsNullOrWhiteSpace(callbackUrl))
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
