using RotaHub.Core;
using Xunit;

namespace RotaHub.Tests;

public sealed class DownloadReleaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rotahub-zip-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static SiteProduct Product(string version, string download = "https://rotaidem.com/files/Demo.zip", string simulator = "MSFS 2024") =>
        new("rotaidem-demo", "Demo", "", "", version, "published", simulator, "", "", "",
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ChangelogEntry>(), download.Length > 0, false, "",
            DownloadUrl: download);

    [Fact]
    public void The_admin_version_and_link_make_a_release()
    {
        var release = Product("1.2.0").DownloadRelease;
        Assert.NotNull(release);
        Assert.True(release!.IsDownload);
        Assert.Equal(new Version(1, 2, 0), release.Version);
    }

    [Theory]
    [InlineData("1.2.0", "")]
    [InlineData("soon", "https://rotaidem.com/files/Demo.zip")]
    public void No_release_without_a_link_or_a_version(string version, string download)
    {
        Assert.Null(Product(version, download).DownloadRelease);
    }

    [Fact]
    public async Task A_product_with_its_own_release_never_falls_back_to_the_website_link()
    {
        // HeavyPro's website "download" is RotaHub itself. When GitHub can't be reached, the old code
        // took that link as HeavyPro 1.0.0 and ran RotaHub as its installer.
        var hub = new HubService(new HubPaths(_root), new HubSettings { LibraryDir = Path.Combine(_root, "Library") },
            new RotaidemApi("https://rotaidem.invalid"), null);
        var heavyPro = HubCatalog.HeavyPro with { ManifestUrl = "https://rotaidem.invalid/rotahub.jws" };
        var (release, problem) = await hub.NewestReleaseAsync(Product("1.0.0", "https://rotaidem.com/files/RotaHub.exe"), heavyPro);
        Assert.Null(release);
        Assert.Contains("internet connection", problem);

        // A website-only product (no signed release) still installs from its link.
        var (own, _) = await hub.NewestReleaseAsync(Product("1.2.0"), heavyPro with { ManifestUrl = null });
        Assert.True(own!.IsDownload);
    }

    [Fact]
    public void The_simulator_on_the_website_picks_the_Community_folder()
    {
        Assert.Equal("msfs2020", Product("1.0.0", simulator: "MSFS 2020").Sim);
        Assert.Equal("msfs2024", Product("1.0.0").Sim);
    }

    private string Folder(params string[] files)
    {
        var dir = Path.Combine(_root, "unpacked-" + Guid.NewGuid().ToString("N"));
        foreach (var f in files)
        {
            var path = Path.Combine(dir, f);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
        return dir;
    }

    private string Layout() => Path.Combine(_root, "layout-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Msfs_packages_go_to_Community()
    {
        var layout = Layout();
        var result = ZipLayout.Arrange(Folder(@"Liveries\pack-a\manifest.json", @"Liveries\pack-a\layout.json",
            @"Liveries\pack-b\manifest.json", @"Liveries\pack-b\layout.json", "ReadMe.txt"), layout, "msfs2024");
        Assert.Equal(new[] { "pack-a", "pack-b" }, result.Community.Select(c => c.Folder).OrderBy(n => n));
        Assert.True(File.Exists(Path.Combine(layout, "community", "pack-a", "manifest.json")));
        Assert.Null(result.AppExe);
    }

    [Fact]
    public void A_zip_that_is_one_package_keeps_the_product_name()
    {
        var layout = Layout();
        var result = ZipLayout.Arrange(Folder("manifest.json", "layout.json"), layout, "msfs2020", "rotaidem-demo");
        var part = Assert.Single(result.Community);
        Assert.Equal("rotaidem-demo", part.Folder);
        Assert.Equal("msfs2020", part.Sim);
    }

    [Fact]
    public void A_program_goes_to_app_and_its_exe_launches()
    {
        var layout = Layout();
        var result = ZipLayout.Arrange(Folder(@"Demo 1.0\Demo.exe", @"Demo 1.0\Demo.dll"), layout, "msfs2024");
        Assert.Equal("Demo.exe", result.AppExe);
        Assert.True(File.Exists(Path.Combine(layout, "app", "Demo.dll")));
        Assert.Empty(result.Community);
    }

    [Fact]
    public void A_RotaHub_package_layout_is_kept()
    {
        var layout = Layout();
        var result = ZipLayout.Arrange(Folder(@"app\Tool.exe", @"community\tool-panel\manifest.json"), layout, "msfs2024");
        Assert.Equal("Tool.exe", result.AppExe);
        Assert.Equal("tool-panel", Assert.Single(result.Community).Folder);
    }

    [Fact]
    public void Two_exes_means_no_guess()
    {
        var result = ZipLayout.Arrange(Folder("A.exe", "B.exe"), Layout(), "msfs2024");
        Assert.Null(result.AppExe);
    }
}
