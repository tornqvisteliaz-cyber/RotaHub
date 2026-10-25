using Rotaidem.Licensing;

namespace RotaHub.Core;

/// <summary>
/// What RotaHub knows about a product beyond the website listing: where its signed release
/// manifest is, how its licence is stored on this PC, and where an install made without
/// RotaHub (its own setup) would be.
/// </summary>
/// <param name="Id">The website product id (Admin → Products).</param>
/// <param name="LicenceProductIds">Ids the licence server may know the product by, tried in order.</param>
/// <param name="LicenceStore">The folder under %LOCALAPPDATA%\Rotaidem that the product reads its key from.</param>
/// <param name="ManifestUrl">The signed release manifest (rotahub.jws) of the newest release.</param>
/// <param name="LegacyExe">The exe its own setup installs, so an existing install is recognised and taken over.</param>
public sealed record HubProduct(string Id, string Name, string[] LicenceProductIds, string LicenceStore, string KeyPrefix,
    string? ManifestUrl, string? LegacyExe)
{
    public bool UsesLicence => LicenceProductIds.Length > 0;

    public bool Matches(string productId) =>
        string.Equals(productId, Id, StringComparison.OrdinalIgnoreCase) ||
        LicenceProductIds.Any(p => LicenseToken.SameProduct(p, productId));
}

public static class HubCatalog
{
    public static readonly HubProduct HeavyPro = new(
        "rotaidem-heavypro", "HeavyPro", new[] { "rotaidem-heavypro", "heavypro" }, "heavypro", LicenseKey.HeavyProPrefix,
        "https://github.com/tornqvisteliaz-cyber/heavypro/releases/latest/download/rotahub.jws",
        @"%LOCALAPPDATA%\HeavyPro\HeavyPro.exe");

    public static readonly IReadOnlyList<HubProduct> All = new[] { HeavyPro };

    /// <summary>RotaHub itself, for its own updates (not listed in the library).</summary>
    public static readonly HubProduct RotaHub = new(
        "rotahub", "RotaHub", Array.Empty<string>(), "rotahub", "",
        "https://github.com/tornqvisteliaz-cyber/rotahub/releases/latest/download/rotahub.jws", null);

    /// <summary>The catalogue entry for a website product, or a plain one (installed through its download) for anything new.</summary>
    public static HubProduct For(SiteProduct product) =>
        All.FirstOrDefault(p => p.Matches(product.Id)) ?? new HubProduct(product.Id, product.Name, Array.Empty<string>(), product.Id, "", null, null);

    /// <summary>The catalogue entry a licence key belongs to.</summary>
    public static HubProduct? ForLicence(AccountLicence licence) => All.FirstOrDefault(p => p.Matches(licence.ProductId));

    public static string Expand(string path) => Environment.ExpandEnvironmentVariables(path);
}
