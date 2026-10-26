using RotaHub.Core;
using Xunit;

namespace RotaHub.Tests;

/// <summary>Products installed by their own installer (HeavyProSetup): which version RotaHub shows.</summary>
public sealed class SetupInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rotahub-setup-" + Guid.NewGuid().ToString("N"));
    private readonly HubService _hub;
    private readonly HubProduct _product;
    private readonly string _exe;

    public SetupInstallTests()
    {
        var paths = new HubPaths(Path.Combine(_root, "hub"));
        _hub = new HubService(paths, new HubSettings { LibraryDir = Path.Combine(_root, "Library") }, new RotaidemApi("https://rotaidem.invalid"), null);
        _exe = Path.Combine(_root, "HeavyPro", "HeavyPro.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
        File.WriteAllText(_exe, "a build whose file version says nothing useful");
        _product = HubCatalog.HeavyPro with { Id = "test-heavypro", LegacyExe = _exe };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void Remember(string version, string? stamp = null) => _hub.Installed.Put(new InstalledProduct
    {
        Product = _product.Id, Kind = "setup", Version = version, ExeStamp = stamp ?? HubService.ExeStamp(_exe)!,
        AppDir = Path.GetDirectoryName(_exe)!, AppExe = "HeavyPro.exe",
    });

    [Fact]
    public void After_RotaHub_installed_it_the_websites_version_counts()
    {
        Remember("0.5.2");
        Assert.Equal("0.5.2", _hub.Detect(_product)!.Version);
    }

    [Fact]
    public void A_program_replaced_with_a_stale_version_number_keeps_the_known_version()
    {
        Remember("0.5.2", stamp: "older");
        Assert.Equal("0.5.2", _hub.Detect(_product)!.Version);
    }

    [Fact]
    public void Without_a_record_the_program_file_decides()
    {
        var found = _hub.Detect(_product)!;
        Assert.Equal("setup", found.Kind);
        Assert.Equal("0.0.0", found.Version);
    }

    [Fact]
    public void Uninstalled_in_Windows_means_not_installed()
    {
        Remember("0.5.2");
        File.Delete(_exe);
        Assert.Null(_hub.Detect(_product));
    }

    [Fact]
    public void HeavyPro_updates_with_its_own_update_mode()
    {
        Assert.Equal("/update", HubCatalog.HeavyPro.UpdateArgs);
    }

    [Fact]
    public void Writing_anything_in_the_folder_changes_its_stamp()
    {
        var before = HubService.FolderStamp(Path.GetDirectoryName(_exe)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_exe)!, "community.path"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(Path.GetDirectoryName(_exe)!, "community.path"), DateTime.UtcNow.AddMinutes(1));
        Assert.NotEqual(before, HubService.FolderStamp(Path.GetDirectoryName(_exe)!));
    }
}
