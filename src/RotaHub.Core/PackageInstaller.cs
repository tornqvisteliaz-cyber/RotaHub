using System.Globalization;
using System.IO.Compression;

namespace RotaHub.Core;

public sealed record InstallResult(InstalledProduct Product, IReadOnlyList<string> Notes);

/// <summary>Programs of the product are open; close them (after asking) and try again.</summary>
public sealed class ProgramsOpenException : Exception
{
    public ProgramsOpenException(IReadOnlyList<string> names) : base(string.Join(", ", names) + " must be closed first.") => Names = names;

    public IReadOnlyList<string> Names { get; }
}

/// <summary>
/// Puts a downloaded, checked package in place:
/// 1. unpack it to Library\{product}\{version};
/// 2. copy app\ to the app's folder (when the release names one), dropping files the old version had and this one does not;
/// 3. link each community\{folder} into the sim's Community folder with a junction;
/// 4. shortcuts, then installed.json; older versions in the library are removed last.
/// Only ever replaces or removes things RotaHub or Rotaidem's own setups made.
/// </summary>
public sealed class PackageInstaller
{
    private readonly HubPaths _paths;
    private readonly HubSettings _settings;
    private readonly InstalledStore _store;

    public PackageInstaller(HubPaths paths, HubSettings settings, InstalledStore store)
    {
        _paths = paths;
        _settings = settings;
        _store = store;
        FindCommunity = sim => SimLocator.Find(sim, _settings);
    }

    /// <summary>The Community folder for a sim, or null when that sim is not installed. Tests replace it.</summary>
    public Func<string, SimInstall?> FindCommunity { get; set; }

    public Func<string, bool> SimRunning { get; set; } = SimLocator.IsRunning;

    public Func<IEnumerable<string>, IReadOnlyList<string>> ProgramsRunning { get; set; } = Processes.Running;

    public bool MakeShortcuts { get; set; } = true;

    public string Library => string.IsNullOrWhiteSpace(_settings.LibraryDir) ? _paths.DefaultLibrary : _settings.LibraryDir;

    public InstallResult Install(HubRelease release, string packageZip, IProgress<string>? step = null) =>
        InstallCore(release, staging => Extract(packageZip, staging), step);

    /// <summary>Like <see cref="Install"/>, from a folder already laid out as a package (app\, community\{folder}\). The folder is used up.</summary>
    public InstallResult InstallFolder(HubRelease release, string folder, IProgress<string>? step = null) =>
        InstallCore(release, staging => MoveFolder(folder, staging), step);

    private InstallResult InstallCore(HubRelease release, Action<string> fill, IProgress<string>? step)
    {
        var notes = new List<string>();
        var previous = _store.Get(release.Product);

        // Everything that can stop the install is checked before anything changes.
        var open = ProgramsRunning(release.Processes);
        if (open.Count > 0)
            throw new ProgramsOpenException(open);
        var targets = new List<(CommunityPart Part, SimInstall Sim)>();
        foreach (var part in release.Community)
        {
            var sim = FindCommunity(part.Sim);
            if (sim == null)
            {
                notes.Add($"{SimLocator.Title(part.Sim)} was not found, so {part.Folder} was not added to it. Pick its Community folder in Settings to add it.");
                continue;
            }
            if (SimRunning(part.Sim))
                throw new InvalidOperationException($"Close {SimLocator.Title(part.Sim)} first: its Community folder can't change while it runs.");
            var link = Path.Combine(sim.Community, part.Folder);
            if (!MayReplace(link, previous))
                throw new InvalidOperationException($"{link} belongs to something else (another add-on or add-on manager). Move it out of the Community folder and try again.");
            targets.Add((part, sim));
        }

        step?.Report("Unpacking");
        var productDir = Path.Combine(Library, Safe(release.Product));
        var versionName = release.Version.ToString(3);
        var versionDir = Path.Combine(productDir, versionName);
        var staging = versionDir + ".new";
        DeleteFolder(staging);
        fill(staging);
        foreach (var (part, _) in targets)
        {
            if (!Directory.Exists(Path.Combine(staging, "community", part.Folder)))
                throw new InvalidDataException($"The package has no community\\{part.Folder} folder.");
        }
        if (release.AppExe != null && !File.Exists(Path.Combine(staging, "app", release.AppExe)))
            throw new InvalidDataException($"The package has no app\\{release.AppExe}.");

        // Reinstalling the same version (repair): move the old copy aside, it is deleted at the end.
        if (Directory.Exists(versionDir))
            Directory.Move(versionDir, versionDir + ".old-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
        Directory.Move(staging, versionDir);

        var installed = new InstalledProduct
        {
            Product = release.Product,
            Version = versionName,
            VersionDir = versionDir,
            Processes = release.Processes.ToList(),
            InstalledAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };

        var appSource = Path.Combine(versionDir, "app");
        if (Directory.Exists(appSource))
        {
            step?.Report("Installing the app");
            if (release.AppDir != null)
            {
                installed.AppDir = HubCatalog.Expand(release.AppDir);
                installed.AppFiles = CopyTree(appSource, installed.AppDir);
                // Files the previous version had and this one does not (e.g. a removed DLL).
                if (previous != null && string.Equals(previous.AppDir, installed.AppDir, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var stale in previous.AppFiles.Except(installed.AppFiles, StringComparer.OrdinalIgnoreCase))
                        TryDeleteFile(Path.Combine(installed.AppDir, stale));
                }
                DeleteFolder(appSource);
            }
            else
            {
                installed.AppDir = appSource;
                installed.AppFiles = ListTree(appSource);
            }
            installed.AppExe = release.AppExe ?? "";
        }

        step?.Report("Linking into the simulator");
        foreach (var (part, sim) in targets)
        {
            var link = Path.Combine(sim.Community, part.Folder);
            RemoveExisting(link);
            var target = Path.Combine(versionDir, "community", part.Folder);
            Junction.Create(link, target);
            installed.Links.Add(new LinkRecord { Link = link, Target = target, Sim = part.Sim });
        }
        // Links of the previous version that this release no longer has.
        if (previous != null)
        {
            foreach (var old in previous.Links.Where(o => !installed.Links.Any(n => string.Equals(n.Link, o.Link, StringComparison.OrdinalIgnoreCase))))
                TryRemoveOurLink(old.Link);
        }

        if (MakeShortcuts && release.Shortcut != null && installed.ExePath is { } exe)
        {
            step?.Report("Adding shortcuts");
            var links = new List<string> { Shortcuts.StartMenu(release.Shortcut) };
            if (_settings.DesktopShortcuts)
                links.Add(Shortcuts.Desktop(release.Shortcut));
            foreach (var shortcut in links)
            {
                try
                {
                    Shortcuts.Create(shortcut, exe, description: release.Name + " by Rotaidem");
                    installed.Shortcuts.Add(shortcut);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    notes.Add("A shortcut could not be made: " + ex.Message);
                }
            }
        }

        _store.Put(installed);

        // Older versions and set-aside copies: nothing points at them any more.
        foreach (var dir in Directory.GetDirectories(productDir))
        {
            if (!string.Equals(dir, versionDir, StringComparison.OrdinalIgnoreCase))
                DeleteFolder(dir);
        }
        return new InstallResult(installed, notes);
    }

    /// <summary>Removes what RotaHub installed for the product. Files the app made itself (settings, logs) stay.</summary>
    public void Uninstall(InstalledProduct product)
    {
        var open = ProgramsRunning(product.Processes);
        if (open.Count > 0)
            throw new ProgramsOpenException(open);
        foreach (var link in product.Links)
        {
            if (SimRunning(link.Sim) && Directory.Exists(link.Link))
                throw new InvalidOperationException($"Close {SimLocator.Title(link.Sim)} first: its Community folder can't change while it runs.");
        }

        foreach (var link in product.Links)
            TryRemoveOurLink(link.Link);
        foreach (var shortcut in product.Shortcuts)
            TryDeleteFile(shortcut);
        if (product.AppDir.Length > 0 && !Junction.IsInside(product.AppDir, Library))
        {
            foreach (var file in product.AppFiles)
                TryDeleteFile(Path.Combine(product.AppDir, file));
            DeleteEmptyFolders(product.AppDir);
        }
        DeleteFolder(Path.Combine(Library, Safe(product.Product)));
        _store.Remove(product.Product);
    }

    /// <summary>What is wrong with an install (empty when all is in place): a missing exe, or a Community link gone or replaced.</summary>
    public IReadOnlyList<string> Check(InstalledProduct product)
    {
        var problems = new List<string>();
        if (product.ExePath is { } exe && !File.Exists(exe))
            problems.Add("The program file is missing.");
        foreach (var link in product.Links)
        {
            if (!Junction.IsLink(link.Link))
                problems.Add(Directory.Exists(link.Link)
                    ? $"{Path.GetFileName(link.Link)} in the Community folder was replaced by a plain folder."
                    : $"{Path.GetFileName(link.Link)} is missing from the Community folder.");
            else if (!string.Equals(Junction.Target(link.Link)?.TrimEnd('\\'), link.Target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                problems.Add($"{Path.GetFileName(link.Link)} in the Community folder points somewhere else.");
            else if (!Directory.Exists(link.Target))
                problems.Add($"The library copy of {Path.GetFileName(link.Link)} is missing.");
        }
        return problems;
    }

    /// <summary>
    /// A Community entry may be replaced when it is free, a link RotaHub made or that points into the
    /// library, or a folder Rotaidem's own setup put there (its manifest names Rotaidem or HeavyPro).
    /// </summary>
    private bool MayReplace(string link, InstalledProduct? previous)
    {
        if (!Directory.Exists(link) && !Junction.IsLink(link))
            return true;
        if (Junction.IsLink(link))
        {
            if (previous?.Links.Any(l => string.Equals(l.Link, link, StringComparison.OrdinalIgnoreCase)) == true)
                return true;
            var target = Junction.Target(link);
            return target != null && (Junction.IsInside(target, Library) || IsRotaidemPackage(target));
        }
        return IsRotaidemPackage(link);
    }

    internal static bool IsRotaidemPackage(string folder)
    {
        try
        {
            var manifest = Path.Combine(folder, "manifest.json");
            if (!File.Exists(manifest))
                return false;
            var creator = JsonNode.Parse(File.ReadAllText(manifest))["creator"].Str();
            return creator.Equals("Rotaidem", StringComparison.OrdinalIgnoreCase) || creator.Equals("HeavyPro", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void RemoveExisting(string link)
    {
        if (Junction.IsLink(link))
            Junction.Delete(link);
        else if (Directory.Exists(link))
            Directory.Delete(link, recursive: true);
    }

    private void TryRemoveOurLink(string link)
    {
        try
        {
            if (!Junction.IsLink(link))
                return;
            var target = Junction.Target(link);
            if (target == null || Junction.IsInside(target, Library))
                Junction.Delete(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
        }
    }

    internal static void Extract(string zipPath, string destination)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination).TrimEnd('\\') + "\\";
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var file = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The package contains an unsafe path: " + entry.FullName);
            if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
            {
                Directory.CreateDirectory(file);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            entry.ExtractToFile(file, overwrite: true);
        }
    }

    /// <summary>Moves a folder, copying when the library is on another drive.</summary>
    internal static void MoveFolder(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(from, to);
            return;
        }
        CopyTree(from, to);
        DeleteFolder(from);
    }

    private static List<string> CopyTree(string source, string destination)
    {
        var copied = new List<string>();
        foreach (var relative in ListTree(source))
        {
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(source, relative), target, overwrite: true);
            copied.Add(relative);
        }
        return copied;
    }

    private static List<string> ListTree(string folder)
    {
        var root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        return Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetFullPath(f).Substring(root.Length)).ToList();
    }

    private static void DeleteEmptyFolders(string folder)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (var sub in Directory.GetDirectories(folder))
            DeleteEmptyFolders(sub);
        if (!Directory.EnumerateFileSystemEntries(folder).Any())
        {
            try { Directory.Delete(folder); } catch (IOException) { }
        }
    }

    private static void DeleteFolder(string folder)
    {
        if (Junction.IsLink(folder))
        {
            Junction.Delete(folder);
            return;
        }
        if (!Directory.Exists(folder))
            return;
        // Never recurse through a link into someone else's files.
        foreach (var sub in Directory.GetDirectories(folder))
            DeleteFolder(sub);
        foreach (var file in Directory.GetFiles(folder))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        Directory.Delete(folder);
    }

    private static void TryDeleteFile(string file)
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

    private static string Safe(string name) => HubRelease.SafeName(name) ? name : throw new InvalidDataException("Unsafe product id: " + name);
}
