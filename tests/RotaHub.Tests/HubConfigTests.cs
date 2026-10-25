using RotaHub.Core;
using Xunit;

namespace RotaHub.Tests;

public class HubConfigTests
{
    private static string Absolute(string path) => "https://rotaidem.com" + path;

    private static HubConfig Read(object data) => HubConfig.Read(JsonNode.Parse(JsonNode.Write(data)), Absolute);

    private static SiteProduct Site(string id) =>
        new(id, id, "", "", "1.0.0", "published", "", "", "", "", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ChangelogEntry>(), true, false, "");

    private static Dictionary<string, object> Product(string id, string manifest = "", string store = "", string prefix = "", string[]? ids = null,
        bool hidden = false, bool featured = false) =>
        new()
        {
            ["id"] = id, ["hidden"] = hidden, ["featured"] = featured, ["manifest_url"] = manifest,
            ["licence_ids"] = ids ?? Array.Empty<string>(), ["licence_store"] = store, ["key_prefix"] = prefix,
        };

    [Fact]
    public void Reads_the_message_lowest_version_and_help_link()
    {
        var config = Read(new Dictionary<string, object>
        {
            ["message"] = new Dictionary<string, object> { ["enabled"] = true, ["text"] = "HeavyPro 0.6 is out", ["level"] = "warning", ["link"] = "/downloads" },
            ["min_version"] = "0.2.0",
            ["help_url"] = "https://discord.gg/rotaidem",
            ["products"] = Array.Empty<object>(),
        });
        Assert.Equal(new HubNotice("HeavyPro 0.6 is out", true, "https://rotaidem.com/downloads"), config.Notice);
        Assert.Equal(new Version(0, 2, 0), config.MinVersion);
        Assert.Equal("https://discord.gg/rotaidem", config.HelpUrl);
        Assert.True(config.TooOld(new Version(0, 1, 9)));
        Assert.False(config.TooOld(new Version(0, 2, 0, 0)));
    }

    [Fact]
    public void A_switched_off_message_and_unsafe_links_are_ignored()
    {
        var config = Read(new Dictionary<string, object>
        {
            ["message"] = new Dictionary<string, object> { ["enabled"] = false, ["text"] = "Hidden" },
            ["min_version"] = "soon",
            ["help_url"] = "file:///C:/Windows/System32/calc.exe",
        });
        Assert.Null(config.Notice);
        Assert.Null(config.MinVersion);
        Assert.Equal("", config.HelpUrl);
        Assert.Equal("", Read(new Dictionary<string, object> { ["help_url"] = "//evil.example/x" }).HelpUrl);
        Assert.Same(HubConfig.Empty.Products, HubConfig.Empty.Products);
        Assert.Empty(HubConfig.Read(default, Absolute).Products);
    }

    [Fact]
    public void Product_settings_are_checked_before_use()
    {
        var config = Read(new Dictionary<string, object>
        {
            ["products"] = new object[]
            {
                Product("good", "https://github.com/x/y/releases/latest/download/rotahub.jws", "good-tool", "gd", new[] { "good", "Good-Tool" }),
                Product("unsafe", "http://plain.example/rotahub.jws", @"..\..\Windows", "far-too-long-prefix", new[] { "ok", "../bad" }),
                Product("../escape"),
            }
        });
        Assert.Equal(new[] { "good", "unsafe" }, config.Products.Select(p => p.Id));
        var good = config.For("GOOD")!;
        Assert.Equal(("https://github.com/x/y/releases/latest/download/rotahub.jws", "good-tool", "GD"), (good.ManifestUrl, good.LicenceStore, good.KeyPrefix));
        Assert.Equal(new[] { "good", "good-tool" }, good.LicenceIds);
        var bad = config.For("unsafe")!;
        Assert.Null(bad.ManifestUrl);
        Assert.Null(bad.LicenceStore);
        Assert.Null(bad.KeyPrefix);
        Assert.Equal(new[] { "ok" }, bad.LicenceIds);
    }

    [Fact]
    public void Reads_the_look_and_keeps_only_safe_pictures()
    {
        var data = new Dictionary<string, object>
        {
            ["look"] = new Dictionary<string, object>
            {
                ["accent_color"] = "#4cc38a", ["login_image"] = "/media/3.jpg", ["library_image"] = "https://cdn.example.com/banner.jpg",
                ["product_image"] = "file:///C:/secret.jpg", ["login_title"] = "Hello pilot", ["library_text"] = new string('x', 400),
            },
            ["products"] = new object[] { new Dictionary<string, object> { ["id"] = "heavypro", ["image"] = "/img/hero.jpg" },
                                          new Dictionary<string, object> { ["id"] = "other", ["image"] = "javascript:alert(1)", ["featured"] = true } },
        };
        var config = Read(data);
        Assert.Equal("#4cc38a", config.Look.AccentColor);
        Assert.Equal("https://rotaidem.com/media/3.jpg", config.Look.LoginImage);
        Assert.Equal("https://cdn.example.com/banner.jpg", config.Look.LibraryImage);
        Assert.Equal("", config.Look.ProductImage);
        Assert.Equal("Hello pilot", config.Look.LoginTitle);
        Assert.Equal(300, config.Look.LibraryText.Length);
        Assert.Equal("https://rotaidem.com/img/hero.jpg", config.For("heavypro")!.Image);
        Assert.Equal("", config.For("other")!.Image);
        Assert.Null(Read(new Dictionary<string, object> { ["look"] = new Dictionary<string, object> { ["accent_color"] = "red; drop" } }).Look.AccentColor);
        Assert.Equal(HubLook.Default, HubConfig.Empty.Look);

        // saved after a refresh, read again at the next start
        var file = Path.Combine(Path.GetTempPath(), "rotahub-test-" + Guid.NewGuid().ToString("N"), "hub.json");
        try
        {
            HubConfig.Save(file, JsonNode.Parse(JsonNode.Write(data)));
            var again = HubConfig.FromFile(file, Absolute);
            Assert.Equal(config.Look, again.Look);
            Assert.Equal(config.Products.Select(p => p.Image), again.Products.Select(p => p.Image));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
        Assert.Same(HubConfig.Empty, HubConfig.FromFile(file, Absolute));
    }

    [Fact]
    public void The_website_settings_change_the_catalogue_but_never_the_exe()
    {
        try
        {
            HubCatalog.Config = Read(new Dictionary<string, object>
            {
                ["products"] = new object[]
                {
                    Product("heavypro", "https://example.com/heavypro-beta/rotahub.jws", featured: true),
                    Product("liveries", "https://example.com/liveries/rotahub.jws", "liveries", "LIVR", new[] { "liveries" }),
                }
            });
            var heavy = HubCatalog.For(Site("heavypro"));
            Assert.Equal("rotaidem-heavypro", heavy.Id);
            Assert.Equal("https://example.com/heavypro-beta/rotahub.jws", heavy.ManifestUrl);
            Assert.Equal(HubCatalog.HeavyPro.LegacyExe, heavy.LegacyExe);
            Assert.Equal(HubCatalog.HeavyPro.LicenceStore, heavy.LicenceStore);

            var liveries = HubCatalog.For(Site("liveries"));
            Assert.Equal(("liveries", "https://example.com/liveries/rotahub.jws", "LIVR"), (liveries.Id, liveries.ManifestUrl, liveries.KeyPrefix));
            Assert.Null(liveries.LegacyExe);
            Assert.Contains(HubCatalog.Known, p => p.Id == "liveries" && p.KeyPrefix == "LIVR");
            Assert.Equal("liveries", HubCatalog.ForLicence(new AccountLicence(1, "LIVR-AAAA-BBBB-CCCC", "liveries", "active", 2, 0, ""))!.Id);

            Assert.Null(HubCatalog.For(Site("plain")).ManifestUrl);
        }
        finally
        {
            HubCatalog.Config = HubConfig.Empty;
        }
        Assert.Equal(HubCatalog.HeavyPro, HubCatalog.For(Site("heavypro")));
    }
}
