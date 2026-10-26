using System.Web.Script.Serialization;

namespace RotaHub.Core;

/// <summary>A Community folder link RotaHub made: the link in the sim and the library folder it points at.</summary>
public sealed class LinkRecord
{
    public string Link { get; set; } = "";
    public string Target { get; set; } = "";
    public string Sim { get; set; } = "";
}

/// <summary>Everything RotaHub put on this PC for one product, so uninstall removes exactly that and nothing else.</summary>
public sealed class InstalledProduct
{
    public string Product { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>Library\{product}\{version}: the unpacked package.</summary>
    public string VersionDir { get; set; } = "";
    /// <summary>Where the app's files went (its own folder, or app\ in the library).</summary>
    public string AppDir { get; set; } = "";
    public string AppExe { get; set; } = "";
    /// <summary>App files relative to AppDir. Files the app makes itself (settings, logs) are not listed and stay.</summary>
    public List<string> AppFiles { get; set; } = new();
    public List<LinkRecord> Links { get; set; } = new();
    public List<string> Shortcuts { get; set; } = new();
    public List<string> Processes { get; set; } = new();
    public string InstalledAt { get; set; } = "";
    /// <summary>"package" (from a signed release) or "setup" (the product's own installer ran).</summary>
    public string Kind { get; set; } = "package";

    /// <summary>
    /// For "setup": size and time of the program file right after RotaHub ran the installer. While it is
    /// unchanged, the version is the one from the website, whatever the file itself claims.
    /// </summary>
    public string ExeStamp { get; set; } = "";

    [ScriptIgnore]
    public Version? ParsedVersion => HubRelease.ParseVersion(Version);

    [ScriptIgnore]
    public string? ExePath => AppExe.Length == 0 ? null : Path.Combine(AppDir, AppExe);
}

/// <summary>installed.json in RotaHub's folder.</summary>
public sealed class InstalledStore
{
    private readonly string _file;
    private readonly object _gate = new();

    public InstalledStore(HubPaths paths) => _file = paths.Installed;

    public InstalledProduct? Get(string product) =>
        Load().FirstOrDefault(p => string.Equals(p.Product, product, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<InstalledProduct> All() => Load();

    public void Put(InstalledProduct product)
    {
        lock (_gate)
        {
            var all = Load().Where(p => !string.Equals(p.Product, product.Product, StringComparison.OrdinalIgnoreCase)).ToList();
            all.Add(product);
            HubPaths.WriteAtomic(_file, JsonNode.Write(all));
        }
    }

    public void Remove(string product)
    {
        lock (_gate)
        {
            var all = Load().Where(p => !string.Equals(p.Product, product, StringComparison.OrdinalIgnoreCase)).ToList();
            HubPaths.WriteAtomic(_file, JsonNode.Write(all));
        }
    }

    private List<InstalledProduct> Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_file))
                    return JsonNode.Read<List<InstalledProduct>>(File.ReadAllText(_file)) ?? new List<InstalledProduct>();
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
            {
            }
            return new List<InstalledProduct>();
        }
    }
}
