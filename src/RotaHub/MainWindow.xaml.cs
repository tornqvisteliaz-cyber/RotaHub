using System.Windows;

namespace RotaHub;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) =>
        {
            var max = WindowState == WindowState.Maximized;
            // Windows maximises a resizable window 8px past each screen edge; pad that back.
            Frame.BorderThickness = max ? new Thickness(8) : new Thickness(1);
            Frame.BorderBrush = (System.Windows.Media.Brush)FindResource(max ? "Navy" : "Line");
            MaxButton.Content = max ? "\uE923" : "\uE922";
            MaxButton.ToolTip = max ? "Restore" : "Maximise";
        };
    }

    private void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
