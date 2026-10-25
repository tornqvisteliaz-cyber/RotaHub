using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RotaHub.Core;

namespace RotaHub;

/// <summary>A small log in RotaHub's folder, for support questions.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string? File { get; set; }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception ex) => Write("ERROR", message + ": " + ex);

    private static void Write(string level, string message)
    {
        if (File == null)
            return;
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(File);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                    info.Delete();
                System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>Product pictures from rotaidem.com, cached on disk so the library also looks right offline.</summary>
public static class ImageCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly Dictionary<string, ImageSource> Memory = new();
    private static string? _folder;

    public static void Init(HubPaths paths) => _folder = Path.Combine(paths.Root, "cache", "images");

    /// <summary>One of the website's own photos built into RotaHub (approach, clouds, hero, night).</summary>
    public static ImageSource Builtin(string name)
    {
        var image = new BitmapImage(new Uri($"pack://application:,,,/Assets/Images/{name}.jpg"));
        image.Freeze();
        return image;
    }

    public static async Task<ImageSource?> GetAsync(string url)
    {
        if (string.IsNullOrEmpty(url))
            return null;
        if (Memory.TryGetValue(url, out var known))
            return known;
        var file = _folder == null ? null : Path.Combine(_folder, Hash(url));
        byte[]? bytes = null;
        try
        {
            bytes = await Http.GetByteArrayAsync(url);
            if (file != null)
            {
                Directory.CreateDirectory(_folder!);
                System.IO.File.WriteAllBytes(file, bytes);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (file != null && System.IO.File.Exists(file))
                bytes = System.IO.File.ReadAllBytes(file);
        }
        if (bytes == null)
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.DecodePixelWidth = 1600;
            image.EndInit();
            image.Freeze();
            Memory[url] = image;
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        {
            return null;
        }
    }

    private static string Hash(string url)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", "").Substring(0, 32);
    }
}

public static class Os
{
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("Could not open " + target, ex);
        }
    }

    public static void ShowInExplorer(string path)
    {
        if (Directory.Exists(path))
            Open(path);
        else if (System.IO.File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true })?.Dispose();
    }

    public static string? PickFolder(string description, string? start)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            ShowNewFolderButton = true,
            SelectedPath = start != null && Directory.Exists(start) ? start : ""
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }
}

/// <summary>Public keys built into RotaHub.</summary>
public static class Keys
{
    public static readonly string? Licence = Read("RotaHub.LicencePublicKey.json");
    public static readonly string? Release = Read("RotaHub.ReleasePublicKey.json");

    private static string? Read(string name)
    {
        using var stream = typeof(Keys).Assembly.GetManifestResourceStream(name);
        if (stream == null)
            return null;
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd().Trim();
        return text.Length > 0 ? text : null;
    }
}
