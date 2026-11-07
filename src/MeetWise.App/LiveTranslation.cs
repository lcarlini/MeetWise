using MeetWise.Audio;
using MeetWise.Core.Logging;
using MeetWise.Core.Settings;
using MeetWise.Intelligence;
using NAudio.CoreAudioApi;

namespace MeetWise.App;

/// <summary>
/// Live translation during a meeting, wired to the real world: other participants' sentences become captions in the user's language, and the user's
/// own sentences can be translated and spoken into a chosen output (the virtual microphone). All of the logic (segmentation, ordering, load shedding,
/// failures, feedback protection) lives in <see cref="LiveTranslationService"/>, which has no UI or audio dependencies and is tested on its own.
/// </summary>
public sealed class LiveTranslation : IDisposable
{
    private readonly LiveTranslationService _service;
    private readonly DeviceSpeechOutput? _output;
    private readonly WindowsVoiceSynthesizer? _voice;
    private DateTime _lastSkippedToast = DateTime.MinValue;
    private readonly Action<TranslationStatus> _status;

    public LiveTranslation(AppSettings settings, Func<Task<ILlmProvider>> llm, Func<Task<ILlmProvider?>> localFallback,
        Action<TranslatedCaption> caption, Action<TranslationStatus> status)
    {
        _status = status;
        var options = new LiveTranslationOptions(settings.CaptionTranslateTo, settings.SpeakTranslationTo, settings.SpokenLanguage,
            Hold: TimeSpan.FromMilliseconds(Math.Clamp(settings.TranslationHoldMs, 300, 5000)));

        // A cloud model that fails may be replaced by the local one, but only when it is already installed and only if the user allows it.
        // The local model never sends anything anywhere, so this can never move data off the PC.
        Func<CancellationToken, Task<ILlmProvider?>>? fallback = settings.TranslationFallbackToLocal && LlmFactory.SendsDataOffDevice(settings) ? _ => localFallback() : null;
        var translator = new LazyLlmTranslator(_ => llm(), new TranslatorOptions(Glossary: settings.Glossary), fallback);

        if (settings.SpeakTranslationTo is not null)
        {
            _voice = new WindowsVoiceSynthesizer();
            _output = new DeviceSpeechOutput(settings);
        }
        _service = new LiveTranslationService(translator, options, _voice, _output);
        _service.Caption += caption;
        _service.Status += OnStatus;
    }

    public bool IsEnabled => _service.IsEnabled;
    public LiveTranslationStats Stats => _service.Stats;

    public void OnFinal(LiveUtterance utterance) => _service.OnFinal(utterance);

    /// <summary>Silences the translated voice right now.</summary>
    public void StopSpeaking() => _service.StopSpeaking();

    private void OnStatus(TranslationStatus s)
    {
        // "behind" notices can come in bursts: at most one every 30 s. Everything else is a change of state and is shown.
        if (s.Kind == TranslationStatusKind.Skipped)
        {
            if (DateTime.UtcNow - _lastSkippedToast < TimeSpan.FromSeconds(30)) return;
            _lastSkippedToast = DateTime.UtcNow;
        }
        AppLog.Info("Live translation status: " + s.Kind); // the message can name a language, never the text
        _status(s);
    }

    public void Dispose()
    {
        _service.Dispose();
        _output?.Dispose();
        _voice?.Dispose();
    }
}

/// <summary>The Windows offline voices (the engine Narrator uses) behind <see cref="ISpeechSynthesizer"/>.</summary>
internal sealed class WindowsVoiceSynthesizer : ISpeechSynthesizer, IDisposable
{
    private readonly WindowsVoice _voice = new();
    public bool SelectLanguage(string language) => _voice.SelectLanguage(language);
    public Task<(float[] Samples, int SampleRate)> SynthesizeAsync(string text, CancellationToken ct) => _voice.SynthesizeAsync(text, ct);
    public void Dispose() => _voice.Dispose();
}

/// <summary>
/// Plays translated speech on the chosen output device. The device is opened on first use and re-opened when the sample rate changes. Before every sentence
/// the choice is checked again (the device may have been unplugged, or may be the speakers that MeetWise records as "Others"); a problem becomes a
/// <see cref="SpeechOutputException"/> that the service reports, never a crash.
/// </summary>
internal sealed class DeviceSpeechOutput : ISpeechOutput, IDisposable
{
    private readonly AppSettings _settings;
    private PcmPlayer? _player;
    private int _rate;

    public DeviceSpeechOutput(AppSettings settings) => _settings = settings;

    public double QueuedSeconds => _player?.QueuedSeconds ?? 0;

    public void Play(float[] samples, int sampleRate)
    {
        using var en = new MMDeviceEnumerator();
        var available = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d => d.ID).ToList();
        string? defaultId = null;
        try { defaultId = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch (Exception) { /* no default output at all */ }
        var (problem, message) = SpeechRouting.Check(_settings.SpeakDeviceId, available, defaultId);
        if (problem != SpeechRoutingProblem.None)
        {
            DisposePlayer(); // do not keep a stale device open
            throw new SpeechOutputException(message);
        }
        try
        {
            if (_player is null || _rate != sampleRate)
            {
                DisposePlayer();
                _player = new PcmPlayer(en.GetDevice(_settings.SpeakDeviceId!), sampleRate);
                _rate = sampleRate;
            }
            _player.Enqueue(samples);
        }
        catch (Exception ex)
        {
            DisposePlayer();
            throw new SpeechOutputException("The device could not be opened (" + ex.GetType().Name + ").", ex);
        }
    }

    public void Stop() => _player?.Clear();

    private void DisposePlayer()
    {
        var p = _player;
        _player = null;
        try { p?.Dispose(); } catch (Exception) { }
    }

    public void Dispose() => DisposePlayer();
}
