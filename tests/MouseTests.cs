using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using AutoTools;

// All mouse tests use these fakes. No native window/input APIs or real forms are used.
internal static class MouseTests
{
    private static int passed;
    internal sealed class FakeWindow : IMouseWindowBackend
    {
        internal bool Valid = true, Foreground = true, Covered, DpiSupported = true;
        internal Size Size = new Size(1280, 720);
        internal Point Origin = new Point(100, 200), Cursor = new Point(600, 500);
        internal int Conversions;
        internal Action OnVisible;
        public void ValidateTarget(WindowTarget target) { if (!Valid) throw new MouseTargetInvalidException("window invalid"); }
        public void EnsureDpiSupported(WindowTarget target) { if (!DpiSupported) throw new MouseDpiException("DPI unsupported"); }
        public bool IsForeground(WindowTarget target) { return Foreground; }
        public Size GetClientSize(WindowTarget target) { return Size; }
        public Point GetCursorPosition() { return Cursor; }
        public Point ScreenToClient(WindowTarget target, Point screen) { return new Point(screen.X - Origin.X, screen.Y - Origin.Y); }
        public Point ClientToScreen(WindowTarget target, Point client) { Conversions++; return new Point(client.X + Origin.X, client.Y + Origin.Y); }
        public bool IsPointVisible(WindowTarget target, Point screen) { if (OnVisible != null) OnVisible(); return !Covered; }
        public Size ApplyClientSize(WindowTarget target, Size expected) { return Size; } // A game may refuse a resize.
    }
    internal sealed class FakeInput : IMouseInputBackend
    {
        internal readonly List<string> Events = new List<string>();
        internal readonly List<Point> Moves = new List<Point>();
        internal readonly FakeWindow Window;
        internal bool Busy;
        internal Action OnMove, OnDown, OnUp;
        internal int Ups, FailUps;
        internal bool FailMove, FailDown;
        internal FakeInput(FakeWindow window) { Window = window; }
        public bool IsLeftButtonDown { get { return Busy; } }
        public void Move(Point screen)
        {
            Events.Add("move"); Moves.Add(screen);
            if (FailMove) throw new InvalidOperationException("move failed");
            Window.Cursor = screen;
            if (OnMove != null) OnMove();
        }
        public void SendLeftButton(bool up)
        {
            Events.Add(up ? "up" : "down");
            if (up)
            {
                Ups++;
                if (Ups <= FailUps) throw new InvalidOperationException("release failed");
                if (OnUp != null) OnUp();
            }
            else
            {
                if (OnDown != null) OnDown();
                if (FailDown) throw new InvalidOperationException("down failed");
            }
        }
        internal string Log { get { return string.Join(",", Events); } }
    }
    internal static MouseOptions Options()
    {
        return new MouseOptions { PointConfigured = true, ClientPoint = new Point(500, 300),
            CapturedSize = new Size(1280, 720), ExpectedSize = new Size(1280, 720), LoopGapMilliseconds = 10 };
    }
    internal static MouseTargetService Service(FakeWindow window)
    {
        return new MouseTargetService(new WindowTarget { Handle = new IntPtr(1), ProcessId = 1 }, window);
    }
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL mouse: " + name);
        passed++;
        Console.WriteLine("PASS mouse: " + name);
    }
    private static async Task Cancelled(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(3000)) != task) throw new Exception("Mouse loop did not stop.");
        try { await task; throw new Exception("Expected mouse cancellation."); }
        catch (OperationCanceledException) { }
    }
    private static async Task<Exception> Failed(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(3000)) != task) throw new Exception("Mouse loop did not fail promptly.");
        try { await task; throw new Exception("Expected mouse failure."); }
        catch (InvalidOperationException error) { return error; }
    }
    internal static async Task Run()
    {
        TestSettings();
        TestAbiAndCoordinates();
        TestCaptureAndSizing();
        await TestInvalidOptions();
        await TestWindowMovement();
        foreach (string reason in new[] { "foreground", "size", "covered", "pause", "busy" }) await TestWaitAndResume(reason);
        foreach (string change in new[] { "foreground", "pause", "cancel", "move", "cursor", "covered", "size", "invalid", "dpi" }) await TestAfterMove(change);
        foreach (string change in new[] { "foreground", "pause", "cancel", "size", "covered", "invalid", "dpi" }) await TestWhileHeld(change);
        await TestSendFailures();
        await TestInvalidTarget();
        await TestFiniteLoops();
        await AmplificationTests.Run();
        Console.WriteLine("PASS mouse: " + passed + " checks (simulated input only)");
    }
    private static void TestSettings()
    {
        var serializer = new XmlSerializer(typeof(AppSettings));
        using (var input = new StringReader("<AppSettings><Mode>1</Mode><VirtualKey>54</VirtualKey></AppSettings>"))
        {
            var old = (AppSettings)serializer.Deserialize(input);
            Check(old.Mode == 1 && old.VirtualKey == 54 && !old.MousePointConfigured && old.MouseCaptureHotkey == 0x76 && old.MouseLoopGapMilliseconds == 500 && old.LoopForever,
                "old config preserves keyboard mode and initializes mouse defaults");
        }
        var settings = new AppSettings { Mode = 2, MousePointConfigured = true, MouseClientX = 0, MouseClientY = 0,
            MouseCapturedWidth = 1280, MouseCapturedHeight = 720, MouseExpectedWidth = 1280, MouseExpectedHeight = 720,
            MouseCaptureHotkey = 0x75, MouseLoopGapMilliseconds = 700, MousePointProcessName = "game", LoopForever = false, LoopCount = 12 };
        using (var output = new StringWriter())
        {
            serializer.Serialize(output, settings);
            using (var input = new StringReader(output.ToString()))
            {
                var restored = (AppSettings)serializer.Deserialize(input);
                Check(restored.Mode == 2 && restored.MousePointConfigured && restored.MouseClientX == 0 && restored.MouseClientY == 0 &&
                    restored.MouseCapturedWidth == 1280 && restored.MouseCapturedHeight == 720 && restored.MouseExpectedWidth == 1280 &&
                    restored.MouseExpectedHeight == 720 && restored.MouseCaptureHotkey == 0x75 && restored.MouseLoopGapMilliseconds == 700 && restored.MousePointProcessName == "game" && !restored.LoopForever && restored.LoopCount == 12,
                    "all mouse settings roundtrip, including configured (0,0)");
            }
        }
        Check((int)MacroMode.SeriaBox == 0 && (int)MacroMode.DoubleSpace == 1 && (int)MacroMode.Amplification == 2, "existing enum values remain stable");
        Check(HotkeyRules.AreDistinct(0x77, 0x78, 0x79, 0x76) && !HotkeyRules.AreDistinct(0x77, 0x78, 0x79, 0x77) &&
            !HotkeyRules.AreDistinct(0x77, 0x78, 0x79, 0x78) && !HotkeyRules.AreDistinct(0x77, 0x78, 0x79, 0x79), "capture hotkey conflicts with all three controls are rejected");
    }
    private static void TestAbiAndCoordinates()
    {
        Assembly assembly = typeof(MouseEngine).Assembly;
        Type input = assembly.GetType("AutoTools.NativeMethods+Input", true);
        Type mouse = assembly.GetType("AutoTools.NativeMethods+MouseInput", true);
        Type info = assembly.GetType("AutoTools.NativeMethods+WindowInfo", true);
        Check(Marshal.SizeOf(input) == (IntPtr.Size == 8 ? 40 : 28) && Marshal.SizeOf(mouse) == (IntPtr.Size == 8 ? 32 : 24) &&
            Marshal.OffsetOf(input, "Data").ToInt32() == (IntPtr.Size == 8 ? 8 : 4) && Marshal.SizeOf(info) == 60, "native INPUT and WINDOWINFO layouts match current process ABI");
        foreach (int width in new[] { 1, 1920, 3840, 7680 })
        {
            foreach (int offset in new[] { 0, width / 2, width - 1 })
            {
                int normalized = NativeMouseInputBackend.NormalizeAxis(-1920 + offset, -1920, width);
                Check(normalized >= 0 && normalized <= 65535 && (long)normalized * width / 65536 == offset,
                    "absolute movement maps to pixel center on negative-origin desktop: " + width + "/" + offset);
            }
        }
        bool rejected = false;
        try { NativeMouseInputBackend.NormalizeAxis(1920, -1920, 3840); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "virtual desktop upper bound is exclusive");
    }
    private static void TestCaptureAndSizing()
    {
        var window = new FakeWindow();
        var service = Service(window);
        Check(service.Capture(window.Size) == new Point(500, 300), "capture converts screen to client coordinates");
        window.Cursor = window.Origin;
        Check(service.Capture(window.Size) == Point.Empty, "client origin (0,0) is a valid capture");
        foreach (string reason in new[] { "foreground", "outside", "covered", "size", "invalid", "dpi" })
        {
            window = new FakeWindow(); service = Service(window);
            if (reason == "foreground") window.Foreground = false;
            if (reason == "outside") window.Cursor = new Point(1380, 200);
            if (reason == "covered") window.Covered = true;
            if (reason == "size") window.Size = new Size(800, 600);
            if (reason == "invalid") window.Valid = false;
            if (reason == "dpi") window.DpiSupported = false;
            bool rejected = false;
            try { service.Capture(new Size(1280, 720)); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "capture rejects " + reason);
        }
        window = new FakeWindow(); service = Service(window);
        window.OnVisible = delegate { window.Foreground = false; };
        bool lostFocus = false;
        try { service.Capture(window.Size); } catch (InvalidOperationException) { lostFocus = true; }
        Check(lostFocus, "capture rechecks focus before returning a point");
        window = new FakeWindow(); service = Service(window);
        Check(service.ApplySize(new Size(800, 600)) == new Size(1280, 720), "resize reports actual size when a game refuses the request");
    }
    private static async Task TestInvalidOptions()
    {
        foreach (string reason in new[] { "unconfigured", "negative", "rightedge", "bottomedge", "capturedsize", "expectedsize", "gap", "loopcount" })
        {
            var options = Options();
            if (reason == "unconfigured") options.PointConfigured = false;
            if (reason == "negative") options.ClientPoint = new Point(-1, 0);
            if (reason == "rightedge") options.ClientPoint = new Point(1280, 0);
            if (reason == "bottomedge") options.ClientPoint = new Point(0, 720);
            if (reason == "capturedsize") options.CapturedSize = new Size(800, 600);
            if (reason == "expectedsize") options.ExpectedSize = Size.Empty;
            if (reason == "gap") options.LoopGapMilliseconds = -1;
            if (reason == "loopcount") options.LoopCount = -1;
            var window = new FakeWindow(); var input = new FakeInput(window);
            bool rejected = false;
            try { await new MouseEngine().RunAsync(options, new ForegroundMouseTransport(Service(window), input), null, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && input.Log == "", "invalid " + reason + " sends no input");
        }
    }
    private static async Task TestWindowMovement()
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new FakeWindow(); var input = new FakeInput(window);
            input.OnUp = delegate { if (input.Ups == 1) window.Origin = new Point(400, 100); else cancel.Cancel(); };
            await Cancelled(new MouseEngine().RunAsync(Options(), new ForegroundMouseTransport(Service(window), input), null, cancel.Token));
            Check(input.Log == "move,down,up,move,down,up" && input.Moves[0] == new Point(600, 500) && input.Moves[1] == new Point(900, 400) && window.Conversions >= 4,
                "moving the game changes screen coordinates on the next cycle");
        }
    }
    private static async Task TestWaitAndResume(string reason)
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new FakeWindow(); var input = new FakeInput(window);
            var transport = new ForegroundMouseTransport(Service(window), input);
            if (reason == "foreground") window.Foreground = false;
            if (reason == "size") window.Size = new Size(800, 600);
            if (reason == "covered") window.Covered = true;
            if (reason == "pause") transport.IsPaused = true;
            if (reason == "busy") input.Busy = true;
            bool sawWait = false;
            input.OnUp = delegate { cancel.Cancel(); };
            await Cancelled(new MouseEngine().RunAsync(Options(), transport, delegate(MouseReadiness state)
            {
                // Busy is checked by Move; all other cases are reported by the initial readiness check.
                if (!state.Ready)
                {
                    Check(input.Log == "", reason + " prevents all mouse events while waiting");
                    sawWait = true;
                    window.Foreground = true; window.Size = new Size(1280, 720); window.Covered = false; transport.IsPaused = false; input.Busy = false;
                }
            }, cancel.Token));
            Check(sawWait && input.Log == "move,down,up", reason + " resumes after conditions recover");
        }
    }
    private static async Task TestAfterMove(string change)
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new FakeWindow(); var input = new FakeInput(window);
            var transport = new ForegroundMouseTransport(Service(window), input);
            input.OnMove = delegate
            {
                if (change == "foreground") window.Foreground = false;
                if (change == "pause") transport.IsPaused = true;
                if (change == "cancel") cancel.Cancel();
                if (change == "move") window.Origin = new Point(300, 400);
                if (change == "cursor") window.Cursor = new Point(10, 10);
                if (change == "covered") window.Covered = true;
                if (change == "size") window.Size = new Size(800, 600);
                if (change == "invalid") window.Valid = false;
                if (change == "dpi") window.DpiSupported = false;
            };
            Task run = new MouseEngine().RunAsync(Options(), transport, delegate(MouseReadiness state) { if (!state.Ready) cancel.Cancel(); }, cancel.Token);
            if (change == "invalid" || change == "dpi") await Failed(run); else await Cancelled(run);
            Check(input.Log == "move", "after movement, " + change + " prevents down and avoids releasing an unowned button");
        }
    }
    private static async Task TestWhileHeld(string change)
    {
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new FakeWindow(); var input = new FakeInput(window);
            var transport = new ForegroundMouseTransport(Service(window), input);
            input.OnDown = delegate
            {
                if (change == "foreground") window.Foreground = false;
                if (change == "pause") transport.IsPaused = true;
                if (change == "cancel") cancel.Cancel();
                if (change == "size") window.Size = new Size(800, 600);
                if (change == "covered") window.Covered = true;
                if (change == "invalid") window.Valid = false;
                if (change == "dpi") window.DpiSupported = false;
            };
            var options = Options(); options.HoldMilliseconds = 5000;
            input.OnUp = delegate { cancel.Cancel(); };
            Task run = new MouseEngine().RunAsync(options, transport, null, cancel.Token);
            if (change == "invalid" || change == "dpi") await Failed(run); else await Cancelled(run);
            Check(input.Log == "move,down,up", "while held, " + change + " releases promptly and prevents further presses");
        }
    }
    private static async Task TestSendFailures()
    {
        var window = new FakeWindow(); var input = new FakeInput(window) { FailMove = true };
        await Failed(new MouseEngine().RunAsync(Options(), new ForegroundMouseTransport(Service(window), input), null, CancellationToken.None));
        Check(input.Log == "move", "failed move stops before down and sends no unowned release");
        window = new FakeWindow(); input = new FakeInput(window) { FailDown = true };
        await Failed(new MouseEngine().RunAsync(Options(), new ForegroundMouseTransport(Service(window), input), null, CancellationToken.None));
        Check(input.Log == "move,down,up", "uncertain failed down still attempts release");
        foreach (int failures in new[] { 1, 2 })
        {
            using (var cancel = new CancellationTokenSource(2500))
            {
                window = new FakeWindow(); input = new FakeInput(window) { FailUps = failures };
                input.OnDown = delegate { cancel.Cancel(); };
                Exception error = await Failed(new MouseEngine().RunAsync(Options(), new ForegroundMouseTransport(Service(window), input), null, cancel.Token));
                Check(input.Log == "move,down,up,up" && error.ToString().Contains("release failed") && ContainsCancellation(error),
                    "release failure is visible during stop and cleanup retries (failures=" + failures + ")");
            }
        }
        window = new FakeWindow(); input = new FakeInput(window) { FailDown = true, FailUps = 2 };
        Exception both = await Failed(new MouseEngine().RunAsync(Options(), new ForegroundMouseTransport(Service(window), input), null, CancellationToken.None));
        Check(both.ToString().Contains("down failed") && both.ToString().Contains("release failed"), "down and release failures both remain visible");
    }
    private static bool ContainsCancellation(Exception error)
    {
        if (error is OperationCanceledException) return true;
        var aggregate = error as AggregateException;
        if (aggregate != null) foreach (var inner in aggregate.InnerExceptions) if (ContainsCancellation(inner)) return true;
        return error.InnerException != null && ContainsCancellation(error.InnerException);
    }
    private static async Task TestInvalidTarget()
    {
        var window = new FakeWindow { Valid = false }; var input = new FakeInput(window);
        var service = Service(window);
        await Failed(new MouseEngine().RunAsync(Options(), new ForegroundMouseTransport(service, input), null, CancellationToken.None));
        window.Valid = true;
        bool latched = false;
        try { service.CheckSize(); } catch (MouseTargetInvalidException) { latched = true; }
        Check(latched && input.Log == "", "an invalid target stays invalid until explicitly reselected");
        window = new FakeWindow { DpiSupported = false }; input = new FakeInput(window);
        await Failed(new MouseEngine().RunAsync(Options(), new ForegroundMouseTransport(Service(window), input), null, CancellationToken.None));
        Check(input.Log == "", "unsupported DPI blocks every mouse event");
        using (var cancel = new CancellationTokenSource(2500))
        {
            window = new FakeWindow(); input = new FakeInput(window);
            window.OnVisible = delegate { window.Foreground = false; };
            await Cancelled(new MouseEngine().RunAsync(Options(), new ForegroundMouseTransport(Service(window), input),
                delegate(MouseReadiness state) { if (!state.Ready) cancel.Cancel(); }, cancel.Token));
            Check(input.Log == "", "focus lost during the final visibility check sends no mouse events");
        }
    }
    private static async Task TestFiniteLoops()
    {
        foreach (int limit in new[] { 1, 3 })
        {
            using (var cancel = new CancellationTokenSource(2500))
            {
                var window = new FakeWindow(); var input = new FakeInput(window);
                var options = Options(); options.LoopCount = limit; options.ClientPoint = Point.Empty;
                if (limit == 1) options.LoopGapMilliseconds = 3600000;
                long completed = 0;
                await new MouseEngine().RunAsync(options, new ForegroundMouseTransport(Service(window), input), null,
                    delegate(long count) { completed = count; }, cancel.Token);
                Check(completed == limit && input.Ups == limit && input.Events.Count == limit * 3 && input.Moves[0] == window.Origin,
                    "configured (0,0) finishes exactly " + limit + " clicks and skips the final gap");
            }
        }
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new FakeWindow(); var input = new FakeInput(window);
            var options = Options(); options.LoopCount = 1;
            long completed = 0;
            input.OnMove = delegate { if (input.Moves.Count == 1) window.Foreground = false; };
            await new MouseEngine().RunAsync(options, new ForegroundMouseTransport(Service(window), input),
                delegate(MouseReadiness state) { if (!state.Ready) window.Foreground = true; },
                delegate(long count) { completed = count; }, cancel.Token);
            Check(completed == 1 && input.Log == "move,move,down,up", "waiting after movement does not consume a finite click count");
        }
        using (var cancel = new CancellationTokenSource(2500))
        {
            var window = new FakeWindow(); var input = new FakeInput(window);
            var options = Options(); options.LoopCount = 2;
            input.OnUp = delegate { options.LoopCount = 1; options.ClientPoint = new Point(-1, -1); };
            await new MouseEngine().RunAsync(options, new ForegroundMouseTransport(Service(window), input), null, cancel.Token);
            Check(input.Ups == 2, "running mouse options are snapshotted, including the finite limit");
        }
    }
}
