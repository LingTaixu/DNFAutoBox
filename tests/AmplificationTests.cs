using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using AutoTools;

internal static class AmplificationTests
{
    private static int passed;
    private sealed class Keyboard : IKeyTransport
    {
        internal readonly List<string> Events = new List<string>();
        internal Action<int> Down, Up;
        public void KeyDown(int key) { Events.Add("down:" + key); if (Down != null) Down(key); }
        public void KeyUp(int key) { Events.Add("up:" + key); if (Up != null) Up(key); }
        internal string Log { get { return string.Join(",", Events); } }
    }
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL sequence: " + name);
        passed++;
        Console.WriteLine("PASS sequence: " + name);
    }
    private static MouseOptions ThreeEnterOptions()
    {
        var options = MouseTests.Options();
        options.ExtraEnterForChat = true;
        return options;
    }
    private static async Task Cancelled(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(3000)) != task) throw new Exception("Sequence did not stop promptly.");
        try { await task; throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { }
    }
    internal static async Task Run()
    {
        foreach (bool enabled in new[] { false, true }) await TestSequenceAndTiming(enabled);
        foreach (int key in new[] { 49, 13 }) await TestCancelHeldKey(key);
        await TestCancelHeldKey(13, 3);
        await TestCancelBeforeThirdEnter();
        await TestCancelHeldMouse();
        await TestPauseBeforeEnter();
        await TestWaitBeforeNumber();
        await TestKeyReleaseFailure();
        await TestCancelDuringLoopGap();
        foreach (bool enabled in new[] { false, true }) await TestFinalCycleGap(enabled);
        foreach (int after in new[] { 1, 2 }) await TestFocusBetweenEnters(after);
        await TestKeyFocusRace();
        await TestZeroGap();
        foreach (bool enabled in new[] { false, true }) await TestOptionSnapshot(enabled);
        Console.WriteLine("PASS sequence: " + passed + " checks (simulated input only)");
    }
    private static async Task TestSequenceAndTiming(bool extraEnter)
    {
        using (var cancel = new CancellationTokenSource(3000))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            var log = new List<string>(); var gaps = new List<long>(); var watch = new Stopwatch();
            long lastUp = -1; int enterUps = 0;
            var options = ThreeEnterOptions(); options.LoopCount = 2; options.LoopGapMilliseconds = 120;
            options.ExtraEnterForChat = extraEnter;
            watch.Start();
            Action<string> down = delegate(string name)
            {
                log.Add(name);
                if (lastUp >= 0) gaps.Add(watch.ElapsedMilliseconds - lastUp);
            };
            keyboard.Down = delegate(int key) { down("down:" + key); };
            keyboard.Up = delegate(int key) { log.Add("up:" + key); lastUp = watch.ElapsedMilliseconds; if (key == 13) enterUps++; };
            input.OnDown = delegate { down("left-down"); };
            input.OnUp = delegate { log.Add("left-up"); lastUp = watch.ElapsedMilliseconds; };
            long completed = 0;
            await new AmplificationEngine().RunAsync(options, new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard, null,
                delegate(long count) { completed = count; if (count == 1) window.Origin = new Point(400, 100); }, cancel.Token);
            string cycle = "down:49,up:49,left-down,left-up,down:13,up:13,down:13,up:13" + (extraEnter ? ",down:13,up:13" : "");
            Check(string.Join(",", log) == cycle + "," + cycle && completed == 2 && enterUps == (extraEnter ? 6 : 4),
                "each finite cycle sends exactly " + (extraEnter ? "three" : "two") + " Enters when the chat option is " + extraEnter);
            bool timing = extraEnter
                ? gaps.Count == 9 && gaps[0] >= 95 && gaps[1] >= 45 && gaps[2] >= 145 && gaps[3] >= 45 && gaps[4] >= 115 && gaps[5] >= 95 && gaps[6] >= 45 && gaps[7] >= 145 && gaps[8] >= 45
                : gaps.Count == 7 && gaps[0] >= 95 && gaps[1] >= 45 && gaps[2] >= 145 && gaps[3] >= 115 && gaps[4] >= 95 && gaps[5] >= 45 && gaps[6] >= 145;
            Check(timing, "sequence timing and next-loop delay follow the final selected Enter (chat option=" + extraEnter + ")");
            Check(input.Moves[0] == new Point(600, 500) && input.Moves[1] == new Point(900, 400), "the full sequence follows the moved game window");
        }
    }
    private static async Task TestCancelHeldKey(int key, int occurrence = 1)
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            int presses = 0;
            keyboard.Down = delegate(int current) { if (current == key && ++presses == occurrence) cancel.Cancel(); };
            long completed = 0;
            await Cancelled(new AmplificationEngine().RunAsync(ThreeEnterOptions(), new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard, null,
                delegate(long count) { completed = count; }, cancel.Token));
            string expected = "down:49,up:49";
            if (key == 13) for (int i = 0; i < occurrence; i++) expected += ",down:13,up:13";
            Check(keyboard.Log == expected && completed == 0,
                "stop while holding " + key + " occurrence " + occurrence + " releases that key and skips the remaining sequence");
        }
    }
    private static async Task TestCancelHeldMouse()
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            input.OnDown = delegate { cancel.Cancel(); };
            await Cancelled(new AmplificationEngine().RunAsync(ThreeEnterOptions(), new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard, null, null, cancel.Token));
            Check(input.Log == "move,down,up" && keyboard.Log == "down:49,up:49", "stop during left click releases it without sending Enter");
        }
    }
    private static async Task TestPauseBeforeEnter()
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            var mouse = new ForegroundMouseTransport(MouseTests.Service(window), input);
            input.OnUp = delegate { mouse.IsPaused = true; };
            bool paused = false; var options = ThreeEnterOptions(); options.LoopCount = 1;
            await new AmplificationEngine().RunAsync(options, mouse, keyboard,
                delegate(MouseReadiness state)
                {
                    if (!state.Ready)
                    {
                        Check(keyboard.Log == "down:49,up:49", "pause after click prevents Enter");
                        paused = true; mouse.IsPaused = false;
                    }
                }, null, cancel.Token);
            Check(paused && keyboard.Log == "down:49,up:49,down:13,up:13,down:13,up:13,down:13,up:13", "resume continues the pending Enter sequence without repeating 1 or click");
        }
    }
    private static async Task TestWaitBeforeNumber()
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow { Foreground = false }; var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            await Cancelled(new AmplificationEngine().RunAsync(ThreeEnterOptions(), new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard,
                delegate(MouseReadiness state) { if (!state.Ready) cancel.Cancel(); }, null, cancel.Token));
            Check(input.Log == "" && keyboard.Log == "", "not foreground sends neither number key nor mouse input");
        }
    }
    private static async Task TestKeyReleaseFailure()
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            keyboard.Down = delegate(int key) { cancel.Cancel(); };
            keyboard.Up = delegate(int key) { throw new InvalidOperationException("key release failed"); };
            bool failed = false;
            try { await new AmplificationEngine().RunAsync(ThreeEnterOptions(), new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard, null, null, cancel.Token); }
            catch (InvalidOperationException error) { failed = error.ToString().Contains("key release failed"); }
            Check(failed && keyboard.Log == "down:49,up:49,up:49" && input.Log == "", "key release failures remain visible on cancellation and cleanup retries once");
        }
    }
    private static async Task TestCancelDuringLoopGap()
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            var options = ThreeEnterOptions(); options.LoopGapMilliseconds = 3600000;
            await Cancelled(new AmplificationEngine().RunAsync(options, new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard, null,
                delegate(long count) { cancel.CancelAfter(20); }, cancel.Token));
            Check(input.Ups == 1 && keyboard.Events.Count == 8, "stop interrupts a long user loop gap without starting the next number press");
        }
    }
    private static async Task TestFinalCycleGap(bool extraEnter)
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            var options = ThreeEnterOptions(); options.LoopCount = 1; options.LoopGapMilliseconds = 3600000;
            options.ExtraEnterForChat = extraEnter;
            long completed = 0;
            await new AmplificationEngine().RunAsync(options, new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard, null,
                delegate(long count) { completed = count; }, cancel.Token);
            Check(completed == 1 && keyboard.Events.Count == (extraEnter ? 8 : 6), "finite sequence skips the final loop delay (chat option=" + extraEnter + ")");
        }
    }
    private static async Task TestFocusBetweenEnters(int after)
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            var options = ThreeEnterOptions(); options.LoopCount = 1;
            int enters = 0; bool waited = false;
            keyboard.Up = delegate(int key) { if (key == 13 && ++enters == after) window.Foreground = false; };
            await new AmplificationEngine().RunAsync(options, new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard,
                delegate(MouseReadiness state)
                {
                    if (!state.Ready)
                    {
                        Check(enters == after && keyboard.Events.Count == 2 + after * 2, "focus loss after Enter " + after + " pauses before the next Enter");
                        waited = true; window.Foreground = true;
                    }
                }, null, cancel.Token);
            Check(waited && enters == 3 && input.Ups == 1, "returning after Enter " + after + " completes the pending Enters without duplicating the click");
        }
    }
    private static async Task TestKeyFocusRace()
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            await Cancelled(new AmplificationEngine().RunAsync(ThreeEnterOptions(), new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard,
                delegate(MouseReadiness state) { if (state.Ready) window.Foreground = false; else cancel.Cancel(); }, null, cancel.Token));
            Check(keyboard.Log == "" && input.Log == "", "focus lost just before the number press sends no down or unowned up");
        }
    }
    private static async Task TestZeroGap()
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            var options = ThreeEnterOptions(); options.LoopCount = 2; options.LoopGapMilliseconds = 0;
            await new AmplificationEngine().RunAsync(options, new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard, null, null, cancel.Token);
            Check(input.Ups == 2 && keyboard.Events.Count == 16, "a user loop gap of zero milliseconds is accepted");
        }
    }
    private static async Task TestCancelBeforeThirdEnter()
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            int enters = 0; long completed = 0;
            keyboard.Up = delegate(int key) { if (key == 13 && ++enters == 2) cancel.CancelAfter(20); };
            await Cancelled(new AmplificationEngine().RunAsync(ThreeEnterOptions(), new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard, null,
                delegate(long count) { completed = count; }, cancel.Token));
            Check(enters == 2 && keyboard.Events.Count == 6 && completed == 0, "stop during the 50ms third-Enter gap skips the third press and does not complete a cycle");
        }
    }
    private static async Task TestOptionSnapshot(bool enabled)
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            var options = MouseTests.Options(); options.LoopCount = 1; options.ExtraEnterForChat = enabled;
            keyboard.Up = delegate(int key) { options.ExtraEnterForChat = !enabled; };
            await new AmplificationEngine().RunAsync(options, new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard, null, null, cancel.Token);
            Check(keyboard.Events.Count == (enabled ? 8 : 6), "chat option is fixed at run start (initial=" + enabled + ")");
        }
    }
}
