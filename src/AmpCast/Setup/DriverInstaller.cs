using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AmpCast.Setup;

/// <summary>
/// Silent install of the virtual WDM audio cable (the "microphone" Discord listens to).
///
/// A virtual sound card is a *root-enumerated* device: there is no hardware
/// that Plug and Play could detect, so "pnputil /add-driver x.inf /install"
/// only puts the package in the driver store and no device appears.
/// We do what "devcon install" does (devcon itself may not be redistributed):
///   1. create a device node ROOT\... with the INF's hardware id (SetupAPI);
///   2. install the driver on it (UpdateDriverForPlugAndPlayDevices).
///
/// Must run elevated and as a 64-bit process on 64-bit Windows.
/// The driver package (.inf/.sys/.cat) must be signed by Microsoft
/// (attestation signing) — Windows 10/11 x64 refuses unsigned kernel drivers.
/// </summary>
public static class DriverInstaller
{
    public const int RebootRequired = 3010;

    /// <returns>0 = ok, 3010 = ok but reboot required.</returns>
    public static int Install(string infPath, string hardwareId)
    {
        infPath = Path.GetFullPath(infPath);
        if (!File.Exists(infPath)) throw new FileNotFoundException("INF not found", infPath);

        if (!DeviceExists(hardwareId))
            CreateRootDevice(infPath, hardwareId);

        if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hardwareId, infPath, INSTALLFLAG_FORCE, out bool reboot))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateDriverForPlugAndPlayDevices failed");

        return reboot ? RebootRequired : 0;
    }

    /// <summary>Removes every device with this hardware id and, if given, the driver package.</summary>
    public static int Uninstall(string hardwareId, string? infPath)
    {
        bool reboot = false;
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (set == INVALID_HANDLE_VALUE) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (!HasHardwareId(set, ref data, hardwareId)) continue;
                if (DiUninstallDevice(IntPtr.Zero, set, ref data, 0, out bool r)) reboot |= r;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        if (!string.IsNullOrEmpty(infPath) && File.Exists(infPath))
        {
            if (DiUninstallDriverW(IntPtr.Zero, Path.GetFullPath(infPath), 0, out bool r)) reboot |= r;
        }
        return reboot ? RebootRequired : 0;
    }

    public static bool DeviceExists(string hardwareId)
    {
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (set == INVALID_HANDLE_VALUE) return false;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                if (HasHardwareId(set, ref data, hardwareId)) return true;
            return false;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static void CreateRootDevice(string infPath, string hardwareId)
    {
        var className = new StringBuilder(MAX_CLASS_NAME_LEN);
        if (!SetupDiGetINFClassW(infPath, out Guid classGuid, className, MAX_CLASS_NAME_LEN, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetINFClass failed");

        IntPtr set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == INVALID_HANDLE_VALUE) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiCreateDeviceInfoW(set, className.ToString(), ref classGuid, null, IntPtr.Zero, DICD_GENERATE_ID, ref data))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiCreateDeviceInfo failed");

            byte[] multiSz = Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref data, SPDRP_HARDWAREID, multiSz, multiSz.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiSetDeviceRegistryProperty failed");

            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref data))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DIF_REGISTERDEVICE failed");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static bool HasHardwareId(IntPtr set, ref SP_DEVINFO_DATA data, string hardwareId)
    {
        var buffer = new byte[2048];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, SPDRP_HARDWAREID, out _, buffer, buffer.Length, out _))
            return false;
        foreach (string id in Encoding.Unicode.GetString(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            if (id.Equals(hardwareId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ------------------------------------------------------------------ Win32

    private const int DIGCF_ALLCLASSES = 0x4;
    private const int DICD_GENERATE_ID = 0x1;
    private const int SPDRP_HARDWAREID = 0x1;
    private const int DIF_REGISTERDEVICE = 0x19;
    private const int INSTALLFLAG_FORCE = 0x1;
    private const int MAX_CLASS_NAME_LEN = 32;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetINFClassW(string infName, out Guid classGuid, StringBuilder className, int classNameSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string deviceName, ref Guid classGuid, string? description, IntPtr hwndParent, int flags, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int property, byte[] buffer, int size);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int property, out int regType, byte[] buffer, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(int installFunction, IntPtr set, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string hardwareId, string fullInfPath, int installFlags, out bool rebootRequired);

    [DllImport("newdev.dll", SetLastError = true)]
    private static extern bool DiUninstallDevice(IntPtr hwndParent, IntPtr set, ref SP_DEVINFO_DATA data, int flags, out bool needReboot);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool DiUninstallDriverW(IntPtr hwndParent, string infPath, int flags, out bool needReboot);
}
