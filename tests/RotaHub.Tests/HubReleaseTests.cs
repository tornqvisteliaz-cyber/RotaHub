using System.Security.Cryptography;
using RotaHub.Core;
using Xunit;

namespace RotaHub.Tests;

public class HubReleaseTests
{
    internal static readonly RSACryptoServiceProvider Key = new(2048);

    internal static string PublicJwk()
    {
        var p = Key.ExportParameters(false);
        return JsonNode.Write(new Dictionary<string, object> { ["kty"] = "RSA", ["n"] = B64(p.Modulus), ["e"] = B64(p.Exponent) });
    }

    internal static string Payload(string product = "rotaidem-heavypro", string version = "0.4.1", string url = "https://example.com/p.zip",
        string sha = "", string folder = "rotaidem-heavypro-panel", string exe = "HeavyPro.exe") =>
        JsonNode.Write(new Dictionary<string, object>
        {
            ["schema"] = 1,
            ["product"] = product,
            ["name"] = "HeavyPro",
            ["version"] = version,
            ["package"] = new Dictionary<string, object> { ["url"] = url, ["sha256"] = sha.Length > 0 ? sha : new string('a', 64), ["size"] = 10 },
            ["app"] = new Dictionary<string, object> { ["exe"] = exe, ["shortcut"] = "HeavyPro", ["processes"] = new[] { "HeavyPro" } },
            ["community"] = new[] { new Dictionary<string, object> { ["folder"] = folder, ["sim"] = "msfs2024" } },
        });

    [Fact]
    public void Accepts_a_release_signed_with_the_built_in_key()
    {
        var release = HubRelease.Verify(HubRelease.Sign(Payload(), Key), PublicJwk(), "rotaidem-heavypro", out var problem);
        Assert.True(release != null, problem);
        Assert.Equal(new Version(0, 4, 1), release!.Version);
        Assert.Equal("rotaidem-heavypro-panel", Assert.Single(release.Community).Folder);
        Assert.Equal("HeavyPro.exe", release.AppExe);
    }

    [Fact]
    public void Refuses_another_key()
    {
        using var other = new RSACryptoServiceProvider(2048);
        Assert.Null(HubRelease.Verify(HubRelease.Sign(Payload(), other), PublicJwk(), "rotaidem-heavypro", out var problem));
        Assert.Contains("not signed by Rotaidem", problem);
    }

    [Fact]
    public void Refuses_an_edited_payload()
    {
        var parts = HubRelease.Sign(Payload(), Key).Split('.');
        var forged = HubRelease.Sign(Payload(url: "https://evil.example/p.zip"), Key).Split('.')[1];
        Assert.Null(HubRelease.Verify(parts[0] + "." + forged + "." + parts[2], PublicJwk(), "rotaidem-heavypro", out _));
    }

    [Theory]
    [InlineData("other-product", "https://example.com/p.zip", "rotaidem-heavypro-panel", "HeavyPro.exe")]
    [InlineData("rotaidem-heavypro", "http://example.com/p.zip", "rotaidem-heavypro-panel", "HeavyPro.exe")]
    [InlineData("rotaidem-heavypro", "https://example.com/p.zip", "..", "HeavyPro.exe")]
    [InlineData("rotaidem-heavypro", "https://example.com/p.zip", "a\\b", "HeavyPro.exe")]
    [InlineData("rotaidem-heavypro", "https://example.com/p.zip", "ok", "..\\evil.exe")]
    [InlineData("rotaidem-heavypro", "https://example.com/p.zip", "ok", "C:\\evil.exe")]
    public void Refuses_releases_for_something_else_or_with_unsafe_paths(string product, string url, string folder, string exe)
    {
        Assert.Null(HubRelease.Verify(HubRelease.Sign(Payload(product, url: url, folder: folder, exe: exe), Key), PublicJwk(), "rotaidem-heavypro", out _));
    }

    [Fact]
    public void Refuses_without_a_built_in_key()
    {
        Assert.Null(HubRelease.Verify(HubRelease.Sign(Payload(), Key), null, "rotaidem-heavypro", out var problem));
        Assert.Contains("release key", problem);
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
