using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;

namespace RotaHub.Core;

/// <summary>A step of an install for the progress bar; Fraction is null while the length is unknown.</summary>
public readonly record struct InstallProgress(string Step, double? Fraction, string Detail = "");

/// <summary>Ties the website, the signed releases and the installer together for the app's pages.</summary>
public sealed class HubService
{
    private readonly HttpClient _http;

    public HubService(HubPaths paths, HubSettings settings, RotaidemApi api, string? releaseKeyJwk)
    {
        Paths = paths;
        Settings = settings;
        Api = api;
        ReleaseKeyJwk = releaseKeyJwk;
        Installed = new InstalledStore(paths);
        Installer = new PackageInstaller(paths, settings, Installed);
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "RotaHub (Windows)");
    }

    public HubPaths Paths { get; }
    public HubSettings Settings { get; }
    public RotaidemApi Api { get; }
    public string? ReleaseKeyJwk { get; }
    public InstalledStore Installed { get; }
    public PackageInstaller Installer { get; }

    /// <summary>
    /// The newest release of a website product, from any of: its signed GitHub release, the RotaHub
    /// section of the website admin, or just its download link and version in the admin. The highest
    /// version wins; on a tie the signed one, then the website's RotaHub section.
    /// </summary>
    public async Task<(HubRelease? Release, string Problem)> NewestReleaseAsync(SiteProduct site, HubProduct product, CancellationToken ct = default)
    {
        var (signed, problem) = await LatestReleaseAsync(product, ct).ConfigureAwait(false);
        var best = new[] { signed, site.Release, site.DownloadRelease }
            .Where(r => r != null)
            .OrderByDescending(r => r!.Version).ThenByDescending(r => r!.Rank)
            .FirstOrDefault();
        if (best != null)
            return (best, "");
        return (null, site.ReleaseProblem.Length > 0 ? site.ReleaseProblem : problem);
    }

    /// <summary>The newest signed release of a product; null with the reason when there is none or it can't be trusted.</summary>
    public async Task<(HubRelease? Release, string Problem)> LatestReleaseAsync(HubProduct product, CancellationToken ct = default)
    {
        if (product.ManifestUrl == null)
            return (null, "");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await _http.GetAsync(product.ManifestUrl, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (null, "No RotaHub release has been published for this product yet.");
            if (!response.IsSuccessStatusCode)
                return (null, $"The release check failed ({(int)response.StatusCode}).");
            var jws = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var release = HubRelease.Verify(jws, ReleaseKeyJwk, product.Id, out var problem);
            return (release, problem);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, "Could not check for releases. Check your internet connection.");
        }
    }

    /// <summary>What is on this PC: RotaHub's record, else an install its own setup made (taken over on the next install).</summary>
    public InstalledProduct? Detect(HubProduct product)
    {
        var record = Installed.Get(product.Id);
        if (record != null && (record.Kind != "setup" || product.LegacyExe == null))
            return record;
        // Installed by its own setup: the program file says whether it is still there.
        if (product.LegacyExe == null)
            return null;
        var exe = HubCatalog.Expand(product.LegacyExe);
        if (!File.Exists(exe))
            return null;
        var info = FileVersionInfo.GetVersionInfo(exe);
        var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        if (record != null && HubRelease.ParseVersion(record.Version) is { } known)
        {
            // RotaHub installed this version: trust the website's number. A program file's own version
            // can be stale (HeavyPro's test builds all say 0.4.0), which would offer the same update forever.
            // If the file changed since (it updated itself), the higher of the two counts.
            if (record.ExeStamp != ExeStamp(exe) && version > known)
                record.Version = version.ToString(3);
            return record;
        }
        return new InstalledProduct
        {
            Product = product.Id,
            Version = version.ToString(3),
            AppDir = Path.GetDirectoryName(exe)!,
            AppExe = Path.GetFileName(exe),
            Processes = { Path.GetFileNameWithoutExtension(exe) },
            Kind = "setup",
        };
    }

    /// <summary>
    /// Installs or updates to <paramref name="release"/>. Null when the download was something RotaHub
    /// can't install by itself (it is then shown in Explorer). Errors reach the caller as they are
    /// (ProgramsOpenException asks to close the program); a continuation that only ran on success
    /// turned every one of them into "A task was canceled".
    /// </summary>
    public async Task<InstallResult?> InstallAsync(HubRelease release, SiteProduct site, HubProduct product, IProgress<InstallProgress>? progress, CancellationToken ct) =>
        release.IsDownload
            ? await InstallDownloadAsync(release, site, product, progress, ct).ConfigureAwait(false)
            : await InstallPackageAsync(release, progress, ct).ConfigureAwait(false);

    private async Task<InstallResult> InstallPackageAsync(HubRelease release, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var zip = Path.Combine(Paths.Downloads, $"{release.Product}-{release.Version.ToString(3)}.zip");
        progress?.Report(new InstallProgress("Downloading", 0));
        var url = release.PackageUrl;
        if (release.NeedsDownloadLink)
        {
            // A licence-only package: the website gives the link to licence holders only.
            url = await Api.PackageUrlAsync(release.Product, ct).ConfigureAwait(false);
            if (!HubRelease.TrustedPackageHost(url, Api.BaseUrl))
                throw new InvalidDataException("The website gave a package link RotaHub does not trust.");
        }
        await Downloader.DownloadAsync(_http, url, zip, release.PackageSha256.Length == 0 ? null : release.PackageSha256, release.PackageSize,
            new Progress<DownloadProgress>(p => progress?.Report(new InstallProgress("Downloading", p.Fraction, Size(p.Received, p.Total)))), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var result = await Task.Run(() => Installer.Install(release, zip, new Progress<string>(s => progress?.Report(new InstallProgress(s, null)))), ct).ConfigureAwait(false);
        TryDelete(zip);
        return result;
    }

    /// <summary>
    /// Installs the product's download link from Admin → Products, at the version set there:
    /// - an .exe or .msi is run (the product's own installer) and the version remembered, so a newer
    ///   version on the website shows as an update;
    /// - a zip laid out as a RotaHub package (app\, community\) is installed as one;
    /// - a zip of MSFS packages (folders with a manifest.json) is linked into the Community folder;
    /// - any other zip is unpacked into the library, its one top-level .exe (if any) becoming Launch;
    /// - anything else is shown in Explorer.
    /// </summary>
    private async Task<InstallResult?> InstallDownloadAsync(HubRelease release, SiteProduct site, HubProduct product, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallProgress("Getting the download", null));
        // Through the website, so licence-only downloads are checked and the download is counted.
        var url = await Api.DownloadUrlAsync(site.Id, ct).ConfigureAwait(false);
        var name = Path.GetFileName(new Uri(url).LocalPath);
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            name = site.Id + ".exe";
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var file = Path.Combine(Paths.Downloads, $"{site.Id}-{release.Version.ToString(3)}{extension}");
        TryDelete(file);
        await Downloader.DownloadAsync(_http, url, file, null, 0,
            new Progress<DownloadProgress>(p => progress?.Report(new InstallProgress("Downloading", p.Total > 0 ? p.Fraction : null, Size(p.Received, p.Total)))), ct).ConfigureAwait(false);

        if (extension is ".exe" or ".msi")
        {
            var exe = product.LegacyExe == null ? null : HubCatalog.Expand(product.LegacyExe);
            var folder = exe == null ? null : Path.GetDirectoryName(exe)!;
            var before = folder == null ? 0 : FolderStamp(folder);
            var updating = exe != null && File.Exists(exe) && product.UpdateArgs != null;
            progress?.Report(new InstallProgress(updating ? $"Updating {site.Name}" : "Running the installer", null,
                updating ? "" : "Finish the installer's steps."));
            var exitCode = 0;
            using (var process = Process.Start(new ProcessStartInfo(file, updating ? product.UpdateArgs! : "") { UseShellExecute = true }))
            {
                if (process != null)
                {
                    await Task.Run(() => process.WaitForExit(), ct).ConfigureAwait(false);
                    exitCode = process.ExitCode;
                }
            }
            TryDelete(file);
            // A cancelled or failed installer: nothing changed, so nothing is remembered.
            if (exitCode != 0)
                throw new InvalidOperationException($"The {site.Name} installer closed without installing (code {exitCode}).");
            // Some installers return 0 when closed without installing: when RotaHub knows the program,
            // it must be there and something in its folder must have been written, or nothing is remembered.
            // (The folder, not the exe: an installer may restore the exe's original file time.)
            if (exe != null && (!File.Exists(exe) || FolderStamp(folder!) == before))
                throw new InvalidOperationException($"The {site.Name} installer closed without installing it.");
            var after = exe == null ? null : ExeStamp(exe);
            var record = Installed.Get(product.Id) ?? new InstalledProduct { Product = product.Id };
            record.Kind = "setup";
            record.Version = release.Version.ToString(3);
            record.InstalledAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            record.ExeStamp = after ?? "";
            if (exe != null)
            {
                record.AppDir = Path.GetDirectoryName(exe)!;
                record.AppExe = Path.GetFileName(exe);
                record.Processes = new List<string> { Path.GetFileNameWithoutExtension(exe) };
            }
            Installed.Put(record);
            return new InstallResult(record, Array.Empty<string>());
        }

        if (extension != ".zip")
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = true })?.Dispose();
            return null;
        }

        progress?.Report(new InstallProgress("Unpacking", null));
        var unpacked = Path.Combine(Paths.Downloads, site.Id + "-unpacked");
        var layout = Path.Combine(Paths.Downloads, site.Id + "-package");
        return await Task.Run(() =>
        {
            try
            {
                foreach (var dir in new[] { unpacked, layout })
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                }
                PackageInstaller.Extract(file, unpacked);
                var shaped = ZipLayout.Arrange(unpacked, layout, site.Sim, site.Id);
                var package = release with
                {
                    AppExe = shaped.AppExe,
                    Shortcut = shaped.AppExe == null ? null : site.Name,
                    Community = shaped.Community,
                    Processes = shaped.AppExe == null ? Array.Empty<string>() : new[] { Path.GetFileNameWithoutExtension(shaped.AppExe) },
                };
                return (InstallResult?)Installer.InstallFolder(package, layout, new Progress<string>(s => progress?.Report(new InstallProgress(s, null))));
            }
            finally
            {
                foreach (var dir in new[] { unpacked, layout })
                {
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
                }
                TryDelete(file);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// For products without a RotaHub release: downloads the installer the website offers and runs it.
    /// Returns once that installer has closed.
    /// </summary>
    public async Task RunWebsiteInstallerAsync(SiteProduct product, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallProgress("Getting the download", null));
        var url = await Api.DownloadUrlAsync(product.Id, ct).ConfigureAwait(false);
        var name = Path.GetFileName(new Uri(url).LocalPath);
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            name = product.Id + ".exe";
        var file = Path.Combine(Paths.Downloads, name);
        TryDelete(file);
        await Downloader.DownloadAsync(_http, url, file, null, 0,
            new Progress<DownloadProgress>(p => progress?.Report(new InstallProgress("Downloading", p.Total > 0 ? p.Fraction : null, Size(p.Received, p.Total)))), ct).ConfigureAwait(false);

        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            // A zip or anything else: show it in Explorer rather than guess how to install it.
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = true })?.Dispose();
            return;
        }
        progress?.Report(new InstallProgress("Running the installer", null, "Finish the installer's steps, then come back here."));
        using var process = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        if (process != null)
            await Task.Run(() => process.WaitForExit(), ct).ConfigureAwait(false);
    }

    /// <summary>Size and last write time of a file: changes whenever an installer replaces it. Null when it is missing.</summary>
    public static string? ExeStamp(string file)
    {
        var info = new FileInfo(file);
        return info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The newest file time in a folder: changes whenever an installer writes anything there.</summary>
    public static long FolderStamp(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => File.GetLastWriteTimeUtc(f).Ticks).DefaultIfEmpty(0).Max()
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static string Size(long received, long total)
    {
        static string Mb(long b) => (b / 1048576.0).ToString(b < 10 * 1048576 ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB";
        return total > 0 ? $"{Mb(received)} of {Mb(total)}" : Mb(received);
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
