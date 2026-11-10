using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutoTools
{
    // A cycle ends after two Enters, or three when the chat workaround is selected.
    public sealed class AmplificationEngine
    {
        public const int ActionGapMilliseconds = 50;
        public const int NumberToClickGapMilliseconds = 100;
        public const int EnterGapMilliseconds = 150;
        public const int ThirdEnterGapMilliseconds = 50;
        public async Task RunAsync(MouseOptions options, ForegroundMouseTransport mouse, IKeyTransport keyboard,
            Action<MouseReadiness> report, Action<long> completed, CancellationToken token)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (mouse == null) throw new ArgumentNullException("mouse");
            if (keyboard == null) throw new ArgumentNullException("keyboard");
            options.Validate();
            int limit = options.LoopCount;
            int gap = options.LoopGapMilliseconds;
            bool extraEnterForChat = options.ExtraEnterForChat;
            var click = new MouseOptions { PointConfigured = options.PointConfigured, ClientPoint = options.ClientPoint,
                ExpectedSize = options.ExpectedSize, CapturedSize = options.CapturedSize,
                HoldMilliseconds = options.HoldMilliseconds, LoopGapMilliseconds = 10, LoopCount = 1 };
            long cycles = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await PressAsync(0x31, click, mouse, keyboard, report, token).ConfigureAwait(false);
                await Task.Delay(NumberToClickGapMilliseconds, token).ConfigureAwait(false);
                await new MouseEngine().RunAsync(click, mouse, report, token).ConfigureAwait(false);
                await Task.Delay(ActionGapMilliseconds, token).ConfigureAwait(false);
                await PressAsync(0x0D, click, mouse, keyboard, report, token).ConfigureAwait(false);
                await Task.Delay(EnterGapMilliseconds, token).ConfigureAwait(false);
                await PressAsync(0x0D, click, mouse, keyboard, report, token).ConfigureAwait(false);
                if (extraEnterForChat)
                {
                    await Task.Delay(ThirdEnterGapMilliseconds, token).ConfigureAwait(false);
                    await PressAsync(0x0D, click, mouse, keyboard, report, token).ConfigureAwait(false);
                }
                cycles++;
                if (completed != null) completed(cycles);
                token.ThrowIfCancellationRequested();
                if (limit > 0 && cycles >= limit) return;
                await Task.Delay(gap, token).ConfigureAwait(false);
            }
        }
        private static async Task PressAsync(int key, MouseOptions options, ForegroundMouseTransport mouse,
            IKeyTransport keyboard, Action<MouseReadiness> report, CancellationToken token)
        {
            while (true)
            {
                var state = mouse.Check(options, token);
                if (report != null) report(state);
                if (!state.Ready) { await Task.Delay(50, token).ConfigureAwait(false); continue; }
                bool attemptedDown = false;
                Exception primary = null;
                try
                {
                    mouse.KeyDown(options, keyboard, key, token, delegate { attemptedDown = true; });
                    int remaining = options.HoldMilliseconds;
                    while (remaining > 0)
                    {
                        int slice = Math.Min(10, remaining);
                        await Task.Delay(slice, token).ConfigureAwait(false);
                        remaining -= slice;
                        var held = mouse.Check(options, token);
                        if (!held.Ready) { if (report != null) report(held); break; }
                    }
                    return;
                }
                catch (MouseNotReadyException waiting)
                {
                    attemptedDown = false;
                    if (report != null) report(waiting.Readiness);
                }
                catch (TargetFocusChangedException) { attemptedDown = false; }
                catch (Exception error) { primary = error; throw; }
                finally
                {
                    if (attemptedDown)
                    {
                        try { keyboard.KeyUp(key); }
                        catch (Exception releaseError)
                        {
                            // Retry cleanup once and retain both the original error and failed up.
                            Exception cleanup = releaseError;
                            try { keyboard.KeyUp(key); }
                            catch (Exception retryError) { cleanup = new AggregateException(releaseError, retryError); }
                            throw new InvalidOperationException("松开按键失败：" + releaseError.Message,
                                primary == null ? cleanup : new AggregateException(primary, cleanup));
                        }
                    }
                }
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
    }
}
