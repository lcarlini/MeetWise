using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using MeetWise.Core.Models;

namespace MeetWise.Tests;

public class ModelManagerTests
{
    private sealed class RangeServer : HttpMessageHandler
    {
        public byte[] Data = Array.Empty<byte>();
        public bool HonorRange = true;
        public List<RangeHeaderValue?> Ranges = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Ranges.Add(request.Headers.Range);
            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            if (from is { } f && HonorRange)
            {
                if (f >= Data.Length) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
                var resp = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Data[(int)f..]) };
                return Task.FromResult(resp);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Data) });
        }
    }

    private static (ModelManager mm, ModelEntry entry, string dest) Setup(RangeServer server, int size = 5000)
    {
        server.Data = new byte[size];
        new Random(2).NextBytes(server.Data);
        var root = Path.Combine(Path.GetTempPath(), "mw-mm-" + Guid.NewGuid().ToString("N"));
        var entry = new ModelEntry("m.bin", "llm", "https://example.test/m.bin", Convert.ToHexString(SHA256.HashData(server.Data)), size);
        var mm = new ModelManager(new HttpClient(server), root);
        return (mm, entry, mm.PathFor(entry));
    }

    [Fact]
    public async Task Download_Fresh_Verifies_AndInstalls()
    {
        var server = new RangeServer();
        var (mm, entry, dest) = Setup(server);
        await mm.EnsureAsync(entry);
        Assert.True(mm.IsInstalled(entry));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task Download_ResumesPartialFile_WithRangeRequest()
    {
        var server = new RangeServer();
        var (mm, entry, dest) = Setup(server);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await File.WriteAllBytesAsync(dest + ".part", server.Data[..2000]);
        await mm.EnsureAsync(entry);
        Assert.Equal(2000, server.Ranges.Single()!.Ranges.First().From);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task Download_ServerIgnoresRange_RestartsCleanly()
    {
        var server = new RangeServer { HonorRange = false };
        var (mm, entry, dest) = Setup(server);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await File.WriteAllBytesAsync(dest + ".part", new byte[] { 9, 9, 9 });
        await mm.EnsureAsync(entry);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task Download_CorruptPartial_IsRejectedByChecksum_AndRemoved()
    {
        var server = new RangeServer();
        var (mm, entry, dest) = Setup(server);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var bad = (byte[])server.Data[..2000].Clone();
        bad[10] ^= 0xFF;
        await File.WriteAllBytesAsync(dest + ".part", bad);
        await Assert.ThrowsAsync<InvalidDataException>(() => mm.EnsureAsync(entry));
        Assert.False(mm.IsInstalled(entry));
        Assert.False(File.Exists(dest + ".part"));
        await mm.EnsureAsync(entry); // a retry now succeeds from scratch
        Assert.True(mm.IsInstalled(entry));
    }

    [Fact]
    public async Task Download_CompletePartial_416_Restarts()
    {
        var server = new RangeServer();
        var (mm, entry, dest) = Setup(server);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await File.WriteAllBytesAsync(dest + ".part", new byte[server.Data.Length + 10]);
        await mm.EnsureAsync(entry);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dest));
    }
}
