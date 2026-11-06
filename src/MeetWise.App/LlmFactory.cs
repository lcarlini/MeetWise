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
