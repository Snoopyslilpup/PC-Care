using System.Windows;
using System.Windows.Threading;
using WhateverITPCCare.Helpers;

namespace WhateverITPCCare;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Make sure a desktop shortcut exists on the signed-in user's REAL desktop.
        // Done here (not just in the installer) so it works even when corporate policy
        // or a redirected/OneDrive desktop hid the installer-created icon.
        ShortcutHelper.EnsureDesktopShortcut();

        // Drop a self-contained uninstaller (Uninstall.cmd + desktop/Start-menu
        // shortcut) that works even without Inno Setup's registered uninstaller.
        UninstallHelper.EnsureUninstaller();

        // Never let an unexpected error crash the app silently – show a friendly
        // message instead so a home user is not left staring at a dead window.
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show(
                "Whatever IT PC Care hit an unexpected problem but will keep running.\n\n" +
                args.Exception.Message,
                "Whatever IT PC Care",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
    }
}
