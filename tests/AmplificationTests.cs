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
    private static async Task Cancelled(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(3000)) != task) throw new Exception("Sequence did not stop promptly.");
        try { await task; throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { }
    }
    internal static async Task Run()
    {
        await TestSequenceAndTiming();
        foreach (int key in new[] { 49, 13 }) await TestCancelHeldKey(key);
        await TestCancelHeldMouse();
        await TestPauseBeforeEnter();
        await TestWaitBeforeNumber();
        await TestKeyReleaseFailure();
        await TestCancelDuringLoopGap();
        await TestFinalCycleGap();
        await TestFocusBetweenEnters();
        await TestKeyFocusRace();
        await TestZeroGap();
        Console.WriteLine("PASS sequence: " + passed + " checks (simulated input only)");
    }
    private static async Task TestSequenceAndTiming()
    {
        using (var cancel = new CancellationTokenSource(3000))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            var log = new List<string>(); var gaps = new List<long>(); var watch = new Stopwatch();
            long lastUp = -1; int enterUps = 0;
            var options = MouseTests.Options(); options.LoopCount = 2; options.LoopGapMilliseconds = 120;
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
            string cycle = "down:49,up:49,left-down,left-up,down:13,up:13,down:13,up:13";
            Check(string.Join(",", log) == cycle + "," + cycle && completed == 2 && enterUps == 4, "each finite cycle sends 1, left click, Enter, Enter in order");
            Check(gaps.Count == 7 && gaps[0] >= 45 && gaps[1] >= 45 && gaps[2] >= 45 && gaps[3] >= 115 && gaps[4] >= 45 && gaps[5] >= 45 && gaps[6] >= 45,
                "action gaps are 50ms and the user loop gap starts after the second Enter release");
            Check(input.Moves[0] == new Point(600, 500) && input.Moves[1] == new Point(900, 400), "the full sequence follows the moved game window");
        }
    }
    private static async Task TestCancelHeldKey(int key)
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            keyboard.Down = delegate(int current) { if (current == key) cancel.Cancel(); };
            long completed = 0;
            await Cancelled(new AmplificationEngine().RunAsync(MouseTests.Options(), new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard, null,
                delegate(long count) { completed = count; }, cancel.Token));
            Check(keyboard.Log == (key == 49 ? "down:49,up:49" : "down:49,up:49,down:13,up:13") && completed == 0,
                "stop while holding " + key + " releases that key and skips the remaining sequence");
        }
    }
    private static async Task TestCancelHeldMouse()
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            input.OnDown = delegate { cancel.Cancel(); };
            await Cancelled(new AmplificationEngine().RunAsync(MouseTests.Options(), new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard, null, null, cancel.Token));
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
            bool paused = false; var options = MouseTests.Options(); options.LoopCount = 1;
            await new AmplificationEngine().RunAsync(options, mouse, keyboard,
                delegate(MouseReadiness state)
                {
                    if (!state.Ready)
                    {
                        Check(keyboard.Log == "down:49,up:49", "pause after click prevents Enter");
                        paused = true; mouse.IsPaused = false;
                    }
                }, null, cancel.Token);
            Check(paused && keyboard.Log == "down:49,up:49,down:13,up:13,down:13,up:13", "resume continues the pending Enter sequence without repeating 1 or click");
        }
    }
    private static async Task TestWaitBeforeNumber()
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow { Foreground = false }; var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            await Cancelled(new AmplificationEngine().RunAsync(MouseTests.Options(), new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard,
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
            try { await new AmplificationEngine().RunAsync(MouseTests.Options(), new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard, null, null, cancel.Token); }
            catch (InvalidOperationException error) { failed = error.ToString().Contains("key release failed"); }
            Check(failed && keyboard.Log == "down:49,up:49,up:49" && input.Log == "", "key release failures remain visible on cancellation and cleanup retries once");
        }
    }
    private static async Task TestCancelDuringLoopGap()
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            var options = MouseTests.Options(); options.LoopGapMilliseconds = 3600000;
            await Cancelled(new AmplificationEngine().RunAsync(options, new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard, null,
                delegate(long count) { cancel.CancelAfter(20); }, cancel.Token));
            Check(input.Ups == 1 && keyboard.Events.Count == 6, "stop interrupts a long user loop gap without starting the next number press");
        }
    }
    private static async Task TestFinalCycleGap()
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            var options = MouseTests.Options(); options.LoopCount = 1; options.LoopGapMilliseconds = 3600000;
            long completed = 0;
            await new AmplificationEngine().RunAsync(options, new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard, null,
                delegate(long count) { completed = count; }, cancel.Token);
            Check(completed == 1, "finite sequence ends after the last Enter and skips the next-loop delay");
        }
    }
    private static async Task TestFocusBetweenEnters()
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            var options = MouseTests.Options(); options.LoopCount = 1;
            int enters = 0; bool waited = false;
            keyboard.Up = delegate(int key) { if (key == 13 && ++enters == 1) window.Foreground = false; };
            await new AmplificationEngine().RunAsync(options, new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard,
                delegate(MouseReadiness state)
                {
                    if (!state.Ready)
                    {
                        Check(enters == 1 && keyboard.Events.Count == 4, "focus loss between Enters pauses before the second Enter");
                        waited = true; window.Foreground = true;
                    }
                }, null, cancel.Token);
            Check(waited && enters == 2 && input.Ups == 1, "returning to foreground completes the pending Enter without duplicating the click");
        }
    }
    private static async Task TestKeyFocusRace()
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            await Cancelled(new AmplificationEngine().RunAsync(MouseTests.Options(), new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard,
                delegate(MouseReadiness state) { if (state.Ready) window.Foreground = false; else cancel.Cancel(); }, null, cancel.Token));
            Check(keyboard.Log == "" && input.Log == "", "focus lost just before the number press sends no down or unowned up");
        }
    }
    private static async Task TestZeroGap()
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new MouseTests.FakeWindow(); var input = new MouseTests.FakeInput(window); var keyboard = new Keyboard();
            var options = MouseTests.Options(); options.LoopCount = 2; options.LoopGapMilliseconds = 0;
            await new AmplificationEngine().RunAsync(options, new ForegroundMouseTransport(MouseTests.Service(window), input), keyboard, null, null, cancel.Token);
            Check(input.Ups == 2 && keyboard.Events.Count == 12, "a user loop gap of zero milliseconds is accepted");
        }
    }
}
