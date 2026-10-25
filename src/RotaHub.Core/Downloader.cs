using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace RotaHub.Core;

/// <summary>Bytes so far and in total (0 when the server does not say).</summary>
public readonly record struct DownloadProgress(long Received, long Total)
{
    public double Fraction => Total > 0 ? Math.Min(1, (double)Received / Total) : 0;
}

public static class Downloader
{
    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destination"/>. An interrupted download is kept as
    /// destination.part and resumed next time. With <paramref name="sha256"/> the file is checked and a
    /// mismatch deleted, so a damaged or swapped file never reaches the installer.
    /// </summary>
    public static async Task DownloadAsync(HttpClient http, string url, string destination, string? sha256, long expectedSize,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) && sha256 != null && HashMatches(destination, sha256))
        {
            var size = new FileInfo(destination).Length;
            progress?.Report(new DownloadProgress(size, size));
            return;
        }

        var part = destination + ".part";
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (expectedSize > 0 && have > expectedSize)
        {
            File.Delete(part);
            have = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0)
            request.Headers.Range = new RangeHeaderValue(have, null);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new IOException("The download could not start. Check your internet connection and try again.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // The part is complete already (or stale); start over cleanly.
                File.Delete(part);
                await DownloadAsync(http, url, destination, sha256, expectedSize, progress, ct).ConfigureAwait(false);
                return;
            }
            if (!response.IsSuccessStatusCode)
                throw new IOException($"The download failed ({(int)response.StatusCode}). Try again in a minute.");

            var resuming = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!resuming)
                have = 0;
            var total = response.Content.Headers.ContentLength is { } length ? length + have : expectedSize;

            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new FileStream(part, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                var lastReport = DateTime.MinValue;
                int read;
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                    have += read;
                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                    {
                        lastReport = DateTime.UtcNow;
                        progress?.Report(new DownloadProgress(have, total));
                    }
                }
            }
            progress?.Report(new DownloadProgress(have, Math.Max(have, total)));
        }

        if (sha256 != null && !HashMatches(part, sha256))
        {
            File.Delete(part);
            throw new InvalidDataException("The download was damaged (its checksum does not match the release). Try again.");
        }
        if (File.Exists(destination))
            File.Delete(destination);
        File.Move(part, destination);
    }

    public static string Sha256(string file)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(file);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private static bool HashMatches(string file, string sha256) =>
        string.Equals(Sha256(file), sha256, StringComparison.OrdinalIgnoreCase);
}
