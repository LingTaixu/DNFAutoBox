using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoTools
{
    public sealed class WindowTarget
    {
        public IntPtr Handle { get; set; }
        public uint ProcessId { get; set; }
        public string Title { get; set; }
        public string ProcessName { get; set; }
        public override string ToString()
        {
            return Title + "  [" + ProcessName + ", " + ProcessId + "]";
        }
    }

    internal static class NativeMethods
    {
        internal delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint count, Input[] inputs, int size);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point { internal int X, Y; internal Point(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowInfo
        {
            internal uint Size;
            internal Rect WindowRect, ClientRect;
            internal uint Style, ExtendedStyle, Status, BorderWidth, BorderHeight;
            internal ushort WindowType, CreatorVersion;
        }
        internal delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool ScreenToClient(IntPtr window, ref Point point);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] internal static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowInfo(IntPtr window, ref WindowInfo info);
        [DllImport("user32.dll")] internal static extern IntPtr GetMenu(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool AdjustWindowRectEx(ref Rect rect, uint style, bool menu, uint extendedStyle);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool AdjustWindowRectExForDpi(ref Rect rect, uint style, bool menu, uint extendedStyle, uint dpi);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
        [DllImport("shcore.dll")] internal static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern int GetDeviceCaps(IntPtr dc, int index);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Input { internal uint Type; internal InputData Data; }
        [StructLayout(LayoutKind.Explicit)]
        internal struct InputData
        {
            [FieldOffset(0)] internal KeyboardInput Keyboard;
            [FieldOffset(0)] internal MouseInput Mouse;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput
        {
            internal ushort VirtualKey, ScanCode;
            internal uint Flags, Time;
            internal UIntPtr ExtraInfo;
        }
        // The mouse member gives the union the size required by the native INPUT ABI.
        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput
        {
            internal int X, Y;
            internal uint MouseData, Flags, Time;
            internal UIntPtr ExtraInfo;
        }
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr handle, int id);

        internal static List<WindowTarget> ListWindows()
        {
            var windows = new List<WindowTarget>();
            uint ownProcess = (uint)Process.GetCurrentProcess().Id;
            EnumWindows(delegate(IntPtr handle, IntPtr unused)
            {
                if (!IsWindowVisible(handle)) return true;
                var title = new StringBuilder(1024);
                GetWindowText(handle, title, title.Capacity);
                if (title.Length == 0) return true;
                uint pid;
                GetWindowThreadProcessId(handle, out pid);
                if (pid == ownProcess || pid == 0) return true;
                string name = "进程";
                try { using (var process = Process.GetProcessById((int)pid)) name = process.ProcessName; }
                catch (ArgumentException) { return true; }
                catch (System.ComponentModel.Win32Exception) { }
                windows.Add(new WindowTarget { Handle = handle, ProcessId = pid, Title = title.ToString(), ProcessName = name });
                return true;
            }, IntPtr.Zero);
            windows.Sort(delegate(WindowTarget a, WindowTarget b) { return string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase); });
            return windows;
        }
    }
}
