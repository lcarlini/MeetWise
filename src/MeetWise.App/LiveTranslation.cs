using MeetWise.Audio;
using MeetWise.Core.Logging;
using MeetWise.Core.Settings;
using MeetWise.Intelligence;
using MeetWise.Speech;
using NAudio.CoreAudioApi;

namespace MeetWise.App;

/// <summary>
/// Live translation during a meeting: other participants' sentences become captions in the user's language, and the user's
/// own sentences can be translated and spoken into a virtual microphone. Work is queued per direction so output keeps its order.
/// </summary>
public sealed class LiveTranslation : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Func<Task<ILlmProvider>> _llm;
    private readonly Action<string, string, string> _caption; // speaker, original, translation
    private readonly SemaphoreSlim _captionLock = new(1, 1);
    private readonly SemaphoreSlim _speakLock = new(1, 1);
    private LlmTranslator? _translator;
    private WindowsVoice? _voice;
    private PcmPlayer? _player;
    private int _playerRate;

    public LiveTranslation(AppSettings settings, Func<Task<ILlmProvider>> llm, Action<string, string, string> caption)
    {
        _settings = settings;
        _llm = llm;
        _caption = caption;
    }

    public bool IsEnabled => _settings.CaptionTranslateTo is not null || _settings.SpeakTranslationTo is not null;

    public void OnFinal(TranscriptEvent e)
    {
        if (e.Channel == "Others" && _settings.CaptionTranslateTo is { } target)
            _ = Task.Run(() => CaptionAsync(e.Text, target));
        else if (e.Channel == "Me" && _settings.SpeakTranslationTo is { } speak)
            _ = Task.Run(() => SpeakAsync(e.Text, speak));
    }

    private async Task<LlmTranslator> TranslatorAsync() => _translator ??= new LlmTranslator(await _llm());

    private async Task CaptionAsync(string text, string target)
    {
        await _captionLock.WaitAsync();
        try
        {
            var translated = await (await TranslatorAsync()).TranslateAsync(text, target);
            if (translated.Length > 0) _caption("Others", text, translated);
        }
        catch (Exception ex) { AppLog.Warn("Caption translation failed: " + ex.Message); }
        finally { _captionLock.Release(); }
    }

    private async Task SpeakAsync(string text, string target)
    {
        await _speakLock.WaitAsync();
        try
        {
            var spoken = target == "same" ? text : await (await TranslatorAsync()).TranslateAsync(text, target);
            _voice ??= new WindowsVoice();
            var lang = target == "same" ? (_settings.SpokenLanguage is "auto" ? "en" : _settings.SpokenLanguage) : target;
            if (!_voice.SelectLanguage(lang))
            {
                AppLog.Warn($"No Windows voice installed for '{lang}'. Add the language in Windows Settings, Time and language, Speech.");
                return;
            }
            var (samples, rate) = await _voice.SynthesizeAsync(spoken);
            if (_player is null || _playerRate != rate)
            {
                _player?.Dispose();
                using var en = new MMDeviceEnumerator();
                _player = new PcmPlayer(en.GetDevice(_settings.SpeakDeviceId!), rate);
                _playerRate = rate;
            }
            // Stale translated speech is worse than none: skip it when the output is already far behind.
            if (_player.QueuedSeconds < 10) _player.Enqueue(samples);
        }
        catch (Exception ex) { AppLog.Warn("Spoken translation failed: " + ex.Message); }
        finally { _speakLock.Release(); }
    }

    public void Dispose()
    {
        _player?.Dispose();
        _voice?.Dispose();
    }
}
