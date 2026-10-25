namespace RotaHub.Core;

/// <summary>Where RotaHub keeps its own files: %LOCALAPPDATA%\Rotaidem\RotaHub.</summary>
public sealed class HubPaths
{
    public HubPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rotaidem", "RotaHub");
    }

    public string Root { get; }

    /// <summary>RotaHub.exe once installed; running from anywhere else installs it here first.</summary>
    public string App => Path.Combine(Root, "app");

    public string Downloads => Path.Combine(Root, "downloads");

    public string Settings => Path.Combine(Root, "settings.json");

    public string Installed => Path.Combine(Root, "installed.json");

    public string Session => Path.Combine(Root, "session.dat");

    public string Log => Path.Combine(Root, "rotahub.log");

    public string DefaultLibrary => Path.Combine(Path.GetDirectoryName(Root)!, "Library");

    /// <summary>Writes a file through a temporary copy, so a crash never leaves half a file.</summary>
    public static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        if (File.Exists(path))
            File.Replace(temp, path, null);
        else
            File.Move(temp, path);
    }
}

/// <summary>The choices on the Settings page.</summary>
public sealed class HubSettings
{
    /// <summary>Where products are unpacked. Community packages are linked from here into the sim.</summary>
    public string LibraryDir { get; set; } = "";

    /// <summary>Community folders the user picked by hand, per sim ("msfs2024", "msfs2020").</summary>
    public Dictionary<string, string> CommunityOverrides { get; set; } = new();

    public bool LaunchAfterInstall { get; set; } = true;

    public bool DesktopShortcuts { get; set; } = true;

    public static HubSettings Load(HubPaths paths)
    {
        try
        {
            if (File.Exists(paths.Settings))
            {
                var s = JsonNode.Read<HubSettings>(File.ReadAllText(paths.Settings));
                if (s != null)
                {
                    s.CommunityOverrides ??= new();
                    if (string.IsNullOrWhiteSpace(s.LibraryDir))
                        s.LibraryDir = paths.DefaultLibrary;
                    return s;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
        }
        return new HubSettings { LibraryDir = paths.DefaultLibrary };
    }

    public void Save(HubPaths paths) => HubPaths.WriteAtomic(paths.Settings, JsonNode.Write(this));
}
