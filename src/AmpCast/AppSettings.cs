using System;
using System.IO;
using System.Text.Json;

namespace AmpCast;

/// <summary>User settings, stored in %APPDATA%\AmpCast\settings.json.</summary>
public sealed class AppSettings
{
    public string? MicDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public double MicVolume { get; set; } = 70;
    public double DawVolume { get; set; } = 70;
    public bool Broadcast { get; set; }
    public bool TrayHintShown { get; set; }

    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AmpCast", "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch
        {
            // corrupted file: start with defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // settings are a convenience; never crash on them
        }
    }
}
