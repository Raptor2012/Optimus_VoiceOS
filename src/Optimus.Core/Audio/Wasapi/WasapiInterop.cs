namespace Optimus.Core.Audio.Wasapi;

using System;
using System.Runtime.InteropServices;

internal enum EDataFlow
{
    eRender = 0,
    eCapture = 1,
    eAll = 2
}

internal enum ERole
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2
}

[Flags]
internal enum AudclntStreamFlags : uint
{
    None = 0,
    CrossProcess = 0x00010000,
    Loopback = 0x00020000,
    EventCallback = 0x00040000,
    NoPersist = 0x00080000,
    RateAdjust = 0x00100000,
    AutoConvertPcm = 0x80000000
}

internal enum AudclntShareMode
{
    Shared = 0,
    Exclusive = 1
}

/// <summary>
/// Native <c>WAVEFORMATEX</c> from mmreg.h, which declares it under
/// <c>#include &lt;pshpack1.h&gt;</c>: exactly 18 bytes, no tail padding. Without
/// <c>Pack = 1</c> the CLR applies natural alignment and produces 20 bytes, which
/// shifts every field of the extensible form that follows it.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

/// <summary>
/// Native <c>WAVEFORMATEXTENSIBLE</c>: 18 + 2 + 4 + 16 = 40 bytes, with
/// <c>SubFormat</c> at offset 24. Under default packing the CLR places
/// <c>SubFormat</c> at offset 28 of a 44-byte struct, so the subformat GUID is read
/// from the wrong bytes and every extensible format is misclassified as integer PCM.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WAVEFORMATEXTENSIBLE
{
    public WAVEFORMATEX Format;
    public ushort wValidBitsPerSample;
    public uint dwChannelMask;
    public Guid SubFormat;
}

internal static class WaveFormatTag
{
    public const ushort Pcm = 0x0001;
    public const ushort IeeeFloat = 0x0003;
    public const ushort Extensible = 0xFFFE;
}

internal static class WaveFormatSizes
{
    /// <summary>Native <c>sizeof(WAVEFORMATEX)</c>.</summary>
    public const int WaveFormatEx = 18;

    /// <summary>Native <c>sizeof(WAVEFORMATEXTENSIBLE)</c>.</summary>
    public const int WaveFormatExtensible = 40;

    /// <summary>Byte offset of <c>SubFormat</c> inside <c>WAVEFORMATEXTENSIBLE</c>.</summary>
    public const int SubFormatOffset = 24;

    /// <summary>Minimum <c>cbSize</c> that makes the extensible tail readable.</summary>
    public const int ExtensibleCbSize = 22;
}

/// <summary>HRESULT values returned by <c>IAudioCaptureClient</c>.</summary>
internal static class AudclntHResults
{
    public const int SOk = 0;

    /// <summary><c>AUDCLNT_S_BUFFER_EMPTY</c>: success, but no packet was acquired.</summary>
    public const int SBufferEmpty = 0x08890001;
}

[Flags]
internal enum AudclntBufferFlags : uint
{
    None = 0,
    DataDiscontinuity = 0x1,
    Silent = 0x2,
    TimestampError = 0x4
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr pClient);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr pClient);
}

[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

    [PreserveSig]
    int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

    [PreserveSig]
    int GetState(out uint pdwState);
}

[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(
        AudclntShareMode ShareMode,
        AudclntStreamFlags StreamFlags,
        long hnsBufferDuration,
        long hnsPeriodicity,
        IntPtr pFormat,
        ref Guid AudioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint pNumBufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long phnsLatency);

    [PreserveSig]
    int GetCurrentPadding(out uint pNumPaddingFrames);

    [PreserveSig]
    int IsFormatSupported(AudclntShareMode ShareMode, IntPtr pFormat, out IntPtr ppClosestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr ppDeviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(
        out IntPtr ppData,
        out uint pNumFramesToRead,
        out uint pdwFlags,
        out ulong pu64DevicePosition,
        out ulong pu64QPCPosition);

    [PreserveSig]
    int ReleaseBuffer(uint NumFramesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint pNumFramesInNextPacket);
}

internal static class WasapiGuids
{
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00AA00389B71");
    public static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00AA00389B71");
}
