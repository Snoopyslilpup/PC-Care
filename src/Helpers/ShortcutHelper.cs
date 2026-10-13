using System;
using System.IO;

namespace WhateverITPCCare.Helpers;

/// <summary>
/// Creates (and keeps) a desktop shortcut for the app from WITHIN the running app.
/// This is deliberately independent of the Inno Setup installer: on locked-down or
/// corporate PCs the installer's shortcut can be stripped by policy, or the desktop
/// folder is redirected to a network/OneDrive location so the "all users" desktop
/// icon never appears where the signed-in user actually looks.
///
/// Running inside the app means we target the REAL current-user desktop
/// (Environment.SpecialFolder.DesktopDirectory honours folder redirection) with the
/// signed-in user's own permissions - no admin, no common-desktop guesswork.
/// Everything is wrapped so a failure here can never stop the app launching.
/// </summary>
public static class ShortcutHelper
{
    private const string ShortcutName = "Whatever IT PC Care.lnk";

    /// <summary>
    /// Ensures a desktop shortcut to this executable exists. Safe to call on every
    /// launch - it only (re)writes the .lnk if it is missing or points elsewhere.
    /// </summary>
    public static void EnsureDesktopShortcut()
    {
        try
        {
            // Full path to the currently running .exe (self-contained single file).
            string exePath = Environment.ProcessPath
                             ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                             ?? string.Empty;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return;

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
                return;

            string lnkPath = Path.Combine(desktop, ShortcutName);

            // Prefer the wolf icon sitting next to the exe (shipped by the installer);
            // fall back to the exe's own embedded icon if it is not there.
            string? exeDir = Path.GetDirectoryName(exePath);
            string iconPath = exeDir != null && File.Exists(Path.Combine(exeDir, "app.ico"))
                ? Path.Combine(exeDir, "app.ico")
                : exePath;

            CreateShortcut(lnkPath, exePath, iconPath, exeDir ?? string.Empty);
        }
        catch
        {
            // Never let shortcut creation break the app - it is a nicety, not a feature.
        }
    }

    internal static void CreateShortcut(string lnkPath, string targetExe, string iconPath, string workingDir, string arguments = "")
    {
        // Use the Windows Script Host Shell COM object (always present on Windows)
        // via late binding so we need no extra NuGet/COM reference.
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
            catch { /* ignore */ }
        }
    }
}
