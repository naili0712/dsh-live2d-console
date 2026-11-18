using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FishPet;

// Whether a window in front fills the whole screen **she is on** (a game, a video, a slide show): not one of her
// own, and not the desktop. A full-screen window on another monitor leaves her alone (local patch, 2026-11-18).
internal static class Fullscreen
{
    internal static bool Covers(nint mine)
    {
        var window = GetForegroundWindow();
        if (window == 0 || window == mine) return false;
        GetWindowThreadProcessId(window, out var id);
        if (id == Environment.ProcessId || !GetWindowRect(window, out var rect)) return false;
        try { if (Process.GetProcessById((int)id).ProcessName == "explorer") return false; } catch { }
        // local patch (全屏): 只看她所在的那块屏会不会被盖满，别拿前台窗口自己那块屏来算。
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(mine, 2), ref info)) return false;
        var screen = info.Monitor;
        return rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
