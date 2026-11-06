using NAudio.Wave;
using Windows.Media.SpeechSynthesis;

namespace MeetWise.Audio;

/// <summary>
/// Offline text-to-speech with the voices installed in Windows 11 (the same engine as Narrator). Used to speak translations,
/// or to re-voice speech in a neutral accent. More languages become available when the user adds Windows language packs.
/// </summary>
public sealed class WindowsVoice : IDisposable
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>BCP-47 tags of the voices installed on this PC, for example en-US, pt-BR.</summary>
    public static IReadOnlyList<string> InstalledLanguages
        => SpeechSynthesizer.AllVoices.Select(v => v.Language).Distinct().OrderBy(l => l).ToList();

    /// <summary>Picks the best installed voice for a language code such as "pt" or "pt-BR". Returns false when none is installed.</summary>
    public bool SelectLanguage(string language)
    {
        var voices = SpeechSynthesizer.AllVoices;
        var voice = voices.FirstOrDefault(v => v.Language.Equals(language, StringComparison.OrdinalIgnoreCase))
                    ?? voices.FirstOrDefault(v => v.Language.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));
        if (voice is null) return false;
        _synth.Voice = voice;
        return true;
    }

    public string CurrentVoice => _synth.Voice.DisplayName;

    /// <summary>Speaks the text to mono float samples. Returns the samples and their sample rate.</summary>
    public async Task<(float[] Samples, int SampleRate)> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var stream = await _synth.SynthesizeTextToStreamAsync(text).AsTask(ct);
            var bytes = new byte[stream.Size];
            using (var input = stream.AsStreamForRead()) await input.ReadExactlyAsync(bytes, ct);

            using var reader = new WaveFileReader(new MemoryStream(bytes));
            var provider = reader.ToSampleProvider();
            var all = new List<float>();
            var buf = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
            int n;
            while ((n = provider.Read(buf, 0, buf.Length)) > 0) all.AddRange(buf.Take(n));
            var mono = AudioRoute.ToMono(all.ToArray(), reader.WaveFormat.Channels);
            return (mono, reader.WaveFormat.SampleRate);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _synth.Dispose();
        _gate.Dispose();
    }
}
