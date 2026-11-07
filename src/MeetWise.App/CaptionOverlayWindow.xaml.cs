using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MeetWise.Audio;

namespace MeetWise.App;

/// <summary>Big floating subtitles for whatever is being said right now. Excluded from screen capture, so it never ends up inside the recording.</summary>
public partial class CaptionOverlayWindow : Window
{
    private readonly DispatcherTimer _fade = new() { Interval = TimeSpan.FromSeconds(6) };

    public CaptionOverlayWindow()
    {
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - 190;
        Visibility = Visibility.Hidden;
        _fade.Tick += (_, _) => { _fade.Stop(); CaptionText.Text = ""; Hide(); };
        SourceInitialized += (_, _) => CaptureGuards.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
    }

    /// <summary>Shows a line; it disappears a few seconds after the last update.</summary>
    public void SetText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        CaptionText.Text = text.Trim();
        if (!IsVisible) Show();
        _fade.Stop();
        _fade.Start();
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
