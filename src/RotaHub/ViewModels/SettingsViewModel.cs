using System.Collections.ObjectModel;
using System.Windows;
using RotaHub.Core;

namespace RotaHub.ViewModels;

public sealed class SettingsViewModel : Observable
{
    private readonly ShellViewModel _shell;

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        ChangeLibraryCommand = new Command(ChangeLibrary);
        OpenLibraryCommand = new Command(() =>
        {
            System.IO.Directory.CreateDirectory(Library);
            Os.Open(Library);
        });
        CheckUpdatesCommand = new Command(() => _shell.CheckSelfUpdateAsync(manual: true));
        UninstallCommand = new Command(UninstallRotaHubAsync);
        LogCommand = new Command(() => Os.ShowInExplorer(_shell.Main.Paths.Log));
        DevicesCommand = new Command(() => Os.Open(_shell.Main.Api.BaseUrl + "/account"));
    }

    private HubSettings Settings => _shell.Main.Settings;

    public ShellViewModel Shell => _shell;

    public string Library => Settings.LibraryDir;

    public ObservableCollection<SimRowViewModel> Sims { get; } = new();

    public bool LaunchAfterInstall
    {
        get => Settings.LaunchAfterInstall;
        set
        {
            Settings.LaunchAfterInstall = value;
            Save();
            Raise();
        }
    }

    public bool AutoUpdate
    {
        get => Settings.AutoUpdate;
        set
        {
            Settings.AutoUpdate = value;
            Save();
            Raise();
        }
    }

    public bool DesktopShortcuts
    {
        get => Settings.DesktopShortcuts;
        set
        {
            Settings.DesktopShortcuts = value;
            Save();
            Raise();
        }
    }

    public Command ChangeLibraryCommand { get; }
    public Command OpenLibraryCommand { get; }
    public Command CheckUpdatesCommand { get; }
    public Command UninstallCommand { get; }
    public Command LogCommand { get; }
    public Command DevicesCommand { get; }

    public void Refresh()
    {
        Sims.Clear();
        foreach (var sim in SimLocator.Sims)
            Sims.Add(new SimRowViewModel(this, sim));
        Raise(nameof(Library));
    }

    internal void Save()
    {
        try
        {
            Settings.Save(_shell.Main.Paths);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _shell.Toast("Settings could not be saved: " + ex.Message, error: true);
        }
    }

    internal HubSettings Raw => Settings;

    internal void Changed(string text)
    {
        Save();
        Refresh();
        foreach (var p in _shell.Products)
            p.Refresh();
        _shell.Toast(text);
    }

    private void ChangeLibrary()
    {
        var folder = Os.PickFolder("Choose where RotaHub keeps installed products. Community packages are linked from here into the simulator.", Library);
        if (folder == null)
            return;
        Settings.LibraryDir = folder;
        var installed = _shell.Main.Hub.Installed.All().Count(p => p.Kind == "package");
        Changed(installed > 0
            ? "New installs go to the new folder. Products already installed stay where they are until you update or repair them."
            : "Library folder changed.");
    }

    private async Task UninstallRotaHubAsync()
    {
        var dialog = new DialogViewModel { Option = "Also uninstall every product RotaHub installed" };
        if (!await _shell.Main.AskAsync("Uninstall RotaHub?",
                "RotaHub, its shortcuts and your saved login are removed from this PC. Products stay installed unless you tick the box.",
                "Uninstall RotaHub", danger: true, dialog: dialog))
            return;
        if (!SelfInstall.IsInstalledCopy(_shell.Main.Paths))
        {
            _shell.Toast("This copy runs from outside RotaHub's install folder (a developer build). Remove it by deleting it.", error: true);
            return;
        }
        SelfInstall.Uninstall(_shell.Main.Paths, _shell.Main.Hub, dialog.OptionChecked);
        Application.Current.Shutdown();
    }
}

public sealed class SimRowViewModel : Observable
{
    private readonly SettingsViewModel _settings;

    public SimRowViewModel(SettingsViewModel settings, string sim)
    {
        _settings = settings;
        Sim = sim;
        Install = SimLocator.Find(sim, settings.Raw);
        Chosen = settings.Raw.CommunityOverrides.ContainsKey(sim);
        ChooseCommand = new Command(Choose);
        ResetCommand = new Command(Reset, () => Chosen);
        OpenCommand = new Command(() => Os.Open(Install!.Community), () => Install != null);
    }

    public string Sim { get; }
    public SimInstall? Install { get; }
    public bool Chosen { get; }
    public bool Found => Install != null;
    public string Title => SimLocator.Title(Sim);
    public string Edition => Install?.Edition ?? "Not found";
    public string Community => Install?.Community ?? "RotaHub did not find this simulator. If it is installed, choose its Community folder.";

    public Command ChooseCommand { get; }
    public Command ResetCommand { get; }
    public Command OpenCommand { get; }

    private void Choose()
    {
        var folder = Os.PickFolder($"Choose the Community folder of {Title}.", Install?.Community);
        if (folder == null)
            return;
        if (!string.Equals(System.IO.Path.GetFileName(folder), "Community", StringComparison.OrdinalIgnoreCase) &&
            System.IO.Directory.Exists(System.IO.Path.Combine(folder, "Community")))
            folder = System.IO.Path.Combine(folder, "Community");
        _settings.Raw.CommunityOverrides[Sim] = folder;
        _settings.Changed($"{Title}: Community folder set. Use Repair on installed products to link them there.");
    }

    private void Reset()
    {
        _settings.Raw.CommunityOverrides.Remove(Sim);
        _settings.Changed($"{Title}: RotaHub finds the Community folder by itself again.");
    }
}
