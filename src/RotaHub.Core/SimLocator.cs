using System.Diagnostics;

namespace RotaHub.Core;

/// <param name="Sim">"msfs2024" or "msfs2020".</param>
/// <param name="Edition">"Steam", "Microsoft Store" or "Chosen by you".</param>
public sealed record SimInstall(string Sim, string Edition, string Community)
{
    public string Title => SimLocator.Title(Sim);
}

/// <summary>Finds Microsoft Flight Simulator's Community folders (Steam and Microsoft Store, 2020 and 2024).</summary>
public static class SimLocator
{
    public static readonly string[] Sims = { "msfs2024", "msfs2020" };

    public static string Title(string sim) => sim == "msfs2020" ? "Microsoft Flight Simulator (2020)" : "Microsoft Flight Simulator 2024";

    /// <summary>The sim's process names, to make sure it is closed before its Community folder changes.</summary>
    public static string[] Processes(string sim) => sim == "msfs2020" ? new[] { "FlightSimulator" } : new[] { "FlightSimulator2024" };

    public static bool IsRunning(string sim) => Processes(sim).Any(name => Process.GetProcessesByName(name).Length > 0);

    /// <summary>The Community folder for <paramref name="sim"/>: the user's choice first, then what the sim's own UserCfg.opt says.</summary>
    public static SimInstall? Find(string sim, HubSettings settings)
    {
        if (settings.CommunityOverrides.TryGetValue(sim, out var chosen) && Directory.Exists(chosen))
            return new SimInstall(sim, "Chosen by you", chosen);
        return Detect(sim).FirstOrDefault();
    }

    public static IEnumerable<SimInstall> Detect(string sim)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var (steamDir, storePackage) = sim == "msfs2020"
            ? ("Microsoft Flight Simulator", "Microsoft.FlightSimulator_8wekyb3d8bbwe")
            : ("Microsoft Flight Simulator 2024", "Microsoft.Limitless_8wekyb3d8bbwe");
        var editions = new[]
        {
            ("Steam", Path.Combine(roaming, steamDir)),
            ("Microsoft Store", Path.Combine(local, "Packages", storePackage, "LocalCache"))
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (edition, folder) in editions)
        {
            // UserCfg.opt names where the sim keeps its packages; that can be any drive.
            var fromCfg = InstalledPackagesPath(Path.Combine(folder, "UserCfg.opt"));
            foreach (var packages in new[] { fromCfg, Path.Combine(folder, "Packages") })
            {
                if (packages == null)
                    continue;
                var community = Path.Combine(packages, "Community");
                if (Directory.Exists(community) && seen.Add(Path.GetFullPath(community)))
                    yield return new SimInstall(sim, edition, community);
            }
        }
    }

    internal static string? InstalledPackagesPath(string userCfg)
    {
        try
        {
            if (!File.Exists(userCfg))
                return null;
            foreach (var line in File.ReadLines(userCfg))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("InstalledPackagesPath", StringComparison.OrdinalIgnoreCase))
                    continue;
                var value = trimmed.Substring("InstalledPackagesPath".Length).Trim().Trim('"');
                return value.Length > 0 ? value : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }
}
