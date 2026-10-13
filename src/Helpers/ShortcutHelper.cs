using System;
using System.IO;

namespace WhateverITPCCare.Helpers;

public static class ShortcutHelper
{
    private const string ShortcutName = "Whatever IT PC Care.lnk";

    public static void EnsureDesktopShortcut()
    {
        try
        {
            string exePath = Environment.ProcessPath
                             ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                             ?? string.Empty;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return;

            string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(userDesktop) || !Directory.Exists(userDesktop))
                return;

            // Avoid duplicate icons: the installer already drops a shortcut on the
            // shared "all users" desktop. If one is present there OR on this user's
            // own desktop, leave it alone - do NOT add a second.
            string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            foreach (string dir in new[] { userDesktop, commonDesktop })
            {
                if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, ShortcutName)))
                    return;
            }

            string lnkPath = Path.Combine(userDesktop, ShortcutName);

            string? exeDir = Path.GetDirectoryName(exePath);
            string iconPath = exeDir != null && File.Exists(Path.Combine(exeDir, "app.ico"))
                ? Path.Combine(exeDir, "app.ico")
                : exePath;

            CreateShortcut(lnkPath, exePath, iconPath, exeDir ?? string.Empty);
        }
        catch
        {
        }
    }

    internal static void CreateShortcut(string lnkPath, string targetExe, string iconPath, string workingDir, string arguments = "")
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
            return;

        object? shell = Activator.CreateInstance(shellType);
        if (shell == null)
            return;

        try
        {
            dynamic wsh = shell;
            dynamic shortcut = wsh.CreateShortcut(lnkPath);
            shortcut.TargetPath = targetExe;
            shortcut.WorkingDirectory = workingDir;
            if (!string.IsNullOrEmpty(arguments))
                shortcut.Arguments = arguments;
            shortcut.IconLocation = iconPath + ",0";
            shortcut.Description = "Whatever IT PC Care";
            shortcut.Save();
        }
        finally
        {
            try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
            catch { }
        }
    }
}
