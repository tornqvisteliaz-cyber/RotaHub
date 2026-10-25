using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace RotaHub;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void Raise(params string[] names)
    {
        foreach (var n in names)
            Raise(n);
    }
}

/// <summary>A command that runs async work and stays disabled while it runs.</summary>
public sealed class Command : ICommand
{
    private readonly Func<object?, Task> _run;
    private readonly Func<object?, bool>? _can;
    private bool _running;

    public Command(Func<Task> run, Func<bool>? can = null) : this(_ => run(), can == null ? null : _ => can()) { }

    public Command(Action run, Func<bool>? can = null) : this(_ => { run(); return Task.CompletedTask; }, can == null ? null : _ => can()) { }

    public Command(Func<object?, Task> run, Func<object?, bool>? can = null)
    {
        _run = run;
        _can = can;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (_can?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await _run(parameter);
        }
        catch (Exception ex)
        {
            Log.Error("Command failed", ex);
            App.Shell?.Toast("Something went wrong: " + ex.Message, error: true);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var on = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            System.Collections.ICollection c => c.Count > 0,
            null => false,
            _ => true
        };
        return on ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class Upper : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value?.ToString() ?? "").ToUpperInvariant();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Badge colours by tone, like the site's .badge.published / coming_soon / in_development.</summary>
public enum Tone
{
    Neutral,
    Ok,
    Info,
    Warn,
    Danger
}

public sealed class ToneBrush : IValueConverter
{
    public bool Soft { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tone = value is Tone t ? t : Tone.Neutral;
        var key = tone switch
        {
            Tone.Ok => "Ok",
            Tone.Info => "Info",
            Tone.Warn => "Warn",
            Tone.Danger => "Danger",
            _ => Soft ? "NavyHover" : "Text"
        };
        if (Soft && tone != Tone.Neutral)
            key += "Soft";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
