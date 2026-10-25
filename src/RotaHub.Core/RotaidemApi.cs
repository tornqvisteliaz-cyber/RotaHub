using System.Net;
using System.Net.Http;
using System.Text;

namespace RotaHub.Core;

public sealed record Account(long Id, string Name, string Email, string Role);

public sealed record ChangelogEntry(string Version, string Date, string Notes);

/// <summary>A product as the website lists it (Admin → Products).</summary>
public sealed record SiteProduct(
    string Id, string Name, string Tagline, string Description, string Version, string Status, string Simulator,
    string Price, string BuyUrl, string ImageUrl, IReadOnlyList<string> Images, IReadOnlyList<string> Features,
    IReadOnlyList<ChangelogEntry> Changelog, bool HasDownload, bool LicenceOnly, string PageUrl)
{
    /// <summary>The picture for cards and the hero: the first gallery image, else the cover.</summary>
    public string Picture => Images.FirstOrDefault() ?? ImageUrl;

    /// <summary>Admin → Products status: published, coming_soon, in_development or draft.</summary>
    public bool IsPublished => Status is "published" or "";

    public bool IsDraft => Status == "draft";

    public string StatusText => Status switch
    {
        "published" or "" => "Available",
        "coming_soon" => "Coming soon",
        "in_development" => "In development",
        _ => Status.Replace('_', ' ')
    };
}

/// <summary>A licence key on the signed-in account (the website's /api/account/licenses).</summary>
public sealed record AccountLicence(long Id, string Key, string ProductId, string Status, int MaxActivations, int Activations, string CreatedAt)
{
    public bool IsActive => Status == "active";
}

public sealed record SignedIn(string Token, Account Account);

/// <summary>A refusal or failure from rotaidem.com, with the website's own message.</summary>
public sealed class ApiException : Exception
{
    public ApiException(int status, string message, bool unverified = false) : base(message)
    {
        Status = status;
        Unverified = unverified;
    }

    /// <summary>0 when the website could not be reached at all.</summary>
    public int Status { get; }

    /// <summary>The account exists but its email address is not confirmed yet.</summary>
    public bool Unverified { get; }

    public bool SignedOut => Status == 401;
}

/// <summary>
/// The rotaidem.com API: the same accounts, sessions, products and licence keys as the website.
/// A RotaHub login is a website session like a browser's, so it shows under "Where you are
/// logged in" on the account page and can be ended from there.
/// </summary>
public sealed class RotaidemApi
{
    public const string DefaultBaseUrl = "https://rotaidem.com";

    private readonly HttpClient _http;

    static RotaidemApi()
    {
        // .NET Framework can default to TLS versions that the host refuses.
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
    }

    public RotaidemApi(string baseUrl = DefaultBaseUrl, HttpMessageHandler? handler = null, string? userAgent = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        // The website runs as a CGI script on shared hosting; the first request after a quiet spell is slow.
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent ?? "RotaHub (Windows)");
    }

    public string BaseUrl { get; }

    /// <summary>The website session token; null when signed out.</summary>
    public string? Token { get; set; }

    public HttpClient Http => _http;

    /// <summary>"/media/1.png" → "https://rotaidem.com/media/1.png".</summary>
    public string Absolute(string url) =>
        string.IsNullOrEmpty(url) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : BaseUrl + (url.StartsWith("/") ? url : "/" + url);

    public async Task<SignedIn> LoginAsync(string emailOrUsername, string password, CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Post, "/api/login", new Dictionary<string, object> { ["email"] = emailOrUsername, ["password"] = password }, ct).ConfigureAwait(false);
        var token = data["token"].Str();
        if (token.Length == 0)
            throw new ApiException(500, "rotaidem.com did not return a login.");
        Token = token;
        // The login answer has no id; /api/me fills in the rest.
        return new SignedIn(token, await MeAsync(ct).ConfigureAwait(false));
    }

    public async Task<Account> MeAsync(CancellationToken ct = default)
    {
        var user = (await SendAsync(HttpMethod.Get, "/api/me", null, ct).ConfigureAwait(false))["user"];
        return new Account(user["id"].Long(), user["name"].Str(), user["email"].Str(), user["role"].Str("Customer"));
    }

    /// <summary>Ends this session on the website. Never throws: signing out locally always works.</summary>
    public async Task LogoutAsync()
    {
        try
        {
            if (Token != null)
                await SendAsync(HttpMethod.Post, "/api/logout", new Dictionary<string, object>(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (ApiException)
        {
        }
        Token = null;
    }

    public async Task ResendConfirmationAsync(string email, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, "/api/verify-email/resend", new Dictionary<string, object> { ["email"] = email }, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<SiteProduct>> ProductsAsync(CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Get, "/api/products", null, ct).ConfigureAwait(false);
        return data["products"].Items.Select(ReadProduct).ToList();
    }

    public async Task<IReadOnlyList<AccountLicence>> LicencesAsync(CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Get, "/api/account/licenses", null, ct).ConfigureAwait(false);
        return data["licenses"].Items.Select(ReadLicence).ToList();
    }

    /// <summary>Adds a key the account does not list yet (e.g. bought for another address) to this account.</summary>
    public async Task<AccountLicence> ClaimLicenceAsync(string key, CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Post, "/api/account/licenses/claim", new Dictionary<string, object> { ["key"] = key }, ct).ConfigureAwait(false);
        return ReadLicence(data["license"]);
    }

    /// <summary>RotaHub's settings from the website (Admin → RotaHub); empty from a website that has none yet.</summary>
    public async Task<HubConfig> HubConfigAsync(CancellationToken ct = default)
    {
        try
        {
            return HubConfig.Read(await SendAsync(HttpMethod.Get, "/api/hub", null, ct).ConfigureAwait(false), Absolute);
        }
        catch (ApiException ex) when (ex.Status == 404)
        {
            return HubConfig.Empty;
        }
    }

    /// <summary>The product's download link from the website (counts the download; licence-only products need a key on the account).</summary>
    public async Task<string> DownloadUrlAsync(string productId, CancellationToken ct = default)
    {
        var data = await SendAsync(HttpMethod.Post, "/api/download/" + Uri.EscapeDataString(productId), new Dictionary<string, object>(), ct).ConfigureAwait(false);
        return Absolute(data["url"].Str());
    }

    private SiteProduct ReadProduct(JsonNode p)
    {
        var id = p["id"].Str();
        return new SiteProduct(
            id, p["name"].Str(id), p["tagline"].Str(), p["description"].Str(), p["version"].Str(), p["status"].Str(), p["simulator"].Str(),
            p["price"].Str(), p["buy_url"].Str(), Absolute(p["image_url"].Str()),
            p["images"].Strings.Select(Absolute).ToList(), p["features"].Strings.ToList(),
            p["changelog"].Items.Select(c => new ChangelogEntry(c["version"].Str(), c["date"].Str(), c["notes"].Str())).ToList(),
            p["download_url"].Str().Length > 0 || p["has_download"].Bool(), p["download_gate"].Str() == "licence",
            Absolute(p["page"].Str("/addons/view?id=" + Uri.EscapeDataString(id))));
    }

    private static AccountLicence ReadLicence(JsonNode l) =>
        new(l["id"].Long(), l["license_key"].Str(), l["product_id"].Str(), l["status"].Str("active"),
            l["max_activations"].Int(2), l["activations"].Int(), l["created_at"].Str());

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        if (Token != null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
            // Some web servers drop Authorization before CGI scripts; the website also reads this copy.
            request.Headers.TryAddWithoutValidation("X-Rotaidem-Token", Token);
        }
        if (body != null)
            request.Content = new StringContent(JsonNode.Write(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new ApiException(0, "Can't reach rotaidem.com. Check your internet connection and try again.");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonNode data;
            try
            {
                data = JsonNode.Parse(text);
            }
            catch (ArgumentException)
            {
                data = default;
            }
            if (response.IsSuccessStatusCode)
                return data;

            var status = (int)response.StatusCode;
            var message = data["error"].Str();
            if (message.Length == 0)
                message = status switch
                {
                    401 => "Your session has ended. Log in again.",
                    404 => "rotaidem.com does not offer this yet.",
                    >= 500 => "rotaidem.com had a problem. Try again in a minute.",
                    _ => $"rotaidem.com refused the request ({status})."
                };
            throw new ApiException(status, message, data["unverified"].Bool());
        }
    }
}
