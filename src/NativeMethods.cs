using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoBox
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
