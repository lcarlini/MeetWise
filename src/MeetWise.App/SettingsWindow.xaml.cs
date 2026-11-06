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
        ProviderCombo_Changed(null, null!);
    }

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

        _settings.CloudProvider = provider.Length == 0 ? null : provider;
        _settings.LlmBaseUrl = UrlBox.Text.Trim().Length == 0 ? null : UrlBox.Text.Trim();
        _settings.LlmModel = ModelBox.Text.Trim().Length == 0 ? null : ModelBox.Text.Trim();
        if (KeyBox.Password.Length > 0 && provider.Length > 0)
            _secrets.Set(provider == "anthropic" ? LlmFactory.AnthropicKeyName : LlmFactory.OpenAiKeyName, KeyBox.Password);
        _settings.TemplateId = TemplateCombo.SelectedValue as string ?? "general";
        _settings.UserNickname = NickBox.Text.Trim().Length == 0 ? null : NickBox.Text.Trim();
        _settings.UserOccupation = OccBox.Text.Trim().Length == 0 ? null : OccBox.Text.Trim();
        _settings.UserContext = CtxBox.Text.Trim().Length == 0 ? null : CtxBox.Text.Trim();
        _settings.Glossary = GlossaryBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _settings.WebhookUrl = WebhookBox.Text.Trim().Length == 0 ? null : WebhookBox.Text.Trim();
        DialogResult = true;
    }
}
