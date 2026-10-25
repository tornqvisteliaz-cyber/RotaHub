using System.Collections.ObjectModel;
using System.Windows;
using Rotaidem.Licensing;
using RotaHub.Core;

namespace RotaHub.ViewModels;

/// <summary>Licence keys: the ones on the account, which are active on this PC, and adding a key.</summary>
public sealed class KeysViewModel : Observable
{
    private readonly ShellViewModel _shell;
    private string _newKey = "";
    private string _message = "";
    private Tone _tone;
    private bool _busy;

    public KeysViewModel(ShellViewModel shell)
    {
        _shell = shell;
        AddCommand = new Command(AddAsync, () => !Busy && NewKey.Trim().Length > 0);
        RefreshCommand = new Command(() => _shell.LoadLicencesAsync());
        BuyCommand = new Command(() => Os.Open(_shell.Main.Api.BaseUrl + "/addons/"));
    }

    public ObservableCollection<KeyRowViewModel> Rows { get; } = new();

    public bool HasKeys => Rows.Count > 0;

    public string NewKey
    {
        get => _newKey;
        set => Set(ref _newKey, value);
    }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public Tone Tone
    {
        get => _tone;
        private set => Set(ref _tone, value);
    }

    public bool Busy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    public Command AddCommand { get; }
    public Command RefreshCommand { get; }
    public Command BuyCommand { get; }

    public void Rebuild()
    {
        Rows.Clear();
        foreach (var licence in _shell.AccountLicences)
            Rows.Add(new KeyRowViewModel(_shell, licence));
        Raise(nameof(HasKeys));
    }

    /// <summary>
    /// Adds a key to the account (so it shows on every PC and on the website) and activates it here.
    /// If the website can't take it (an older site, or the key belongs to another account) it is still
    /// activated on this PC.
    /// </summary>
    private async Task AddAsync()
    {
        var key = LicenseKey.Normalize(NewKey);
        if (key == null)
        {
            Show("That is not a licence key. It looks like HPRO-XXXX-XXXX-XXXX.", Tone.Danger);
            return;
        }
        var product = HubCatalog.ForKey(key, _shell.Products.Select(p => p.Hub));
        Busy = true;
        Show("Checking the key…", Tone.Neutral);
        try
        {
            var onAccount = false;
            try
            {
                await _shell.Main.Api.ClaimLicenceAsync(key);
                onAccount = true;
            }
            catch (ApiException ex) when (ex.Status is 404 or 409 or 403)
            {
                Log.Info("Claim: " + ex.Message);
                if (ex.Status == 409)
                    Show(ex.Message, Tone.Warn);
            }

            if (product == null)
            {
                Show(onAccount ? "The key is on your account." : "RotaHub does not know which product this key is for. Check it on rotaidem.com.", onAccount ? Tone.Ok : Tone.Danger);
                return;
            }
            var result = await _shell.Main.Licences.ActivateAsync(product, key);
            if (result.IsValid)
            {
                NewKey = "";
                Show(onAccount ? $"Added to your account and activated for {product.Name} on this PC." : $"Activated for {product.Name} on this PC.", Tone.Ok);
            }
            else
            {
                Show(result.Message, Tone.Danger);
            }
            await _shell.LoadLicencesAsync();
        }
        catch (ApiException ex)
        {
            Show(ex.Message, Tone.Danger);
        }
        finally
        {
            Busy = false;
        }
    }

    private void Show(string message, Tone tone)
    {
        Message = message;
        Tone = tone;
    }
}

public sealed class KeyRowViewModel : Observable
{
    private readonly ShellViewModel _shell;
    private bool _busy;

    public KeyRowViewModel(ShellViewModel shell, AccountLicence licence)
    {
        _shell = shell;
        Licence = licence;
        Product = HubCatalog.ForLicence(licence, shell.Products.Select(p => p.Hub));
        var local = Product == null ? null : shell.Main.Licences.OnThisPc(Product);
        HereKey = local?.Key == licence.Key;
        HereWorks = HereKey && local!.Works;
        CopyCommand = new Command(Copy);
        ToggleCommand = new Command(ToggleAsync, () => !Busy && Product != null && Licence.IsActive);
    }

    public AccountLicence Licence { get; }
    public HubProduct? Product { get; }
    public bool HereKey { get; }
    public bool HereWorks { get; }

    public string ProductName => _shell.ProductName(Licence.ProductId);
    public string Key => Licence.Key;
    public string Added => Licence.CreatedAt.Length >= 10 ? "Added " + Licence.CreatedAt.Substring(0, 10) : "";
    public string StatusText => Licence.IsActive ? "Active" : "Revoked";
    public Tone StatusTone => Licence.IsActive ? Tone.Ok : Tone.Danger;
    public string Activations => $"{Licence.Activations} of {Licence.MaxActivations} PCs";
    public string HereText => HereWorks ? "Active on this PC" : HereKey ? "Saved here, needs checking" : "Not on this PC";
    public Tone HereTone => HereWorks ? Tone.Ok : HereKey ? Tone.Warn : Tone.Neutral;
    public string ToggleText => HereWorks ? "Deactivate here" : "Activate here";

    public bool Busy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    public Command CopyCommand { get; }
    public Command ToggleCommand { get; }

    private void Copy()
    {
        try
        {
            Clipboard.SetText(Key);
            _shell.Toast("Key copied.");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            _shell.Toast("The clipboard is busy. Try again.", error: true);
        }
    }

    private async Task ToggleAsync()
    {
        if (Product == null)
            return;
        if (HereWorks && !await _shell.Main.AskAsync("Deactivate on this PC?",
                $"{ProductName} stops working on this PC until a key is activated here again. The freed activation can be used on another PC.",
                "Deactivate", danger: true))
            return;
        Busy = true;
        try
        {
            var result = HereWorks
                ? await _shell.Main.Licences.DeactivateAsync(Product)
                : await _shell.Main.Licences.ActivateAsync(Product, Key);
            _shell.Toast(result.Status == LicenseStatus.Valid
                ? (HereWorks ? $"{ProductName} is deactivated on this PC." : $"{ProductName} is activated on this PC.")
                : result.Message, error: result.Status != LicenseStatus.Valid);
            await _shell.LoadLicencesAsync();
        }
        finally
        {
            Busy = false;
        }
    }
}
