using System.IO.Compression;
using System.Runtime.InteropServices;

namespace MeetWise.Core.Models;

/// <summary>
/// Optional "GPU acceleration pack": the CUDA 13 runtime libraries (cudart + cuBLAS, about 400 MB) that Whisper needs to run on an NVIDIA GPU.
/// They are NVIDIA's official redistributable archives, downloaded on demand from NVIDIA with SHA-256 verification, so the
/// user does not have to install the CUDA Toolkit. Without the pack everything still works on the CPU.
/// </summary>
public static class CudaRuntimePack
{
    private const string Base = "https://developer.download.nvidia.com/compute/cuda/redist/";

    public static readonly ModelEntry CudaRt = new(
        "cuda_cudart-13.0.96.zip", "cuda", Base + "cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-13.0.96-archive.zip",
        "a2ed875f9997aa24904fb70cc9db3acd9308433cde99bc8e63ec1271c9da31b4", 2_873_447);

    public static readonly ModelEntry CuBlas = new(
        "libcublas-13.1.0.3.zip", "cuda", Base + "libcublas/windows-x86_64/libcublas-windows-x86_64-13.1.0.3-archive.zip",
        "4ac4847bbe4f7709b244956fcfc32197a2954ee70b155cb67eebd9ee26f7e339", 403_672_823);

    public static readonly string[] RequiredDlls = { "cudart64_13.dll", "cublas64_13.dll", "cublasLt64_13.dll" };

    public static string Directory_ => Path.Combine(AppPaths.Local, "cuda", "13");

    public static bool IsInstalled(string? dir = null)
        => RequiredDlls.All(d => File.Exists(Path.Combine(dir ?? Directory_, d)));

    public static async Task EnsureAsync(HttpClient http, IProgress<double>? progress = null, string? modelsRoot = null, string? targetDir = null, CancellationToken ct = default)
    {
        var dir = targetDir ?? Directory_;
        if (IsInstalled(dir)) return;
        var manager = new ModelManager(http, modelsRoot);
        System.IO.Directory.CreateDirectory(dir);

        // cuBLAS is 99% of the bytes, so report progress for it only.
        await manager.EnsureAsync(CudaRt, null, ct);
        await manager.EnsureAsync(CuBlas, progress, ct);
        ExtractDlls(manager.PathFor(CudaRt), dir, new[] { "cudart64_13.dll" });
        ExtractDlls(manager.PathFor(CuBlas), dir, new[] { "cublas64_13.dll", "cublasLt64_13.dll" });

        if (!IsInstalled(dir)) throw new InvalidDataException("The CUDA archives did not contain the expected libraries.");
        // The archives are only needed for extraction; free ~400 MB.
        TryDelete(manager.PathFor(CuBlas));
        TryDelete(manager.PathFor(CudaRt));
    }

    internal static void ExtractDlls(string zipPath, string targetDir, IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (!wanted.Contains(entry.Name)) continue;
            var dest = Path.Combine(targetDir, entry.Name);
            var tmp = dest + ".tmp";
            entry.ExtractToFile(tmp, overwrite: true);
            File.Move(tmp, dest, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string path);

    /// <summary>Makes the libraries visible to native loaders. Call before the first Whisper use in this process.</summary>
    public static bool Activate(string? dir = null)
    {
        dir ??= Directory_;
        if (!IsInstalled(dir)) return false;
        AddDllDirectory(dir);
        // llama.cpp's loader (LLamaSharp) detects CUDA from CUDA_PATH\version.json, as the full toolkit would provide it.
        // The pack plays that role, so the GPU backend is chosen without installing the CUDA Toolkit.
        try
        {
            var versionFile = Path.Combine(dir, "version.json");
            if (!File.Exists(versionFile))
                File.WriteAllText(versionFile, "{\"cuda\": {\"name\": \"CUDA SDK\", \"version\": \"13.0.96\"}}");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUDA_PATH")))
                Environment.SetEnvironmentVariable("CUDA_PATH", dir);
        }
        catch (IOException) { /* detection is a convenience; the CPU backend still works */ }
        // Whisper.net locates cudart through the normal search path, so PATH is needed as well.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.Split(';').Contains(dir, StringComparer.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", dir + ";" + path);
        return true;
    }
}
