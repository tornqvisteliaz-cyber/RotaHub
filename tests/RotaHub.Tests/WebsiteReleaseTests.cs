using RotaHub.Core;
using Xunit;

namespace RotaHub.Tests;

public class WebsiteReleaseTests
{
    private const string Site = "https://rotaidem.com";

    private static string Absolute(string url) => url.StartsWith("/") ? Site + url : url;

    private static JsonNode Hub(string url = "/files/HeavyPro-0.6.0.zip", string sha = "", string appDir = @"%LOCALAPPDATA%\HeavyPro",
        string exe = "HeavyPro.exe", string folder = "rotaidem-heavypro-panel", string version = "0.6.0", bool gated = false) =>
        JsonNode.Parse(JsonNode.Write(new Dictionary<string, object>
        {
            ["version"] = version,
            ["package_url"] = url,
            ["has_package"] = gated,
            ["sha256"] = sha.Length > 0 ? sha : new string('b', 64),
            ["size"] = 1234,
            ["app"] = new Dictionary<string, object> { ["dir"] = appDir, ["exe"] = exe, ["shortcut"] = "HeavyPro", ["processes"] = new[] { "HeavyPro" } },
            ["community"] = new[] { new Dictionary<string, object> { ["folder"] = folder, ["sim"] = "msfs2024" } },
        }));

    private static HubRelease? Read(JsonNode hub, out string problem) =>
        HubRelease.FromWebsite(hub, "rotaidem-heavypro", "HeavyPro", Absolute, Site, out problem);

    [Fact]
    public void Reads_a_release_set_up_in_the_website_admin()
    {
        var release = Read(Hub(), out var problem);
        Assert.True(release != null, problem);
        Assert.Equal("https://rotaidem.com/files/HeavyPro-0.6.0.zip", release!.PackageUrl);
        Assert.Equal(new Version(0, 6, 0), release.Version);
        Assert.True(release.IsWebsite);
        Assert.Equal("rotaidem-heavypro-panel", Assert.Single(release.Community).Folder);
    }

    [Fact]
    public void Accepts_Rotaidem_GitHub_releases()
    {
        Assert.NotNull(Read(Hub(url: "https://github.com/tornqvisteliaz-cyber/heavypro/releases/download/v0.6.0/HeavyPro-rotahub.zip"), out _));
    }

    [Fact]
    public void A_licence_only_package_gets_its_link_later()
    {
        var release = Read(Hub(url: "", gated: true), out var problem);
        Assert.True(release != null, problem);
        Assert.True(release!.NeedsDownloadLink);
    }

    [Theory]
    [InlineData("http://rotaidem.com/files/x.zip")]
    [InlineData("https://evil.example/x.zip")]
    [InlineData("https://github.com/someone-else/repo/releases/download/v1/x.zip")]
    [InlineData("https://rotaidem.com.evil.example/x.zip")]
    public void Refuses_packages_from_elsewhere(string url)
    {
        Assert.Null(Read(Hub(url: url), out var problem));
        Assert.NotEqual("", problem);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"%APPDATA%\Microsoft")]
    [InlineData(@"%LOCALAPPDATA%\..\Roaming")]
    [InlineData(@"%USERPROFILE%\Documents")]
    public void Keeps_the_app_under_LocalAppData(string appDir)
    {
        Assert.Null(Read(Hub(appDir: appDir), out var problem));
        Assert.Contains("app", problem);
    }

    [Fact]
    public void Running_from_the_library_needs_no_app_folder()
    {
        var release = Read(Hub(appDir: ""), out var problem);
        Assert.True(release != null, problem);
        Assert.Null(release!.AppDir);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(@"a\b")]
    public void Refuses_unsafe_Community_folders(string folder)
    {
        Assert.Null(Read(Hub(folder: folder), out _));
    }

    [Fact]
    public void Needs_a_version_and_a_checksum()
    {
        Assert.Null(Read(Hub(version: "soon"), out _));
        Assert.Null(Read(Hub(sha: "abc"), out _));
    }

    [Fact]
    public void Website_licence_settings_make_a_licensed_product()
    {
        var site = new SiteProduct("rotaidem-skyfeel", "SkyFeel", "", "", "1.0.0", "published", "MSFS 2024", "", "", "",
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ChangelogEntry>(), true, false, "",
            Licence: new WebsiteLicence(Array.Empty<string>(), "skyfeel", "SKYF"));
        var hub = HubCatalog.For(site);
        Assert.True(hub.UsesLicence);
        Assert.Equal(new[] { "rotaidem-skyfeel" }, hub.LicenceProductIds);
        Assert.Equal("skyfeel", hub.LicenceStore);
        Assert.Same(hub, HubCatalog.ForKey("SKYF-AAAA-BBBB-CCCC", new[] { hub }));
    }
}
