using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RotaHub.Core;
using RotaHub.ViewModels;

namespace RotaHub;

public partial class App : Application
{
    private const string ShowEventName = @"Local\Rotaidem.RotaHub.Show";
    private Mutex? _single;

    /// <summary>The signed-in shell, for toasts from anywhere; null on the login screen.</summary>
    public static ShellViewModel? Shell { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
        var paths = new HubPaths();
        Directory.CreateDirectory(paths.Root);
        Log.File = paths.Log;
        ImageCache.Init(paths);
        DispatcherUnhandledException += OnCrash;

        var args = e.Args.Select(a => a.ToLowerInvariant()).ToArray();
        if (args.Contains("--uninstall"))
        {
            UninstallFromWindows(paths);
            return;
        }

        // Started from a download: install into %LOCALAPPDATA% and run from there.
        // Debug builds and --portable run where they are.
#if !DEBUG
        if (!args.Contains("--portable") && !SelfInstall.IsInstalledCopy(paths))
        {
            try
            {
                var installed = SelfInstall.Install(paths);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installed) { UseShellExecute = true })?.Dispose();
                Shutdown();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Most likely RotaHub is already open from its install folder: show that one.
                Log.Error("Self install", ex);
            }
        }
#endif

        _single = new Mutex(true, @"Local\Rotaidem.RotaHub", out var first);
        if (!first)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            Shutdown();
            return;
        }

        if (args.Contains("--updated"))
        {
            SelfInstall.CleanUpAfterUpdate(paths);
            if (SelfInstall.IsInstalledCopy(paths))
                SelfInstall.Register(paths);
        }

        Log.Info($"RotaHub {SelfInstall.Current} starting");
        var main = new MainViewModel(paths);
        var window = new MainWindow { DataContext = main };
        MainWindow = window;
        window.Show();
        ListenForSecondStart(window);
        _ = main.StartAsync();
    }

    /// <summary>A second RotaHub start brings this window to the front instead.</summary>
    private static void ListenForSecondStart(Window window)
    {
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (show.WaitOne())
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (window.WindowState == WindowState.Minimized)
                        window.WindowState = WindowState.Normal;
                    window.Activate();
                    window.Topmost = true;
                    window.Topmost = false;
                }));
            }
        }) { IsBackground = true };
        thread.Start();
    }

    /// <summary>"Uninstall" in Windows' Apps list.</summary>
    private void UninstallFromWindows(HubPaths paths)
    {
        var answer = MessageBox.Show(
            "Uninstall RotaHub?\n\nChoose Yes to also uninstall every product RotaHub installed (such as HeavyPro), or No to keep them.",
            "RotaHub", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Cancel)
        {
            var settings = HubSettings.Load(paths);
            var hub = new HubService(paths, settings, new RotaidemApi(), Keys.Release);
            SelfInstall.Uninstall(paths, hub, answer == MessageBoxResult.Yes);
            MessageBox.Show("RotaHub has been uninstalled.", "RotaHub", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        Shutdown();
    }

    private static void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled", e.Exception);
        Shell?.Toast("Something went wrong: " + e.Exception.Message, error: true);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}
