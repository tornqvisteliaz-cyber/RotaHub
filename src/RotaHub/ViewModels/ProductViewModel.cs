using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Rotaidem.Licensing;
using RotaHub.Core;

namespace RotaHub.ViewModels;

/// <summary>One product: its card in the library and its own page.</summary>
public sealed class ProductViewModel : Observable
{
    private readonly ShellViewModel _shell;
    private SiteProduct _site;
    private InstalledProduct? _installed;
    private HubRelease? _release;
    private string _releaseProblem = "";
    private LocalLicence? _local;
    private IReadOnlyList<string> _problems = Array.Empty<string>();
    private IReadOnlyList<string> _notes = Array.Empty<string>();
    private ImageSource _picture;
    private bool _busy;
    private string _step = "";
    private string _detail = "";
    private double _progress;
    private bool _indeterminate;
    private bool _checkingRelease;

    public ProductViewModel(ShellViewModel shell, SiteProduct site)
    {
        _shell = shell;
        _site = site;
        Hub = HubCatalog.For(site);
        _picture = ImageCache.Builtin(FallbackPicture(site.Id));
        OpenCommand = new Command(() => _shell.Open(this));
        BackCommand = new Command(() => _shell.Navigate("library"));
        PrimaryCommand = new Command(PrimaryAsync, () => !Busy && PrimaryEnabled);
        RepairCommand = new Command(() => InstallAsync(repair: true), () => !Busy && _release != null && IsInstalled);
        UninstallCommand = new Command(UninstallAsync, () => !Busy && CanUninstall);
        FolderCommand = new Command(() => Os.ShowInExplorer(_installed?.ExePath ?? _installed?.VersionDir ?? ""), () => IsInstalled);
        WebsiteCommand = new Command(() => Os.Open(Site.PageUrl));
        BuyCommand = new Command(() => Os.Open(Site.BuyUrl.Length > 0 ? Site.BuyUrl : Site.PageUrl));
        ActivateCommand = new Command(() => ActivateAsync(quiet: false), () => !Busy && AccountKey != null && !LicenceWorksHere);
        ShowImageCommand = new Command(p =>
        {
            if (p is ImageSource image)
                Picture = image;
            return Task.CompletedTask;
        });
        CommunityCommand = new Command(p => { Os.ShowInExplorer(p as string ?? ""); return Task.CompletedTask; });
        Refresh();
        _ = LoadPictureAsync();
    }

    public SiteProduct Site => _site;
    public HubProduct Hub { get; private set; }

    public Command OpenCommand { get; }
    public Command BackCommand { get; }
    public Command PrimaryCommand { get; }
    public Command RepairCommand { get; }
    public Command UninstallCommand { get; }
    public Command FolderCommand { get; }
    public Command WebsiteCommand { get; }
    public Command BuyCommand { get; }
    public Command ActivateCommand { get; }
    public Command CommunityCommand { get; }
    public Command ShowImageCommand { get; }

    /// <summary>The website's product pictures, for the strip under the hero.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ImageSource> Gallery { get; } = new();

    public string Name => Site.Name;
    public string Tagline => Site.Tagline;
    public string Kicker => Site.Simulator.Length > 0 ? Site.Simulator : "Rotaidem";
    public string Description => Site.Description.Trim();
    public IReadOnlyList<string> Features => Site.Features;
    public IReadOnlyList<ChangelogEntry> Changelog => Site.Changelog;
    public bool HasChangelog => Changelog.Count > 0 || ReleaseNotes.Length > 0;
    public string ReleaseNotes => _release?.Notes ?? "";
    public string Price => Site.Price;

    public ImageSource Picture
    {
        get => _picture;
        private set => Set(ref _picture, value);
    }

    public bool IsInstalled => _installed != null;
    public bool ByRotaHub => _installed?.Kind == "package";
    public bool CanUninstall => IsInstalled;

    /// <summary>The update runs the product's own installer, which needs the user; RotaHub never starts one by itself.</summary>
    public bool UpdateNeedsYou => _release?.IsDownload == true &&
        (Site.DownloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Site.DownloadUrl.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || Site.DownloadUrl.Length == 0);

    /// <summary>The version an update would bring, for notifications.</summary>
    public string UpdateVersion => _release?.Version.ToString(3) ?? "";
    public bool UpdateAvailable => _installed != null && _release != null && (_installed.ParsedVersion == null || _release.Version > _installed.ParsedVersion);
    public bool NeedsRepair => ByRotaHub && _problems.Count > 0;
    public IReadOnlyList<string> Problems => _problems;
    public IReadOnlyList<string> Notes => _notes;
    public IReadOnlyList<LinkRecord> Links => _installed?.Links ?? (IReadOnlyList<LinkRecord>)Array.Empty<LinkRecord>();

    public string InstalledText => _installed == null
        ? "Not installed"
        : _installed.Kind == "setup" ? $"{_installed.Version} (installed with its own setup)" : _installed.Version;

    public string LatestText => _release != null
        ? _release.Version.ToString(3) + (_release.Published.Length >= 10 ? " · " + _release.Published.Substring(0, 10) : "")
        : _checkingRelease ? "Checking…" : Site.HasDownload ? "Set a version like 1.0.0 in the admin" : "Not published yet";

    public string Location => _installed?.AppDir ?? "";

    /// <summary>The badge on the card: installed state first, otherwise the website's status.</summary>
    public string Badge => Busy ? "Working" : UpdateAvailable ? "Update" : NeedsRepair ? "Needs repair" : IsInstalled ? "Installed" : Site.StatusText;

    public Tone BadgeTone => Busy ? Tone.Info
        : UpdateAvailable || NeedsRepair ? Tone.Warn
        : IsInstalled ? Tone.Ok
        : Site.Status switch { "coming_soon" => Tone.Info, "in_development" => Tone.Warn, "published" or "" => Tone.Ok, _ => Tone.Neutral };

    /// <summary>One line for the sidebar: progress while working, otherwise the state.</summary>
    public string StatusLine => Busy ? (Indeterminate || Progress <= 0 ? Step : $"{Step} · {Progress:P0}")
        : UpdateAvailable ? $"Update to {_release!.Version.ToString(3)}"
        : NeedsRepair ? "Needs repair"
        : IsInstalled ? "Installed · v" + _installed!.Version
        : _release != null || Site.HasDownload ? "Not installed"
        : Site.StatusText;

    public string CardVersion => IsInstalled ? "v" + _installed!.Version : Site.Price.Length > 0 ? Site.Price : Site.Version.Length > 0 ? "v" + Site.Version : "";

    // ---------- the main button ----------

    /// <summary>A signed RotaHub release, or else the download the website offers.</summary>
    private bool CanInstall => _release != null || (!_checkingRelease && Site.HasDownload);

    public bool PrimaryEnabled => IsInstalled ? (_installed!.ExePath != null || UpdateAvailable || NeedsRepair) : CanInstall;

    public string PrimaryText =>
        UpdateAvailable ? $"Update to {_release!.Version.ToString(3)}"
        : NeedsRepair ? "Repair"
        : IsInstalled ? (_installed!.ExePath != null ? "Launch" : "Installed")
        : _release != null ? "Install"
        : _checkingRelease ? "Checking…"
        : Site.HasDownload ? "Download and install"
        : Site.Status == "coming_soon" ? "Coming soon"
        : Site.Status == "in_development" ? "In development"
        : "Not available yet";

    public string PrimaryIcon => UpdateAvailable || (!IsInstalled && CanInstall) ? "\uE896" : NeedsRepair ? "\uE90F" : IsInstalled ? "\uE768" : "\uE823";

    // ---------- licence ----------

    /// <summary>The account's active key for this product, if it has one.</summary>
    public AccountLicence? AccountKey => Hub.UsesLicence
        ? _shell.AccountLicences.Where(l => l.IsActive && Hub.Matches(l.ProductId)).OrderByDescending(l => _local?.Key == l.Key).FirstOrDefault()
        : null;

    public bool UsesLicence => Hub.UsesLicence;
    public bool LicenceWorksHere => _local?.Works == true;

    public string LicenceTitle => !Hub.UsesLicence ? ""
        : LicenceWorksHere ? "Active on this PC"
        : AccountKey != null ? "On your account, not active on this PC yet"
        : _local != null ? "A key is saved here but is not working"
        : "No licence key on your account";

    public string LicenceDetail => !Hub.UsesLicence ? ""
        : LicenceWorksHere ? (_local!.Key + (_local.Check.MaxActivations > 0 ? $" · {_local.Check.ActivationsUsed} of {_local.Check.MaxActivations} PCs" : ""))
        : AccountKey != null ? AccountKey.Key + " · RotaHub activates it when you install or launch."
        : _local != null ? _local.Check.Message
        : $"Buy {Name} on rotaidem.com and the key appears here, or add one under Licence keys.";

    public Tone LicenceTone => LicenceWorksHere ? Tone.Ok : AccountKey != null ? Tone.Info : Tone.Warn;

    public bool ShowBuy => Hub.UsesLicence && AccountKey == null && !LicenceWorksHere && Site.IsPublished;

    // ---------- progress ----------

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
                RaiseState();
        }
    }

    public string Step
    {
        get => _step;
        private set
        {
            if (Set(ref _step, value))
                Raise(nameof(StatusLine));
        }
    }

    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            if (Set(ref _progress, value))
                Raise(nameof(StatusLine));
        }
    }

    public bool Indeterminate
    {
        get => _indeterminate;
        private set => Set(ref _indeterminate, value);
    }

    public void Update(SiteProduct site)
    {
        _site = site;
        Hub = HubCatalog.For(site);
        Raise(nameof(Site), nameof(Name), nameof(Tagline), nameof(Kicker), nameof(Description), nameof(Features), nameof(Changelog), nameof(Price));
        Refresh();
        _ = LoadPictureAsync();
    }

    /// <summary>Re-reads what is on this PC (install record, Community links, saved licence).</summary>
    public void Refresh()
    {
        _installed = _shell.Main.Hub.Detect(Hub);
        _problems = _installed?.Kind == "package" ? _shell.Main.Hub.Installer.Check(_installed) : Array.Empty<string>();
        _local = _shell.Main.Licences.OnThisPc(Hub);
        RaiseState();
    }

    public async Task CheckReleaseAsync()
    {
        _checkingRelease = true;
        RaiseState();
        try
        {
            var (release, problem) = await _shell.Main.Hub.NewestReleaseAsync(Site, Hub);
            _release = release;
            _releaseProblem = problem;
            if (release == null && problem.Length > 0)
                Log.Info($"{Hub.Id} release: {problem}");
        }
        finally
        {
            _checkingRelease = false;
            RaiseState();
        }
    }

    private void RaiseState()
    {
        Raise(nameof(IsInstalled), nameof(ByRotaHub), nameof(CanUninstall), nameof(UpdateAvailable), nameof(NeedsRepair), nameof(Problems), nameof(Notes),
            nameof(Links), nameof(InstalledText), nameof(LatestText), nameof(Location), nameof(Badge), nameof(BadgeTone), nameof(CardVersion),
            nameof(PrimaryEnabled), nameof(PrimaryText), nameof(PrimaryIcon), nameof(AccountKey), nameof(LicenceWorksHere), nameof(LicenceTitle),
            nameof(LicenceDetail), nameof(LicenceTone), nameof(ShowBuy), nameof(ReleaseNotes), nameof(HasChangelog), nameof(StatusLine),
            nameof(UpdateNeedsYou), nameof(UpdateVersion));
        _shell.UpdatesChanged();
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private async Task LoadPictureAsync()
    {
        var image = await ImageCache.GetAsync(Site.Picture);
        if (image != null)
            Picture = image;
        var urls = Site.Images.Count > 0 ? Site.Images : Site.ImageUrl.Length > 0 ? new[] { Site.ImageUrl } : Array.Empty<string>();
        var pictures = new List<ImageSource>();
        foreach (var url in urls.Distinct())
        {
            if (await ImageCache.GetAsync(url) is { } picture)
                pictures.Add(picture);
        }
        Gallery.Clear();
        if (pictures.Count > 1)
        {
            foreach (var picture in pictures)
                Gallery.Add(picture);
        }
    }

    /// <summary>A product with no pictures on the website yet gets one of the site's own photos, the same one each time.</summary>
    private static string FallbackPicture(string id)
    {
        var photos = new[] { "approach", "night", "clouds", "hero" };
        var hash = id.Aggregate(17, (h, c) => unchecked(h * 31 + c));
        return photos[(hash & 0x7fffffff) % photos.Length];
    }

    // ---------- actions ----------

    private Task PrimaryAsync()
    {
        if (UpdateAvailable || NeedsRepair || !IsInstalled)
            return InstallAsync(repair: NeedsRepair && !UpdateAvailable);
        return LaunchAsync();
    }

    /// <summary>An automatic update in the background: never asks, skips when the program is open, never launches.</summary>
    public Task UpdateQuietlyAsync() => UpdateAvailable && !UpdateNeedsYou && !Busy ? InstallAsync(repair: false, quiet: true) : Task.CompletedTask;

    /// <summary>"Update all": every update, asking where it must (open programs, installers).</summary>
    public Task UpdateAsync() => UpdateAvailable && !Busy ? InstallAsync(repair: false) : Task.CompletedTask;

    private async Task InstallAsync(bool repair, bool quiet = false)
    {
        Busy = true;
        _notes = Array.Empty<string>();
        try
        {
            if (_release == null)
            {
                await _shell.Main.Hub.RunWebsiteInstallerAsync(Site, Reporter(), CancellationToken.None);
                Refresh();
                _shell.Toast(IsInstalled ? $"{Name} is installed." : $"The {Name} installer has closed.");
                return;
            }

            var fresh = !IsInstalled;
            var release = _release;
            InstallResult? result = null;
            while (true)
            {
                try
                {
                    result = await _shell.Main.Hub.InstallAsync(release, Site, Hub, Reporter(), CancellationToken.None);
                    break;
                }
                catch (ProgramsOpenException open)
                {
                    Indeterminate = false;
                    if (quiet)
                        return; // an automatic update waits until the program is closed
                    if (!await _shell.Main.AskAsync($"Close {Name}?",
                            $"{string.Join(", ", open.Names)} is open. RotaHub closes it to {(fresh ? "install" : "update")} {Name}. Save anything you are working on first.",
                            "Close and continue"))
                        return;
                    Step = "Closing " + Name;
                    await Processes.CloseAsync(open.Names, TimeSpan.FromSeconds(8));
                }
            }
            if (result == null)
            {
                _shell.Toast($"{Name} was downloaded but is not something RotaHub can install by itself, so it is shown in Explorer.");
                return;
            }
            _notes = result.Notes;

            if (Hub.UsesLicence && !LicenceWorksHere && AccountKey != null)
                await ActivateAsync(quiet: true);

            Refresh();
            _shell.Toast(repair ? $"{Name} is repaired." : fresh ? $"{Name} {release.Version.ToString(3)} is installed." : $"{Name} is updated to {release.Version.ToString(3)}.");
            if (fresh && !quiet && _shell.Main.Settings.LaunchAfterInstall && _installed?.ExePath != null)
                Start();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ApiException or System.ComponentModel.Win32Exception)
        {
            Log.Error("Install " + Hub.Id, ex);
            if (!quiet)
                _shell.Toast(ex.Message, error: true);
            Refresh();
        }
        finally
        {
            Busy = false;
            Step = "";
            Detail = "";
            Progress = 0;
        }
    }

    private async Task LaunchAsync()
    {
        if (Hub.UsesLicence && !LicenceWorksHere && AccountKey != null)
        {
            Busy = true;
            try
            {
                await ActivateAsync(quiet: true);
            }
            finally
            {
                Busy = false;
                Step = "";
            }
        }
        Start();
    }

    private void Start()
    {
        var exe = _installed?.ExePath;
        if (exe == null || !File.Exists(exe))
        {
            _shell.Toast($"{Name}'s program file is missing. Use Repair.", error: true);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _shell.Toast($"{Name} could not start: {ex.Message}", error: true);
        }
    }

    private async Task ActivateAsync(bool quiet)
    {
        var key = AccountKey;
        if (key == null)
            return;
        Step = "Activating your licence";
        Indeterminate = true;
        var result = await _shell.Main.Licences.ActivateAsync(Hub, key.Key);
        Indeterminate = false;
        Refresh();
        _shell.Keys.Rebuild();
        if (result.IsValid)
        {
            if (!quiet)
                _shell.Toast($"{Name} is activated on this PC.");
            _ = _shell.LoadLicencesAsync();
        }
        else
        {
            _shell.Toast($"The licence could not be activated: {result.Message}", error: true);
        }
    }

    private async Task UninstallAsync()
    {
        if (_installed == null)
            return;
        if (_installed.Kind == "setup")
        {
            // Its own installer put it there, so Windows' Apps list uninstalls it.
            if (await _shell.Main.AskAsync($"Uninstall {Name}?",
                    $"{Name} was installed with its own installer, so it is uninstalled in Windows: Settings → Apps opens, find {Name} there.",
                    "Open Apps"))
            {
                _shell.Main.Hub.Installed.Remove(Hub.Id);
                Os.Open("ms-settings:appsfeatures");
                Refresh();
            }
            return;
        }
        var dialog = new DialogViewModel();
        if (Hub.UsesLicence && LicenceWorksHere)
        {
            dialog.Option = "Also free this PC's licence activation, so the key can be used on another PC";
        }
        if (!await _shell.Main.AskAsync($"Uninstall {Name}?",
                $"RotaHub removes {Name}'s program files" + (_installed.Links.Count > 0 ? " and its packages in the Community folder" : "") +
                ". Your settings stay, so they are there again if you reinstall.", "Uninstall", danger: true, dialog: dialog))
            return;

        Busy = true;
        Step = "Uninstalling";
        Indeterminate = true;
        try
        {
            while (true)
            {
                try
                {
                    var record = _installed;
                    await Task.Run(() => _shell.Main.Hub.Installer.Uninstall(record));
                    break;
                }
                catch (ProgramsOpenException open)
                {
                    if (!await _shell.Main.AskAsync($"Close {Name}?", $"{string.Join(", ", open.Names)} is open and must close first.", "Close and continue"))
                        return;
                    await Processes.CloseAsync(open.Names, TimeSpan.FromSeconds(8));
                }
            }
            if (dialog.OptionChecked)
            {
                Step = "Freeing the licence activation";
                var freed = await _shell.Main.Licences.DeactivateAsync(Hub);
                if (freed.Status != LicenseStatus.Valid)
                    _shell.Toast("The activation could not be freed: " + freed.Message, error: true);
                _ = _shell.LoadLicencesAsync();
            }
            Refresh();
            _shell.Toast($"{Name} is uninstalled.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Error("Uninstall " + Hub.Id, ex);
            _shell.Toast(ex.Message, error: true);
        }
        finally
        {
            Busy = false;
            Indeterminate = false;
            Step = "";
        }
    }

    private IProgress<InstallProgress> Reporter() => new Progress<InstallProgress>(p =>
    {
        Step = p.Step;
        Detail = p.Detail;
        Indeterminate = p.Fraction == null;
        Progress = p.Fraction ?? 0;
    });
}
