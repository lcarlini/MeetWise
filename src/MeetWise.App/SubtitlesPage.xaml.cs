using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MeetWise.Audio;
using MeetWise.Core.Logging;
using MeetWise.Core.Models;
using MeetWise.Speech;
using MeetWise.Storage;
using Microsoft.Win32;

namespace MeetWise.App;

/// <summary>After recording: pick lines, edit them, then copy or save them as SRT, VTT or TXT, or send them to the AI.</summary>
public partial class SubtitlesPage : UserControl
{
    private sealed record ItemChoice(Meeting Meeting)
    {
        public string Display => $"{Meeting.Title}   ·   {Meeting.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}   ·   {Loc.T("lib.kind." + Meeting.Kind)}";
    }

    private readonly StudioContext _ctx;
    private readonly ObservableCollection<SubtitleRow> _rows = new();
    private readonly ICollectionView _view;
    private Meeting? _meeting;
    private int _lastClicked = -1;
    private bool _loading;

    public SubtitlesPage(StudioContext ctx)
    {
        InitializeComponent();
        _ctx = ctx;
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = o => FilterBox.Text.Length == 0 || ((SubtitleRow)o).Text.Contains(FilterBox.Text, StringComparison.CurrentCultureIgnoreCase);
        Grid.ItemsSource = _view;
        _rows.CollectionChanged += (_, _) => EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _ctx.ItemOpened += id => Dispatcher.BeginInvoke(() => Select(id));
        _ctx.ItemsChanged += () => Dispatcher.BeginInvoke(ReloadItems);
        ReloadItems();
        Loc.Instance.PropertyChanged += (_, _) => UpdateCount();
        UpdateCount();
    }

    // ---- items ----

    private void ReloadItems()
    {
        var keep = _meeting?.Id ?? _ctx.CurrentItemId;
        _loading = true;
        var items = _ctx.Db.ListMeetings(limit: 300).Select(m => new ItemChoice(m)).ToList();
        ItemCombo.ItemsSource = items;
        ItemCombo.SelectedItem = items.FirstOrDefault(i => i.Meeting.Id == keep) ?? items.FirstOrDefault();
        _loading = false;
        LoadSelected();
    }

    private void Select(long id)
    {
        if (ItemCombo.ItemsSource is not List<ItemChoice> items || items.All(i => i.Meeting.Id != id)) ReloadItems();
        _loading = true;
        ItemCombo.SelectedItem = ((List<ItemChoice>)ItemCombo.ItemsSource).FirstOrDefault(i => i.Meeting.Id == id);
        _loading = false;
        LoadSelected();
    }

    private void Item_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) LoadSelected();
    }

    private void LoadSelected()
    {
        _meeting = (ItemCombo.SelectedItem as ItemChoice)?.Meeting;
        _rows.Clear();
        _lastClicked = -1;
        if (_meeting is not null)
        {
            _ctx.SetCurrent(_meeting.Id);
            foreach (var s in _ctx.Db.GetTranscript(_meeting.Id))
                _rows.Add(new SubtitleRow { SegmentId = s.Id, Start = s.StartSec, End = s.EndSec, Speaker = s.Speaker, Text = s.Text, Language = s.Language });
        }
        OpenVideoButton.IsEnabled = _meeting is { } m && (File.Exists(m.VideoPath ?? "") || File.Exists(m.AudioPath ?? ""));
        RefineButton.IsEnabled = _meeting?.AudioPath is { } a && a.EndsWith(".mic.wav", StringComparison.OrdinalIgnoreCase) && File.Exists(a);
        PublishSelection();
    }

    // ---- selection ----

    private IEnumerable<SubtitleRow> Visible => _view.Cast<SubtitleRow>();

    private void RowCheck_Click(object sender, RoutedEventArgs e)
    {
        var row = (SubtitleRow)((FrameworkElement)sender).DataContext;
        var visible = Visible.ToList();
        int index = visible.IndexOf(row);
        bool check = ((CheckBox)sender).IsChecked == true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _lastClicked >= 0 && _lastClicked < visible.Count)
            foreach (var r in visible.Skip(Math.Min(index, _lastClicked)).Take(Math.Abs(index - _lastClicked) + 1)) r.IsSelected = check;
        else row.IsSelected = check;
        _lastClicked = index;
        Grid.Items.Refresh();
        PublishSelection();
    }

    private void SelectAll_Click(object s, RoutedEventArgs e) { foreach (var r in Visible) r.IsSelected = true; AfterBulk(); }
    private void SelectNone_Click(object s, RoutedEventArgs e) { foreach (var r in _rows) r.IsSelected = false; AfterBulk(); }
    private void Invert_Click(object s, RoutedEventArgs e) { foreach (var r in Visible) r.IsSelected = !r.IsSelected; AfterBulk(); }

    private void AfterBulk()
    {
        Grid.Items.Refresh();
        PublishSelection();
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) { _view.Refresh(); UpdateCount(); }

    private IReadOnlyList<SubtitleRow> Chosen() => _rows.Where(r => r.IsSelected).ToList();

    /// <summary>The selected lines, or every line when nothing is selected.</summary>
    private IReadOnlyList<SubtitleCue> CuesToExport()
    {
        var chosen = Chosen();
        return (chosen.Count > 0 ? chosen : _rows).Select(r => r.ToCue()).ToList();
    }

    private void PublishSelection()
    {
        _ctx.SelectedCues = Chosen().Select(r => r.ToCue()).ToList();
        UpdateCount();
    }

    private void UpdateCount() => CountText.Text = Loc.T("sub.selected", _rows.Count(r => r.IsSelected), _rows.Count);

    // ---- copy and save ----

    private double Offset => _meeting?.VideoPath is not null ? _meeting.SubtitleOffset : 0;

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        var cues = CuesToExport();
        bool speakers = cues.Select(c => c.Speaker).Distinct().Count() > 1;
        Clipboard.SetText(SubtitleFormatter.ToText(cues, timestamps: false, speakers: speakers));
        StatusText.Text = Loc.T("sub.copied", cues.Count);
    }

    private void CopySrt_Click(object sender, RoutedEventArgs e)
    {
        var cues = CuesToExport();
        Clipboard.SetText(SubtitleFormatter.ToSrt(cues, Offset));
        StatusText.Text = Loc.T("sub.copied", cues.Count);
    }

    private void SaveSrt_Click(object sender, RoutedEventArgs e) => Save("srt", "SubRip subtitles", c => SubtitleFormatter.ToSrt(c, Offset));
    private void SaveVtt_Click(object sender, RoutedEventArgs e) => Save("vtt", "WebVTT subtitles", c => SubtitleFormatter.ToVtt(c, Offset));
    private void SaveTxt_Click(object sender, RoutedEventArgs e) => Save("txt", "Text", c => SubtitleFormatter.ToText(c, timestamps: false, speakers: c.Select(x => x.Speaker).Distinct().Count() > 1));

    private void Save(string ext, string label, Func<IReadOnlyList<SubtitleCue>, string> render)
    {
        var cues = CuesToExport();
        if (cues.Count == 0) return;
        var dlg = new SaveFileDialog
        {
            Filter = $"{label} (*.{ext})|*.{ext}",
            FileName = YouTubeImporter.SafeName(_meeting?.Title ?? "subtitles") + "." + ext,
            InitialDirectory = Directory.Exists(_ctx.Settings.OutputFolder) ? _ctx.Settings.OutputFolder : null
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, render(cues), new System.Text.UTF8Encoding(false)); // no byte-order mark: some players reject it in SRT
        StatusText.Text = Loc.T("sub.saved", dlg.FileName);
    }

    // ---- edit and delete ----

    private void Grid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not SubtitleRow row) return;
        // the binding writes the new value after this event, so persist once it has landed
        Dispatcher.BeginInvoke(() =>
        {
            _ctx.Db.UpdateSegmentText(row.SegmentId, row.Text);
            _ctx.Db.SetSegmentSpeaker(row.SegmentId, row.Speaker);
            PublishSelection();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Grid.CurrentItem is not SubtitleRow row || Keyboard.FocusedElement is TextBox) return;
        _ctx.Db.DeleteSegment(row.SegmentId);
        _rows.Remove(row);
        PublishSelection();
        e.Handled = true;
    }

    // ---- other actions ----

    private void Ask_Click(object sender, RoutedEventArgs e)
    {
        PublishSelection();
        _ctx.Navigate("assistant");
    }

    private void OpenVideo_Click(object sender, RoutedEventArgs e)
    {
        var path = File.Exists(_meeting?.VideoPath ?? "") ? _meeting!.VideoPath : _meeting?.AudioPath;
        if (path is not null && File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Re-transcribes the recording's audio with Whisper (microphone and system audio separately) for the best accuracy.</summary>
    private async void Refine_Click(object sender, RoutedEventArgs e)
    {
        if (_meeting?.AudioPath is not { } mic) return;
        RefineButton.IsEnabled = false;
        try
        {
            var loop = mic.EndsWith(".mic.wav", StringComparison.OrdinalIgnoreCase) ? mic[..^8] + ".loop.wav" : null;
            var status = new Progress<string>(t => StatusText.Text = t);
            await _ctx.OfferGpuPackAsync(status);
            _ctx.Llm.Release(); // free VRAM for Whisper
            var manager = new ModelManager(_ctx.Http);
            var model = ModelCatalog.WhisperFor(_ctx.Hardware);
            await manager.EnsureAsync(model, new Progress<double>(p => StatusText.Text = $"Downloading {model.Id}… {p:P0}"));
            CudaRuntimePack.Activate();

            var progress = new Progress<double>(p => StatusText.Text = $"{Loc.T("sub.refining")} {p:P0}");
            var meetingId = _meeting.Id;
            int count = await Task.Run(async () =>
            {
                using var whisper = new WhisperTranscriber(manager.PathFor(model));
                return await MeetingRefiner.RefineAsync(_ctx.Db, meetingId, mic, loop, whisper, _ctx.Settings.SubtitleLanguage, _ctx.Settings.Glossary, progress);
            });
            StatusText.Text = $"{count} · {model.Id}";
            LoadSelected();
        }
        catch (Exception ex)
        {
            AppLog.Error("Refine failed", ex);
            StatusText.Text = ex.Message;
        }
        finally { RefineButton.IsEnabled = _meeting?.AudioPath?.EndsWith(".mic.wav", StringComparison.OrdinalIgnoreCase) == true; }
    }
}
