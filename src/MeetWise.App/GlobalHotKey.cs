using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MeetWise.App;

/// <summary>A system-wide keyboard shortcut (works while another app has focus). Registered against a window's handle.</summary>
public sealed class GlobalHotKey : IDisposable
{
    public const uint Control = 0x2, Shift = 0x4, NoRepeat = 0x4000;
    private const int WmHotKey = 0x0312;
    private static int _nextId = 0x4D57; // "MW"

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly int _id = Interlocked.Increment(ref _nextId);
    private readonly Action _action;

    /// <summary>False when another program already owns the shortcut.</summary>
    public bool IsRegistered { get; }

    public GlobalHotKey(HwndSource source, uint modifiers, uint virtualKey, Action action)
    {
        _source = source;
        _action = action;
        IsRegistered = RegisterHotKey(source.Handle, _id, modifiers | NoRepeat, virtualKey);
        if (IsRegistered) source.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == _id) { handled = true; _action(); }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (!IsRegistered) return;
        _source.RemoveHook(Hook);
        UnregisterHotKey(_source.Handle, _id);
    }
}
