using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FloodgateSpike;

/// <summary>
/// Minimal P/Invoke surface for WinDivert 2.2.x. Struct layout and constants were checked
/// against include/windivert.h (basil00/WinDivert, master) on 2026-11-27.
/// </summary>
internal static unsafe class WinDivertNative
{
    private const string Dll = "WinDivert.dll";

    // WINDIVERT_LAYER
    public const int LayerNetwork = 0;
    public const int LayerFlow = 2;

    // WINDIVERT_EVENT (flow layer). Note: ESTABLISHED = 1, DELETED = 2.
    public const byte EventFlowEstablished = 1;
    public const byte EventFlowDeleted = 2;

    // WINDIVERT_FLAG_*
    public const ulong FlagSniff = 0x0001;
    public const ulong FlagRecvOnly = 0x0004;

    // WINDIVERT_SHUTDOWN_BOTH (bit flags: RECV = 1, SEND = 2)
    public const int ShutdownBoth = 3;

    public static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>
    /// WINDIVERT_ADDRESS is 80 bytes: Timestamp (8), bitfields (8), then a 64-byte union.
    /// Byte 8 = Layer, byte 9 = Event, byte 10 = flag bits (Sniffed, Outbound, Loopback, ...).
    /// The union starts at offset 16. Only the flow-layer fields we need are declared.
    /// Flow union: EndpointId (16), ParentEndpointId (24), ProcessId (32),
    /// LocalAddr[4] (36), RemoteAddr[4] (52), LocalPort (68), RemotePort (70), Protocol (72).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 80)]
    public struct Address
    {
        [FieldOffset(0)] public long Timestamp;
        [FieldOffset(8)] public byte Layer;
        [FieldOffset(9)] public byte Event;
        [FieldOffset(10)] public byte Flags;
        [FieldOffset(32)] public uint ProcessId;
        [FieldOffset(68)] public ushort LocalPort;
        [FieldOffset(70)] public ushort RemotePort;
        [FieldOffset(72)] public byte Protocol;
    }

    [DllImport(Dll, EntryPoint = "WinDivertOpen", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr Open([MarshalAs(UnmanagedType.LPStr)] string filter, int layer, short priority, ulong flags);

    [DllImport(Dll, EntryPoint = "WinDivertRecv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Recv(IntPtr handle, byte* packet, uint packetLen, uint* recvLen, Address* addr);

    [DllImport(Dll, EntryPoint = "WinDivertSend", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Send(IntPtr handle, byte* packet, uint packetLen, uint* sendLen, Address* addr);

    [DllImport(Dll, EntryPoint = "WinDivertShutdown", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shutdown(IntPtr handle, int how);

    [DllImport(Dll, EntryPoint = "WinDivertClose", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Close(IntPtr handle);

    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint milliseconds);

    /// <summary>Plain-language explanation of the error WinDivertOpen most often returns.</summary>
    public static string Describe(int error)
    {
        string hint = error switch
        {
            2 => "WinDivert.dll or WinDivert64.sys was not found next to the program. Run setup.ps1.",
            5 => "Access denied. Run this PowerShell window as Administrator.",
            87 => "Invalid parameter (bad filter, or a struct layout mismatch).",
            577 => "Windows refused the driver's signature (ERROR_INVALID_IMAGE_HASH). THIS is the driver-signing failure the spike is testing for.",
            1058 => "The WinDivert driver service is disabled.",
            1060 => "The WinDivert driver service does not exist (install failed).",
            1275 => "Windows blocked the driver (ERROR_DRIVER_BLOCKED). THIS is the driver-signing / policy failure the spike is testing for.",
            _ => "Unlisted error. See Microsoft's system error code list.",
        };
        return $"Win32 error {error}: {new Win32Exception(error).Message}. {hint}";
    }
}
