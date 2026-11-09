using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutoBox
{
    public sealed class MacroOptions
    {
        public MacroOptions()
        {
            HoldMilliseconds = 10;
            ActionGapMilliseconds = 50;
            LoopGapMilliseconds = 60;
        }
        public int VirtualKey { get; set; }
        public int HoldMilliseconds { get; set; }
        public int ActionGapMilliseconds { get; set; }
        public int LoopGapMilliseconds { get; set; }
        public void Validate()
        {
            if (VirtualKey < 0x31 || VirtualKey > 0x35) throw new ArgumentOutOfRangeException("VirtualKey", "操作键仅支持主键盘数字 1–5。");
            if (HoldMilliseconds < 10 || HoldMilliseconds > 5000) throw new ArgumentOutOfRangeException("HoldMilliseconds");
            if (ActionGapMilliseconds < 0 || ActionGapMilliseconds > 600000) throw new ArgumentOutOfRangeException("ActionGapMilliseconds");
            if (LoopGapMilliseconds < 10 || LoopGapMilliseconds > 3600000) throw new ArgumentOutOfRangeException("LoopGapMilliseconds");
        }
    }

    public interface IKeyTransport
    {
        void KeyDown(int virtualKey);
        void KeyUp(int virtualKey);
    }

    public interface IReadyKeyTransport
    {
        bool IsReady { get; }
    }

    public sealed class TargetFocusChangedException : Exception { }

    public sealed class PausableKeyTransport : IKeyTransport, IReadyKeyTransport
    {
        private readonly IKeyTransport inner;
        private volatile bool paused;
        public PausableKeyTransport(IKeyTransport inner)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            this.inner = inner;
        }
        public bool IsPaused { get { return paused; } set { paused = value; } }
        public bool IsReady
        {
            get
            {
                var ready = inner as IReadyKeyTransport;
                return !paused && (ready == null || ready.IsReady);
            }
        }
        public void KeyDown(int key)
        {
            if (paused) throw new TargetFocusChangedException();
            inner.KeyDown(key);
        }
        public void KeyUp(int key) { inner.KeyUp(key); }
    }

    public sealed class MacroEngine
    {
        public async Task RunAsync(MacroOptions options, IKeyTransport transport, Action<long> completed, CancellationToken cancellation)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (transport == null) throw new ArgumentNullException("transport");
            options.Validate();
            long cycles = 0;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                await PressAsync(options.VirtualKey, options.HoldMilliseconds, transport, cancellation).ConfigureAwait(false);
                await Task.Delay(options.ActionGapMilliseconds, cancellation).ConfigureAwait(false);
                await PressAsync(0x20, options.HoldMilliseconds, transport, cancellation).ConfigureAwait(false);
                if (completed != null) completed(++cycles);
                await Task.Delay(options.LoopGapMilliseconds, cancellation).ConfigureAwait(false);
            }
        }

        private static async Task PressAsync(int key, int hold, IKeyTransport transport, CancellationToken cancellation)
        {
            // Check before every down event, including the space in the same cycle.
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var ready = transport as IReadyKeyTransport;
                if (ready != null && !ready.IsReady)
                {
                    await Task.Delay(50, cancellation).ConfigureAwait(false);
                    continue;
                }
                try { transport.KeyDown(key); break; }
                catch (TargetFocusChangedException) { }
                await Task.Delay(50, cancellation).ConfigureAwait(false);
            }
            bool pressed = false;
            Exception primaryError = null;
            try
            {
                pressed = true;
                await Task.Delay(hold, cancellation).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                primaryError = error;
                throw;
            }
            finally
            {
                // Always attempt a release, including cancellation while a key is held.
                if (pressed)
                {
                    try { transport.KeyUp(key); }
                    catch (Exception releaseError)
                    {
                        // A failed release must remain visible even when stop was requested.
                        if (primaryError == null || primaryError is OperationCanceledException)
                            throw new InvalidOperationException("松开按键失败：" + releaseError.Message, releaseError);
                    }
                }
            }
        }
    }
}
