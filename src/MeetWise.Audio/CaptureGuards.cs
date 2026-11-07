using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MeetWise.Audio;

/// <summary>Small Windows safeguards for recording, ported from RecBox.</summary>
public static class CaptureGuards
{
    public const long MinFreeBytesToStart = 1024L * 1024 * 1024;
    public const long MinFreeBytesWhileRecording = 400L * 1024 * 1024;

    private const uint WdaNone = 0x0, WdaExcludeFromCapture = 0x11;
    private const uint EsSystemRequired = 0x1, EsDisplayRequired = 0x2, EsContinuous = 0x80000000;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong free, out ulong total, out ulong totalFree);

    /// <summary>Hides a window (MeetWise itself, the caption overlay) from screen recordings. Needs Windows 10 2004 or newer.</summary>
    public static bool ExcludeFromCapture(IntPtr hwnd, bool exclude = true)
        => hwnd != IntPtr.Zero && SetWindowDisplayAffinity(hwnd, exclude ? WdaExcludeFromCapture : WdaNone);

    /// <summary>Keeps the PC (and screen) awake while recording. Call with false to release.</summary>
    public static void KeepAwake(bool on) => SetThreadExecutionState(on ? EsContinuous | EsSystemRequired | EsDisplayRequired : EsContinuous);

    /// <summary>Free bytes available to the user on the volume of <paramref name="folder"/>, or -1 if unknown.</summary>
    public static long FreeBytes(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder)) ?? folder;
            return GetDiskFreeSpaceEx(root, out var free, out _, out _) ? (long)free : -1;
        }
        catch (Exception) { return -1; }
    }

    /// <summary>Null when there is room to start; otherwise a message for the user. Unknown free space never blocks a recording.</summary>
    public static string? CheckCanStart(string folder, Func<string, long>? freeBytes = null)
    {
        long free = (freeBytes ?? FreeBytes)(folder);
        return free >= 0 && free < MinFreeBytesToStart
            ? $"Not enough disk space to start: {free / (1024 * 1024)} MB free, at least {MinFreeBytesToStart / (1024 * 1024)} MB needed."
            : null;
    }

    public static bool ShouldStopForSpace(string folder, Func<string, long>? freeBytes = null)
    {
        long free = (freeBytes ?? FreeBytes)(folder);
        return free >= 0 && free < MinFreeBytesWhileRecording;
    }
}

/// <summary>A Windows job object that kills FFmpeg if MeetWise itself dies, so no encoder keeps running unseen.</summary>
internal static class ChildProcessJob
{
    private static readonly Lazy<IntPtr> Handle = new(Create);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    private static IntPtr Create()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new ExtendedLimit { Basic = { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ } };
            SetInformationJobObject(job, 9, ref info, Marshal.SizeOf<ExtendedLimit>());
            return job;
        }
        catch (Exception) { return IntPtr.Zero; }
    }

    public static void Assign(Process process)
    {
        try
        {
            var job = Handle.Value;
            if (job != IntPtr.Zero) AssignProcessToJobObject(job, process.Handle);
        }
        catch (Exception) { /* best effort */ }
    }
}
