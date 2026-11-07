using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MeetWise.Intelligence;

namespace MeetWise.App;

/// <summary>Private on-screen coaching: shows only sentences that had real mistakes, with the changed words marked.</summary>
public partial class CoachOverlayWindow : Window
{
    private const int MaxCards = 3;
    private int _sentences, _withTips;

    public CoachOverlayWindow()
    {
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Bottom - 220;
        UpdateHeader();
    }

    public void Add(CoachFeedback fb)
    {
        _sentences++;
        if (!fb.Ok) _withTips++;
        UpdateHeader();
        if (fb.Ok) return;

        EmptyText.Visibility = Visibility.Collapsed;
        Cards.Children.Insert(0, BuildCard(fb));
        while (Cards.Children.Count > MaxCards) Cards.Children.RemoveAt(Cards.Children.Count - 1);
    }

    private void UpdateHeader()
    {
        HeaderText.Text = _sentences == 0 ? "English coach" : $"English coach · {_sentences} sentence(s), {_withTips} with tips";
    }

    private static UIElement BuildCard(CoachFeedback fb)
    {
        var sentence = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 16, Foreground = Brushes.White };
        foreach (var token in WordDiff.Compute(fb.Original, fb.Corrected))
        {
            var run = new Run(token.Text + " ");
            switch (token.Kind)
            {
                case DiffKind.Removed:
                    run.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A));
                    run.TextDecorations = TextDecorations.Strikethrough;
                    break;
                case DiffKind.Added:
                    run.Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0xE3, 0x8F));
                    run.FontWeight = FontWeights.SemiBold;
                    break;
            }
            sentence.Inlines.Add(run);
        }

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(sentence);
        foreach (var issue in fb.Issues)
            panel.Children.Add(new TextBlock
            {
                Text = $"• {issue.Original} → {issue.Suggestion}" + (string.IsNullOrWhiteSpace(issue.Why) ? "" : $": {issue.Why}"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xB7, 0xC0, 0xDC)),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
        if (fb.WordsPerMinute is { } wpm && wpm > 180)
            panel.Children.Add(new TextBlock { Text = $"Pace: {wpm:0} words/min is fast; slow down a little.", Foreground = Brushes.Orange, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        return panel;
    }

    public void Reset()
    {
        _sentences = _withTips = 0;
        Cards.Children.Clear();
        EmptyText.Visibility = Visibility.Visible;
        UpdateHeader();
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
}
