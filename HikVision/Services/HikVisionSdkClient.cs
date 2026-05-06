using System.Runtime.InteropServices;
using Serilog;
using HikVision.Config;
using HikVision.Sdk;
using static HikVision.Sdk.HCNetSdkBindings;

namespace HikVision.Services;

public class HikVisionSdkClient : IDisposable
{
    private readonly HikVisionConfig _config;
    private readonly HikVisionDeviceState _deviceState;
    private static readonly RemoteConfigCallback RemoteConfigCallbackInstance = HandleRemoteConfigCallback;
    private int _loginId = -1;
    private bool _initialized;

    public HikVisionSdkClient(HikVisionConfig config, HikVisionDeviceState deviceState)
    {
        _config = config;
        _deviceState = deviceState;
    }

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            Log.Information("Initializing HCNetSDK");

            if (!NET_DVR_Init())
            {
                throw new InvalidOperationException("NET_DVR_Init() returned false");
            }

            _initialized = true;
            Log.Information("HCNetSDK initialized successfully");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize HCNetSDK");
            throw;
        }
    }

    public (bool Success, uint ErrorCode, string Message) TestLogin()
    {
        if (!_initialized)
        {
            Initialize();
        }

        if (_loginId >= 0)
        {
            Log.Information("Reusing existing SDK login {LoginId}", _loginId);
            return (true, 0, "Login already established");
        }

        try
        {
            var deviceIp = _deviceState.RequireDeviceIp();

            Log.Information("Attempting SDK login to {DeviceIp}:{Port}", deviceIp, HikVisionDefaults.SdkPort);

            var loginInfo = new NET_DVR_USER_LOGIN_INFO
            {
                sDeviceAddress = new byte[129],
                sUserName = new byte[64],
                sPassword = new byte[64],
                byRes3 = new byte[119],
                byUseTransport = 0,
                wPort = HikVisionDefaults.SdkPort,
                cbLoginResult = IntPtr.Zero,
                pUser = IntPtr.Zero,
                bUseAsynLogin = 0,
                byProxyType = 0,
                byUseUTCTime = 0,
                byLoginMode = 0,
                byHttps = 0,
                iProxyID = 0,
                byVerifyMode = 0
            };

            SetString(loginInfo.sDeviceAddress, deviceIp);
            SetString(loginInfo.sUserName, _config.Username);
            SetString(loginInfo.sPassword, _config.Password);

            var result = NET_DVR_Login_V40(ref loginInfo, out var deviceInfo);

            if (result < 0)
            {
                var errorCode = NET_DVR_GetLastError();
                Log.Warning("SDK login failed with error code {ErrorCode}", errorCode);
                return (false, errorCode, $"Login failed with SDK error code {errorCode}");
            }

            _loginId = result;

            var serialNumber = deviceInfo.struDeviceV30.sSerialNumber is { Length: > 0 }
                ? System.Text.Encoding.ASCII.GetString(deviceInfo.struDeviceV30.sSerialNumber).TrimEnd('\0')
                : string.Empty;

            Log.Information("SDK login successful. Login ID: {LoginId}. Device serial: {Serial}", _loginId, serialNumber);
            return (true, 0, "Login successful");
        }
        catch (DllNotFoundException ex)
        {
            Log.Error(ex, "HCNetSDK.dll was not found");
            return (false, 0, "HCNetSDK.dll not found. Put the Hikvision Windows SDK DLLs in the executable folder's lib directory, next to lib/HCNetSDK.dll, or add them to PATH.");
        }
        catch (BadImageFormatException ex)
        {
            Log.Error(ex, "HCNetSDK.dll architecture mismatch");
            return (false, 0, "HCNetSDK.dll architecture mismatch. Use the x64 SDK DLLs with a 64-bit .NET build.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error during SDK login");
            return (false, 0, $"Unexpected error: {ex.Message}");
        }
    }

    public (bool Success, uint ErrorCode, string Message, string? AbilityXml, bool? SupportsCaptureFingerprint) GetAccessControlAbility()
    {
        var (loginSuccess, loginErrorCode, loginMessage) = TestLogin();

        if (!loginSuccess)
        {
            return (false, loginErrorCode, loginMessage, null, null);
        }

        IntPtr outBuffer = IntPtr.Zero;

        try
        {
            const int outputBufferSize = 256 * 1024;
            outBuffer = Marshal.AllocHGlobal(outputBufferSize);
            Span<byte> zeroFill = stackalloc byte[1];
            Marshal.Copy(zeroFill.ToArray(), 0, outBuffer, 1);

            var result = NET_DVR_GetDeviceAbility(
                _loginId,
                ACS_ABILITY,
                IntPtr.Zero,
                0,
                outBuffer,
                outputBufferSize);

            if (!result)
            {
                var errorCode = NET_DVR_GetLastError();
                Log.Warning("NET_DVR_GetDeviceAbility failed with SDK error {ErrorCode}", errorCode);
                return (false, errorCode, $"Get device ability failed with SDK error code {errorCode}", null, null);
            }

            var rawBytes = new byte[outputBufferSize];
            Marshal.Copy(outBuffer, rawBytes, 0, outputBufferSize);

            var xmlLength = Array.IndexOf(rawBytes, (byte)0);

            if (xmlLength < 0)
            {
                xmlLength = rawBytes.Length;
            }

            var abilityXml = System.Text.Encoding.UTF8.GetString(rawBytes, 0, xmlLength).Trim();
            var supportsCaptureFingerprint = abilityXml.Contains("<isSupportCaptureFingerPrint>true</isSupportCaptureFingerPrint>", StringComparison.OrdinalIgnoreCase);

            Log.Information("ACS ability fetched successfully. CaptureFingerPrint support: {SupportsCaptureFingerPrint}", supportsCaptureFingerprint);

            return (true, 0, "ACS ability fetched successfully", abilityXml, supportsCaptureFingerprint);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error while reading ACS ability");
            return (false, 0, $"Unexpected error: {ex.Message}", null, null);
        }
        finally
        {
            if (outBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(outBuffer);
            }
        }
    }

    public async Task<FingerprintCaptureResult>
        StartRemoteFingerprintCaptureAsync(string employeeNo, int fingerprintId, int timeoutSeconds)
    {
        if (_loginId < 0)
        {
            Log.Warning("Fingerprint capture requested without an SDK login session");
            return FingerprintCaptureResult.Failed("failed", "SDK session not established. Run test-login first.", 0);
        }

        if (fingerprintId < 1 || fingerprintId > 10)
        {
            return FingerprintCaptureResult.Failed("failed", "FingerprintId must be between 1 and 10", 0);
        }

        var remoteHandle = -1;
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        GCHandle? callbackHandle = null;

        try
        {
            Log.Information(
                "Starting remote fingerprint capture for employee {EmployeeNo}, fingerprint {FingerprintId}, timeout {TimeoutSeconds}s",
                employeeNo,
                fingerprintId,
                timeoutSeconds);

            var cond = new NET_DVR_CAPTURE_FINGERPRINT_COND
            {
                dwSize = (uint)Marshal.SizeOf<NET_DVR_CAPTURE_FINGERPRINT_COND>(),
                byFingerPrintPicType = 0,
                byFingerNo = (byte)fingerprintId,
                byRes = new byte[126]
            };

            var condSize = Marshal.SizeOf<NET_DVR_CAPTURE_FINGERPRINT_COND>();
            var condPtr = Marshal.AllocHGlobal(condSize);
            var callbackContext = new RemoteConfigCallbackContext(_config.Debug);
            callbackHandle = GCHandle.Alloc(callbackContext);

            try
            {
                Marshal.StructureToPtr(cond, condPtr, false);
                remoteHandle = NET_DVR_StartRemoteConfig(
                    _loginId,
                    CAPTURE_FINGERPRINT_INFO,
                    condPtr,
                    (uint)condSize,
                    RemoteConfigCallbackInstance,
                    GCHandle.ToIntPtr(callbackHandle.Value));
            }
            finally
            {
                Marshal.FreeHGlobal(condPtr);
            }

            if (remoteHandle < 0)
            {
                var errorCode = NET_DVR_GetLastError();
                Log.Warning("NET_DVR_StartRemoteConfig failed with SDK error {ErrorCode}", errorCode);
                callbackContext.AddEvent($"start_remote_config_failed:{errorCode}");
                return FingerprintCaptureResult.Failed(
                    MapSdkErrorCodeToStatus(errorCode),
                    $"Failed to start remote capture. SDK error {errorCode}",
                    errorCode,
                    callbackContext.ToDebugInfo());
            }

            Log.Information("Remote capture session started with handle {RemoteHandle}", remoteHandle);
            callbackContext.AddEvent($"remote_handle:{remoteHandle}");

            var cfgSize = Marshal.SizeOf<NET_DVR_CAPTURE_FINGERPRINT_CFG>();
            var cfgPtr = Marshal.AllocHGlobal(cfgSize);
            var pictureBufferSize = 1024 * 1024;
            var pictureBufferPtr = Marshal.AllocHGlobal(pictureBufferSize);

            try
            {
                var cfgTemplate = new NET_DVR_CAPTURE_FINGERPRINT_CFG
                {
                    dwSize = (uint)cfgSize,
                    byFingerData = new byte[768],
                    dwFingerPrintPicSize = (uint)pictureBufferSize,
                    pFingerPrintPicBuffer = pictureBufferPtr,
                    byRes = new byte[62]
                };

                Marshal.StructureToPtr(cfgTemplate, cfgPtr, false);

                var pollCount = 0;
                await Task.Delay(1500);

                while (DateTime.UtcNow < deadline)
                {
                    if (callbackContext.CaptureResult is not null)
                    {
                        var callbackResult = callbackContext.CaptureResult;
                        Log.Information(
                            "Callback reported fingerprint capture success. Finger {FingerNo}, data size {DataSize}, quality {Quality}",
                            callbackResult.FingerNumber,
                            callbackResult.FingerprintDataSize,
                            callbackResult.FingerprintQuality);

                        return FingerprintCaptureResult.SuccessResult(
                            $"Fingerprint captured. Quality: {callbackResult.FingerprintQuality}/100",
                            callbackResult.FingerprintDataSize,
                            callbackResult.FingerprintQuality,
                            callbackResult.FingerprintData,
                            callbackContext.ToDebugInfo()
                        );
                    }

                    if (callbackContext.FailureStatus is not null)
                    {
                        Log.Warning(
                            "Callback reported capture failure status {FailureStatus} with SDK error {ErrorCode}",
                            callbackContext.FailureStatus,
                            callbackContext.LastErrorCode);

                        return FingerprintCaptureResult.Failed(
                            MapSdkErrorCodeToStatus(callbackContext.LastErrorCode),
                            $"Remote capture failed. SDK error {callbackContext.LastErrorCode}",
                            callbackContext.LastErrorCode,
                            callbackContext.ToDebugInfo()
                        );
                    }

                    pollCount++;
                    var status = NET_DVR_GetNextRemoteConfig(remoteHandle, cfgPtr, (uint)cfgSize);

                    if (pollCount <= 3 || pollCount % 10 == 0)
                    {
                        Log.Information("Remote capture poll {PollCount} returned status {Status}", pollCount, status);
                    }

                    switch ((NET_SDK_REMOTE_CONFIG_STATUS)status)
                    {
                        case NET_SDK_REMOTE_CONFIG_STATUS.Success:
                            var cfg = Marshal.PtrToStructure<NET_DVR_CAPTURE_FINGERPRINT_CFG>(cfgPtr);
                            var fingerprintData = cfg.dwFingerPrintDataSize > 0
                                ? cfg.byFingerData.Take((int)cfg.dwFingerPrintDataSize).ToArray()
                                : null;
                            Log.Information(
                                "Fingerprint captured successfully. Finger {FingerNo}, data size {DataSize}, quality {Quality}",
                                cfg.byFingerNo,
                                cfg.dwFingerPrintDataSize,
                                cfg.byFingerPrintQuality);

                            return FingerprintCaptureResult.SuccessResult(
                                $"Fingerprint captured. Quality: {cfg.byFingerPrintQuality}/100",
                                (int)cfg.dwFingerPrintDataSize,
                                cfg.byFingerPrintQuality,
                                fingerprintData,
                                callbackContext.ToDebugInfo()
                            );

                        case NET_SDK_REMOTE_CONFIG_STATUS.NeedWait:
                            await Task.Delay(300);
                            continue;

                        case NET_SDK_REMOTE_CONFIG_STATUS.Finish:
                            Log.Warning("Remote capture finished without returning fingerprint data");
                            callbackContext.AddEvent("remote_config_finish_without_data");
                            return FingerprintCaptureResult.Failed(
                                "failed",
                                "Device ended the capture session without returning fingerprint data",
                                0,
                                callbackContext.ToDebugInfo());

                        case NET_SDK_REMOTE_CONFIG_STATUS.Failed:
                            var failedErrorCode = NET_DVR_GetLastError();
                            Log.Warning("Remote capture failed with SDK error {ErrorCode}", failedErrorCode);
                            callbackContext.AddEvent($"poll_failed:{failedErrorCode}");
                            return FingerprintCaptureResult.Failed(
                                "failed",
                                $"Device rejected fingerprint capture. SDK error {failedErrorCode}",
                                failedErrorCode,
                                callbackContext.ToDebugInfo());

                        case NET_SDK_REMOTE_CONFIG_STATUS.Exception:
                            var exceptionErrorCode = NET_DVR_GetLastError();
                            Log.Warning("Remote capture exception with SDK error {ErrorCode}", exceptionErrorCode);
                            callbackContext.AddEvent($"poll_exception:{exceptionErrorCode}");
                            return FingerprintCaptureResult.Failed(
                                "failed",
                                $"Remote capture exception. SDK error {exceptionErrorCode}",
                                exceptionErrorCode,
                                callbackContext.ToDebugInfo());

                        default:
                            if (status < 0)
                            {
                                var errorCode = NET_DVR_GetLastError();
                                var remoteState = GetRemoteConfigState(remoteHandle);
                                Log.Warning(
                                    "NET_DVR_GetNextRemoteConfig returned negative status {Status} with SDK error {ErrorCode} and remote state {RemoteState}. Callback status={CallbackStatus}, callback count={CallbackCount}",
                                    status,
                                    errorCode,
                                    remoteState?.ToString() ?? "unknown",
                                    callbackContext.LastCallbackStatus?.ToString() ?? "none",
                                    callbackContext.CallbackInvocationCount);

                                if (errorCode == 17 && DateTime.UtcNow < deadline)
                                {
                                    callbackContext.LastErrorCode = errorCode;
                                    callbackContext.LastRemoteConfigState = remoteState;
                                    callbackContext.AddEvent($"negative_poll_status:{status}:error:{errorCode}:state:{remoteState?.ToString() ?? "unknown"}");
                                    await Task.Delay(500);
                                    continue;
                                }

                                return FingerprintCaptureResult.Failed(
                                    MapSdkErrorCodeToStatus(errorCode),
                                    $"Remote capture failed. SDK error {errorCode}",
                                    errorCode,
                                    callbackContext.ToDebugInfo());
                            }

                            await Task.Delay(300);
                            continue;
                    }
                }

                Log.Warning("Remote capture timed out after {TimeoutSeconds}s", timeoutSeconds);
                callbackContext.AddEvent("capture_timeout");
                return FingerprintCaptureResult.Failed(
                    "timeout",
                    $"No finger placed within {timeoutSeconds} seconds",
                    0,
                    callbackContext.ToDebugInfo());
            }
            finally
            {
                Marshal.FreeHGlobal(pictureBufferPtr);
                Marshal.FreeHGlobal(cfgPtr);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error during remote fingerprint capture");
            return FingerprintCaptureResult.Failed("failed", $"Error: {ex.Message}", 0);
        }
        finally
        {
            if (callbackHandle is { } handle && handle.IsAllocated)
            {
                handle.Free();
            }

            if (remoteHandle >= 0)
            {
                var stopResult = NET_DVR_StopRemoteConfig(remoteHandle);
                Log.Information("Stopped remote capture handle {RemoteHandle}. Result: {StopResult}", remoteHandle, stopResult);
            }
        }
    }

    public void Logout()
    {
        if (_loginId < 0)
        {
            return;
        }

        try
        {
            Log.Information("Logging out SDK session {LoginId}", _loginId);
            NET_DVR_Logout(_loginId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while logging out SDK session {LoginId}", _loginId);
        }
        finally
        {
            _loginId = -1;
        }
    }

    public void Cleanup()
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            Logout();
            Log.Information("Cleaning up HCNetSDK");
            NET_DVR_Cleanup();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while cleaning up HCNetSDK");
        }
        finally
        {
            _initialized = false;
        }
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }

    ~HikVisionSdkClient()
    {
        Cleanup();
    }

    private static string MapSdkErrorCodeToStatus(uint errorCode)
    {
        return errorCode switch
        {
            17 => "parameter_error",
            _ => "failed"
        };
    }

    private static int? GetRemoteConfigState(int remoteHandle)
    {
        var statePtr = Marshal.AllocHGlobal(sizeof(int));

        try
        {
            if (!NET_DVR_GetRemoteConfigState(remoteHandle, statePtr))
            {
                return null;
            }

            return Marshal.ReadInt32(statePtr);
        }
        finally
        {
            Marshal.FreeHGlobal(statePtr);
        }
    }

    private static void HandleRemoteConfigCallback(uint dwType, IntPtr lpBuffer, uint dwBufLen, IntPtr pUserData)
    {
        if (pUserData == IntPtr.Zero)
        {
            return;
        }

        var handle = GCHandle.FromIntPtr(pUserData);

        if (handle.Target is not RemoteConfigCallbackContext context)
        {
            return;
        }

        context.CallbackInvocationCount++;

        if (dwType == NET_SDK_CALLBACK_TYPE_STATUS && lpBuffer != IntPtr.Zero && dwBufLen >= sizeof(int))
        {
            var callbackStatus = (uint)Marshal.ReadInt32(lpBuffer);
            context.LastCallbackStatus = callbackStatus;
            context.AddEvent($"callback_status:{callbackStatus}");
            return;
        }

        if (dwType == NET_SDK_CALLBACK_TYPE_DATA && lpBuffer != IntPtr.Zero && dwBufLen >= Marshal.SizeOf<NET_DVR_CAPTURE_FINGERPRINT_CFG>())
        {
            var cfg = Marshal.PtrToStructure<NET_DVR_CAPTURE_FINGERPRINT_CFG>(lpBuffer);
            var fingerprintData = cfg.dwFingerPrintDataSize > 0
                ? cfg.byFingerData.Take((int)cfg.dwFingerPrintDataSize).ToArray()
                : null;
            context.CaptureResult = new CallbackCaptureResult(
                (int)cfg.dwFingerPrintDataSize,
                cfg.byFingerPrintQuality,
                cfg.byFingerNo,
                fingerprintData);
            context.AddEvent($"callback_data:size:{cfg.dwFingerPrintDataSize}:quality:{cfg.byFingerPrintQuality}:finger:{cfg.byFingerNo}");
            return;
        }

        if (dwType == NET_SDK_CALLBACK_TYPE_PROGRESS && lpBuffer != IntPtr.Zero && dwBufLen >= sizeof(int))
        {
            context.LastProgress = Marshal.ReadInt32(lpBuffer);
            context.AddEvent($"callback_progress:{context.LastProgress}");
            return;
        }

        context.FailureStatus ??= dwType;
        context.AddEvent($"callback_failure_status:{dwType}");
    }

    private sealed class RemoteConfigCallbackContext
    {
        private readonly bool _verboseTracing;
        private readonly List<string> _events = [];

        public RemoteConfigCallbackContext(bool verboseTracing)
        {
            _verboseTracing = verboseTracing;
        }

        public int CallbackInvocationCount { get; set; }
        public uint? LastCallbackStatus { get; set; }
        public int? LastProgress { get; set; }
        public uint LastErrorCode { get; set; }
        public uint? FailureStatus { get; set; }
        public int? LastRemoteConfigState { get; set; }
        public CallbackCaptureResult? CaptureResult { get; set; }

        public void AddEvent(string value)
        {
            if (_verboseTracing)
            {
                _events.Add(value);
            }
        }

        public Models.FingerprintDebugInfo ToDebugInfo()
        {
            return new Models.FingerprintDebugInfo
            {
                CallbackInvocationCount = CallbackInvocationCount,
                LastCallbackStatus = LastCallbackStatus,
                LastProgress = LastProgress,
                LastRemoteConfigState = LastRemoteConfigState,
                Events = [.. _events]
            };
        }
    }

    private sealed record CallbackCaptureResult(
        int FingerprintDataSize,
        int FingerprintQuality,
        int FingerNumber,
        byte[]? FingerprintData);

    public sealed record FingerprintCaptureResult(
        bool Success,
        uint ErrorCode,
        string Status,
        string Message,
        int FingerprintDataSize,
        int Quality,
        byte[]? FingerprintData,
        Models.FingerprintDebugInfo? Debug)
    {
        public static FingerprintCaptureResult SuccessResult(
            string message,
            int fingerprintDataSize,
            int quality,
            byte[]? fingerprintData,
            Models.FingerprintDebugInfo? debug = null)
        {
            return new FingerprintCaptureResult(true, 0, "success", message, fingerprintDataSize, quality, fingerprintData, debug);
        }

        public static FingerprintCaptureResult Failed(
            string status,
            string message,
            uint errorCode,
            Models.FingerprintDebugInfo? debug = null)
        {
            return new FingerprintCaptureResult(false, errorCode, status, message, 0, 0, null, debug);
        }
    }
}
