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

    /// <summary>
    /// How RotaHub handles a website product: the built-in entry for products that have one (HeavyPro's
    /// signed GitHub releases and its older setup), otherwise what the website admin set up for it.
    /// A product without licence settings on the website installs without a key.
    /// </summary>
    public static HubProduct For(SiteProduct product)
    {
        var known = All.FirstOrDefault(p => p.Matches(product.Id));
        if (known != null)
            return known;
        var licence = product.Licence;
        if (licence == null)
            return new HubProduct(product.Id, product.Name, Array.Empty<string>(), product.Id, "", null, null);
        var ids = licence.ProductIds.Count > 0 ? licence.ProductIds.ToArray() : new[] { product.Id };
        var store = HubRelease.SafeName(licence.Store) ? licence.Store : product.Id;
        return new HubProduct(product.Id, product.Name, ids, store, licence.Prefix, null, null);
    }

    /// <summary>The product a licence key belongs to, among the website's products and the built-in ones.</summary>
    public static HubProduct? ForLicence(AccountLicence licence, IEnumerable<HubProduct> known) =>
        known.Concat(All).FirstOrDefault(p => p.UsesLicence && p.Matches(licence.ProductId));

    /// <summary>The product a typed key belongs to, by its prefix (HPRO-… is HeavyPro).</summary>
    public static HubProduct? ForKey(string key, IEnumerable<HubProduct> known) =>
        known.Concat(All).FirstOrDefault(p => p.KeyPrefix.Length > 0 && key.StartsWith(p.KeyPrefix + "-", StringComparison.Ordinal));

    public static string Expand(string path) => Environment.ExpandEnvironmentVariables(path);
}
