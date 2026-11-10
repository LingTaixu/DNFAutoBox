using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace AutoTools
{
    internal sealed class MouseNotReadyException : Exception
    {
        internal readonly MouseReadiness Readiness;
        internal MouseNotReadyException(MouseReadiness readiness) { Readiness = readiness; }
    }

    public sealed class ForegroundMouseTransport
    {
        private readonly object gate = new object();
        private readonly MouseTargetService target;
        private readonly IMouseInputBackend backend;
        private bool paused;
        private bool held;
        public ForegroundMouseTransport(MouseTargetService target, IMouseInputBackend backend)
        {
            if (target == null) throw new ArgumentNullException("target");
            if (backend == null) throw new ArgumentNullException("backend");
            this.target = target;
            this.backend = backend;
        }
        public bool IsPaused { get { lock (gate) return paused; } set { lock (gate) paused = value; } }
        public bool HasHeldButton { get { lock (gate) return held; } }
        public MouseReadiness Check(MouseOptions options, CancellationToken token)
        {
            lock (gate) return CheckCore(options, token);
        }
        private MouseReadiness CheckCore(MouseOptions options, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (paused) return MouseTargetService.Wait(MouseRunState.ManualPause, "已手动暂停 · 按暂停快捷键或点击继续恢复");
            return target.Evaluate(options);
        }
        public Point Move(MouseOptions options, CancellationToken token)
        {
            lock (gate)
            {
                var state = CheckCore(options, token);
                if (!state.Ready) throw new MouseNotReadyException(state);
                if (backend.IsLeftButtonDown) throw new MouseNotReadyException(MouseTargetService.Wait(MouseRunState.ButtonBusy, "已暂停 · 请松开鼠标左键"));
                token.ThrowIfCancellationRequested();
                backend.Move(state.ScreenPoint);
                return state.ScreenPoint;
            }
        }
        public void LeftDown(MouseOptions options, Point movedPoint, CancellationToken token)
        {
            lock (gate)
            {
                var state = CheckCore(options, token);
                if (!state.Ready) throw new MouseNotReadyException(state);
                if (state.ScreenPoint != movedPoint || !target.CursorAt(movedPoint))
                    throw new MouseNotReadyException(MouseTargetService.Wait(MouseRunState.PointMoved, "已暂停 · 窗口或光标已移动，正在重新定位"));
                if (backend.IsLeftButtonDown) throw new MouseNotReadyException(MouseTargetService.Wait(MouseRunState.ButtonBusy, "已暂停 · 请松开鼠标左键"));
                token.ThrowIfCancellationRequested();
                // Treat a failing call as an uncertain down; cleanup must still try an up.
                held = true;
                backend.SendLeftButton(false);
            }
        }
        public void Release()
        {
            lock (gate)
            {
                if (!held) return;
                // Release is deliberately independent of foreground, pause and cancellation.
                backend.SendLeftButton(true);
                held = false;
            }
        }
        internal void KeyDown(MouseOptions options, IKeyTransport keyboard, int key, CancellationToken token, Action sending)
        {
            lock (gate)
            {
                var state = CheckCore(options, token);
                if (!state.Ready) throw new MouseNotReadyException(state);
                token.ThrowIfCancellationRequested();
                sending();
                keyboard.KeyDown(key);
            }
        }
    }

    public sealed class MouseEngine
    {
        public async Task RunAsync(MouseOptions options, ForegroundMouseTransport transport, Action<MouseReadiness> report, CancellationToken token)
        {
            await RunAsync(options, transport, report, null, token).ConfigureAwait(false);
        }
        public async Task RunAsync(MouseOptions options, ForegroundMouseTransport transport, Action<MouseReadiness> report, Action<long> completed, CancellationToken token)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (transport == null) throw new ArgumentNullException("transport");
            options.Validate();
            // Snapshot mutable UI/options state for the entire run.
            options = new MouseOptions { PointConfigured = options.PointConfigured, ClientPoint = options.ClientPoint,
                CapturedSize = options.CapturedSize, ExpectedSize = options.ExpectedSize,
                HoldMilliseconds = options.HoldMilliseconds, LoopGapMilliseconds = options.LoopGapMilliseconds, LoopCount = options.LoopCount };
            Exception primary = null;
            long cycles = 0;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var readiness = transport.Check(options, token);
                    if (report != null) report(readiness);
                    if (!readiness.Ready) { await Task.Delay(50, token).ConfigureAwait(false); continue; }
                    Exception clickFailure = null;
                    bool clicked = false;
                    try
                    {
                        Point screen = transport.Move(options, token);
                        transport.LeftDown(options, screen, token);
                        clicked = true;
                        // Poll while held so pause/focus/size changes release promptly even with a long hold.
                        int remaining = options.HoldMilliseconds;
                        while (remaining > 0)
                        {
                            int slice = Math.Min(10, remaining);
                            await Task.Delay(slice, token).ConfigureAwait(false);
                            remaining -= slice;
                            var heldState = transport.Check(options, token);
                            if (!heldState.Ready) { if (report != null) report(heldState); break; }
                        }
                    }
                    catch (MouseNotReadyException waiting)
                    {
                        if (report != null) report(waiting.Readiness);
                    }
                    catch (Exception error) { clickFailure = error; throw; }
                    finally
                    {
                        try { transport.Release(); }
                        catch (Exception releaseError)
                        {
                            throw new InvalidOperationException("松开鼠标左键失败：" + releaseError.Message,
                                clickFailure == null ? releaseError : new AggregateException(clickFailure, releaseError));
                        }
                    }
                    if (clicked)
                    {
                        cycles++;
                        if (completed != null) completed(cycles);
                        token.ThrowIfCancellationRequested();
                        if (options.LoopCount > 0 && cycles >= options.LoopCount) return;
                    }
                    await Task.Delay(options.LoopGapMilliseconds, token).ConfigureAwait(false);
                }
            }
            catch (Exception error) { primary = error; throw; }
            finally
            {
                try { transport.Release(); }
                catch (Exception releaseError)
                {
                    throw new InvalidOperationException("松开鼠标左键失败：" + releaseError.Message,
                        primary == null ? releaseError : new AggregateException(primary, releaseError));
                }
            }
        }
    }
}
