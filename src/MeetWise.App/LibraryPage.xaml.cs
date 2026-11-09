using MeetWise.App.Try;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MeetWise.Audio;
using MeetWise.Core;
using MeetWise.Core.Logging;
using MeetWise.Storage;

namespace MeetWise.App;

public sealed class LibraryRow
{
    public required Meeting Meeting { get; init; }
    public string? Snippet { get; init; }
    public string Icon => Meeting.Kind switch { "recording" => "", "youtube" => "", "import" => "", _ => "" }; // camera, play, file, people
    public string Title => Snippet is null ? Meeting.Title : $"{Meeting.Title}\n“{Snippet}”";
    public string KindText => Loc.T("lib.kind." + Meeting.Kind);
    public string Date => Meeting.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

/// <summary>One import job as the list shows it. Updates arrive from the worker thread and are marshalled here.</summary>
public sealed class JobRow : INotifyPropertyChanged
{
    private readonly ImportJob _job;
    private readonly System.Windows.Threading.Dispatcher _ui;

    public JobRow(ImportJob job, System.Windows.Threading.Dispatcher ui)
    {
        _job = job;
        _ui = ui;
        job.PropertyChanged += (_, _) => _ui.BeginInvoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)));
        Loc.Instance.PropertyChanged += (_, _) => _ui.BeginInvoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)));
    }

    public string Name => _job.Name;
    public double Progress => _job.Progress;
    public string Message => _job.Message;
    public string StatusText => Loc.T(_job.Status switch { JobStatus.Queued => "lib.queued", JobStatus.Working => "lib.working", JobStatus.Done => "lib.done", _ => "lib.failed" });
    public Visibility BarVisibility => _job.Status == JobStatus.Working ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MessageVisibility => _job.Message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Everything recorded or imported, search across all transcripts, and the ways to bring media in: files, YouTube links, a watched folder.</summary>
public partial class LibraryPage : UserControl
{
    private sealed record Choice(string Value, string Text) { public override string ToString() => Text; }

    private readonly StudioContext _ctx;
    private readonly ObservableCollection<LibraryRow> _rows = new();
    private readonly ObservableCollection<JobRow> _jobs = new();
    private WatchFolder? _watch;

    public LibraryPage(StudioContext ctx)
    {
        InitializeComponent();
        _ctx = ctx;
        ImportTest.Setup(new QuickSpec
        {
            TitleKey = "quick.imp.title", HintKey = "quick.imp.hint", AllowFile = true,
            Run = i => QuickRunners.ImportPreview(_ctx, i, ImportNoise.IsChecked == true, ImportLanguage.SelectedValue as string ?? Languages.Auto, ImportTranslate.IsChecked == true)
        });
        ItemGrid.ItemsSource = _rows;
        Jobs.ItemsSource = _jobs;
        _rows.CollectionChanged += (_, _) => EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.TextChanged += (_, _) => SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        YouTubeBox.TextChanged += (_, _) => YouTubeHint.Visibility = YouTubeBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        FillLanguages();
        Loc.Instance.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Loc.Language)) { FillLanguages(); Reload(); } };
        _ctx.ItemsChanged += () => Dispatcher.BeginInvoke(Reload);
        _ctx.Imports.JobAdded += j => Dispatcher.BeginInvoke(() => _jobs.Insert(0, new JobRow(j, Dispatcher)));
        _ctx.Imports.ItemAdded += _ => Dispatcher.BeginInvoke(Reload);
        foreach (var j in _ctx.Imports.Jobs) _jobs.Insert(0, new JobRow(j, Dispatcher));

        var s = _ctx.Settings;
        ImportNoise.IsChecked = s.ImportNoiseFilter;
        WatchPath.Text = s.WatchFolder;
        WatchHelp.Text = "To Process  →  Output / Processed / Errors";
        WatchCheck.IsChecked = s.WatchFolderEnabled;
        if (s.WatchFolderEnabled) StartWatching();
        Reload();
    }

    private void FillLanguages()
    {
        var keep = ImportLanguage.SelectedValue as string ?? _ctx.Settings.SubtitleLanguage;
        ImportLanguage.ItemsSource = new[] { Languages.Auto }.Concat(Languages.All.Select(l => l.Code))
            .Select(c => new Choice(c, Languages.Name(c, Loc.Instance.Language))).ToList();
        ImportLanguage.SelectedValue = Languages.IsKnown(keep) ? keep : Languages.Auto;
    }

    // ---- list and search ----

    private void Reload()
    {
        var q = SearchBox.Text.Trim();
        _rows.Clear();
        if (q.Length == 0)
        {
            foreach (var m in _ctx.Db.ListMeetings(limit: 500)) _rows.Add(new LibraryRow { Meeting = m });
            return;
        }
        var seen = new HashSet<long>();
        foreach (var hit in _ctx.Db.Search(q, 200))
            if (seen.Add(hit.MeetingId) && _ctx.Db.GetMeeting(hit.MeetingId) is { } m)
                _rows.Add(new LibraryRow { Meeting = m, Snippet = hit.Text.Length > 120 ? hit.Text[..120] + "…" : hit.Text });
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => Reload();

    private LibraryRow? Selected => ItemGrid.SelectedItem as LibraryRow;

    private void Item_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenSubtitles_Click(sender, e);

    private void OpenSubtitles_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } r) _ctx.OpenItem(r.Meeting.Id, r.Meeting.Kind == "meeting" ? "subtitles" : "subtitles");
    }

    private void OpenAssistant_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } r) _ctx.OpenItem(r.Meeting.Id, "assistant");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r) return;
        var path = new[] { r.Meeting.VideoPath, r.Meeting.AudioPath, r.Meeting.Source }.FirstOrDefault(p => p is not null && File.Exists(p));
        if (path is not null) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(_ctx.Settings.OutputFolder)) Process.Start("explorer.exe", $"\"{_ctx.Settings.OutputFolder}\"");
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r) return;
        var name = PromptWindow.Ask(Window.GetWindow(this), Loc.T("lib.rename"), r.Meeting.Title);
        if (string.IsNullOrWhiteSpace(name)) return;
        _ctx.Db.SetTitle(r.Meeting.Id, name.Trim());
        _ctx.NotifyItemsChanged();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r) return;
        if (MessageBox.Show(Loc.T("lib.confirmdelete", r.Meeting.Title), "MeetWise", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _ctx.Db.DeleteMeeting(r.Meeting.Id);
        _ctx.NotifyItemsChanged();
    }

    // ---- importing ----

    private ImportOptions Options() => new(
        Language: ImportLanguage.SelectedValue as string ?? Languages.Auto,
        TranslateToEnglish: ImportTranslate.IsChecked == true,
        NoiseFilter: ImportNoise.IsChecked == true,
        WriteSrt: ImportSrt.IsChecked == true,
        Vocabulary: _ctx.Settings.Glossary);

    private string OutputFolder => Path.Combine(_ctx.Settings.OutputFolder, "Subtitles");

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "Audio and video|" + string.Join(";", WatchFolder.MediaExtensions.Select(x => "*" + x)) + "|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        _ctx.Settings.ImportNoiseFilter = ImportNoise.IsChecked == true;
        _ctx.Save();
        await PrepareAsync();
        var options = Options();
        foreach (var file in dlg.FileNames) _ctx.Imports.EnqueueFile(file, options, OutputFolder);
    }

    private async void ImportYouTube_Click(object sender, RoutedEventArgs e)
    {
        var url = YouTubeBox.Text.Trim();
        if (YouTubeUrl.ExtractId(url) is null) { MessageBox.Show("That is not a YouTube video link.", "MeetWise"); return; }
        await PrepareAsync();
        _ctx.Imports.EnqueueYouTube(url, Options(), OutputFolder);
        YouTubeBox.Clear();
    }

    /// <summary>Whisper runs on the GPU for imports: offer the acceleration pack once, and free the language model's memory first.</summary>
    private async Task PrepareAsync()
    {
        await _ctx.OfferGpuPackAsync();
        _ctx.Llm.Release();
    }

    // ---- watched folder ----

    private void Watch_Click(object sender, RoutedEventArgs e)
    {
        _ctx.Settings.WatchFolderEnabled = WatchCheck.IsChecked == true;
        _ctx.Save();
        if (_ctx.Settings.WatchFolderEnabled) StartWatching(); else StopWatching();
    }

    private void WatchFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(WatchPath.Text) ? WatchPath.Text : null };
        if (dlg.ShowDialog() != true) return;
        WatchPath.Text = dlg.FolderName;
        _ctx.Settings.WatchFolder = dlg.FolderName;
        _ctx.Save();
        if (_ctx.Settings.WatchFolderEnabled) { StopWatching(); StartWatching(); }
    }

    private void StartWatching()
    {
        StopWatching();
        try
        {
            var paths = WatchPaths.For(_ctx.Settings.WatchFolder);
            _watch = new WatchFolder(paths, async (file, outDir) =>
            {
                await Dispatcher.InvokeAsync(() => _ctx.Llm.Release());
                await _ctx.Imports.ImportForWatchAsync(file, outDir, WatchOptions());
            });
            _watch.Start();
        }
        catch (Exception ex)
        {
            AppLog.Error("Watch folder could not start", ex);
            WatchHelp.Text = ex.Message;
            WatchCheck.IsChecked = false;
        }
    }

    /// <summary>Watched files use the language and options chosen here at the moment each file arrives.</summary>
    private ImportOptions WatchOptions() => Dispatcher.Invoke(Options);

    public void Shutdown() => StopWatching();

    private void StopWatching()
    {
        _watch?.Dispose();
        _watch = null;
    }
}

/// <summary>A tiny "type a name" dialog.</summary>
public sealed class PromptWindow : Window
{
    private readonly TextBox _box = new() { MinWidth = 320, Margin = new Thickness(0, 0, 0, 14), Padding = new Thickness(6) };

    private PromptWindow(string title, string initial)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        _box.Text = initial;
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = Loc.T("common.close"), IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        Content = new StackPanel { Margin = new Thickness(20), Children = { _box, buttons } };
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(Window? owner, string title, string initial)
    {
        var w = new PromptWindow(title, initial) { Owner = owner };
        return w.ShowDialog() == true ? w._box.Text : null;
    }
}
