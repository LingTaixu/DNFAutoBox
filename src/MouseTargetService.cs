using System;
using System.Drawing;

namespace AutoTools
{
    public sealed class MouseOptions
    {
        public bool PointConfigured { get; set; }
        public Point ClientPoint { get; set; }
        public Size CapturedSize { get; set; }
        public Size ExpectedSize { get; set; }
        public int HoldMilliseconds { get; set; }
        public int LoopGapMilliseconds { get; set; }
        public int LoopCount { get; set; } // Zero means keep running until stopped.
        public MouseOptions() { HoldMilliseconds = 10; LoopGapMilliseconds = 500; }
        public void Validate()
        {
            ValidateSize(ExpectedSize);
            if (!PointConfigured) throw new InvalidOperationException("未取点，请先取点或确认已存点位。");
            if (CapturedSize != ExpectedSize) throw new InvalidOperationException("取点尺寸与期望尺寸不符，请重新取点。");
            if (!Contains(ExpectedSize, ClientPoint)) throw new InvalidOperationException("已存坐标超出客户区，请重新取点。");
            if (HoldMilliseconds < 10 || HoldMilliseconds > 5000) throw new ArgumentOutOfRangeException("HoldMilliseconds");
            if (LoopGapMilliseconds < 0 || LoopGapMilliseconds > 3600000) throw new ArgumentOutOfRangeException("LoopGapMilliseconds");
            if (LoopCount < 0 || LoopCount > 1000000000) throw new ArgumentOutOfRangeException("LoopCount");
        }
        public static void ValidateSize(Size size)
        {
            if (size.Width < 1 || size.Height < 1 || size.Width > 32768 || size.Height > 32768)
                throw new ArgumentOutOfRangeException("size", "客户区宽高必须在 1–32768 之间。");
        }
        public static bool Contains(Size size, Point point)
        {
            return point.X >= 0 && point.Y >= 0 && point.X < size.Width && point.Y < size.Height;
        }
    }

    public enum MouseRunState { Stopped, Running, ManualPause, WaitingForeground, SizeMismatch, PointCovered, PointMoved, ButtonBusy, DpiUnsupported, TargetInvalid, InputFailed }

    public sealed class MouseReadiness
    {
        public MouseRunState State { get; private set; }
        public string Message { get; private set; }
        public Point ScreenPoint { get; private set; }
        public bool Ready { get { return State == MouseRunState.Running; } }
        public MouseReadiness(MouseRunState state, string message, Point point) { State = state; Message = message; ScreenPoint = point; }
    }

    public sealed class MouseTargetInvalidException : InvalidOperationException
    {
        public MouseTargetInvalidException(string message) : base(message) { }
    }
    public sealed class MouseDpiException : InvalidOperationException
    {
        public MouseDpiException(string message) : base(message) { }
    }

    public interface IMouseWindowBackend
    {
        void ValidateTarget(WindowTarget target);
        void EnsureDpiSupported(WindowTarget target);
        bool IsForeground(WindowTarget target);
        Size GetClientSize(WindowTarget target);
        Point GetCursorPosition();
        Point ScreenToClient(WindowTarget target, Point screen);
        Point ClientToScreen(WindowTarget target, Point client);
        bool IsPointVisible(WindowTarget target, Point screen);
        Size ApplyClientSize(WindowTarget target, Size expected);
    }

    public sealed class MouseTargetService
    {
        private readonly WindowTarget target;
        private readonly IMouseWindowBackend backend;
        private bool invalid;
        public MouseTargetService(WindowTarget target) : this(target, new NativeMouseWindowBackend()) { }
        public MouseTargetService(WindowTarget target, IMouseWindowBackend backend)
        {
            if (target == null || target.Handle == IntPtr.Zero || target.ProcessId == 0) throw new ArgumentException("请选择目标窗口。");
            if (backend == null) throw new ArgumentNullException("backend");
            this.target = new WindowTarget { Handle = target.Handle, ProcessId = target.ProcessId };
            this.backend = backend;
        }
        private void Validate()
        {
            if (invalid) throw new MouseTargetInvalidException("目标窗口已失效，请刷新并重新选择、确认点位。");
            try { backend.ValidateTarget(target); }
            catch (MouseTargetInvalidException) { invalid = true; throw; }
        }
        public Size CheckSize()
        {
            Validate();
            backend.EnsureDpiSupported(target);
            return backend.GetClientSize(target);
        }
        public Size ApplySize(Size expected)
        {
            MouseOptions.ValidateSize(expected);
            CheckSize();
            return backend.ApplyClientSize(target, expected);
        }
        public Point Capture(Size expected)
        {
            MouseOptions.ValidateSize(expected);
            Size actual = CheckSize();
            if (!backend.IsForeground(target)) throw new InvalidOperationException("取点失败：请切回所选游戏窗口后再按取点快捷键。");
            if (actual != expected) throw new InvalidOperationException(SizeMessage(expected, actual));
            Point screen = backend.GetCursorPosition();
            Point client = backend.ScreenToClient(target, screen);
            if (!MouseOptions.Contains(actual, client)) throw new InvalidOperationException("取点失败：鼠标位于游戏客户区外。");
            if (!backend.IsPointVisible(target, screen)) throw new InvalidOperationException("取点失败：目标位置被其他窗口覆盖。");
            Validate();
            if (!backend.IsForeground(target) || backend.GetClientSize(target) != actual || backend.ClientToScreen(target, client) != screen)
                throw new InvalidOperationException("取点失败：窗口位置、尺寸或焦点发生变化，请重试。");
            return client;
        }
        public MouseReadiness Evaluate(MouseOptions options)
        {
            Validate();
            backend.EnsureDpiSupported(target);
            if (!backend.IsForeground(target)) return Wait(MouseRunState.WaitingForeground, "已暂停 · 等待游戏前台");
            Size actual = backend.GetClientSize(target);
            if (actual != options.ExpectedSize) return Wait(MouseRunState.SizeMismatch, SizeMessage(options.ExpectedSize, actual));
            Point screen = backend.ClientToScreen(target, options.ClientPoint);
            if (!backend.IsPointVisible(target, screen)) return Wait(MouseRunState.PointCovered, "已暂停 · 目标点被覆盖或不在可见屏幕内");
            Validate();
            if (!backend.IsForeground(target)) return Wait(MouseRunState.WaitingForeground, "已暂停 · 等待游戏前台");
            return new MouseReadiness(MouseRunState.Running, "循环中 · 赌增幅强化", screen);
        }
        public bool CursorAt(Point point) { return backend.GetCursorPosition() == point; }
        public static string SizeMessage(Size expected, Size actual)
        {
            return "尺寸不符 · 期望 " + expected.Width + " × " + expected.Height + "，实际 " + actual.Width + " × " + actual.Height + "；请恢复尺寸或停止后重新取点。";
        }
        internal static MouseReadiness Wait(MouseRunState state, string message) { return new MouseReadiness(state, message, Point.Empty); }
    }

    public static class HotkeyRules
    {
        public static bool AreDistinct(params int[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
                for (int j = i + 1; j < keys.Length; j++) if (keys[i] == keys[j]) return false;
            return true;
        }
    }
}
