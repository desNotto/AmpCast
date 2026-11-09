using System;
using System.IO;

namespace AmpCast.Setup;

/// <summary>
/// Admin-only maintenance commands, executed by the installer (elevated):
///   AmpCast.exe --register-asio            register AmpCastAsio64/32.dll from the exe folder
///   AmpCast.exe --unregister-asio
///   AmpCast.exe --install-driver &lt;inf&gt; &lt;hwid&gt;
///   AmpCast.exe --uninstall-driver &lt;hwid&gt; [inf]
/// Exit code: 0 ok, 1 error, 3010 reboot required. Errors go to %TEMP%\AmpCast-setup.log.
/// </summary>
public static class CommandLine
{
    public const int NotACommand = -1;

    public static int Run(string[] args)
    {
        if (args.Length == 0) return NotACommand;
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--register-asio":
                    Log(AsioRegistration.Register(AppContext.BaseDirectory));
                    return 0;

                case "--unregister-asio":
                    AsioRegistration.Unregister();
                    return 0;

                case "--install-driver" when args.Length >= 3:
                    return DriverInstaller.Install(args[1], args[2]);

                case "--uninstall-driver" when args.Length >= 2:
                    return DriverInstaller.Uninstall(args[1], args.Length >= 3 ? args[2] : null);

                default:
                    return NotACommand;
            }
        }
        catch (Exception ex)
        {
            Log($"{string.Join(' ', args)}{Environment.NewLine}{ex}");
            return 1;
        }
    }

    private static void Log(string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "AmpCast-setup.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
        }
        catch
        {
            // logging must never break setup
        }
    }
}
