using System.Text.RegularExpressions;

namespace RotaHub.Core;

/// <summary>A message for everyone using RotaHub, shown as a bar at the top (Admin → RotaHub on the website).</summary>
public sealed record HubNotice(string Text, bool Warning, string Link);

/// <summary>What the website sets for one product in RotaHub (Admin → RotaHub → Products in RotaHub).</summary>
public sealed record HubProductSettings(string Id, bool Hidden, bool Featured, string? ManifestUrl,
    IReadOnlyList<string> LicenceIds, string? LicenceStore, string? KeyPrefix, string Image = "");

/// <summary>
/// RotaHub's look from the website (Admin → RotaHub → Look of the app). Empty strings and a null colour
/// mean RotaHub's own. Pictures are full https addresses.
/// </summary>
public sealed record HubLook(string? AccentColor, string LoginImage, string LibraryImage, string ProductImage,
    string LoginTitle, string LoginText, string LibraryTitle, string LibraryText)
{
    public static readonly HubLook Default = new(null, "", "", "", "", "", "", "");
}

/// <summary>
/// RotaHub's settings from rotaidem.com (/api/hub): the message bar, the lowest RotaHub version that
/// should be used, the help link, and per-product settings. Read again on every refresh.
/// Everything is checked here too, not only on the website: a release file must be https and is
/// signature-checked before anything is installed from it, and a licence folder is one plain name,
/// so a changed setting can never point RotaHub outside %LOCALAPPDATA%\Rotaidem.
/// </summary>
public sealed class HubConfig
{
    public static readonly HubConfig Empty = new(null, null, "", Array.Empty<HubProductSettings>());

    private static readonly Regex Colour = new("^#[0-9a-fA-F]{6}$");
    private static readonly Regex Picture = new(@"^(/img/[A-Za-z0-9._-]+|/media/\d+\.(jpg|png|webp|gif))$");

    private static readonly Regex Id = new("^[a-z0-9][a-z0-9._-]{0,59}$");
    private static readonly Regex Prefix = new("^[A-Z0-9]{2,8}$");
    private static readonly Regex VersionText = new(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$");

    public HubConfig(HubNotice? notice, Version? minVersion, string helpUrl, IReadOnlyList<HubProductSettings> products, HubLook? look = null,
        IReadOnlyList<ChangelogEntry>? changelog = null)
    {
        Changelog = changelog ?? Array.Empty<ChangelogEntry>();
        Notice = notice;
        MinVersion = minVersion;
        HelpUrl = helpUrl;
        Products = products;
        Look = look ?? HubLook.Default;
    }

    public HubLook Look { get; }

    /// <summary>RotaHub's own version history (Admin → RotaHub → RotaHub versions), newest first.</summary>
    public IReadOnlyList<ChangelogEntry> Changelog { get; }

    /// <summary>The notes the website has for a RotaHub version; null when there are none.</summary>
    public ChangelogEntry? NotesFor(Version version) =>
        Changelog.FirstOrDefault(c => System.Version.TryParse(c.Version, out var v) && HubRelease.Normalize(v) == HubRelease.Normalize(version));

    public HubNotice? Notice { get; }

    /// <summary>Copies older than this ask to be updated; null allows every version.</summary>
    public Version? MinVersion { get; }

    /// <summary>The Help link in the menu; empty for the website's support page.</summary>
    public string HelpUrl { get; }

    public IReadOnlyList<HubProductSettings> Products { get; }

    public HubProductSettings? For(string productId) =>
        Products.FirstOrDefault(p => string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));

    public bool TooOld(Version current) => MinVersion != null && HubRelease.Normalize(current) < MinVersion;

    /// <param name="absolute">Turns a page on the site ("/downloads") into a full address.</param>
    public static HubConfig Read(JsonNode data, Func<string, string> absolute)
    {
        var m = data["message"];
        var text = m["text"].Str().Trim();
        var notice = m["enabled"].Bool() && text.Length > 0
            ? new HubNotice(text.Length > 300 ? text.Substring(0, 300) : text, m["level"].Str() == "warning", Link(m["link"].Str(), absolute))
            : null;
        var min = data["min_version"].Str().Trim();
        var products = new List<HubProductSettings>();
        foreach (var p in data["products"].Items.Take(60))
        {
            var id = p["id"].Str().Trim().ToLowerInvariant();
            if (!Id.IsMatch(id) || products.Any(x => x.Id == id))
                continue;
            var manifest = p["manifest_url"].Str().Trim();
            var store = p["licence_store"].Str().Trim().ToLowerInvariant();
            var prefix = p["key_prefix"].Str().Trim().ToUpperInvariant();
            products.Add(new HubProductSettings(id, p["hidden"].Bool(), p["featured"].Bool(),
                manifest.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && Uri.IsWellFormedUriString(manifest, UriKind.Absolute) ? manifest : null,
                p["licence_ids"].Strings.Select(s => s.Trim().ToLowerInvariant()).Where(s => Id.IsMatch(s)).Take(5).ToList(),
                Id.IsMatch(store) ? store : null,
                Prefix.IsMatch(prefix) ? prefix : null,
                Image(p["image"].Str(), absolute)));
        }
        var l = data["look"];
        var colour = l["accent_color"].Str().Trim();
        var look = new HubLook(Colour.IsMatch(colour) ? colour : null,
            Image(l["login_image"].Str(), absolute), Image(l["library_image"].Str(), absolute), Image(l["product_image"].Str(), absolute),
            Clip(l["login_title"].Str(), 60), Clip(l["login_text"].Str(), 300), Clip(l["library_title"].Str(), 40), Clip(l["library_text"].Str(), 300));
        var changelog = data["changelog"].Items.Take(50)
            .Select(c => new ChangelogEntry(Clip(c["version"].Str(), 30), Clip(c["date"].Str(), 10), Clip(c["notes"].Str(), 3000)))
            .Where(c => c.Version.Length > 0).ToList();
        return new HubConfig(notice, VersionText.IsMatch(min) ? Version.Parse(min) : null, Link(data["help_url"].Str(), absolute), products, look, changelog);
    }

    /// <summary>The settings saved by the last successful read, so RotaHub looks right from the first screen and offline.</summary>
    public static HubConfig FromFile(string path, Func<string, string> absolute)
    {
        try
        {
            return File.Exists(path) ? Read(JsonNode.Parse(File.ReadAllText(path)), absolute) : Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return Empty;
        }
    }

    public static void Save(string path, JsonNode data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonNode.Write(data.Raw ?? new Dictionary<string, object>()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Clip(string value, int max)
    {
        var text = value.Trim();
        return text.Length > max ? text.Substring(0, max) : text;
    }

    /// <summary>Pictures: https addresses, or pictures on the website (/media/12.jpg, /img/hero.jpg).</summary>
    private static string Image(string value, Func<string, string> absolute)
    {
        var url = value.Trim();
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && Uri.IsWellFormedUriString(url, UriKind.Absolute))
            return url;
        return Picture.IsMatch(url) ? absolute(url) : "";
    }

    /// <summary>https:// addresses and pages on the site only; anything else (file:, javascript:, a program) is dropped.</summary>
    private static string Link(string value, Func<string, string> absolute)
    {
        var link = value.Trim();
        if (link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && Uri.IsWellFormedUriString(link, UriKind.Absolute))
            return link;
        if (link.StartsWith("/") && !link.StartsWith("//") && !link.Contains("\\"))
            return absolute(link);
        return "";
    }
}
