using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Rotaidem.Licensing;

namespace RotaHub.Core;

/// <summary>The licence saved on this PC for a product, as the product itself will see it at start-up.</summary>
public sealed record LocalLicence(string Key, LicenseResult Check)
{
    public bool Works => Check.IsValid;
}

/// <summary>
/// Activates keys on this PC exactly like a product's own installer: same server, same machine id, and
/// the signed answer saved where the product reads it (%LOCALAPPDATA%\Rotaidem\{store}\license.json).
/// So a product installed by RotaHub starts already activated.
/// </summary>
public sealed class Licences
{
    private static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(14);
    private readonly string _baseUrl;
    private readonly HttpClient _http;
    private readonly string? _publicKeyJwk;

    public Licences(string baseUrl, string? publicKeyJwk, HttpClient? http = null)
    {
        _baseUrl = baseUrl;
        _publicKeyJwk = publicKeyJwk;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public LocalLicence? OnThisPc(HubProduct product)
    {
        if (!product.UsesLicence)
            return null;
        var saved = new LicenseStore(product.LicenceStore).Load();
        if (saved == null)
            return null;
        // The signed answer names the product id the key was activated under; try each.
        LicenseResult? check = null;
        foreach (var id in product.LicenceProductIds)
        {
            check = Client(id).CheckOffline(new LicenseStore(product.LicenceStore), OfflineGrace);
            if (check.IsValid)
                break;
        }
        return new LocalLicence(saved.Key, check!);
    }

    /// <summary>Activates <paramref name="key"/> here (uses one of its activations unless this PC already has one) and saves it for the product.</summary>
    public async Task<LicenseResult> ActivateAsync(HubProduct product, string key, CancellationToken ct = default)
    {
        var normalized = LicenseKey.Normalize(key);
        if (normalized == null)
            return new LicenseResult(LicenseStatus.InvalidFormat, "That is not a licence key. It looks like " + (product.KeyPrefix.Length > 0 ? product.KeyPrefix : "XXXX") + "-XXXX-XXXX-XXXX.");
        var result = await FirstMatchingProduct(product, c => c.ActivateAsync(normalized, ct)).ConfigureAwait(false);
        if (result.IsValid)
            new LicenseStore(product.LicenceStore).Save(normalized, DateTimeOffset.UtcNow, result.Token);
        return result;
    }

    /// <summary>Frees this PC's activation (e.g. before moving to a new PC) and forgets the key here.</summary>
    public async Task<LicenseResult> DeactivateAsync(HubProduct product, CancellationToken ct = default)
    {
        var store = new LicenseStore(product.LicenceStore);
        var saved = store.Load();
        if (saved == null)
            return new LicenseResult(LicenseStatus.NoKey, "No key is saved on this PC.");
        var result = await FirstMatchingProduct(product, c => c.DeactivateAsync(saved.Key, ct)).ConfigureAwait(false);
        // Deactivated, or the key no longer exists: either way it is no use on this PC any more.
        if (result.Status is LicenseStatus.Valid or LicenseStatus.NotFound or LicenseStatus.Revoked)
            store.Clear();
        return result;
    }

    private LicenseClient Client(string productId) => new(_baseUrl, productId, _http, _publicKeyJwk);

    private async Task<LicenseResult> FirstMatchingProduct(HubProduct product, Func<LicenseClient, Task<LicenseResult>> call)
    {
        LicenseResult? result = null;
        foreach (var id in product.LicenceProductIds)
        {
            result = await call(Client(id)).ConfigureAwait(false);
            if (result.Status != LicenseStatus.WrongProduct)
                return result;
        }
        return result ?? new LicenseResult(LicenseStatus.WrongProduct, "This product does not use licence keys.");
    }
}

/// <summary>The website login, kept between starts and encrypted for this Windows user (DPAPI).</summary>
public sealed class SessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RotaHub session v1");
    private readonly string _file;

    public SessionStore(HubPaths paths) => _file = paths.Session;

    public SignedIn? Load()
    {
        try
        {
            if (!File.Exists(_file))
                return null;
            var text = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_file), Entropy, DataProtectionScope.CurrentUser));
            var data = JsonNode.Parse(text);
            var token = data["token"].Str();
            if (token.Length == 0)
                return null;
            return new SignedIn(token, new Account(data["id"].Long(), data["name"].Str(), data["email"].Str(), data["role"].Str("Customer")));
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Save(SignedIn session)
    {
        var text = JsonNode.Write(new Dictionary<string, object>
        {
            ["token"] = session.Token,
            ["id"] = session.Account.Id,
            ["name"] = session.Account.Name,
            ["email"] = session.Account.Email,
            ["role"] = session.Account.Role,
        });
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllBytes(_file, ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Entropy, DataProtectionScope.CurrentUser));
    }

    public void Clear()
    {
        if (File.Exists(_file))
            File.Delete(_file);
    }
}
