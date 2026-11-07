using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace MeetWise.Audio;

public sealed class MonitorInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>GDI device name such as \\.\DISPLAY1; stable across restarts.</summary>
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>Native (physical) pixel size and position on the virtual desktop.</summary>
    public int Width { get; init; }
    public int Height { get; init; }
    public int BoundsX { get; init; }
    public int BoundsY { get; init; }
    public int RefreshRate { get; init; }
    public bool IsPrimary { get; init; }

    public string DisplayName => $"{Name} ({Width}×{Height})" + (IsPrimary ? " (primary)" : string.Empty);
    public override string ToString() => DisplayName;
}

/// <summary>
/// Lists monitors in native pixels (ported from RecBox). Screen.Bounds is DPI-virtualised for system-DPI-aware WPF processes, so a 1920x1080
/// display at 125% would report 1536x864 and be captured only partially; EnumDisplaySettings always returns the real mode.
/// </summary>
public sealed class MonitorService
{
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var screens = Screen.AllScreens;
        var list = new List<MonitorInfo>(screens.Length);
        for (var i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            int x = screen.Bounds.X, y = screen.Bounds.Y, w = screen.Bounds.Width, h = screen.Bounds.Height, hz = 0;

            var dm = new Native.DEVMODE { dmSize = (short)Marshal.SizeOf<Native.DEVMODE>() };
            if (Native.EnumDisplaySettings(screen.DeviceName, Native.EnumCurrentSettings, ref dm) && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
            {
                x = dm.dmPositionX; y = dm.dmPositionY; w = dm.dmPelsWidth; h = dm.dmPelsHeight; hz = dm.dmDisplayFrequency;
            }

            // H.264 4:2:0 needs even dimensions.
            w &= ~1;
            h &= ~1;

            var number = Regex.Match(screen.DeviceName ?? string.Empty, @"(\d+)$");
            list.Add(new MonitorInfo
            {
                Index = i,
                Name = number.Success ? $"Display {number.Groups[1].Value}" : $"Display {i + 1}",
                DeviceName = screen.DeviceName ?? string.Empty,
                Width = w, Height = h, BoundsX = x, BoundsY = y, RefreshRate = hz, IsPrimary = screen.Primary
            });
        }
        return list;
    }

    /// <summary>The remembered monitor if it still exists, otherwise the primary one.</summary>
    public MonitorInfo? Find(string? deviceName)
    {
        var monitors = GetMonitors();
        return monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
               ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
    }

    internal static class Native
    {
        public const int EnumCurrentSettings = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    }
}
