using System.Net;
using System.Net.Http;
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
    public async Task A_download_that_is_RotaHub_itself_is_never_a_product_release()
    {
        // HeavyPro's website "download" can be RotaHub itself. When GitHub can't be reached, the old
        // code took that link as HeavyPro 1.0.0 and ran RotaHub as its installer.
        var hub = new HubService(new HubPaths(_root), new HubSettings { LibraryDir = Path.Combine(_root, "Library") },
            new RotaidemApi("https://rotaidem.invalid"), null);
        var heavyPro = HubCatalog.HeavyPro with { ManifestUrl = "https://rotaidem.invalid/rotahub.jws" };
        var (release, problem) = await hub.NewestReleaseAsync(Product("1.0.0", "https://rotaidem.com/files/RotaHub.exe"), heavyPro);
        Assert.Null(release);
        Assert.Contains("internet connection", problem);
        Assert.Null(Product("1.0.0", "https://rotaidem.com/files/RotaHub.exe").DownloadRelease);

        // A website-only product (no signed release) still installs from its link.
        var (own, _) = await hub.NewestReleaseAsync(Product("1.2.0"), heavyPro with { ManifestUrl = null });
        Assert.True(own!.IsDownload);
    }

    [Fact]
    public async Task A_private_signed_release_falls_back_to_the_websites_download()
    {
        // The HeavyPro repo is private: its rotahub.jws cannot be read (GitHub answers 404, or the
        // check fails). RotaHub then installs the download and version set in Admin → Products.
        var hub = new HubService(new HubPaths(_root), new HubSettings { LibraryDir = Path.Combine(_root, "Library") },
            new RotaidemApi("https://rotaidem.invalid"), null);
        var heavyPro = HubCatalog.HeavyPro with { ManifestUrl = "https://rotaidem.invalid/rotahub.jws" };
        var (release, problem) = await hub.NewestReleaseAsync(Product("0.6.0", "https://rotaidem.com/files/HeavyProSetup.exe"), heavyPro);
        Assert.True(release != null, problem);
        Assert.True(release!.IsDownload);
        Assert.Equal(new Version(0, 6, 0), release.Version);
    }

    [Fact]
    public async Task A_product_with_has_download_and_no_link_still_comes_from_the_website()
    {
        // HeavyPro on rotaidem.com today: version 1.0.0, has_download true, download_url empty and no
        // RotaHub block in Admin → Products. Its GitHub releases are private, so the signed release
        // cannot be checked and the website's version and download button are the only source.
        var hub = new HubService(new HubPaths(_root), new HubSettings { LibraryDir = Path.Combine(_root, "Library") },
            new RotaidemApi("https://rotaidem.invalid"), null);
        var heavyPro = HubCatalog.HeavyPro with { ManifestUrl = "https://rotaidem.invalid/rotahub.jws" };
        var site = new SiteProduct("rotaidem-heavypro", "Rotaidem HeavyPro", "", "", "1.0.0", "coming_soon", "MSFS 2024", "", "",
            "", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ChangelogEntry>(), true, false, "", DownloadUrl: "");
        var (release, problem) = await hub.NewestReleaseAsync(site, heavyPro);
        Assert.True(release != null, problem);
        Assert.True(release!.IsDownload);
        Assert.Equal(new Version(1, 0, 0), release.Version);
        Assert.Equal("", release.PackageUrl);
    }

    private sealed class Stub : System.Net.Http.HttpMessageHandler
    {
        private readonly string _json;

        public Stub(string json) => _json = json;

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(_json) });
    }

    [Fact]
    public async Task The_websites_own_json_for_a_private_release_still_installs()
    {
        var json = JsonNode.Write(new Dictionary<string, object>
        {
            ["products"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["id"] = "rotaidem-heavypro", ["name"] = "Rotaidem HeavyPro", ["version"] = "1.0.0",
                    ["status"] = "coming_soon", ["simulator"] = "MSFS 2024",
                    ["has_download"] = true, ["download_url"] = "",
                }
            }
        });
        var api = new RotaidemApi("https://rotaidem.invalid", new Stub(json));
        var site = Assert.Single(await api.ProductsAsync());
        Assert.True(site.HasDownload);
        Assert.Null(site.Release);

        var hub = new HubService(new HubPaths(_root), new HubSettings { LibraryDir = Path.Combine(_root, "Library") }, api, null);
        var (release, problem) = await hub.NewestReleaseAsync(site, HubCatalog.HeavyPro with { ManifestUrl = "https://rotaidem.invalid/rotahub.jws" });
        Assert.True(release != null, problem);
        Assert.Equal(new Version(1, 0, 0), release!.Version);
        Assert.True(release.IsDownload);
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
