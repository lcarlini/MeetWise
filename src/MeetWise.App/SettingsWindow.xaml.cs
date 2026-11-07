using System.Windows;
using System.Windows.Controls;
using MeetWise.Core.Settings;
using MeetWise.Intelligence;

namespace MeetWise.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SecretStore _secrets;

    public SettingsWindow(AppSettings settings, SecretStore secrets)
    {
        InitializeComponent();
        _settings = settings;
        _secrets = secrets;

        var captionLangs = new Dictionary<string, string> { [""] = "Off" };
        foreach (var kv in Languages.Where(k => k.Key != "auto")) captionLangs[kv.Key] = kv.Value;
        var speakLangs = new Dictionary<string, string>(captionLangs) { ["same"] = "Same language (re-voice in a neutral accent)" };
        CaptionLangCombo.ItemsSource = captionLangs;
        CaptionLangCombo.SelectedValue = settings.CaptionTranslateTo ?? "";
        SpeakLangCombo.ItemsSource = speakLangs;
        SpeakLangCombo.SelectedValue = settings.SpeakTranslationTo ?? "";
        SpeakDeviceCombo.ItemsSource = new MeetWise.Audio.AudioDeviceService().GetRenderDevices()
            .OrderByDescending(d => MeetWise.Audio.AudioDeviceService.LooksVirtual(d.Name)).ToList();
        SpeakDeviceCombo.SelectedValue = settings.SpeakDeviceId;
        LangCombo.ItemsSource = Languages;
        LangCombo.SelectedValue = settings.SpokenLanguage;
        if (LangCombo.SelectedValue is null) LangCombo.SelectedValue = "auto";
        TemplateCombo.ItemsSource = MeetingTemplates.All;
        TemplateCombo.SelectedValue = settings.TemplateId;
        ProviderCombo.SelectedIndex = settings.CloudProvider switch { "anthropic" => 1, "openai-compatible" => 2, _ => 0 };
        UrlBox.Text = settings.LlmBaseUrl ?? "";
        ModelBox.Text = settings.LlmModel ?? "";
        NickBox.Text = settings.UserNickname ?? "";
        OccBox.Text = settings.UserOccupation ?? "";
        CtxBox.Text = settings.UserContext ?? "";
        GlossaryBox.Text = string.Join(Environment.NewLine, settings.Glossary);
        WebhookBox.Text = settings.WebhookUrl ?? "";
        HideCheck.IsChecked = settings.HideWindowFromCapture;
        CalendarBox.Text = settings.CalendarUrl ?? "";
        DetectCheck.IsChecked = settings.AutoDetectMeetings;
        ProviderCombo_Changed(null, null!);
    }

    public static readonly Dictionary<string, string> Languages = new()
    {
        ["auto"] = "Detect automatically", ["en"] = "English", ["pt"] = "Portuguese", ["es"] = "Spanish", ["fr"] = "French",
        ["de"] = "German", ["it"] = "Italian", ["nl"] = "Dutch", ["pl"] = "Polish", ["hi"] = "Hindi", ["ko"] = "Korean",
        ["ja"] = "Japanese", ["zh"] = "Chinese", ["ru"] = "Russian", ["tr"] = "Turkish", ["ar"] = "Arabic"
    };

    private string SelectedProvider => (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void ProviderCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (CloudPanel is null) return;
        CloudPanel.Visibility = SelectedProvider.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UrlBox.IsEnabled = SelectedProvider == "openai-compatible";
    }

    private void Kit_Click(object sender, RoutedEventArgs e)
    {
        var kit = Glossary.StarterKits[(string)((Button)sender).Tag];
        var current = GlossaryBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        GlossaryBox.Text = string.Join(Environment.NewLine, current.Concat(kit).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var provider = SelectedProvider;
        if (provider == "openai-compatible" && (!Uri.TryCreate(UrlBox.Text, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")))
        {
            MessageBox.Show("Enter a valid http(s) server URL.", "MeetWise");
            return;
        }
        if (WebhookBox.Text.Length > 0 && (!Uri.TryCreate(WebhookBox.Text, UriKind.Absolute, out var w) || w.Scheme is not ("http" or "https")))
        {
            MessageBox.Show("The webhook must be a valid http(s) URL.", "MeetWise");
            return;
        }

        if (CalendarBox.Text.Trim().Length > 0 && !(Uri.TryCreate(CalendarBox.Text.Trim(), UriKind.Absolute, out var c) && c.Scheme is "http" or "https" or "webcal"))
        {
            MessageBox.Show("The calendar address must start with https:// or webcal://.", "MeetWise");
            return;
        }

        _settings.CloudProvider = provider.Length == 0 ? null : provider;
        _settings.LlmBaseUrl = UrlBox.Text.Trim().Length == 0 ? null : UrlBox.Text.Trim();
        _settings.LlmModel = ModelBox.Text.Trim().Length == 0 ? null : ModelBox.Text.Trim();
        if (KeyBox.Password.Length > 0 && provider.Length > 0)
            _secrets.Set(provider == "anthropic" ? LlmFactory.AnthropicKeyName : LlmFactory.OpenAiKeyName, KeyBox.Password);
        _settings.SpokenLanguage = LangCombo.SelectedValue as string ?? "auto";
        _settings.CaptionTranslateTo = (CaptionLangCombo.SelectedValue as string) is { Length: > 0 } cl ? cl : null;
        _settings.SpeakTranslationTo = (SpeakLangCombo.SelectedValue as string) is { Length: > 0 } sl ? sl : null;
        _settings.SpeakDeviceId = SpeakDeviceCombo.SelectedValue as string;
        if (_settings.SpeakTranslationTo is not null && _settings.SpeakDeviceId is null)
        {
            MessageBox.Show("Choose the output device for the spoken translation.", "MeetWise");
            return;
        }
        _settings.TemplateId = TemplateCombo.SelectedValue as string ?? "general";
        _settings.UserNickname = NickBox.Text.Trim().Length == 0 ? null : NickBox.Text.Trim();
        _settings.UserOccupation = OccBox.Text.Trim().Length == 0 ? null : OccBox.Text.Trim();
        _settings.UserContext = CtxBox.Text.Trim().Length == 0 ? null : CtxBox.Text.Trim();
        _settings.Glossary = GlossaryBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _settings.HideWindowFromCapture = HideCheck.IsChecked == true;
        _settings.CalendarUrl = CalendarBox.Text.Trim().Length == 0 ? null : CalendarBox.Text.Trim();
        _settings.AutoDetectMeetings = DetectCheck.IsChecked == true;
        _settings.WebhookUrl = WebhookBox.Text.Trim().Length == 0 ? null : WebhookBox.Text.Trim();
        DialogResult = true;
    }
}
