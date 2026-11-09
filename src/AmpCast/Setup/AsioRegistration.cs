using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace AmpCast.Setup;

/// <summary>
/// Registers the native "AmpCast ASIO" driver so that FL Studio, Ableton,
/// Studio One, Reaper, Cubase… list it in their ASIO device menu.
///
/// A DAW finds ASIO drivers like this:
///   1. enumerates HKLM\SOFTWARE\ASIO\&lt;driver name&gt; and reads its "CLSID" value;
///   2. calls CoCreateInstance(CLSID) → COM looks up
///      HKLM\SOFTWARE\Classes\CLSID\{CLSID}\InprocServer32 = path of the driver DLL.
/// The DLL must be native (C++): it is loaded into the DAW process, so a
/// registry entry pointing to managed code would not work. 64-bit DAWs read
/// the 64-bit registry view, 32-bit DAWs the WOW6432Node view, hence two DLLs.
///
/// Requires administrator rights (HKLM). Called by the installer through
/// "AmpCast.exe --register-asio".
/// </summary>
public static class AsioRegistration
{
    public const string DriverName = "AmpCast ASIO";
    public const string Clsid = "{7C3B1A52-4E9D-4F7B-9A1E-AC0A5CA57001}";
    public const string Dll64 = "AmpCastAsio64.dll";
    public const string Dll32 = "AmpCastAsio32.dll";

    private const string AsioRoot = @"SOFTWARE\ASIO";
    private static readonly string ClsidKey = $@"SOFTWARE\Classes\CLSID\{Clsid}";

    /// <summary>Registers the DLLs found in <paramref name="directory"/>. Returns a short report.</summary>
    public static string Register(string directory)
    {
        string dll64 = Path.Combine(directory, Dll64);
        string dll32 = Path.Combine(directory, Dll32);
        var report = new List<string>();

        if (File.Exists(dll64))
        {
            Write(Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32, dll64);
            report.Add("64-bit: " + dll64);
        }
        if (File.Exists(dll32) && Environment.Is64BitOperatingSystem)
        {
            Write(RegistryView.Registry32, dll32);
            report.Add("32-bit: " + dll32);
        }
        if (report.Count == 0)
            throw new FileNotFoundException($"{Dll64} / {Dll32} not found in {directory}");
        return string.Join(Environment.NewLine, report);
    }

    public static void Unregister()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            hklm.DeleteSubKeyTree(ClsidKey, throwOnMissingSubKey: false);
            hklm.DeleteSubKeyTree($@"{AsioRoot}\{DriverName}", throwOnMissingSubKey: false);
        }
    }

    public static bool IsRegistered()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey($@"{AsioRoot}\{DriverName}");
        return key?.GetValue("CLSID") is string s && s.Equals(Clsid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Real ASIO interfaces installed on this PC (AmpCast itself excluded).</summary>
    public static IReadOnlyList<string> GetHardwareDrivers()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var root = hklm.OpenSubKey(AsioRoot);
                if (root is null) continue;
                foreach (string name in root.GetSubKeyNames())
                {
                    using var sub = root.OpenSubKey(name);
                    string? clsid = sub?.GetValue("CLSID") as string;
                    if (clsid is null || clsid.Equals(Clsid, StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals(DriverName, StringComparison.OrdinalIgnoreCase)) continue;
                    names.Add(name);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Some vendors lock their keys; skip them.
            }
        }
        return new List<string>(names);
    }

    private static void Write(RegistryView view, string dllPath)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);

        using (var key = hklm.CreateSubKey(ClsidKey, writable: true))
            key.SetValue(string.Empty, "AmpCast ASIO Driver");

        using (var key = hklm.CreateSubKey(ClsidKey + @"\InprocServer32", writable: true))
        {
            key.SetValue(string.Empty, dllPath);
            key.SetValue("ThreadingModel", "Apartment");
        }

        using (var key = hklm.CreateSubKey($@"{AsioRoot}\{DriverName}", writable: true))
        {
            key.SetValue("CLSID", Clsid);
            key.SetValue("Description", DriverName);
        }
    }
}

/// <summary>
/// Per-user settings read by the native driver when a DAW opens it
/// (HKCU\Software\desNotto\AmpCast — no admin rights needed).
/// </summary>
public static class DriverConfig
{
    private const string KeyPath = @"Software\desNotto\AmpCast";

    public static string? AsioDevice
    {
        get => Read<string>("AsioDevice");
        set => WriteValue("AsioDevice", value ?? string.Empty, RegistryValueKind.String);
    }

    /// <summary>Endpoint ID the driver plays the stereo screen-share output to.</summary>
    public static string? ShareDevice
    {
        get => Read<string>("ShareDevice");
        set => WriteValue("ShareDevice", value ?? string.Empty, RegistryValueKind.String);
    }

    public static bool Lock48k
    {
        get => Read<object>("Lock48k") is not int v || v != 0;   // default: on
        set => WriteValue("Lock48k", value ? 1 : 0, RegistryValueKind.DWord);
    }

    private static T? Read<T>(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name) is T value ? value : default;
    }

    private static void WriteValue(string name, object value, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(name, value, kind);
    }
}
