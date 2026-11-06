using System.ComponentModel;

namespace MeetWise.App;

public sealed class TranscriptLine : INotifyPropertyChanged
{
    private string _text = "";
    public string Speaker { get; init; } = "";
    public string Time { get; init; } = "";
    public bool IsFinal { get; set; }

    public string Text
    {
        get => _text;
        set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
