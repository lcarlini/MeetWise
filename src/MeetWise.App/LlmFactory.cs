using System.Net.Http;
using MeetWise.Core.Hardware;
using MeetWise.Core.Models;
using MeetWise.Core.Settings;
using MeetWise.Intelligence;

namespace MeetWise.App;

/// <summary>Builds the configured LLM backend. Default is fully local; a cloud key is an optional upgrade.</summary>
public sealed class LlmFactory
{
    public const string AnthropicKeyName = "anthropic";
    public const string OpenAiKeyName = "openai-compatible";

    private readonly HttpClient _http;
    private readonly SecretStore _secrets;
    private ILlmProvider? _cached;
    private string? _cachedKey;

    public LlmFactory(HttpClient http, SecretStore secrets)
    {
        _http = http;
        _secrets = secrets;
    }

    /// <summary>Frees the model (and its VRAM). The 8B model and Whisper cannot both fit on an 8 GB card, so heavy steps run one after another.</summary>
    public void Release()
    {
        (_cached as IDisposable)?.Dispose();
        _cached = null;
        _cachedKey = null;
        _localCached?.Dispose();
        _localCached = null;
    }

    /// <summary>
    /// True when using these settings would send transcript text to a server outside this PC: the Anthropic API, or an OpenAI-compatible endpoint that is not
    /// on this machine. The local model and a server on localhost (Ollama, LM Studio) keep everything on the PC.
    /// </summary>
    public static bool SendsDataOffDevice(AppSettings s)
    {
        if (s.CloudProvider == "anthropic") return true;
        if (s.CloudProvider != "openai-compatible") return false;
        if (!Uri.TryCreate(s.LlmBaseUrl, UriKind.Absolute, out var uri)) return true; // unknown destination: assume the worst
        return !(uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }

    private LocalLlamaProvider? _localCached;

    /// <summary>The local model, only when it is already on disk (a call must never start a multi-gigabyte download). Used as the fallback when a cloud model fails.</summary>
    public async Task<ILlmProvider?> TryGetInstalledLocalAsync(HardwareProfile hw)
    {
        if (_cached is LocalLlamaProvider alreadyLocal) return alreadyLocal; // never load the same model twice: 8 GB of VRAM fit one
        if (_localCached is not null) return _localCached;
        var (model, layers) = ModelCatalog.LlmFor(hw);
        var manager = new ModelManager(_http);
        if (!manager.IsInstalled(model)) return null;
        _localCached = await Task.Run(() => new LocalLlamaProvider(manager.PathFor(model), 8192, layers));
        return _localCached;
    }

    public async Task<ILlmProvider> GetAsync(AppSettings s, HardwareProfile hw, IProgress<double>? progress = null)
    {
        var key = $"{s.CloudProvider}|{s.LlmBaseUrl}|{s.LlmModel}";
        if (_cached is not null && key == _cachedKey) return _cached;
        (_cached as IDisposable)?.Dispose();

        switch (s.CloudProvider)
        {
            case "anthropic":
                var anthropicKey = _secrets.Get(AnthropicKeyName) ?? throw new InvalidOperationException("Add your Anthropic API key in Settings, or switch to the local model.");
                _cached = new AnthropicProvider(_http, anthropicKey, string.IsNullOrWhiteSpace(s.LlmModel) ? "claude-sonnet-5-5" : s.LlmModel);
                break;
            case "openai-compatible":
                if (string.IsNullOrWhiteSpace(s.LlmBaseUrl) || string.IsNullOrWhiteSpace(s.LlmModel))
                    throw new InvalidOperationException("Set the server URL and model name in Settings.");
                _cached = new OpenAiCompatibleProvider(_http, s.LlmBaseUrl, s.LlmModel, _secrets.Get(OpenAiKeyName));
                break;
            default:
                var (model, layers) = ModelCatalog.LlmFor(hw);
                var manager = new ModelManager(_http);
                await manager.EnsureAsync(model, progress);
                _cached = await Task.Run(() => new LocalLlamaProvider(manager.PathFor(model), 8192, layers));
                break;
        }
        _cachedKey = key;
        return _cached;
    }
}
