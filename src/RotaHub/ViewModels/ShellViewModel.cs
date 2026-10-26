using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using RotaHub.Core;

namespace RotaHub.ViewModels;

/// <summary>Signed in: the sidebar and the page next to it.</summary>
public sealed class ShellViewModel : Observable
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private Account _account;
    private object _page;
    private string _section = "library";
    private bool _offline;
    private bool _loading;
    private string _loadError = "";
    private string _toast = "";
    private bool _toastError;
    private HubRelease? _selfUpdate;
    private bool _updatingSelf;
    private string _selfUpdateStep = "";

    public ShellViewModel(MainViewModel main, Account account)
    {
        Main = main;
        _account = account;
        Keys = new KeysViewModel(this);
        Settings = new SettingsViewModel(this);
        Library = new LibraryPage(this);
        _page = Library;
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast("");
        };
        NavigateCommand = new Command(p => { Navigate(p as string ?? "library"); return Task.CompletedTask; });
        RefreshCommand = new Command(() => LoadAsync());
        SignOutCommand = new Command(() => Main.SignOutAsync());
        WebsiteCommand = new Command(() => Os.Open(Main.Api.BaseUrl));
        AccountCommand = new Command(() => Os.Open(Main.Api.BaseUrl + "/account"));
        SelfUpdateCommand = new Command(UpdateSelfAsync);
        DismissToastCommand = new Command(() => Toast(""));
        UpdateAllCommand = new Command(UpdateAllAsync, () => Updates > 0);
        // What is set in the website admin reaches RotaHub without a restart: look again every few
        // minutes, and whenever RotaHub's window is brought back after a while.
        _checkTimer.Tick += (_, _) => _ = CheckNowAsync();
        _checkTimer.Start();
    }

    public MainViewModel Main { get; }
    public LibraryPage Library { get; }
    public KeysViewModel Keys { get; }
    public SettingsViewModel Settings { get; }
    public ObservableCollection<ProductViewModel> Products { get; } = new();
    public IReadOnlyList<AccountLicence> AccountLicences { get; private set; } = Array.Empty<AccountLicence>();

    public Account Account
    {
        get => _account;
        set
        {
            if (Set(ref _account, value))
                Raise(nameof(Initial), nameof(FirstName));
        }
    }

    public string Initial => Account.Name.Length > 0 ? Account.Name.Substring(0, 1).ToUpperInvariant() : "R";

    public string FirstName => Account.Name.Split(' ')[0];

    /// <summary>The library itself, a product page, the keys or the settings.</summary>
    public object Page
    {
        get => _page;
        private set => Set(ref _page, value);
    }

    public bool IsLibrary
    {
        get => _section == "library";
        set { if (value) Navigate("library"); }
    }

    public bool IsKeys
    {
        get => _section == "keys";
        set { if (value) Navigate("keys"); }
    }

    public bool IsSettings
    {
        get => _section == "settings";
        set { if (value) Navigate("settings"); }
    }

    public bool Offline
    {
        get => _offline;
        set => Set(ref _offline, value);
    }

    public bool Loading
    {
        get => _loading;
        private set => Set(ref _loading, value);
    }

    public string LoadError
    {
        get => _loadError;
        private set => Set(ref _loadError, value);
    }

    /// <summary>The big banner on the library: the first installed add-on, else the first one on sale.</summary>
    public ProductViewModel? Featured => Products.FirstOrDefault(p => p.IsInstalled)
        ?? Products.FirstOrDefault(p => p.Site.IsPublished)
        ?? Products.FirstOrDefault();

    public bool NothingToShow => !Loading && Products.Count == 0 && LoadError.Length == 0;

    public string ToastText
    {
        get => _toast;
        private set => Set(ref _toast, value);
    }

    public bool ToastError
    {
        get => _toastError;
        private set => Set(ref _toastError, value);
    }

    public HubRelease? SelfUpdate
    {
        get => _selfUpdate;
        private set
        {
            if (Set(ref _selfUpdate, value))
                Raise(nameof(SelfUpdateText));
        }
    }

    public string SelfUpdateText => SelfUpdate == null ? "" : $"RotaHub {SelfUpdate.Version.ToString(3)} is ready to install.";

    public bool UpdatingSelf
    {
        get => _updatingSelf;
        private set => Set(ref _updatingSelf, value);
    }

    public string SelfUpdateStep
    {
        get => _selfUpdateStep;
        private set => Set(ref _selfUpdateStep, value);
    }

    public string Version => "RotaHub " + SelfInstall.Current.ToString(3);

    public Command NavigateCommand { get; }
    public Command RefreshCommand { get; }
    public Command SignOutCommand { get; }
    public Command WebsiteCommand { get; }
    public Command AccountCommand { get; }
    public Command SelfUpdateCommand { get; }
    public Command DismissToastCommand { get; }
    public Command UpdateAllCommand { get; }

    private readonly DispatcherTimer _checkTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly HashSet<string> _announced = new();
    private DateTime _lastCheck = DateTime.MinValue;
    private bool _checking;

    /// <summary>How many installed add-ons have a newer version on the website.</summary>
    public int Updates => Products.Count(p => p.UpdateAvailable);

    public string UpdatesText => Updates == 1
        ? $"An update is ready: {Products.First(p => p.UpdateAvailable).Name} {Products.First(p => p.UpdateAvailable).UpdateVersion}."
        : $"{Updates} updates are ready.";

    /// <summary>Called by a product whenever its state changes.</summary>
    public void UpdatesChanged() => Raise(nameof(Updates), nameof(UpdatesText));

    /// <summary>Signed out: no more background checks for this account.</summary>
    public void Close() => _checkTimer.Stop();

    /// <summary>RotaHub's window came back to the front.</summary>
    public void WindowActivated()
    {
        if (DateTime.UtcNow - _lastCheck > TimeSpan.FromMinutes(1))
            _ = CheckNowAsync();
    }

    /// <summary>
    /// Looks at the website again in the background: new products, new versions and download links,
    /// new keys. Announces each new update once, and installs it when automatic updates are on and the
    /// update needs nobody (installers always wait for the user).
    /// </summary>
    public async Task CheckNowAsync()
    {
        if (_checking || Products.Any(p => p.Busy))
            return;
        _checking = true;
        try
        {
            await LoadAsync(quiet: true);
            foreach (var product in Products.Where(p => p.UpdateAvailable).ToList())
            {
                var key = product.Site.Id + "@" + product.UpdateVersion;
                if (!_announced.Add(key))
                    continue;
                if (Main.Settings.AutoUpdate && !product.UpdateNeedsYou)
                {
                    await product.UpdateQuietlyAsync();
                    if (!product.UpdateAvailable)
                    {
                        Notifier.Show($"{product.Name} updated", $"RotaHub installed {product.Name} {product.CardVersion.TrimStart('v')}.", () => Open(product));
                        continue;
                    }
                }
                Notifier.Show($"{product.Name} {product.UpdateVersion} is available", "Open RotaHub to update.", () => Open(product));
                Toast($"{product.Name} {product.UpdateVersion} is available.");
            }
        }
        finally
        {
            _checking = false;
        }
    }

    private async Task UpdateAllAsync()
    {
        foreach (var product in Products.Where(p => p.UpdateAvailable).ToList())
            await product.UpdateAsync();
    }

    public void Navigate(string section)
    {
        _section = section;
        Page = section switch
        {
            "keys" => Keys,
            "settings" => Settings,
            _ => Library
        };
        if (section == "settings")
            Settings.Refresh();
        Raise(nameof(IsLibrary), nameof(IsKeys), nameof(IsSettings));
    }

    public void Open(ProductViewModel product)
    {
        _section = "library";
        product.Refresh();
        Page = product;
        Raise(nameof(IsLibrary), nameof(IsKeys), nameof(IsSettings));
    }

    public void Toast(string text, bool error = false)
    {
        _toastTimer.Stop();
        ToastError = error;
        ToastText = text;
        if (text.Length > 0)
        {
            _toastTimer.Interval = TimeSpan.FromSeconds(error ? 10 : 6);
            _toastTimer.Start();
        }
    }

    /// <summary>Products and keys from the website, then each product's newest release.</summary>
    public Task LoadAsync() => LoadAsync(quiet: false);

    /// <param name="quiet">A background check: no spinner, and a failure keeps what is shown.</param>
    public async Task LoadAsync(bool quiet)
    {
        _lastCheck = DateTime.UtcNow;
        Loading = !quiet;
        if (!quiet)
            LoadError = "";
        Raise(nameof(NothingToShow));
        try
        {
            var productsTask = Main.Api.ProductsAsync();
            var licencesTask = LoadLicencesAsync();
            var products = await productsTask;
            await licencesTask;
            Offline = false;

            var visible = products.Where(p => !p.IsDraft).ToList();
            // Keep the existing view models (a running install keeps its progress).
            foreach (var gone in Products.Where(vm => visible.All(p => p.Id != vm.Site.Id) && !vm.Busy).ToList())
                Products.Remove(gone);
            foreach (var site in visible)
            {
                var vm = Products.FirstOrDefault(p => p.Site.Id == site.Id);
                if (vm == null)
                {
                    vm = new ProductViewModel(this, site);
                    Products.Add(vm);
                }
                else
                {
                    vm.Update(site);
                }
            }
            // Installed first, then the website's order.
            var ordered = Products.OrderByDescending(p => p.IsInstalled).ThenBy(p => visible.FindIndex(s => s.Id == p.Site.Id)).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var at = Products.IndexOf(ordered[i]);
                if (at != i)
                    Products.Move(at, i);
            }
            Raise(nameof(Featured));
            await Task.WhenAll(Products.Select(p => p.CheckReleaseAsync()));
        }
        catch (ApiException ex) when (ex.SignedOut)
        {
            await Main.SignOutAsync("Your login has ended. Log in again.");
            return;
        }
        catch (ApiException ex)
        {
            Offline = ex.Status == 0;
            if (!quiet)
                LoadError = ex.Message;
        }
        finally
        {
            Loading = false;
            Raise(nameof(NothingToShow), nameof(Featured));
        }
        _ = CheckSelfUpdateAsync(manual: false);
    }

    public async Task LoadLicencesAsync()
    {
        try
        {
            AccountLicences = await Main.Api.LicencesAsync();
        }
        catch (ApiException ex) when (!ex.SignedOut)
        {
            Log.Info("Licences: " + ex.Message);
        }
        foreach (var p in Products)
            p.Refresh();
        Keys.Rebuild();
    }

    public async Task CheckSelfUpdateAsync(bool manual)
    {
        var (release, problem) = await Main.Hub.LatestReleaseAsync(HubCatalog.RotaHub);
        if (release != null && release.Version > SelfInstall.Current)
        {
            SelfUpdate = release;
            if (manual)
                Toast(SelfUpdateText);
        }
        else if (manual)
        {
            Toast(release != null || problem.Length == 0 ? $"You have the newest RotaHub ({SelfInstall.Current.ToString(3)})." : problem, error: release == null && problem.Length > 0 && !problem.StartsWith("No RotaHub release"));
        }
    }

    private async Task UpdateSelfAsync()
    {
        if (SelfUpdate == null)
            return;
        if (!SelfInstall.IsInstalledCopy(Main.Paths))
        {
            Toast("This copy of RotaHub runs from outside its install folder (a developer build), so it does not update itself.", error: true);
            return;
        }
        if (Products.Any(p => p.Busy))
        {
            Toast("Wait for the install that is running to finish, then update RotaHub.");
            return;
        }
        UpdatingSelf = true;
        try
        {
            await SelfInstall.UpdateAsync(Main.Hub, SelfUpdate, new Progress<InstallProgress>(p => SelfUpdateStep = p.Fraction is { } f ? $"{p.Step} · {f:P0}" : p.Step), CancellationToken.None);
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("Self update", ex);
            Toast("RotaHub could not update: " + ex.Message, error: true);
        }
        finally
        {
            UpdatingSelf = false;
        }
    }

    public string ProductName(string productId) =>
        Products.FirstOrDefault(p => p.Hub.Matches(productId) || p.Site.Id == productId)?.Name
        ?? HubCatalog.All.FirstOrDefault(h => h.Matches(productId))?.Name
        ?? productId;
}

/// <summary>The library grid (its own type so the page area picks the right view).</summary>
public sealed class LibraryPage
{
    public LibraryPage(ShellViewModel shell) => Shell = shell;

    public ShellViewModel Shell { get; }
}
