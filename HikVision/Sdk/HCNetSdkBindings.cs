using System.Runtime.InteropServices;
using System.Text;

namespace HikVision.Sdk;

/// <summary>
/// PInvoke bindings to Hikvision HCNetSDK.dll based on the official HCNetSDK header.
/// </summary>
public static class HCNetSdkBindings
{
    private const string HCNetSdkDll = "HCNetSDK.dll";

    public const uint ACS_ABILITY = 0x801;
    public const uint CAPTURE_FINGERPRINT_INFO = 2504;
    public const uint NET_SDK_CALLBACK_TYPE_STATUS = 0;
    public const uint NET_SDK_CALLBACK_TYPE_PROGRESS = 1;
    public const uint NET_SDK_CALLBACK_TYPE_DATA = 2;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void RemoteConfigCallback(
        uint dwType,
        IntPtr lpBuffer,
        uint dwBufLen,
        IntPtr pUserData);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NET_DVR_USER_LOGIN_INFO
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 129)]
        public byte[] sDeviceAddress;

        public byte byUseTransport;
        public ushort wPort;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] sUserName;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] sPassword;

        public IntPtr cbLoginResult;
        public IntPtr pUser;
        public int bUseAsynLogin;
        public byte byProxyType;
        public byte byUseUTCTime;
        public byte byLoginMode;
        public byte byHttps;
        public int iProxyID;
        public byte byVerifyMode;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 119)]
        public byte[] byRes3;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NET_DVR_DEVICEINFO_V30
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
        public byte[] sSerialNumber;

        public byte byAlarmInPortNum;
        public byte byAlarmOutPortNum;
        public byte byDiskNum;
        public byte byDVRType;
        public byte byChanNum;
        public byte byStartChan;
        public byte byAudioChanNum;
        public byte byIPChanNum;
        public byte byZeroChanNum;
        public byte byMainProto;
        public byte bySubProto;
        public byte bySupport;
        public byte bySupport1;
        public byte bySupport2;
        public ushort wDevType;
        public byte bySupport3;
        public byte byMultiStreamProto;
        public byte byStartDChan;
        public byte byStartDTalkChan;
        public byte byHighDChanNum;
        public byte bySupport4;
        public byte byLanguageType;
        public byte byVoiceInChanNum;
        public byte byStartVoiceInChanNo;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public byte[] byRes3;

        public byte byMirrorChanNum;
        public ushort wStartMirrorChanNo;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public byte[] byRes2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NET_DVR_DEVICEINFO_V40
    {
        public NET_DVR_DEVICEINFO_V30 struDeviceV30;
        public byte bySupportLock;
        public byte byRetryLoginTime;
        public byte byPasswordLevel;
        public byte byProxyType;
        public uint dwSurplusLockTime;
        public byte byCharEncodeType;
        public byte bySupportDev5;
        public byte bySupport;
        public byte byLoginMode;
        public uint dwOEMCode;
        public int iResidualValidity;
        public byte byResidualValidity;
        public byte bySingleStartDTalkChan;
        public byte bySingleDTalkChanNums;
        public byte byPassWordResetLevel;
        public byte bySupportStreamEncrypt;
        public byte byMarketType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 238)]
        public byte[] byRes2;
    }

    public enum NET_SDK_REMOTE_CONFIG_STATUS : int
    {
        Success = 1000,
        NeedWait = 1001,
        Finish = 1002,
        Failed = 1003,
        Exception = 1004
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NET_DVR_CAPTURE_FINGERPRINT_COND
    {
        public uint dwSize;
        public byte byFingerPrintPicType;
        public byte byFingerNo;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)]
        public byte[] byRes;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NET_DVR_CAPTURE_FINGERPRINT_CFG
    {
        public uint dwSize;
        public uint dwFingerPrintDataSize;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 768)]
        public byte[] byFingerData;

        public uint dwFingerPrintPicSize;
        public IntPtr pFingerPrintPicBuffer;
        public byte byFingerNo;
        public byte byFingerPrintQuality;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 62)]
        public byte[] byRes;
    }

    public static void SetString(byte[] field, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Array.Clear(field, 0, field.Length);
        Array.Copy(bytes, field, Math.Min(bytes.Length, field.Length - 1));
    }

    [DllImport(HCNetSdkDll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool NET_DVR_Init();

    [DllImport(HCNetSdkDll, CallingConvention = CallingConvention.StdCall)]
    public static extern void NET_DVR_Cleanup();

    [DllImport(HCNetSdkDll, CallingConvention = CallingConvention.StdCall)]
    public static extern int NET_DVR_Login_V40(
        [In] ref NET_DVR_USER_LOGIN_INFO pLoginInfo,
        [Out] out NET_DVR_DEVICEINFO_V40 lpDeviceInfo);

    [DllImport(HCNetSdkDll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool NET_DVR_Logout(int iUserID);

    [DllImport(HCNetSdkDll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint NET_DVR_GetLastError();

    [DllImport(HCNetSdkDll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool NET_DVR_GetDeviceAbility(
        int lUserID,
        uint dwAbilityType,
        IntPtr pInBuf,
        uint dwInLength,
        IntPtr pOutBuf,
        uint dwOutLength);

    [DllImport(HCNetSdkDll, CallingConvention = CallingConvention.StdCall)]
    public static extern int NET_DVR_StartRemoteConfig(
        int lUserID,
        uint dwCommand,
        IntPtr lpInBuffer,
        uint dwInBufferLen,
        RemoteConfigCallback? cbStateCallback,
        IntPtr pUserData);

    [DllImport(HCNetSdkDll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool NET_DVR_StopRemoteConfig(int lHandle);

    [DllImport(HCNetSdkDll, CallingConvention = CallingConvention.StdCall)]
    public static extern int NET_DVR_GetNextRemoteConfig(
        int lHandle,
        IntPtr lpOutBuff,
        uint dwOutBuffSize);

    [DllImport(HCNetSdkDll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool NET_DVR_GetRemoteConfigState(
        int lHandle,
        IntPtr pState);
}
