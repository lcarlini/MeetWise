using SharpCompress.Readers;
using System.Security.Cryptography;

namespace MeetWise.Core.Models;

public sealed record ModelEntry(string Id, string Kind, string Url, string Sha256, long SizeBytes);

/// <summary>Downloads models on demand to %LocalAppData%\MeetWise\models with SHA-256 verification.</summary>
public sealed class ModelManager
{
    private readonly HttpClient _http;
    private readonly string _root;

    public ModelManager(HttpClient http, string? root = null)
    {
        _http = http;
        _root = root ?? AppPaths.Models;
    }

    public string PathFor(ModelEntry m) => Path.Combine(_root, m.Kind, m.Id);

    public bool IsInstalled(ModelEntry m) => IsArchive(m) ? Directory.Exists(PathFor(m)) : File.Exists(PathFor(m));

    private static bool IsArchive(ModelEntry m) => m.Url.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase);

    public async Task EnsureAsync(ModelEntry m, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (IsInstalled(m)) return;
        var archive = IsArchive(m);
        var dest = archive ? Path.Combine(_root, m.Kind, m.Id + ".tar.bz2") : PathFor(m);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".part";
        // Resume a partial download with an HTTP Range request; fall back to a full download if the server ignores it.
        long existing = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
        using (var req = new HttpRequestMessage(HttpMethod.Get, m.Url))
        {
            if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(tmp); // stale or complete-but-unverified partial: start over
                existing = 0;
                using var retry = new HttpRequestMessage(HttpMethod.Get, m.Url);
                using var full = await _http.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, ct);
                await CopyAsync(full, tmp, 0, m.SizeBytes, progress, ct);
            }
            else
            {
                resp.EnsureSuccessStatusCode();
                bool resumed = resp.StatusCode == System.Net.HttpStatusCode.PartialContent && existing > 0;
                await CopyAsync(resp, tmp, resumed ? existing : 0, m.SizeBytes, progress, ct);
            }
        }
        if (!string.Equals(await Sha256Async(tmp, ct), m.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tmp);
            throw new InvalidDataException($"Checksum mismatch for model {m.Id}.");
        }
        File.Move(tmp, dest, overwrite: true);
        if (archive)
        {
            // Extract to a staging folder first so a half-extracted model is never seen as installed.
            var kindDir = Path.Combine(_root, m.Kind);
            var staging = Path.Combine(kindDir, m.Id + ".extracting");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            await Task.Run(() => ExtractTarBz2(dest, staging), ct);
            var inner = Path.Combine(staging, m.Id);
            Directory.Move(Directory.Exists(inner) ? inner : staging, PathFor(m));
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            File.Delete(dest);
        }
    }

    internal static void ExtractTarBz2(string archive, string target)
    {
        Directory.CreateDirectory(target);
        using var stream = File.OpenRead(archive);
        using var reader = SharpCompress.Readers.ReaderFactory.OpenReader(stream);
        reader.WriteAllToDirectory(target, new SharpCompress.Common.ExtractionOptions { ExtractFullPath = true, Overwrite = true });
    }

    private static async Task CopyAsync(HttpResponseMessage resp, string tmp, long offset, long expectedSize,
        IProgress<double>? progress, CancellationToken ct)
    {
        resp.EnsureSuccessStatusCode();
        long total = offset + (resp.Content.Headers.ContentLength ?? Math.Max(0, expectedSize - offset));
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(tmp, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write);
        var buf = new byte[81920];
        long done = offset;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            if (total > 0) progress?.Report((double)done / total);
        }
    }

    public static async Task<string> Sha256Async(string file, CancellationToken ct = default)
    {
        await using var s = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(s, ct));
    }
}
