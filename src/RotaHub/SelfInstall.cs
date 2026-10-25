using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using Microsoft.Win32;
using RotaHub.Core;

namespace RotaHub;

/// <summary>
/// RotaHub installs itself: the downloaded RotaHub.exe copies itself to %LOCALAPPDATA%\Rotaidem\RotaHub\app,
/// adds Start menu and desktop shortcuts and an entry in Windows' Apps list (with an uninstaller), and
/// restarts from there. No administrator rights, no separate setup program.
/// </summary>
public static class SelfInstall
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RotaHub";

    public static string CurrentExe => Process.GetCurrentProcess().MainModule!.FileName;

    public static string InstalledExe(HubPaths paths) => Path.Combine(paths.App, "RotaHub.exe");

    public static bool IsInstalledCopy(HubPaths paths) =>
        string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe(paths)), StringComparison.OrdinalIgnoreCase);

    public static Version Current => HubRelease.Normalize(typeof(SelfInstall).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Copies this exe into place and registers it. Returns the installed exe.</summary>
    public static string Install(HubPaths paths)
    {
        var target = InstalledExe(paths);
        Directory.CreateDirectory(paths.App);
        File.Copy(CurrentExe, target, overwrite: true);
        Register(paths);
        Log.Info($"Installed RotaHub {Current} to {target}");
        return target;
    }

    /// <summary>Shortcuts and the Apps list entry; also refreshes the version shown there after an update.</summary>
    public static void Register(HubPaths paths)
    {
        var exe = InstalledExe(paths);
        try
        {
            Shortcuts.Create(Shortcuts.StartMenu("RotaHub"), exe, description: "RotaHub by Rotaidem");
            if (!File.Exists(Shortcuts.Desktop("RotaHub")))
                Shortcuts.Create(Shortcuts.Desktop("RotaHub"), exe, description: "RotaHub by Rotaidem");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Log.Error("Shortcuts", ex);
        }

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "RotaHub");
        key.SetValue("DisplayVersion", Current.ToString(3));
        key.SetValue("Publisher", "Rotaidem");
        key.SetValue("DisplayIcon", exe + ",0");
        key.SetValue("InstallLocation", paths.App);
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("URLInfoAbout", "https://rotaidem.com");
        key.SetValue("HelpLink", "https://rotaidem.com/support");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
    }

    /// <summary>Removes RotaHub (and, if asked, every product it installed). The exe deletes itself after it exits.</summary>
    public static void Uninstall(HubPaths paths, HubService? hub, bool removeProducts)
    {
        if (removeProducts && hub != null)
        {
            foreach (var product in hub.Installed.All())
            {
                try
                {
                    hub.Installer.Uninstall(product);
                }
                catch (Exception ex)
                {
                    Log.Error("Uninstalling " + product.Product, ex);
                }
            }
        }
        foreach (var link in new[] { Shortcuts.StartMenu("RotaHub"), Shortcuts.Desktop("RotaHub") })
        {
            try { if (File.Exists(link)) File.Delete(link); } catch (IOException) { }
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        new SessionStore(paths).Clear();

        // A running exe can't delete itself: a hidden command prompt does it a moment after RotaHub closes.
        // installed.json stays, so products kept on the PC are recognised if RotaHub is installed again.
        var script = $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{paths.App}\" & rmdir /s /q \"{paths.Downloads}\" & rmdir /s /q \"{Path.Combine(paths.Root, "cache")}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", script) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
    }

    /// <summary>
    /// Replaces the installed RotaHub.exe with the release's and starts it. A running exe can be renamed
    /// though not overwritten, so the old one becomes RotaHub.old.exe and is deleted on the next start.
    /// </summary>
    public static async Task UpdateAsync(HubService hub, HubRelease release, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var zip = Path.Combine(hub.Paths.Downloads, $"rotahub-{release.Version.ToString(3)}.zip");
        await Downloader.DownloadAsync(hub.Api.Http, release.PackageUrl, zip, release.PackageSha256, release.PackageSize,
            new Progress<DownloadProgress>(p => progress?.Report(new InstallProgress("Downloading RotaHub " + release.Version.ToString(3), p.Fraction))), ct);

        var fresh = Path.Combine(hub.Paths.Downloads, "RotaHub.new.exe");
        using (var archive = ZipFile.OpenRead(zip))
        {
            // Some zip tools write backslashes in entry names.
            var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), "app/RotaHub.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The RotaHub release has no app/RotaHub.exe.");
            entry.ExtractToFile(fresh, overwrite: true);
        }
        File.Delete(zip);

        var exe = InstalledExe(hub.Paths);
        var old = Path.Combine(hub.Paths.App, "RotaHub.old.exe");
        if (File.Exists(old))
            File.Delete(old);
        if (File.Exists(exe))
            File.Move(exe, old);
        File.Move(fresh, exe);
        Log.Info($"Updated RotaHub {Current} -> {release.Version}");
        Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = true })?.Dispose();
    }

    public static void CleanUpAfterUpdate(HubPaths paths)
    {
        try
        {
            var old = Path.Combine(paths.App, "RotaHub.old.exe");
            if (File.Exists(old))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
