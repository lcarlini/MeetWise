using MeetWise.App.Try;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeetWise.Audio;
using MeetWise.Core;
using MeetWise.Core.Logging;
using MeetWise.Intelligence;
using MeetWise.Speech;
using Microsoft.Win32;

namespace MeetWise.App;

public sealed class ChatBubble : INotifyPropertyChanged
{
    private string _text = "";
    public bool IsAssistant { get; init; }
    public string Text { get => _text; set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); } }
    public string Name => IsAssistant ? Loc.T("ai.assistant") : Loc.T("ai.you");
    public Visibility ToolsVisibility => IsAssistant && Text.Length > 0 && !Text.StartsWith('…') ? Visibility.Visible : Visibility.Collapsed;
    public Brush Background => IsAssistant ? new SolidColorBrush(Color.FromArgb(22, 109, 140, 255)) : Brushes.Transparent;
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Send commands to the AI about a recording: summarize, chapters, translate, or any question. Works on the selected lines, or on everything.</summary>
public partial class AssistantPage : UserControl
{
    private readonly StudioContext _ctx;
    private readonly ObservableCollection<ChatBubble> _bubbles = new();
    private CancellationTokenSource? _cts;
    private long _loadedFor = -1;

    public AssistantPage(StudioContext ctx)
    {
        InitializeComponent();
        _ctx = ctx;
        AiTest.Setup(new QuickSpec
        {
            TitleKey = "quick.ai.title", HintKey = "quick.ai.hint", RunKey = "quick.ai.run",
            Run = i => QuickRunners.AssistantSample(ctx, i)
        });
        Chat.ItemsSource = _bubbles;
        _bubbles.CollectionChanged += (_, _) => EmptyText.Visibility = _bubbles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Input.TextChanged += (_, _) => Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        FillLanguages();
        Loc.Instance.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Loc.Language)) { FillLanguages(); Refresh(); } };
        _ctx.ItemOpened += _ => Dispatcher.BeginInvoke(Refresh);
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Refresh(); };
    }

    private void FillLanguages()
    {
        var keep = TranslateCombo.SelectedValue as string ?? (Loc.Instance.Language == "en" ? "pt" : "en");
        TranslateCombo.ItemsSource = Languages.All.Select(l => new { Value = l.Code, Text = l.NameIn(Loc.Instance.Language) }).ToList();
        TranslateCombo.SelectedValue = keep;
    }

    /// <summary>Reloads the conversation and the "working on…" line for the current recording.</summary>
    private void Refresh()
    {
        var id = _ctx.CurrentItemId;
        var meeting = id == 0 ? null : _ctx.Db.GetMeeting(id);
        ItemText.Text = meeting?.Title ?? "";
        if (id != _loadedFor)
        {
            _loadedFor = id;
            _bubbles.Clear();
            if (meeting is not null)
                foreach (var m in _ctx.Db.GetAiMessages(id)) _bubbles.Add(new ChatBubble { IsAssistant = m.Role == "assistant", Text = m.Content });
            ScrollToEnd();
        }
        int total = meeting is null ? 0 : _ctx.Db.GetTranscript(id).Count;
        ContextText.Text = _ctx.SelectedCues.Count > 0 ? Loc.T("ai.context.sel", _ctx.SelectedCues.Count) : Loc.T("ai.context.all", total);
    }

    private (IReadOnlyList<AssistantLine> Lines, bool Selection) Context()
    {
        if (_ctx.SelectedCues.Count > 0)
            return (_ctx.SelectedCues.Select(c => new AssistantLine(c.Start, c.Speaker, c.Text)).ToList(), true);
        return (_ctx.Db.GetTranscript(_ctx.CurrentItemId).Select(s => new AssistantLine(s.StartSec, s.Speaker, s.Text)).ToList(), false);
    }

    // ---- sending ----

    private async void Command_Click(object sender, RoutedEventArgs e)
    {
        var cmd = Enum.Parse<AssistantCommand>((string)((Button)sender).Tag);
        await RunAsync(new AssistantRequest(cmd), ((Button)sender).Content?.ToString() ?? cmd.ToString());
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        var code = TranslateCombo.SelectedValue as string ?? "en";
        await RunAsync(new AssistantRequest(AssistantCommand.Translate, Language: code), $"{Loc.T("ai.cmd.translate")} {Languages.Name(code, Loc.Instance.Language)}");
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendTextAsync();

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; _ = SendTextAsync(); }
    }

    private async Task SendTextAsync()
    {
        var text = Input.Text.Trim();
        if (text.Length == 0) return;
        Input.Clear();
        await RunAsync(new AssistantRequest(AssistantCommand.Custom, text), text);
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async Task RunAsync(AssistantRequest request, string shownAsUser)
    {
        if (_ctx.CurrentItemId == 0) return;
        if (_cts is not null) return; // one question at a time
        var itemId = _ctx.CurrentItemId;
        var (lines, selection) = Context();
        var history = _bubbles.Where(b => b.Text.Length > 0 && !b.Text.StartsWith('…'))
            .TakeLast(10).Select(b => new ChatMessage(b.IsAssistant ? "assistant" : "user", b.Text)).ToList();

        _bubbles.Add(new ChatBubble { Text = shownAsUser });
        _ctx.Db.AddAiMessage(itemId, "user", shownAsUser);
        var pending = new ChatBubble { IsAssistant = true, Text = "… " + Loc.T("ai.thinking") };
        _bubbles.Add(pending);
        ScrollToEnd();
        _cts = new CancellationTokenSource();
        SendButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        try
        {
            var status = new Progress<string>(t => pending.Text = "… " + t);
            var llm = await _ctx.GetLlmAsync(status);
            pending.Text = "… " + Loc.T("ai.thinking");
            var answer = await new TranscriptAssistant(llm).RunAsync(lines, request, selection, history, _ctx.Profile, _cts.Token);
            pending.Text = answer;
            _ctx.Db.AddAiMessage(itemId, "assistant", answer);
            ContextText.Text += "   ·   " + Loc.T("ai.engine", llm.Name);
        }
        catch (OperationCanceledException) { _bubbles.Remove(pending); }
        catch (Exception ex)
        {
            AppLog.Error("Assistant failed", ex);
            pending.Text = ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SendButton.Visibility = Visibility.Visible;
            StopButton.Visibility = Visibility.Collapsed;
            Chat.Items.Refresh();
            ScrollToEnd();
        }
    }

    private void ScrollToEnd()
    {
        if (_bubbles.Count > 0) Chat.ScrollIntoView(_bubbles[^1]);
    }

    // ---- replies ----

    private void CopyReply_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ChatBubble b) Clipboard.SetText(b.Text);
    }

    private void SaveReply_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ChatBubble b) return;
        var meeting = _ctx.Db.GetMeeting(_ctx.CurrentItemId);
        var folder = new[] { meeting?.VideoPath, meeting?.AudioPath }.Select(p => p is null ? null : Path.GetDirectoryName(p)).FirstOrDefault(d => d is not null && Directory.Exists(d))
                     ?? _ctx.Settings.OutputFolder;
        var dlg = new SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
            FileName = $"{YouTubeImporter.SafeName(meeting?.Title ?? "assistant")}.ai-{DateTime.Now:yyyyMMdd-HHmm}.md",
            InitialDirectory = Directory.Exists(folder) ? folder : null
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, b.Text, new System.Text.UTF8Encoding(false));
        _ctx.ShowToast(Loc.T("ai.saved", dlg.FileName));
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_ctx.CurrentItemId == 0) return;
        _ctx.Db.ClearAiMessages(_ctx.CurrentItemId);
        _bubbles.Clear();
    }
}
