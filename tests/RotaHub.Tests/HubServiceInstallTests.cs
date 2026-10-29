using System.IO.Compression;
using System.Security.Cryptography;
using RotaHub.Core;
using Xunit;

namespace RotaHub.Tests;

public sealed class HubServiceInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rotahub-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task An_install_error_reaches_the_caller_instead_of_task_canceled()
    {
        // HeavyPro 0.6.1 and 0.6.2 downloaded fine, then RotaHub said only "A task was canceled":
        // the error from the install step (here: HeavyPro still open) was swallowed, so RotaHub
        // never offered "Close and continue".
        var paths = new HubPaths(Path.Combine(_root, "hub"));
        var hub = new HubService(paths, new HubSettings { LibraryDir = Path.Combine(_root, "Library") }, new RotaidemApi("https://example.com"), null);
        hub.Installer.ProgramsRunning = _ => new[] { "HeavyPro" };
        hub.Installer.SimRunning = _ => false;
        hub.Installer.FindCommunity = sim => new SimInstall(sim, "Test", Path.Combine(_root, "Community"));
        hub.Installer.MakeShortcuts = false;

        // Already downloaded and matching its hash, so nothing goes to the network.
        var zip = Path.Combine(paths.Downloads, "rotaidem-heavypro-0.6.2.zip");
        Directory.CreateDirectory(paths.Downloads);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(archive.CreateEntry("app/HeavyPro.exe").Open()))
            w.Write("exe");
        string sha;
        using (var sha256 = SHA256.Create())
        using (var file = File.OpenRead(zip))
            sha = BitConverter.ToString(sha256.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        var release = new HubRelease("rotaidem-heavypro", "HeavyPro", new Version("0.6.2"), "", "https://example.com/p.zip", sha, new FileInfo(zip).Length,
            Path.Combine(_root, "HeavyPro"), "HeavyPro.exe", "HeavyPro", new[] { "HeavyPro" }, Array.Empty<CommunityPart>(), "");

        var open = await Assert.ThrowsAsync<ProgramsOpenException>(() => hub.InstallAsync(release, null!, null!, null, CancellationToken.None));
        Assert.Equal(new[] { "HeavyPro" }, open.Names);
    }
}
