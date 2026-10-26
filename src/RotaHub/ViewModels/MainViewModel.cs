using System.Windows.Controls;
using RotaHub.Core;

namespace RotaHub.ViewModels;

/// <summary>The window: either the login or the signed-in shell, plus the dialog shown over either.</summary>
public sealed class MainViewModel : Observable
{
    private object? _screen;
    private DialogViewModel? _dialog;

    public MainViewModel(HubPaths paths)
    {
        Paths = paths;
        Settings = HubSettings.Load(paths);
        Api = new RotaidemApi(ServerUrl(), userAgent: $"RotaHub/{SelfInstall.Current.ToString(3)} (Windows NT {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor})");
        Hub = new HubService(paths, Settings, Api, Keys.Release);
        Licences = new Licences(Api.BaseUrl, Keys.Licence);
        Sessions = new SessionStore(paths);
    }

    /// <summary>rotaidem.com; debug builds can point at a local copy of the website with ROTAHUB_SERVER.</summary>
    private static string ServerUrl()
    {
#if DEBUG
        var local = Environment.GetEnvironmentVariable("ROTAHUB_SERVER");
        if (!string.IsNullOrWhiteSpace(local))
            return local!;
#endif
        return RotaidemApi.DefaultBaseUrl;
    }

    public HubPaths Paths { get; }
    public HubSettings Settings { get; }
    public RotaidemApi Api { get; }
    public HubService Hub { get; }
    public Licences Licences { get; }
    public SessionStore Sessions { get; }

    public object? Screen
    {
        get => _screen;
        private set => Set(ref _screen, value);
    }

    public DialogViewModel? Dialog
    {
        get => _dialog;
        private set => Set(ref _dialog, value);
    }

    public async Task StartAsync()
    {
        var saved = Sessions.Load();
        if (saved == null)
        {
            Screen = new LoginViewModel(this);
            return;
        }

        // Open straight away with the saved login; the website confirms it in the background.
        Api.Token = saved.Token;
        var shell = ShowShell(saved.Account);
        try
        {
            var account = await Api.MeAsync();
            Sessions.Save(new SignedIn(saved.Token, account));
            shell.Account = account;
        }
        catch (ApiException ex) when (ex.SignedOut)
        {
            await SignOutAsync("Your login has ended (signed out on the website, or not used for a long time). Log in again.");
        }
        catch (ApiException ex)
        {
            shell.Offline = ex.Status == 0;
        }
    }

    public void CompleteLogin(SignedIn session)
    {
        Sessions.Save(session);
        ShowShell(session.Account);
    }

    public async Task SignOutAsync(string? message = null)
    {
        App.Shell?.Close();
        await Api.LogoutAsync();
        Sessions.Clear();
        App.Shell = null;
        Screen = new LoginViewModel(this) { Error = message ?? "" };
    }

    private ShellViewModel ShowShell(Account account)
    {
        var shell = new ShellViewModel(this, account);
        App.Shell = shell;
        Screen = shell;
        _ = shell.LoadAsync();
        return shell;
    }

    /// <summary>A question in RotaHub's own style. True when the main button was chosen.</summary>
    public async Task<bool> AskAsync(string title, string message, string accept, string cancel = "Cancel", bool danger = false, DialogViewModel? dialog = null)
    {
        dialog ??= new DialogViewModel();
        dialog.Title = title;
        dialog.Message = message;
        dialog.Accept = accept;
        dialog.Cancel = cancel;
        dialog.Danger = danger;
        Dialog = dialog;
        try
        {
            return await dialog.Answer;
        }
        finally
        {
            Dialog = null;
        }
    }
}

public sealed class DialogViewModel : Observable
{
    private readonly TaskCompletionSource<bool> _answer = new();

    public DialogViewModel()
    {
        AcceptCommand = new Command(() => _answer.TrySetResult(true));
        CancelCommand = new Command(() => _answer.TrySetResult(false));
    }

    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string Accept { get; set; } = "OK";
    public string Cancel { get; set; } = "Cancel";
    public bool Danger { get; set; }
    public bool HasCancel => Cancel.Length > 0;

    /// <summary>An optional tick box under the message.</summary>
    public string Option { get; set; } = "";
    public bool OptionChecked { get; set; }

    public Command AcceptCommand { get; }
    public Command CancelCommand { get; }
    public Task<bool> Answer => _answer.Task;
}

public sealed class LoginViewModel : Observable
{
    private readonly MainViewModel _main;
    private string _email = "";
    private string _error = "";
    private bool _busy;
    private bool _unverified;
    private Tone _tone = Tone.Danger;

    public LoginViewModel(MainViewModel main)
    {
        _main = main;
        LoginCommand = new Command(p => LoginAsync(p as PasswordBox), _ => !Busy);
        ResendCommand = new Command(ResendAsync);
        SignUpCommand = new Command(() => Os.Open(main.Api.BaseUrl + "/signup"));
        ForgotCommand = new Command(() => Os.Open(main.Api.BaseUrl + "/forgot-password"));
        WebsiteCommand = new Command(() => Os.Open(main.Api.BaseUrl));
    }

    public string Email
    {
        get => _email;
        set => Set(ref _email, value);
    }

    public string Error
    {
        get => _error;
        set => Set(ref _error, value);
    }

    /// <summary>Neutral while working, Danger for a refusal, Ok for good news.</summary>
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

    public bool Unverified
    {
        get => _unverified;
        private set => Set(ref _unverified, value);
    }

    public Command LoginCommand { get; }
    public Command ResendCommand { get; }
    public Command SignUpCommand { get; }
    public Command ForgotCommand { get; }
    public Command WebsiteCommand { get; }

    public System.Windows.Media.ImageSource SidePicture { get; } = ImageCache.Builtin("hero");

    private async Task LoginAsync(PasswordBox? box)
    {
        var password = box?.Password ?? "";
        if (Email.Trim().Length == 0 || password.Length == 0)
        {
            ShowError("Enter your email address (or username) and password.");
            return;
        }
        Busy = true;
        Tone = Tone.Neutral;
        Unverified = false;
        Error = "Logging in…";
        try
        {
            var session = await _main.Api.LoginAsync(Email.Trim(), password);
            Error = "";
            _main.CompleteLogin(session);
        }
        catch (ApiException ex)
        {
            Unverified = ex.Unverified;
            ShowError(ex.Status == 401 ? "That email or password is not right." : ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task ResendAsync()
    {
        try
        {
            await _main.Api.ResendConfirmationAsync(Email.Trim());
            Unverified = false;
            Tone = Tone.Ok;
            Error = "A new confirmation link is on its way. Check your inbox (and spam), click it, then log in here.";
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        Tone = Tone.Danger;
        Error = message;
    }
}
