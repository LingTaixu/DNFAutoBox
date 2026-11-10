using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutoTools
{
    public sealed class NativeMouseWindowBackend : IMouseWindowBackend
    {
        public void ValidateTarget(WindowTarget target)
        {
            uint pid;
            if (!NativeMethods.IsWindow(target.Handle) || NativeMethods.GetWindowThreadProcessId(target.Handle, out pid) == 0 || pid != target.ProcessId)
                throw new MouseTargetInvalidException("目标窗口已关闭或发生变化，请刷新并重新选择、确认点位。");
        }
        public bool IsForeground(WindowTarget target)
        {
            return !NativeMethods.IsIconic(target.Handle) && NativeMethods.IsWindowVisible(target.Handle) && NativeMethods.GetForegroundWindow() == target.Handle;
        }
        public Size GetClientSize(WindowTarget target)
        {
            NativeMethods.Rect rect;
            if (!NativeMethods.GetClientRect(target.Handle, out rect)) throw Error("读取客户区尺寸失败");
            return new Size(rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
        public Point GetCursorPosition()
        {
            NativeMethods.Point point;
            if (!NativeMethods.GetCursorPos(out point)) throw Error("读取鼠标位置失败");
            return new Point(point.X, point.Y);
        }
        public Point ScreenToClient(WindowTarget target, Point screen)
        {
            var point = new NativeMethods.Point(screen.X, screen.Y);
            if (!NativeMethods.ScreenToClient(target.Handle, ref point)) throw Error("屏幕坐标转换失败");
            return new Point(point.X, point.Y);
        }
        public Point ClientToScreen(WindowTarget target, Point client)
        {
            var point = new NativeMethods.Point(client.X, client.Y);
            if (!NativeMethods.ClientToScreen(target.Handle, ref point)) throw Error("客户区坐标转换失败");
            return new Point(point.X, point.Y);
        }
        public bool IsPointVisible(WindowTarget target, Point screen)
        {
            bool onScreen = false;
            foreach (Screen display in Screen.AllScreens) if (display.Bounds.Contains(screen)) { onScreen = true; break; }
            if (!onScreen) return false;
            IntPtr hit = NativeMethods.WindowFromPoint(new NativeMethods.Point(screen.X, screen.Y));
            return hit == target.Handle || (hit != IntPtr.Zero && NativeMethods.IsChild(target.Handle, hit));
        }
        public Size ApplyClientSize(WindowTarget target, Size expected)
        {
            ValidateTarget(target);
            EnsureDpiSupported(target);
            if (NativeMethods.IsIconic(target.Handle)) throw new InvalidOperationException("请先还原游戏窗口再应用尺寸。");
            var info = new NativeMethods.WindowInfo { Size = (uint)Marshal.SizeOf(typeof(NativeMethods.WindowInfo)) };
            if (!NativeMethods.GetWindowInfo(target.Handle, ref info)) throw Error("读取窗口样式失败");
            uint style = info.Style;
            uint extended = info.ExtendedStyle;
            var rect = new NativeMethods.Rect { Right = expected.Width, Bottom = expected.Height };
            bool adjusted;
            try { adjusted = NativeMethods.AdjustWindowRectExForDpi(ref rect, style, NativeMethods.GetMenu(target.Handle) != IntPtr.Zero, extended, SystemDpi()); }
            catch (EntryPointNotFoundException) { adjusted = NativeMethods.AdjustWindowRectEx(ref rect, style, NativeMethods.GetMenu(target.Handle) != IntPtr.Zero, extended); }
            if (!adjusted) throw Error("计算客户区对应的窗口尺寸失败");
            // NOMOVE | NOZORDER | NOACTIVATE: resizing never repositions or activates the game.
            if (!NativeMethods.SetWindowPos(target.Handle, IntPtr.Zero, 0, 0, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x0016))
                throw Error("应用窗口尺寸失败");
            ValidateTarget(target);
            return GetClientSize(target);
        }
        private static uint SystemDpi()
        {
            IntPtr dc = NativeMethods.GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) throw Error("读取系统 DPI 失败");
            try
            {
                int dpi = NativeMethods.GetDeviceCaps(dc, 88);
                if (dpi <= 0) throw new MouseDpiException("无法确认系统 DPI，鼠标操作已阻止。");
                return (uint)dpi;
            }
            finally { NativeMethods.ReleaseDC(IntPtr.Zero, dc); }
        }
        public void EnsureDpiSupported(WindowTarget target)
        {
            // The original manifest is system-DPI-aware. Reject configurations where its
            // coordinates may be virtualized instead of silently mixing logical/physical pixels.
            uint dpi = SystemDpi();
            try
            {
                uint targetDpi = NativeMethods.GetDpiForWindow(target.Handle);
                if (targetDpi == 0) throw new MouseTargetInvalidException("无法读取目标窗口 DPI，请重新选择窗口。");
                if (targetDpi != dpi) throw new MouseDpiException("目标窗口 DPI 与本程序不一致，鼠标操作已阻止；请统一缩放并重启游戏和工具。");
            }
            catch (EntryPointNotFoundException) { }
            string reason = null;
            bool legacy = false;
            bool enumerated = NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                delegate(IntPtr monitor, IntPtr dc, ref NativeMethods.Rect bounds, IntPtr data)
                {
                    try
                    {
                        int scale;
                        if (NativeMethods.GetScaleFactorForMonitor(monitor, out scale) != 0)
                            reason = "无法确认显示器缩放，鼠标操作已阻止。";
                        else if (Math.Abs((long)scale * 96 - (long)dpi * 100) > 100)
                            reason = "暂不支持混合 DPI 或运行中改变缩放；请统一所有显示器缩放并重启工具。";
                    }
                    catch (DllNotFoundException) { legacy = true; }
                    catch (EntryPointNotFoundException) { legacy = true; }
                    return reason == null;
                }, IntPtr.Zero);
            if (reason != null) throw new MouseDpiException(reason);
            if (!enumerated) throw Error("枚举显示器失败");
            if (legacy && Screen.AllScreens.Length != 1) throw new MouseDpiException("当前系统无法验证多屏 DPI，请使用单显示器运行鼠标模式。");
        }
        internal static Exception Error(string action)
        {
            int code = Marshal.GetLastWin32Error();
            return new InvalidOperationException(action + "（错误码 " + code + "）。", new Win32Exception(code));
        }
    }

    public interface IMouseInputBackend
    {
        bool IsLeftButtonDown { get; }
        void Move(Point screen);
        void SendLeftButton(bool up);
    }

    public sealed class NativeMouseInputBackend : IMouseInputBackend
    {
        public bool IsLeftButtonDown { get { return (NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0; } }
        public static int NormalizeAxis(int coordinate, int origin, int length)
        {
            long relative = (long)coordinate - origin;
            if (length > 65536) throw new InvalidOperationException("虚拟桌面跨度超过 65536 像素，无法可靠定位鼠标。");
            if (length <= 0 || relative < 0 || relative >= length) throw new ArgumentOutOfRangeException("coordinate", "目标点不在虚拟桌面内。");
            return (int)Math.Min(65535L, ((relative * 2 + 1) * 65536L) / (2L * length));
        }
        public void Move(Point screen)
        {
            var input = new NativeMethods.Input { Type = 0 };
            input.Data.Mouse.X = NormalizeAxis(screen.X, NativeMethods.GetSystemMetrics(76), NativeMethods.GetSystemMetrics(78));
            input.Data.Mouse.Y = NormalizeAxis(screen.Y, NativeMethods.GetSystemMetrics(77), NativeMethods.GetSystemMetrics(79));
            input.Data.Mouse.Flags = 0x0001 | 0x8000 | 0x4000; // MOVE | ABSOLUTE | VIRTUALDESK
            Send(input);
        }
        public void SendLeftButton(bool up)
        {
            var input = new NativeMethods.Input { Type = 0 };
            input.Data.Mouse.Flags = up ? 0x0004u : 0x0002u;
            Send(input);
        }
        private static void Send(NativeMethods.Input input)
        {
            if (NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(NativeMethods.Input))) != 1)
                throw NativeMouseWindowBackend.Error("鼠标输入失败，请确认目标与工具权限");
        }
    }
}
