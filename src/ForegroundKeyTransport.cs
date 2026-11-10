using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AutoBox
{
    public interface IForegroundKeyboardBackend
    {
        bool IsTargetForeground(WindowTarget target);
        void SendKey(int virtualKey, bool keyUp);
    }

    public sealed class NativeKeyboardBackend : IForegroundKeyboardBackend
    {
        public bool IsTargetForeground(WindowTarget target)
        {
            uint pid;
            if (!NativeMethods.IsWindow(target.Handle) || NativeMethods.GetWindowThreadProcessId(target.Handle, out pid) == 0 || pid != target.ProcessId)
                throw new InvalidOperationException("目标窗口已关闭或发生变化，请刷新并重新选择。");
            return NativeMethods.GetForegroundWindow() == target.Handle;
        }

        public void SendKey(int virtualKey, bool keyUp)
        {
            // Physical number-row keys above QWER; these are not numpad scan codes.
            ushort scan;
            if (virtualKey >= 0x31 && virtualKey <= 0x36) scan = (ushort)(virtualKey - 0x31 + 0x02);
            else if (virtualKey == 0x20) scan = 0x39;
            else throw new ArgumentOutOfRangeException("virtualKey");
            var input = new NativeMethods.Input { Type = 1 };
            input.Data.Keyboard.ScanCode = scan;
            input.Data.Keyboard.Flags = 0x0008u | (keyUp ? 0x0002u : 0u);
            if (NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(NativeMethods.Input))) != 1)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException("模拟按键失败（错误码 " + error + "）。请确认目标游戏和本程序的权限。", new Win32Exception(error));
            }
        }
    }

    public sealed class ForegroundKeyTransport : IKeyTransport, IReadyKeyTransport
    {
        private readonly WindowTarget target;
        private readonly IForegroundKeyboardBackend backend;
        private int heldKey;
        public ForegroundKeyTransport(WindowTarget target) : this(target, new NativeKeyboardBackend()) { }
        public ForegroundKeyTransport(WindowTarget target, IForegroundKeyboardBackend backend)
        {
            if (target == null || target.Handle == IntPtr.Zero || target.ProcessId == 0) throw new ArgumentException("请选择目标窗口。");
            if (backend == null) throw new ArgumentNullException("backend");
            this.target = new WindowTarget { Handle = target.Handle, ProcessId = target.ProcessId };
            this.backend = backend;
        }
        public bool IsReady { get { return backend.IsTargetForeground(target); } }
        public void KeyDown(int virtualKey)
        {
            if (!IsReady) throw new TargetFocusChangedException();
            backend.SendKey(virtualKey, false);
            heldKey = virtualKey;
        }
        public void KeyUp(int virtualKey)
        {
            if (heldKey != virtualKey) return;
            // Release even after a focus change so our own press cannot remain held.
            backend.SendKey(virtualKey, true);
            heldKey = 0;
        }
    }
}
