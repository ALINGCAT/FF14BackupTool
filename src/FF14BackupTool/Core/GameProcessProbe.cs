using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;

namespace FF14BackupTool.Core
{
    public static class GameProcessProbe
    {
        public static bool IsGameRunning()
        {
            try
            {
                return Process.GetProcessesByName("ffxiv_dx11").Any()
                    || Process.GetProcessesByName("ffxiv").Any();
            }
            catch
            {
                return false;
            }
        }

        public static bool IsLauncherRunning()
        {
            try
            {
                return Process.GetProcessesByName("ffxivboot").Any()
                    || Process.GetProcessesByName("ffxivlauncher").Any();
            }
            catch
            {
                return false;
            }
        }
    }

    public static class ElevationHelper
    {
        public static bool IsAdministrator()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool RelaunchAsAdministrator(string arguments)
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                var startInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments ?? string.Empty,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(startInfo);
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // 1223 = the user cancelled the UAC prompt.
                return ex.NativeErrorCode == 1223 ? false : false;
            }
            catch
            {
                return false;
            }
        }
    }
}
