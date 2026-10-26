namespace RotaHub.Core;

/// <summary>What a downloaded zip turned into: the program to launch (if any) and the Community packages in it.</summary>
public sealed record ArrangedZip(string? AppExe, IReadOnlyList<CommunityPart> Community);

/// <summary>
/// Turns an unpacked download into RotaHub's package layout (app\, community\{folder}\), so a plain
/// zip set as a product's download link in the website admin installs like a RotaHub package:
/// - already app\ and/or community\: kept as it is;
/// - MSFS packages (folders with a manifest.json, anywhere in the first few levels): each goes to community\;
/// - anything else: everything goes to app\, and a single .exe at the top becomes the program to launch.
/// A zip that only wraps everything in one folder is looked into first.
/// </summary>
public static class ZipLayout
{
    public static ArrangedZip Arrange(string unpacked, string layout, string sim, string fallbackName = "package")
    {
        var root = Unwrap(unpacked);

        if (Directory.Exists(Path.Combine(root, "app")) || Directory.Exists(Path.Combine(root, "community")))
        {
            PackageInstaller.MoveFolder(root, layout);
            return new ArrangedZip(SingleExe(Path.Combine(layout, "app")), CommunityIn(Path.Combine(layout, "community"), sim));
        }

        var packages = IsMsfsPackage(root) ? new List<string> { root } : FindPackages(root, depth: 3);
        if (packages.Count > 0)
        {
            var community = Path.Combine(layout, "community");
            Directory.CreateDirectory(community);
            var parts = new List<CommunityPart>();
            foreach (var package in packages)
            {
                var name = package == root ? Safe(fallbackName) : Path.GetFileName(package);
                if (!HubRelease.SafeName(name) || parts.Any(p => string.Equals(p.Folder, name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                PackageInstaller.MoveFolder(package, Path.Combine(community, name));
                parts.Add(new CommunityPart(name, sim));
            }
            return new ArrangedZip(null, parts);
        }

        Directory.CreateDirectory(layout);
        PackageInstaller.MoveFolder(root, Path.Combine(layout, "app"));
        return new ArrangedZip(SingleExe(Path.Combine(layout, "app")), Array.Empty<CommunityPart>());
    }

    /// <summary>An MSFS package folder: its manifest.json describes it (layout.json lists its files).</summary>
    internal static bool IsMsfsPackage(string folder) =>
        File.Exists(Path.Combine(folder, "manifest.json")) && File.Exists(Path.Combine(folder, "layout.json"));

    private static string Unwrap(string folder)
    {
        while (true)
        {
            var dirs = Directory.GetDirectories(folder);
            if (dirs.Length != 1 || Directory.GetFiles(folder).Length > 0 || IsMsfsPackage(folder) || IsMsfsPackage(dirs[0])
                || string.Equals(Path.GetFileName(dirs[0]), "app", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(dirs[0]), "community", StringComparison.OrdinalIgnoreCase))
                return folder;
            folder = dirs[0];
        }
    }

    private static List<string> FindPackages(string folder, int depth)
    {
        var found = new List<string>();
        if (depth <= 0)
            return found;
        foreach (var dir in Directory.GetDirectories(folder))
        {
            if (IsMsfsPackage(dir))
                found.Add(dir);
            else
                found.AddRange(FindPackages(dir, depth - 1));
        }
        return found;
    }

    private static IReadOnlyList<CommunityPart> CommunityIn(string community, string sim) =>
        Directory.Exists(community)
            ? Directory.GetDirectories(community).Select(d => new CommunityPart(Path.GetFileName(d), sim)).Where(p => HubRelease.SafeName(p.Folder)).ToList()
            : Array.Empty<CommunityPart>();

    private static string? SingleExe(string app)
    {
        if (!Directory.Exists(app))
            return null;
        var exes = Directory.GetFiles(app, "*.exe", SearchOption.TopDirectoryOnly);
        return exes.Length == 1 ? Path.GetFileName(exes[0]) : null;
    }

    private static string Safe(string name)
    {
        var clean = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray()).Trim('.');
        return clean.Length > 0 ? clean : "package";
    }
}
