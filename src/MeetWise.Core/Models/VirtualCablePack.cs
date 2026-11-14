using System.IO.Compression;

namespace MeetWise.Core.Models;

/// <summary>
/// The official VB-CABLE driver pack by VB-Audio (vb-cable.com, donationware). It is NOT bundled in MeetWise's installer, because its licence
/// forbids integrating the package in another software's installation procedure without the author's agreement. Instead, when the user asks
/// for the MeetWise Microphone, the unmodified pack is downloaded from VB-Audio and its own setup program is run.
/// </summary>
public static class VirtualCablePack
{
    public const string Origin = "www.vb-cable.com";
    public const string Notice = "The MeetWise Microphone is powered by VB-CABLE, a virtual audio driver by VB-Audio (www.vb-cable.com). VB-CABLE is donationware: all participations are welcome.";
    public const string DonateUrl = "https://vb-audio.com/Cable/";

    public static readonly ModelEntry Archive = new(
        "VBCABLE_Driver_Pack46.zip", "vbcable", "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip",
        "bc44e8974191a5a2427d6c979e2dab08dbb09925f3f435dfa2f0ba3e45b6eb90", 1_324_680);

    public static string Directory_ => Path.Combine(AppPaths.Local, "vbcable");

    public static string SetupExe(string? dir = null) => Path.Combine(dir ?? Directory_, "VBCABLE_Setup_x64.exe");

    public static bool IsExtracted(string? dir = null) => File.Exists(SetupExe(dir));

    /// <summary>Downloads (hash-verified) and unpacks the pack, unmodified, and returns the path of the setup program.</summary>
    public static async Task<string> EnsureAsync(HttpClient http, IProgress<double>? progress = null, string? modelsRoot = null,
        string? targetDir = null, CancellationToken ct = default)
    {
        var dir = targetDir ?? Directory_;
        if (IsExtracted(dir)) return SetupExe(dir);

        var manager = new ModelManager(http, modelsRoot);
        await manager.EnsureAsync(Archive, progress, ct);
        System.IO.Directory.CreateDirectory(dir);
        ZipFile.ExtractToDirectory(manager.PathFor(Archive), dir, overwriteFiles: true);
        if (!IsExtracted(dir)) throw new InvalidDataException("The VB-CABLE package did not contain its setup program.");
        return SetupExe(dir);
    }
}
