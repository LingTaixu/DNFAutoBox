using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using AutoTools;

internal static class MacroTests
{
    private static int passed;
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private sealed class FakeTransport : IKeyTransport
    {
        internal readonly List<string> Events = new List<string>();
        internal Action<int> Down;
        internal Action<int> Up;
        public void KeyDown(int key) { Events.Add("down:" + key); if (Down != null) Down(key); }
        public void KeyUp(int key) { Events.Add("up:" + key); if (Up != null) Up(key); }
    }

    private sealed class FakeKeyboard : IForegroundKeyboardBackend
    {
        internal volatile bool Foreground;
        internal readonly List<string> Events = new List<string>();
        internal Action<int, bool> OnSend;
        internal Func<bool> OnFocusCheck;
        public bool IsTargetForeground(WindowTarget target) { return OnFocusCheck == null ? Foreground : OnFocusCheck(); }
        public void SendKey(int key, bool up)
        {
            lock (Events) Events.Add((up ? "up:" : "down:") + key);
            if (OnSend != null) OnSend(key, up);
        }
        internal string Log { get { lock (Events) return string.Join(",", Events); } }
    }

    private sealed class Receiver : Form
    {
        internal readonly List<string> KeyEvents = new List<string>();
        internal readonly List<uint> Parameters = new List<uint>();
        internal Receiver()
        {
            Text = "AutoTools test receiver";
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-10000, -10000);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x100 || message.Msg == 0x101)
            {
                KeyEvents.Add((message.Msg == 0x100 ? "down:" : "up:") + message.WParam.ToInt32());
                Parameters.Add(unchecked((uint)message.LParam.ToInt64()));
                return;
            }
            base.WndProc(ref message);
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Console.WriteLine("Logic test process: " + (IntPtr.Size * 8) + "-bit");
            TestSequence().GetAwaiter().GetResult();
            TestDoubleSpaceSequence().GetAwaiter().GetResult();
            TestCancelBetweenSpaces().GetAwaiter().GetResult();
            TestCancelSecondSpace().GetAwaiter().GetResult();
            TestDoubleSpaceFocusLoss().GetAwaiter().GetResult();
            TestModeSettings();
            TestCancelHeld().GetAwaiter().GetResult();
            TestCancelSpace().GetAwaiter().GetResult();
            TestCancelBetweenActions().GetAwaiter().GetResult();
            TestReleaseFailure().GetAwaiter().GetResult();
            TestInvalidOptions();
            TestForegroundPause().GetAwaiter().GetResult();
            TestFocusLoss().GetAwaiter().GetResult();
            TestPauseCancellation().GetAwaiter().GetResult();
            TestFocusRace().GetAwaiter().GetResult();
            TestManualPauseAndResume().GetAwaiter().GetResult();
            TestManualPauseWhileHeld().GetAwaiter().GetResult();
            TestFiniteLoops().GetAwaiter().GetResult();
            MouseTests.Run().GetAwaiter().GetResult();
            if (Array.IndexOf(args, "--interactive") >= 0)
            {
                TestNativeTargetValidation();
                TestUiPreview();
            }
            Console.WriteLine("PASS: " + passed + " checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static MacroOptions Options()
    {
        return new MacroOptions { VirtualKey = 0x31, HoldMilliseconds = 10, ActionGapMilliseconds = 0, LoopGapMilliseconds = 10 };
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        passed++;
        Console.WriteLine("PASS: " + name);
    }
    private static async Task ExpectCancellation(Task task)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(3000));
        if (finished != task) throw new Exception("Loop failed to stop within 3 seconds.");
        try { await task; throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { }
    }
    private static async Task TestSequence()
    {
        using (var cancel = new CancellationTokenSource(3000))
        {
            var fake = new FakeTransport();
            var options = Options();
            options.VirtualKey = 0x36;
            await ExpectCancellation(new MacroEngine().RunAsync(options, fake,
                delegate(long count) { if (count == 2) cancel.Cancel(); }, cancel.Token));
            Check(string.Join(",", fake.Events) == "down:54,up:54,down:32,up:32,down:54,up:54,down:32,up:32", "repeat number-row 6 then space twice");
        }
    }
    private static async Task TestDoubleSpaceSequence()
    {
        using (var cancel = new CancellationTokenSource(3000))
        {
            var fake = new FakeTransport();
            var options = Options();
            options.Mode = MacroMode.DoubleSpace;
            var gap = new Stopwatch();
            long measuredGap = 0;
            int spacePresses = 0;
            fake.Down = delegate(int key)
            {
                if (key == 32 && ++spacePresses == 2) measuredGap = gap.ElapsedMilliseconds;
            };
            fake.Up = delegate(int key) { if (key == 32 && spacePresses == 1) gap.Start(); };
            await ExpectCancellation(new MacroEngine().RunAsync(options, fake,
                delegate(long count) { if (count == 2) cancel.Cancel(); }, cancel.Token));
            Check(string.Join(",", fake.Events) == "down:49,up:49,down:32,up:32,down:32,up:32,down:49,up:49,down:32,up:32,down:32,up:32", "double-space mode repeats number key then two space taps");
            Check(measuredGap >= 45, "second space waits approximately 50ms after first space release");
        }
    }
    private static async Task TestCancelBetweenSpaces()
    {
        using (var cancel = new CancellationTokenSource(3000))
        {
            var fake = new FakeTransport();
            var options = Options();
            options.Mode = MacroMode.DoubleSpace;
            long completed = 0;
            fake.Up = delegate(int key) { if (key == 32) cancel.Cancel(); };
            await ExpectCancellation(new MacroEngine().RunAsync(options, fake,
                delegate(long count) { completed = count; }, cancel.Token));
            Check(string.Join(",", fake.Events) == "down:49,up:49,down:32,up:32" && completed == 0, "stop between spaces skips second space and does not complete the cycle");
        }
    }
    private static async Task TestCancelSecondSpace()
    {
        using (var cancel = new CancellationTokenSource(3000))
        {
            var fake = new FakeTransport();
            var options = Options();
            options.Mode = MacroMode.DoubleSpace;
            int spacePresses = 0;
            fake.Down = delegate(int key) { if (key == 32 && ++spacePresses == 2) cancel.Cancel(); };
            await ExpectCancellation(new MacroEngine().RunAsync(options, fake, null, cancel.Token));
            Check(string.Join(",", fake.Events) == "down:49,up:49,down:32,up:32,down:32,up:32", "stop while holding second space still releases it");
        }
    }
    private static async Task TestDoubleSpaceFocusLoss()
    {
        using (var cancel = new CancellationTokenSource(3000))
        {
            var keyboard = new FakeKeyboard { Foreground = true };
            var firstSpaceReleased = new TaskCompletionSource<bool>();
            int releases = 0;
            keyboard.OnSend = delegate(int key, bool up)
            {
                if (key == 32 && up && ++releases == 1)
                {
                    keyboard.Foreground = false;
                    firstSpaceReleased.SetResult(true);
                }
            };
            var options = Options();
            options.Mode = MacroMode.DoubleSpace;
            Task run = new MacroEngine().RunAsync(options, Transport(keyboard), delegate(long count) { cancel.Cancel(); }, cancel.Token);
            if (await Task.WhenAny(firstSpaceReleased.Task, Task.Delay(2000)) != firstSpaceReleased.Task)
                throw new Exception("First space did not finish in time.");
            await Task.Delay(100);
            Check(keyboard.Log == "down:49,up:49,down:32,up:32", "losing focus between spaces pauses before the second press");
            keyboard.Foreground = true;
            await ExpectCancellation(run);
            Check(keyboard.Log == "down:49,up:49,down:32,up:32,down:32,up:32", "returning to foreground completes the pending second space");
        }
    }
    private static void TestModeSettings()
    {
        var serializer = new XmlSerializer(typeof(AppSettings));
        using (var text = new StringReader("<AppSettings><VirtualKey>54</VirtualKey></AppSettings>"))
        {
            var old = (AppSettings)serializer.Deserialize(text);
            Check(old.Mode == (int)MacroMode.SeriaBox && old.VirtualKey == 54, "old settings keep the original single-space mode");
        }
        var settings = new AppSettings { Mode = (int)MacroMode.DoubleSpace, VirtualKey = 54 };
        using (var output = new StringWriter())
        {
            serializer.Serialize(output, settings);
            using (var input = new StringReader(output.ToString()))
            {
                var restored = (AppSettings)serializer.Deserialize(input);
                Check(restored.Mode == (int)MacroMode.DoubleSpace && restored.VirtualKey == 54, "selected double-space mode survives settings serialization");
            }
        }
    }
    private static async Task TestCancelHeld()
    {
        using (var cancel = new CancellationTokenSource(3000))
        {
            var fake = new FakeTransport();
            fake.Down = delegate(int key) { cancel.Cancel(); };
            var options = Options();
            options.HoldMilliseconds = 5000;
            var watch = Stopwatch.StartNew();
            await ExpectCancellation(new MacroEngine().RunAsync(options, fake, null, cancel.Token));
            Check(string.Join(",", fake.Events) == "down:49,up:49", "stop during key hold releases key without pressing space");
            Check(watch.ElapsedMilliseconds < 1000, "stop interrupts a five-second hold promptly");
        }
    }
    private static async Task TestCancelSpace()
    {
        using (var cancel = new CancellationTokenSource(3000))
        {
            var fake = new FakeTransport();
            fake.Down = delegate(int key) { if (key == 32) cancel.Cancel(); };
            await ExpectCancellation(new MacroEngine().RunAsync(Options(), fake, null, cancel.Token));
            Check(string.Join(",", fake.Events) == "down:49,up:49,down:32,up:32", "stop while holding space releases space");
        }
    }
    private static async Task TestCancelBetweenActions()
    {
        using (var cancel = new CancellationTokenSource(3000))
        {
            var fake = new FakeTransport();
            fake.Up = delegate(int key) { cancel.Cancel(); };
            var options = Options();
            options.ActionGapMilliseconds = 600000;
            await ExpectCancellation(new MacroEngine().RunAsync(options, fake, null, cancel.Token));
            Check(string.Join(",", fake.Events) == "down:49,up:49", "stop interrupts action gap and skips space");
        }
    }
    private static async Task TestReleaseFailure()
    {
        using (var cancel = new CancellationTokenSource(3000))
        {
            var fake = new FakeTransport();
            fake.Down = delegate(int key) { cancel.Cancel(); };
            fake.Up = delegate(int key) { throw new InvalidOperationException("receiver closed"); };
            try
            {
                await new MacroEngine().RunAsync(Options(), fake, null, cancel.Token);
                throw new Exception("Expected failed release to be reported.");
            }
            catch (InvalidOperationException error)
            {
                Check(error.InnerException != null && error.InnerException.Message == "receiver closed", "release failure remains visible during cancellation");
            }
        }
    }
    private static void TestInvalidOptions()
    {
        var options = Options();
        options.LoopGapMilliseconds = 0;
        try { options.Validate(); throw new Exception("Expected invalid gap to be rejected."); }
        catch (ArgumentOutOfRangeException) { Check(true, "reject invalid timing"); }
    }
    private static void TestNativeTargetValidation()
    {
        using (var receiver = new Receiver())
        {
            IntPtr handle = receiver.Handle;
            uint pid;
            GetWindowThreadProcessId(handle, out pid);
            var wrong = new ForegroundKeyTransport(new WindowTarget { Handle = handle, ProcessId = pid + 1 });
            try { wrong.KeyDown(49); throw new Exception("Expected process mismatch to be rejected."); }
            catch (InvalidOperationException) { Check(true, "reject a window belonging to a different process"); }
            var transport = new ForegroundKeyTransport(new WindowTarget { Handle = handle, ProcessId = pid });
            receiver.Dispose();
            try { transport.KeyDown(49); throw new Exception("Expected closed window to be rejected."); }
            catch (InvalidOperationException) { Check(true, "stop sending after target closes"); }
        }
    }
    private static ForegroundKeyTransport Transport(FakeKeyboard keyboard)
    {
        return new ForegroundKeyTransport(new WindowTarget { Handle = new IntPtr(1), ProcessId = 1 }, keyboard);
    }
    private static async Task TestForegroundPause()
    {
        using (var cancel = new CancellationTokenSource(3000))
        {
            var keyboard = new FakeKeyboard();
            Task run = new MacroEngine().RunAsync(Options(), Transport(keyboard), delegate(long count) { cancel.Cancel(); }, cancel.Token);
            await Task.Delay(100);
            Check(keyboard.Log == "", "no presses while another app is foreground");
            keyboard.Foreground = true;
            await ExpectCancellation(run);
            Check(keyboard.Log == "down:49,up:49,down:32,up:32", "returning to target resumes number then space");
        }
    }
    private static async Task TestFocusLoss()
    {
        using (var cancel = new CancellationTokenSource(3000))
        {
            var keyboard = new FakeKeyboard { Foreground = true };
            keyboard.OnSend = delegate(int key, bool up) { if (!up) keyboard.Foreground = false; };
            Task run = new MacroEngine().RunAsync(Options(), Transport(keyboard), null, cancel.Token);
            await Task.Delay(100);
            Check(keyboard.Log == "down:49,up:49", "focus loss releases held key and pauses before space");
            cancel.Cancel();
            await ExpectCancellation(run);
        }
    }
    private static async Task TestPauseCancellation()
    {
        using (var cancel = new CancellationTokenSource())
        {
            var keyboard = new FakeKeyboard();
            Task run = new MacroEngine().RunAsync(Options(), Transport(keyboard), null, cancel.Token);
            var watch = Stopwatch.StartNew();
            cancel.Cancel();
            await ExpectCancellation(run);
            Check(watch.ElapsedMilliseconds < 1000 && keyboard.Log == "", "stop interrupts foreground wait without sending input");
        }
    }
    private static async Task TestFocusRace()
    {
        using (var cancel = new CancellationTokenSource(3000))
        {
            var keyboard = new FakeKeyboard();
            int checks = 0;
            keyboard.OnFocusCheck = delegate
            {
                if (++checks == 1) return true;
                cancel.Cancel();
                return false;
            };
            await ExpectCancellation(new MacroEngine().RunAsync(Options(), Transport(keyboard), null, cancel.Token));
            Check(keyboard.Log == "", "recheck immediately before down prevents sending after a focus change");
        }
    }
    private static async Task TestManualPauseAndResume()
    {
        using (var cancel = new CancellationTokenSource(3000))
        {
            var keyboard = new FakeKeyboard { Foreground = true };
            var transport = new PausableKeyTransport(Transport(keyboard)) { IsPaused = true };
            Task run = new MacroEngine().RunAsync(Options(), transport, delegate(long count) { cancel.Cancel(); }, cancel.Token);
            keyboard.Foreground = false;
            await Task.Delay(60);
            keyboard.Foreground = true;
            await Task.Delay(60);
            Check(keyboard.Log == "", "manual pause remains paused after switching away and back");
            transport.IsPaused = false;
            await ExpectCancellation(run);
            Check(keyboard.Log == "down:49,up:49,down:32,up:32", "manual resume continues the key sequence");
        }
    }
    private static async Task TestManualPauseWhileHeld()
    {
        using (var cancel = new CancellationTokenSource(3000))
        {
            var fake = new FakeTransport();
            var transport = new PausableKeyTransport(fake);
            fake.Down = delegate(int key) { transport.IsPaused = true; };
            Task run = new MacroEngine().RunAsync(Options(), transport, null, cancel.Token);
            await Task.Delay(100);
            Check(string.Join(",", fake.Events) == "down:49,up:49", "pausing during a hold releases the key and prevents the next press");
            cancel.Cancel();
            await ExpectCancellation(run);
            Check(string.Join(",", fake.Events) == "down:49,up:49", "stop while manually paused does not send more input");
        }
    }
    private static void TestUiPreview()
    {
        Application.EnableVisualStyles();
        using (var form = new MainForm(false))
        {
            form.Show();
            Application.DoEvents();
            form.PerformLayout();
            Check(form.Controls.Count > 0 && form.Controls[0].Visible, "main window content is visible");
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-preview.png"));
            }
            var action = FindCombo(form, "操作按键 · 主键盘 1–6");
            if (action == null) throw new Exception("Action key selector missing.");
            action.Focus();
            // Select while closed: an open native list can also follow the real mouse pointer.
            SendMessage(action.Handle, 0x0100, new IntPtr((int)Keys.Down), IntPtr.Zero);
            SendMessage(action.Handle, 0x0101, new IntPtr((int)Keys.Down), IntPtr.Zero);
            Application.DoEvents();
            bool actionSelectedByKeyboard = action.Text == "2";
            action.DroppedDown = true;
            Application.DoEvents();
            SendMessage(action.Handle, 0x0100, new IntPtr((int)Keys.Enter), IntPtr.Zero);
            SendMessage(action.Handle, 0x0101, new IntPtr((int)Keys.Enter), IntPtr.Zero);
            Application.DoEvents();
            Check(actionSelectedByKeyboard && !action.DroppedDown, "pixel dropdown supports native arrow-key selection and Enter confirmation");
            var mode = FindCombo(form, "模式选择");
            if (mode == null) throw new Exception("Mode selector missing.");
            mode.Focus();
            SendMessage(mode.Handle, 0x0100, new IntPtr((int)Keys.Down), IntPtr.Zero);
            SendMessage(mode.Handle, 0x0101, new IntPtr((int)Keys.Down), IntPtr.Zero);
            Application.DoEvents();
            bool modeSelectedByKeyboard = mode.Text == "双击空格模式";
            mode.DroppedDown = true;
            Application.DoEvents();
            SendMessage(mode.Handle, 0x0100, new IntPtr((int)Keys.Enter), IntPtr.Zero);
            SendMessage(mode.Handle, 0x0101, new IntPtr((int)Keys.Enter), IntPtr.Zero);
            Application.DoEvents();
            Check(modeSelectedByKeyboard && !mode.DroppedDown, "mode dropdown switches from Seria box to double-space using the keyboard");
            // Close follows normal disposal so the tray icon and timer are released too.
            form.Close();
            Check(true, "main window constructs and renders successfully");
        }
    }
    private static async Task TestFiniteLoops()
    {
        foreach (MacroMode mode in new[] { MacroMode.SeriaBox, MacroMode.DoubleSpace })
        {
            foreach (int limit in new[] { 1, 3 })
            {
                var options = Options(); options.Mode = mode; options.LoopCount = limit;
                if (limit == 1) options.LoopGapMilliseconds = 3600000;
                var fake = new FakeTransport();
                long completed = 0;
                using (var cancel = new CancellationTokenSource(2500))
                {
                    await new MacroEngine().RunAsync(options, fake, delegate(long count) { completed = count; }, cancel.Token);
                    Check(completed == limit && fake.Events.Count == limit * (mode == MacroMode.DoubleSpace ? 6 : 4),
                        mode + " completes exactly " + limit + " cycles and skips the final loop gap");
                }
            }
        }
    }
    private static ComboBox FindCombo(Control parent, string name)
    {
        foreach (Control child in parent.Controls)
        {
            var combo = child as ComboBox;
            if (combo != null && combo.AccessibleName == name) return combo;
            var nested = FindCombo(child, name);
            if (nested != null) return nested;
        }
        return null;
    }
}
