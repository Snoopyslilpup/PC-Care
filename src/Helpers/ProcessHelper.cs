using System;
using System.Diagnostics;
using System.Text;

namespace WhateverITPCCare.Helpers;

public static class ProcessHelper
{
    /// <summary>
    /// Run a console command and capture its output. Used for safe, well-known
    /// Windows maintenance commands (ipconfig, sfc, DISM, net stop/start, etc.).
    /// </summary>
    public static (int code, string output) Run(string file, string args, bool elevated = false, int timeoutMs = 600000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = elevated,
            CreateNoWindow = !elevated,
            RedirectStandardOutput = !elevated,
            RedirectStandardError = !elevated,
        };

        if (elevated)
            psi.Verb = "runas"; // triggers the UAC prompt

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (-1, "Could not start process.");

            if (elevated)
            {
                p.WaitForExit(timeoutMs);
                return (p.HasExited ? p.ExitCode : -1, "Completed (ran with Administrator rights).");
            }

            var sb = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return (-1, "Timed out.");
            }
            return (p.ExitCode, sb.ToString());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Usually the user clicked "No" on the UAC prompt.
            return (-1, "Cancelled – Administrator permission was not granted.");
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>Open a path, URL or shell item with the default handler.</summary>
    public static void OpenShell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* ignore – nothing we can safely do */ }
    }
}
