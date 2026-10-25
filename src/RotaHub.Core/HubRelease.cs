using System.Security.Cryptography;
using System.Text;

namespace RotaHub.Core;

/// <summary>A Community folder package inside a release, linked into the sim's Community folder.</summary>
/// <param name="Folder">The package folder name, e.g. "rotaidem-heavypro-panel".</param>
/// <param name="Sim">"msfs2024" or "msfs2020".</param>
public sealed record CommunityPart(string Folder, string Sim);

/// <summary>
/// One release of a product, from its signed manifest (rotahub.jws): a compact JWS (RS256) signed in
/// CI with the Rotaidem release key, the same key that signs HeavyPro's update.jws. RotaHub trusts only
/// the public half built into it, and installs a package only if its SHA-256 matches the manifest.
/// </summary>
public sealed record HubRelease(
    string Product, string Name, Version Version, string Published, string PackageUrl, string PackageSha256, long PackageSize,
    string? AppDir, string? AppExe, string? Shortcut, IReadOnlyList<string> Processes, IReadOnlyList<CommunityPart> Community, string Notes,
    string Source = "signed")
{
    /// <summary>"signed" (a signed rotahub.jws, e.g. from GitHub) or "website" (Admin → Products on rotaidem.com).</summary>
    public bool IsWebsite => Source == "website";

    /// <summary>A licence-only package whose link the website hands out at download time.</summary>
    public bool NeedsDownloadLink => PackageUrl.Length == 0;

    /// <summary>
    /// Payload, schema 1:
    /// {"schema":1,"product":"rotaidem-heavypro","name":"HeavyPro","version":"0.4.1","published":"…",
    ///  "package":{"url":"https://…zip","sha256":"…","size":123},
    ///  "app":{"dir":"%LOCALAPPDATA%\\HeavyPro","exe":"HeavyPro.exe","shortcut":"HeavyPro","processes":["HeavyPro"]},
    ///  "community":[{"folder":"rotaidem-heavypro-panel","sim":"msfs2024"}],"notes":"…"}
    /// The package zip holds app\… (copied to app.dir, or run from the library when there is none)
    /// and community\{folder}\… for each Community package.
    /// </summary>
    public static HubRelease? Verify(string? jws, string? publicKeyJwk, string expectedProduct, out string problem)
    {
        problem = "";
        try
        {
            if (string.IsNullOrWhiteSpace(publicKeyJwk))
            {
                problem = "This RotaHub was built without the release key.";
                return null;
            }
            var parts = (jws ?? "").Trim().Split('.');
            if (parts.Length != 3)
            {
                problem = "The release file is not a signed manifest.";
                return null;
            }
            if (JsonNode.Parse(Encoding.UTF8.GetString(FromBase64Url(parts[0])))["alg"].Str() != "RS256")
            {
                problem = "The release file uses an unexpected signature.";
                return null;
            }

            var key = JsonNode.Parse(publicKeyJwk);
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportParameters(new RSAParameters { Modulus = FromBase64Url(key["n"].Str()), Exponent = FromBase64Url(key["e"].Str()) });
                if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    problem = "The release file is not signed by Rotaidem.";
                    return null;
                }
            }

            var root = JsonNode.Parse(Encoding.UTF8.GetString(FromBase64Url(parts[1])));
            var package = root["package"];
            var url = package["url"].Str();
            var sha = package["sha256"].Str().ToLowerInvariant();
            var app = root["app"];
            var community = root["community"].Items
                .Select(c => new CommunityPart(c["folder"].Str(), c["sim"].Str("msfs2024")))
                .ToList();
            if (root["schema"].Int(1) != 1
                || !string.Equals(root["product"].Str(), expectedProduct, StringComparison.OrdinalIgnoreCase)
                || !Version.TryParse(root["version"].Str(), out var version)
                || !url.StartsWith("https://", StringComparison.Ordinal)
                || sha.Length != 64 || sha.Any(c => !Uri.IsHexDigit(c))
                || community.Any(c => !SafeName(c.Folder))
                || (app["exe"].Exists && !SafeRelative(app["exe"].Str())))
            {
                problem = "The release file does not describe this product.";
                return null;
            }

            return new HubRelease(
                root["product"].Str(), root["name"].Str(expectedProduct), Normalize(version), root["published"].Str(), url, sha, package["size"].Long(),
                NullIfEmpty(app["dir"].Str()), NullIfEmpty(app["exe"].Str()), NullIfEmpty(app["shortcut"].Str()),
                app["processes"].Strings.ToList(), community, root["notes"].Str());
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or InvalidOperationException)
        {
            problem = "The release file could not be read.";
            return null;
        }
    }

    /// <summary>
    /// A release set up in the website's admin (Admin → Products → RotaHub), from the product's "rotahub"
    /// object in /api/products. Same fields as a signed manifest, without the signature: the website is
    /// the source, over HTTPS, and RotaHub still checks the package's SHA-256 before unpacking it.
    /// Because nothing signs it, the app may only go under %LOCALAPPDATA% (or run from the library),
    /// and the package must come from the website itself or from Rotaidem's GitHub.
    /// </summary>
    /// <param name="absolute">Turns a site path like /files/x.zip into a full https URL.</param>
    public static HubRelease? FromWebsite(JsonNode hub, string productId, string productName, Func<string, string> absolute, string siteBase, out string problem)
    {
        problem = "";
        if (!hub.Exists)
            return null;
        var version = ParseVersion(hub["version"].Str());
        var url = hub["package_url"].Str();
        url = url.Length == 0 ? "" : absolute(url);
        var sha = hub["sha256"].Str().ToLowerInvariant();
        var app = hub["app"];
        var appDir = app["dir"].Str();
        var community = hub["community"].Items.Select(c => new CommunityPart(c["folder"].Str(), c["sim"].Str("msfs2024"))).ToList();
        var gated = url.Length == 0 && hub["has_package"].Bool();

        if (version == null)
            problem = "The website's RotaHub release has no version.";
        else if (url.Length == 0 && !gated)
            problem = "The website's RotaHub release has no package.";
        else if (url.Length > 0 && !TrustedPackageHost(url, siteBase))
            problem = "The website's RotaHub package must be hosted on rotaidem.com or Rotaidem's GitHub.";
        else if (sha.Length != 64 || sha.Any(c => !Uri.IsHexDigit(c)))
            problem = "The website's RotaHub release has no valid SHA-256.";
        else if (community.Any(c => !SafeName(c.Folder)) || (app["exe"].Exists && !SafeRelative(app["exe"].Str())))
            problem = "The website's RotaHub release has an unsafe file name.";
        else if (appDir.Length > 0 && !appDir.StartsWith(@"%LOCALAPPDATA%\", StringComparison.OrdinalIgnoreCase))
            problem = @"The website's RotaHub release may only install the app under %LOCALAPPDATA%\.";
        else if (appDir.Length > 0 && appDir.Substring(@"%LOCALAPPDATA%\".Length).Split('\\').Any(part => !SafeName(part)))
            problem = "The website's RotaHub release has an unsafe app folder.";
        if (problem.Length > 0)
            return null;

        return new HubRelease(productId, productName, version!, hub["published"].Str(), url, sha, hub["size"].Long(),
            NullIfEmpty(appDir), NullIfEmpty(app["exe"].Str()), NullIfEmpty(app["shortcut"].Str()),
            app["processes"].Strings.ToList(), community, hub["notes"].Str(), "website");
    }

    internal static bool TrustedPackageHost(string url, string siteBase)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && !IsLocalDev(uri))
            return false;
        var site = Uri.TryCreate(siteBase, UriKind.Absolute, out var s) ? s.Host : "rotaidem.com";
        return uri.Host.Equals(site, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase)
            || (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/tornqvisteliaz-cyber/", StringComparison.OrdinalIgnoreCase));
    }

    // A debug build pointed at a local copy of the website (ROTAHUB_SERVER) serves plain http.
    private static bool IsLocalDev(Uri uri) => uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp;

    /// <summary>Signs a manifest payload; CI does the same in packaging/New-HubManifest.ps1.</summary>
    public static string Sign(string payloadJson, RSA privateKey)
    {
        var head = ToBase64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var body = ToBase64Url(Encoding.UTF8.GetBytes(payloadJson));
        var signature = privateKey.SignData(Encoding.ASCII.GetBytes(head + "." + body), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return head + "." + body + "." + ToBase64Url(signature);
    }

    /// <summary>major.minor.patch; a missing part counts as 0.</summary>
    public static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    public static Version? ParseVersion(string? text) =>
        Version.TryParse((text ?? "").Trim().TrimStart('v', 'V'), out var v) ? Normalize(v) : null;

    /// <summary>One folder name, nothing that could climb out of the Community folder.</summary>
    internal static bool SafeName(string name) =>
        name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name != "." && name != "..";

    internal static bool SafeRelative(string path) =>
        path.Length > 0 && !Path.IsPathRooted(path) && path.Replace('\\', '/').Split('/').All(SafeName);

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
