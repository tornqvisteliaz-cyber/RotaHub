using System.IO.Compression;
using RotaHub.Core;
using Xunit;

namespace RotaHub.Tests;

public sealed class PackageInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rotahub-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _community;
    private readonly string _appDir;
    private readonly HubPaths _paths;
    private readonly HubSettings _settings;
    private readonly InstalledStore _store;
    private readonly PackageInstaller _installer;

    public PackageInstallerTests()
    {
        _community = Path.Combine(_root, "Community");
        _appDir = Path.Combine(_root, "HeavyPro");
        Directory.CreateDirectory(_community);
        _paths = new HubPaths(Path.Combine(_root, "hub"));
        _settings = new HubSettings { LibraryDir = Path.Combine(_root, "Library") };
        _store = new InstalledStore(_paths);
        _installer = new PackageInstaller(_paths, _settings, _store)
        {
            FindCommunity = sim => new SimInstall(sim, "Test", _community),
            SimRunning = _ => false,
            ProgramsRunning = _ => Array.Empty<string>(),
            MakeShortcuts = false,
        };
    }

    public void Dispose()
    {
        foreach (var link in Directory.Exists(_community) ? Directory.GetDirectories(_community) : Array.Empty<string>())
        {
            if (Junction.IsLink(link))
                Junction.Delete(link);
        }
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private HubRelease Release(string version, bool withPanel = true) => new(
        "rotaidem-heavypro", "HeavyPro", new Version(version), "", "https://example.com/p.zip", new string('a', 64), 0,
        _appDir, "HeavyPro.exe", "HeavyPro", new[] { "HeavyPro" },
        withPanel ? new[] { new CommunityPart("rotaidem-heavypro-panel", "msfs2024") } : Array.Empty<CommunityPart>(), "");

    private string Package(string version, params string[] extraFiles)
    {
        var zip = Path.Combine(_root, $"pkg-{version}-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        void Add(string name, string text)
        {
            using var w = new StreamWriter(archive.CreateEntry(name).Open());
            w.Write(text);
        }
        Add("app/HeavyPro.exe", "exe " + version);
        Add("community/rotaidem-heavypro-panel/manifest.json", "{\"creator\": \"Rotaidem\", \"package_version\": \"" + version + "\"}");
        foreach (var f in extraFiles)
            Add(f, "x");
        return zip;
    }

    [Fact]
    public void Installs_app_and_links_the_panel_into_Community()
    {
        var result = _installer.Install(Release("0.4.1"), Package("0.4.1"));

        Assert.Equal("exe 0.4.1", File.ReadAllText(Path.Combine(_appDir, "HeavyPro.exe")));
        var link = Path.Combine(_community, "rotaidem-heavypro-panel");
        Assert.True(Junction.IsLink(link));
        Assert.Contains("0.4.1", File.ReadAllText(Path.Combine(link, "manifest.json")));
        Assert.Empty(_installer.Check(result.Product));
        Assert.Equal("0.4.1", _store.Get("rotaidem-heavypro")!.Version);
    }

    [Fact]
    public void Update_moves_the_link_removes_the_old_version_and_stale_files()
    {
        _installer.Install(Release("0.4.1"), Package("0.4.1", "app/Old.dll"));
        _installer.Install(Release("0.5.0"), Package("0.5.0"));

        var link = Path.Combine(_community, "rotaidem-heavypro-panel");
        Assert.Contains("0.5.0", File.ReadAllText(Path.Combine(link, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(_appDir, "Old.dll")));
        var versions = Directory.GetDirectories(Path.Combine(_settings.LibraryDir, "rotaidem-heavypro")).Select(Path.GetFileName).ToList();
        Assert.Equal(new[] { "0.5.0" }, versions);
    }

    [Fact]
    public void Repair_of_the_same_version_works()
    {
        _installer.Install(Release("0.4.1"), Package("0.4.1"));
        var result = _installer.Install(Release("0.4.1"), Package("0.4.1"));
        Assert.Empty(_installer.Check(result.Product));
    }

    [Fact]
    public void Uninstall_removes_what_it_installed_and_keeps_user_files()
    {
        var installed = _installer.Install(Release("0.4.1"), Package("0.4.1")).Product;
        File.WriteAllText(Path.Combine(_appDir, "settings.json"), "mine");

        _installer.Uninstall(installed);

        Assert.False(Directory.Exists(Path.Combine(_community, "rotaidem-heavypro-panel")));
        Assert.False(File.Exists(Path.Combine(_appDir, "HeavyPro.exe")));
        Assert.True(File.Exists(Path.Combine(_appDir, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(_settings.LibraryDir, "rotaidem-heavypro")));
        Assert.Null(_store.Get("rotaidem-heavypro"));
    }

    [Fact]
    public void Takes_over_the_panel_folder_Rotaidem_setup_made()
    {
        var old = Path.Combine(_community, "rotaidem-heavypro-panel");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "manifest.json"), "{\"creator\": \"Rotaidem\"}");

        _installer.Install(Release("0.4.1"), Package("0.4.1"));

        Assert.True(Junction.IsLink(old));
    }

    [Fact]
    public void Leaves_someone_elses_folder_alone()
    {
        var foreign = Path.Combine(_community, "rotaidem-heavypro-panel");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "manifest.json"), "{\"creator\": \"Someone\"}");

        Assert.Throws<InvalidOperationException>(() => _installer.Install(Release("0.4.1"), Package("0.4.1")));
        Assert.False(Junction.IsLink(foreign));
        Assert.False(File.Exists(Path.Combine(_appDir, "HeavyPro.exe")));
    }

    [Fact]
    public void Will_not_change_Community_while_the_sim_runs()
    {
        _installer.SimRunning = _ => true;
        var ex = Assert.Throws<InvalidOperationException>(() => _installer.Install(Release("0.4.1"), Package("0.4.1")));
        Assert.Contains("Close Microsoft Flight Simulator 2024", ex.Message);
    }

    [Fact]
    public void Asks_to_close_the_app_first()
    {
        _installer.ProgramsRunning = names => names.ToList();
        Assert.Throws<ProgramsOpenException>(() => _installer.Install(Release("0.4.1"), Package("0.4.1")));
    }

    [Fact]
    public void Without_the_sim_the_app_still_installs()
    {
        _installer.FindCommunity = _ => null;
        var result = _installer.Install(Release("0.4.1"), Package("0.4.1"));
        Assert.Single(result.Notes);
        Assert.Empty(result.Product.Links);
        Assert.True(File.Exists(Path.Combine(_appDir, "HeavyPro.exe")));
    }

    [Fact]
    public void Refuses_a_package_that_escapes_its_folder()
    {
        var zip = Path.Combine(_root, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(archive.CreateEntry("../../evil.txt").Open()))
            w.Write("x");
        Assert.Throws<InvalidDataException>(() => _installer.Install(Release("0.4.1", withPanel: false), zip));
    }

    [Fact]
    public void Junction_points_at_its_target_and_deleting_it_keeps_the_target()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.txt"), "a");
        var link = Path.Combine(_root, "link");

        Junction.Create(link, target);
        Assert.Equal(target, Junction.Target(link), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("a", File.ReadAllText(Path.Combine(link, "a.txt")));

        Junction.Delete(link);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "a.txt")));
    }
}
