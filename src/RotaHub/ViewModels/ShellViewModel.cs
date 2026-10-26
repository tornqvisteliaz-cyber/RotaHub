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
    private HubConfig _config = HubConfig.Empty;
    private bool _whatsNewChecked;

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
        RefreshCommand = new Command(LoadAsync);
        SignOutCommand = new Command(() => Main.SignOutAsync());
        WebsiteCommand = new Command(() => Os.Open(Main.Api.BaseUrl));
        AccountCommand = new Command(() => Os.Open(Main.Api.BaseUrl + "/account"));
        SelfUpdateCommand = new Command(UpdateSelfAsync);
        DismissToastCommand = new Command(() => Toast(""));
        NoticeCommand = new Command(() => { if (Config.Notice is { Link.Length: > 0 } n) Os.Open(n.Link); return Task.CompletedTask; });
        HelpCommand = new Command(() => Os.Open(Config.HelpUrl.Length > 0 ? Config.HelpUrl : Main.Api.BaseUrl + "/support"));
        WhatsNewCommand = new Command(ShowUpdateNotesAsync);
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

    /// <summary>RotaHub's settings from the website (Admin → RotaHub).</summary>
    public HubConfig Config
    {
        get => _config;
        private set
        {
            if (Set(ref _config, value))
                Raise(nameof(NoticeText), nameof(NoticeWarning), nameof(NoticeHasLink), nameof(TooOldText));
        }
    }

    public string NoticeText => Config.Notice?.Text ?? "";

    public bool NoticeWarning => Config.Notice?.Warning == true;

    public bool NoticeHasLink => Config.Notice?.Link.Length > 0;

    public string TooOldText => Config.TooOld(SelfInstall.Current)
        ? $"This RotaHub ({SelfInstall.Current.ToString(3)}) is older than Rotaidem supports. Update to {Config.MinVersion!.ToString(3)} or newer before installing or updating products."
        : "";

    public Command NavigateCommand { get; }
    public Command RefreshCommand { get; }
    public Command SignOutCommand { get; }
    public Command WebsiteCommand { get; }
    public Command AccountCommand { get; }
    public Command SelfUpdateCommand { get; }
    public Command DismissToastCommand { get; }
    public Command NoticeCommand { get; }
    public Command HelpCommand { get; }
    public Command WhatsNewCommand { get; }

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
    public async Task LoadAsync()
    {
        Loading = true;
        LoadError = "";
        Raise(nameof(NothingToShow));
        try
        {
            var productsTask = Main.Api.ProductsAsync();
            var licencesTask = LoadLicencesAsync();
            var configTask = LoadConfigAsync();
            var products = await productsTask;
            await licencesTask;
            await configTask;
            Offline = false;
            _ = ShowWhatsNewAfterUpdateAsync();

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
            // Hidden in Admin → RotaHub: gone from the library, unless it is on this PC (it can still be updated and uninstalled).
            foreach (var hidden in Products.Where(vm => Config.For(vm.Site.Id)?.Hidden == true && !vm.IsInstalled && !vm.Busy).ToList())
                Products.Remove(hidden);
            // Installed first, then pinned ones, then the website's order.
            var ordered = Products.OrderByDescending(p => p.IsInstalled).ThenByDescending(p => Config.For(p.Site.Id)?.Featured == true)
                .ThenBy(p => visible.FindIndex(s => s.Id == p.Site.Id)).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var at = Products.IndexOf(ordered[i]);
                if (at != i)
                    Products.Move(at, i);
            }
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
            LoadError = ex.Message;
        }
        finally
        {
            Loading = false;
            Raise(nameof(NothingToShow));
        }
        _ = CheckSelfUpdateAsync(manual: false);
    }

    /// <summary>The update bar's "What's new": the website's notes for that version, else the release's own.</summary>
    private Task ShowUpdateNotesAsync()
    {
        if (SelfUpdate == null)
            return Task.CompletedTask;
        var version = SelfUpdate.Version.ToString(3);
        var notes = Config.NotesFor(SelfUpdate.Version)?.Notes is { Length: > 0 } n ? n : SelfUpdate.Notes;
        return Main.AskAsync($"What's new in RotaHub {version}", notes.Length > 0 ? notes : "There are no notes for this version yet.", "Close", cancel: "");
    }

    /// <summary>Once after RotaHub updated itself: the notes for the version now running (Admin → RotaHub → RotaHub versions).</summary>
    private async Task ShowWhatsNewAfterUpdateAsync()
    {
        if (_whatsNewChecked)
            return;
        _whatsNewChecked = true;
        var current = SelfInstall.Current.ToString(3);
        var settings = Main.Settings;
        if (settings.LastVersionSeen == current)
            return;
        var updated = settings.LastVersionSeen.Length > 0; // a fresh install has nothing "new" to tell
        settings.LastVersionSeen = current;
        try
        {
            settings.Save(Main.Paths);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving the settings", ex);
        }
        if (updated && Config.NotesFor(SelfInstall.Current) is { Notes.Length: > 0 } entry)
            await Main.AskAsync($"What's new in RotaHub {current}", entry.Notes, "Got it", cancel: "");
    }

    /// <summary>The website's RotaHub settings; on a failure the last ones stay in use.</summary>
    private async Task LoadConfigAsync()
    {
        try
        {
            Config = await Main.RefreshConfigAsync();
            Keys.Rebuild(); // keys of products the website just added
        }
        catch (ApiException ex) when (!ex.SignedOut)
        {
            Log.Info("RotaHub settings: " + ex.Message);
        }
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
