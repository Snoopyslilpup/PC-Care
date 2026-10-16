using System;
using System.IO;
using System.Text;

namespace WhateverITPCCare.Helpers;

/// <summary>
/// Provides a self-contained uninstaller that does NOT depend on Inno Setup's
/// unins000.exe. On locked-down PCs (or when the app was simply copied/run
/// without the installer) there is no registered uninstaller, so the app writes
/// its own "Uninstall.cmd" next to the exe and drops a matching desktop shortcut.
///
/// The generated script closes the app, removes the shortcuts and Start-menu
/// folder, then deletes the install folder from a detached process so it can
/// remove itself cleanly. All paths are resolved by the app (which knows the
/// REAL current-user desktop even when it is redirected) and baked into the
/// script as literals, so the script needs no path guessing of its own.
/// </summary>
public static class UninstallHelper
{
    private const string AppShortcut = "Whatever IT PC Care.lnk";
    private const string UninstallShortcut = "Uninstall Whatever IT PC Care.lnk";
    private const string UninstallScript = "Uninstall.cmd";

    public static void EnsureUninstaller()
    {
        try
        {
            string exePath = Environment.ProcessPath
                             ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                             ?? string.Empty;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return;

            string? exeDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(exeDir) || !Directory.Exists(exeDir))
                return;

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "Whatever IT PC Care");

            string scriptPath = Path.Combine(exeDir, UninstallScript);
            string script = BuildScript(exeDir, desktop, startMenu);
            File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

            // Desktop + Start-menu shortcut that launches the uninstaller, with the
            // wolf icon so it is recognisable.
            string iconPath = File.Exists(Path.Combine(exeDir, "app.ico"))
                ? Path.Combine(exeDir, "app.ico")
                : exePath;

            if (!string.IsNullOrWhiteSpace(desktop) && Directory.Exists(desktop))
            {
                ShortcutHelper.CreateShortcut(
                    Path.Combine(desktop, UninstallShortcut),
                    scriptPath, iconPath, exeDir);
            }

            try
            {
                Directory.CreateDirectory(startMenu);
                ShortcutHelper.CreateShortcut(
                    Path.Combine(startMenu, UninstallShortcut),
                    scriptPath, iconPath, exeDir);
            }
            catch { /* Start-menu is optional */ }
        }
        catch
        {
            // The uninstaller is a convenience - never let it break app launch.
        }
    }

    private static string BuildScript(string appDir, string desktop, string startMenu)
    {
        string appLnk = Path.Combine(desktop, AppShortcut);
        string uninstLnk = Path.Combine(desktop, UninstallShortcut);

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("title Uninstall Whatever IT PC Care");
        sb.AppendLine("echo.");
        sb.AppendLine("echo Uninstalling Whatever IT PC Care...");
        sb.AppendLine("echo.");
        sb.AppendLine("choice /C YN /N /M \"Remove Whatever IT PC Care from this PC? (Y/N) \"");
        sb.AppendLine("if errorlevel 2 goto :cancel");
        sb.AppendLine("echo Closing the app...");
        sb.AppendLine("taskkill /IM WhateverITPCCare.exe /F >nul 2>&1");
        sb.AppendLine("ping -n 2 127.0.0.1 >nul");
        sb.AppendLine("echo Removing shortcuts...");
        sb.AppendLine($"del \"{appLnk}\" >nul 2>&1");
        sb.AppendLine($"del \"{uninstLnk}\" >nul 2>&1");
        sb.AppendLine($"rmdir /S /Q \"{startMenu}\" >nul 2>&1");
        sb.AppendLine("echo Removing program files...");
        // Delete the install folder from a detached cmd so this script can remove itself.
        sb.AppendLine($"start \"\" /b cmd /c \"ping -n 3 127.0.0.1 >nul & rmdir /S /Q \"{appDir}\"\"");
        sb.AppendLine("echo.");
        sb.AppendLine("echo Whatever IT PC Care has been removed. You can close this window.");
        sb.AppendLine("ping -n 3 127.0.0.1 >nul");
        sb.AppendLine("exit /b 0");
        sb.AppendLine(":cancel");
        sb.AppendLine("echo Uninstall cancelled.");
        sb.AppendLine("ping -n 2 127.0.0.1 >nul");
        sb.AppendLine("exit /b 1");
        return sb.ToString();
    }
}
