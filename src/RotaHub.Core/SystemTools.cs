using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RotaHub.Core;

/// <summary>Start menu and desktop shortcuts, through Windows' own WScript.Shell (no extra library).</summary>
public static class Shortcuts
{
    public static string StartMenu(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", name + ".lnk");

    public static string Desktop(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name + ".lnk");

    public static void Create(string link, string target, string? arguments = null, string? description = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is not available.");
        var shell = Activator.CreateInstance(type)!;
        object? shortcut = null;
        try
        {
            shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link })!;
            var t = shortcut.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(target)! });
            if (arguments != null)
                t.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
            if (description != null)
                t.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut != null)
                Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}

/// <summary>Programs that must be closed before their files change.</summary>
public static class Processes
{
    public static IReadOnlyList<string> Running(IEnumerable<string> names) =>
        names.Where(n => n.Length > 0)
            .Where(n =>
            {
                var found = Process.GetProcessesByName(n);
                foreach (var p in found)
                    p.Dispose();
                return found.Length > 0;
            })
            .ToList();

    /// <summary>Asks each program to close (as if its window's X was clicked), then ends it after a wait.</summary>
    public static async Task<bool> CloseAsync(IEnumerable<string> names, TimeSpan wait)
    {
        var processes = names.SelectMany(Process.GetProcessesByName).ToList();
        try
        {
            foreach (var p in processes)
            {
                try { if (!p.HasExited) p.CloseMainWindow(); } catch (InvalidOperationException) { }
            }
            var until = DateTime.UtcNow + wait;
            while (DateTime.UtcNow < until && processes.Any(p => !p.HasExited))
                await Task.Delay(200).ConfigureAwait(false);
            foreach (var p in processes.Where(p => !p.HasExited))
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
            return processes.All(p => p.HasExited);
        }
        finally
        {
            foreach (var p in processes)
                p.Dispose();
        }
    }
}
