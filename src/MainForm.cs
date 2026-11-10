using System;
using System.Drawing;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AutoTools
{
    internal sealed class KeyChoice
    {
        public int Code;
        public string Name;
        public KeyChoice(int code, string name) { Code = code; Name = name; }
        public override string ToString() { return Name; }
    }

    internal sealed class ModeChoice
    {
        internal MacroMode Mode;
        private readonly string name;
        internal ModeChoice(MacroMode mode, string name) { Mode = mode; this.name = name; }
        public override string ToString() { return name; }
    }

    public sealed partial class MainForm : Form
    {
        private readonly PixelComboBox windows = MakeCombo(false);
        private readonly PixelComboBox actionKey = MakeCombo(true);
        private readonly PixelComboBox mode = MakeCombo(false);
        private readonly PixelComboBox toggleKey = MakeCombo(true);
        private readonly PixelComboBox stopKey = MakeCombo(true);
        private readonly PixelComboBox pauseKey = MakeCombo(true);
        private readonly CheckBox loopForever = new CheckBox { Text = "一直循环", AutoSize = false, Width = 100, Height = 32 };
        private readonly NumericUpDown loopCount = MakeNumber(1, 1000000000);
        private long completedCycles;
        private int runningLoopLimit;
        private readonly PixelButton refresh = new PixelButton { Text = "刷新窗口", Width = 100, Height = 40 };
        private readonly PixelButton start = new PixelButton { Text = "开始循环", Kind = PixelButtonKind.Primary };
        private readonly PixelButton stop = new PixelButton { Text = "停止", Kind = PixelButtonKind.Stop, Enabled = false };
        private readonly PixelButton pause = new PixelButton { Text = "暂停", Kind = PixelButtonKind.Pause, Enabled = false };
        private readonly Label status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        private readonly ToolTip statusTip = new ToolTip();
        private readonly PixelStatusPanel statusHost = new PixelStatusPanel();
        private readonly NotifyIcon tray;
        private readonly System.Windows.Forms.Timer statusTimer;
        private readonly AppSettings settings;
        private readonly bool enableGlobalHotkeys;
        private CancellationTokenSource cancellation;
        private Task runningTask;
        private PausableKeyTransport pauseTransport;
        private bool closing;
        private bool closeAfterStop;
        private bool initializing = true;

        public MainForm() : this(true) { }
        public MainForm(bool enableHotkeys)
        {
            enableGlobalHotkeys = enableHotkeys;
            settings = enableHotkeys ? AppSettings.Load() : new AppSettings();
            using (var identity = WindowsIdentity.GetCurrent())
                Text = "auto-tools · 前台输入循环 · " + (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ? "管理员" : "未提权");
            ClientSize = new Size(760, 572);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = PixelTheme.BodyFont;
            BackColor = PixelTheme.Canvas;
            ForeColor = PixelTheme.Ink;
            DoubleBuffered = true;
            Icon = PixelTheme.AppIcon;
            FillKeys(actionKey, false);
            FillKeys(toggleKey, true);
            FillKeys(stopKey, true);
            FillKeys(pauseKey, true);
            FillKeys(captureKey, true);
            SelectKey(actionKey, settings.VirtualKey, 0x31);
            SelectKey(toggleKey, settings.ToggleHotkey, 0x77);
            SelectKey(stopKey, settings.StopHotkey, 0x78);
            SelectKey(pauseKey, settings.PauseHotkey, 0x79);
            SelectKey(captureKey, settings.MouseCaptureHotkey, 0x76);
            mode.Items.Add(new ModeChoice(MacroMode.SeriaBox, "赛利亚盒子模式"));
            mode.Items.Add(new ModeChoice(MacroMode.DoubleSpace, "双击空格模式"));
            mode.Items.Add(new ModeChoice(MacroMode.Amplification, "赌增幅强化"));
            mode.SelectedIndex = settings.Mode >= 0 && settings.Mode <= 2 ? settings.Mode : 0;
            loopForever.Checked = settings.LoopForever;
            loopCount.Value = Clamp(settings.LoopCount, 1, 1000000000, 1);
            loopCount.Enabled = !loopForever.Checked;
            loopForever.CheckedChanged += delegate { loopCount.Enabled = !loopForever.Checked && cancellation == null; };
            BuildLayout();
            InitializeMouseControls();

            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("显示窗口", null, delegate { RestoreWindow(); });
            trayMenu.Items.Add("开始 / 停止", null, delegate { Toggle(); });
            trayMenu.Items.Add("停止", null, delegate { RequestStop(); });
            trayMenu.Items.Add("暂停 / 继续", null, delegate { TogglePause(); });
            trayMenu.Items.Add("退出", null, delegate { Close(); });
            tray = new NotifyIcon { Icon = Icon, Text = "auto-tools · 已停止", ContextMenuStrip = trayMenu, Visible = true };
            tray.DoubleClick += delegate { RestoreWindow(); };
            statusTimer = new System.Windows.Forms.Timer { Interval = 150 };
            statusTimer.Tick += delegate
            {
                if (cancellation == null || cancellation.IsCancellationRequested) return;
                if (mouseTransport != null)
                {
                    var state = mouseState;
                    if (state != null) SetStatus(state.Message + LoopProgress(), state.State == MouseRunState.SizeMismatch || state.State == MouseRunState.DpiUnsupported);
                    tray.Text = "auto-tools · " + (state == null ? "鼠标循环启动中" : state.Ready ? "鼠标循环中" : "鼠标循环已暂停");
                    return;
                }
                if (pauseTransport != null && pauseTransport.IsPaused)
                {
                    SetStatus("已手动暂停 · 按 " + pauseKey.Text + " 或点击继续恢复" + LoopProgress(), false);
                    tray.Text = "auto-tools · 已手动暂停";
                    return;
                }
                var selected = windows.SelectedItem as WindowTarget;
                bool foreground = selected != null && NativeMethods.GetForegroundWindow() == selected.Handle;
                string sequence = actionKey.Text + (SelectedMode == MacroMode.DoubleSpace ? " → 空格 ×2" : " → 空格");
                SetStatus((foreground ? "循环中 · " + mode.Text + " · " + sequence : "已暂停 · 请切回选中的游戏窗口") + LoopProgress(), false);
                tray.Text = foreground ? "auto-tools · 循环中" : "auto-tools · 已暂停，等待游戏前台";
            };
            statusTimer.Start();
            refresh.Click += delegate { RefreshWindows(); };
            start.Click += delegate { StartLoop(); };
            stop.Click += delegate { RequestStop(); };
            pause.Click += delegate { TogglePause(); };
            toggleKey.SelectedIndexChanged += delegate { HotkeysChanged(); };
            stopKey.SelectedIndexChanged += delegate { HotkeysChanged(); };
            pauseKey.SelectedIndexChanged += delegate { HotkeysChanged(); };
            captureKey.SelectedIndexChanged += delegate { HotkeysChanged(); };
            mode.SelectedIndexChanged += delegate { UpdateModeView(); HotkeysChanged(); };
            windows.SelectedIndexChanged += delegate { MouseTargetChanged(); };
            Shown += delegate { UpdateModeView(); if (enableGlobalHotkeys) RegisterHotkeys(); };
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    Hide();
                    ShowInTaskbar = false;
                }
            };
            FormClosing += OnClosing;
            FormClosed += delegate
            {
                NativeMethods.UnregisterHotKey(Handle, 1);
                NativeMethods.UnregisterHotKey(Handle, 2);
                NativeMethods.UnregisterHotKey(Handle, 3);
                NativeMethods.UnregisterHotKey(Handle, 4);
                statusTimer.Dispose();
                tray.Visible = false;
                tray.Dispose();
                trayMenu.Dispose();
                statusTip.Dispose();
            };
            RefreshWindows();
            initializing = false;
            SetStatus("已停止 · 选择目标窗口后开始", false);
        }

        private static PixelComboBox MakeCombo(bool pixelText)
        {
            return new PixelComboBox { PixelText = pixelText };
        }
        private static void FillKeys(ComboBox combo, bool hotkeysOnly)
        {
            if (!hotkeysOnly)
            {
                for (int code = 0x31; code <= 0x36; code++) combo.Items.Add(new KeyChoice(code, ((char)code).ToString()));
                return;
            }
            for (int i = 0; i < 12; i++) combo.Items.Add(new KeyChoice(0x70 + i, "F" + (i + 1)));
        }
        private static void SelectKey(ComboBox combo, int requested, int fallback)
        {
            foreach (KeyChoice key in combo.Items) if (key.Code == requested) { combo.SelectedItem = key; return; }
            foreach (KeyChoice key in combo.Items) if (key.Code == fallback) { combo.SelectedItem = key; return; }
        }
        private static int SelectedKey(ComboBox combo) { return ((KeyChoice)combo.SelectedItem).Code; }
        private MacroMode SelectedMode { get { return ((ModeChoice)mode.SelectedItem).Mode; } }

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24, 18, 24, 18), ColumnCount = 1, RowCount = 0, BackColor = PixelTheme.Canvas };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(layout, new PixelHeader { Dock = DockStyle.Fill, BackColor = PixelTheme.Canvas, TabStop = false, Margin = new Padding(0) }, 66);
            AddRow(layout, new Label { Text = "目标游戏窗口", Dock = DockStyle.Fill }, 26);
            var targets = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, 0, 8) };
            targets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            targets.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            windows.Dock = DockStyle.Fill;
            windows.Margin = new Padding(0, 3, 0, 0);
            windows.AccessibleName = "目标游戏窗口";
            targets.Controls.Add(windows, 0, 0);
            refresh.Dock = DockStyle.Fill;
            refresh.Margin = new Padding(8, 0, 0, 0);
            targets.Controls.Add(refresh, 1, 0);
            AddRow(layout, targets, 50);

            var settingsPanel = new PixelPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 15), Margin = new Padding(0, 6, 0, 12) };
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0), BackColor = PixelTheme.Paper };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
            AddField(fields, "模式选择", mode, 0);
            AddField(fields, "操作按键 · 主键盘 1–6", actionKey, 1);
            actionLabel = fields.GetControlFromPosition(0, 1);
            keyFields = fields;
            var repeats = new FlowLayoutPanel { Height = 38, WrapContents = false, Margin = new Padding(0) };
            repeats.Controls.Add(loopForever);
            repeats.Controls.Add(loopCount);
            loopCount.Width = 102;
            AddField(fields, "循环次数 · 取消勾选后指定次数", repeats, 2);
            repeats.Width = 220;
            AddField(fields, "开始 / 停止快捷键", toggleKey, 3);
            AddField(fields, "停止快捷键", stopKey, 4);
            AddField(fields, "暂停 / 继续快捷键", pauseKey, 5);
            settingsPanel.Controls.Add(fields);
            AddRow(layout, settingsPanel, 312);
            settingsRow = layout.RowStyles[layout.RowStyles.Count - 1];
            AddRow(layout, BuildMousePanel(), 0);
            mouseRow = layout.RowStyles[layout.RowStyles.Count - 1];
            status.BackColor = PixelTheme.Paper;
            statusHost.Dock = DockStyle.Fill;
            statusHost.Margin = new Padding(0, 0, 0, 14);
            statusHost.Controls.Add(status);
            AddRow(layout, statusHost, 64);
            var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 3 };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            start.Dock = stop.Dock = pause.Dock = DockStyle.Fill;
            start.Margin = new Padding(0, 0, 12, 0);
            stop.Margin = new Padding(0, 0, 12, 0);
            buttons.Controls.Add(start, 0, 0);
            buttons.Controls.Add(stop, 1, 0);
            buttons.Controls.Add(pause, 2, 0);
            AddRow(layout, buttons, 56);
            Controls.Add(layout);
        }
        private static void AddRow(TableLayoutPanel layout, Control control, int height)
        {
            int row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(control, 0, row);
        }
        private static void AddField(TableLayoutPanel table, string text, Control input, int row)
        {
            table.RowCount = row + 1;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            table.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            input.Width = 172;
            input.Anchor = AnchorStyles.Right;
            input.Margin = new Padding(0, 3, 0, 3);
            input.AccessibleName = text;
            table.Controls.Add(input, 1, row);
        }

        private void RefreshWindows()
        {
            var previous = windows.SelectedItem as WindowTarget;
            windows.Items.Clear();
            foreach (WindowTarget target in NativeMethods.ListWindows()) windows.Items.Add(target);
            foreach (WindowTarget target in windows.Items)
                if (previous != null && previous.Handle == target.Handle && previous.ProcessId == target.ProcessId) { windows.SelectedItem = target; return; }
            foreach (WindowTarget target in windows.Items)
                if (!string.IsNullOrEmpty(settings.ProcessName) && string.Equals(settings.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase))
                { windows.SelectedItem = target; return; }
            foreach (WindowTarget target in windows.Items)
                if (target.ProcessName.IndexOf("DNF", StringComparison.OrdinalIgnoreCase) >= 0 || target.Title.Contains("地下城"))
                { windows.SelectedItem = target; return; }
            // Leave unselected if no matching game is found, so another app is never picked by default.
        }
        private void HotkeysChanged()
        {
            if (!initializing && enableGlobalHotkeys && IsHandleCreated) RegisterHotkeys();
        }
        private void RegisterHotkeys()
        {
            captureHotkeyRegistered = false;
            NativeMethods.UnregisterHotKey(Handle, 1);
            NativeMethods.UnregisterHotKey(Handle, 2);
            NativeMethods.UnregisterHotKey(Handle, 3);
            NativeMethods.UnregisterHotKey(Handle, 4);
            if (!HotkeysAreDistinct())
            {
                SetStatus("控制快捷键和取点快捷键必须不同，请重新选择。", true);
                return;
            }
            bool toggleOk = NativeMethods.RegisterHotKey(Handle, 1, 0x4000, (uint)SelectedKey(toggleKey));
            bool stopOk = NativeMethods.RegisterHotKey(Handle, 2, 0x4000, (uint)SelectedKey(stopKey));
            bool pauseOk = NativeMethods.RegisterHotKey(Handle, 3, 0x4000, (uint)SelectedKey(pauseKey));
            captureHotkeyRegistered = SelectedMode != MacroMode.Amplification || NativeMethods.RegisterHotKey(Handle, 4, 0x4000, (uint)SelectedKey(captureKey));
            if (!toggleOk || !stopOk || !pauseOk || !captureHotkeyRegistered)
                SetStatus("快捷键被占用：" + (!toggleOk ? toggleKey.Text + " " : "") + (!stopOk ? stopKey.Text + " " : "") + (!pauseOk ? pauseKey.Text + " " : "") + (!captureHotkeyRegistered ? captureKey.Text : "") + "；请换键或使用按钮。", true);
            else SetStatus(SelectedMode == MacroMode.Amplification ? MouseIdleStatus() : "已停止 · " + toggleKey.Text + " 开始 / 停止，" + stopKey.Text + " 停止，" + pauseKey.Text + " 暂停 / 继续", false);
        }
        private bool HotkeysAreDistinct()
        {
            return SelectedMode == MacroMode.Amplification
                ? HotkeyRules.AreDistinct(SelectedKey(toggleKey), SelectedKey(stopKey), SelectedKey(pauseKey), SelectedKey(captureKey))
                : HotkeyRules.AreDistinct(SelectedKey(toggleKey), SelectedKey(stopKey), SelectedKey(pauseKey));
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312 && !closing)
            {
                if (message.WParam.ToInt32() == 1) Toggle();
                if (message.WParam.ToInt32() == 2) RequestStop();
                if (message.WParam.ToInt32() == 3) TogglePause();
                if (message.WParam.ToInt32() == 4) CaptureMousePoint();
            }
            base.WndProc(ref message);
        }
        private void Toggle()
        {
            if (closing) return;
            if (cancellation == null) StartLoop();
            else RequestStop();
        }
        private async void StartLoop()
        {
            if (cancellation != null || closing) return;
            if (!TryReleasePendingInput()) return;
            var target = windows.SelectedItem as WindowTarget;
            if (target == null) { RestoreWindow(); SetStatus("请先启动游戏，刷新并选择目标窗口。", true); return; }
            if (!HotkeysAreDistinct()) { SetStatus("控制快捷键和取点快捷键必须不同。", true); return; }

            if (SelectedMode == MacroMode.Amplification) { await StartMouseLoopAsync(target); return; }

            var options = new MacroOptions
            {
                VirtualKey = SelectedKey(actionKey), Mode = SelectedMode, LoopCount = SelectedLoopLimit
            };
            SaveSettings();
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            var transport = new PausableKeyTransport(new ForegroundKeyTransport(target));
            pauseTransport = transport;
            completedCycles = 0;
            runningLoopLimit = options.LoopCount;
            SetRunning(true);
            SetStatus("循环已启动 · 游戏在前台时发送按键，切走自动暂停", false);
            tray.Text = "auto-tools · 循环中";
            Exception failure = null;
            bool finished = false;
            try
            {
                runningTask = Task.Run(delegate
                {
                    return new MacroEngine().RunAsync(options, transport, delegate(long count) { Interlocked.Exchange(ref completedCycles, count); }, token);
                });
                await runningTask;
                finished = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { failure = error; }
            finally
            {
                runningTask = null;
                cancellation.Dispose();
                cancellation = null;
                pauseTransport = null;
                SetRunning(false);
                tray.Text = "auto-tools · 已停止";
                SetStatus(failure == null ? (finished ? "已完成 " + Interlocked.Read(ref completedCycles) + " 次循环" : "已停止 · 已完成 " + Interlocked.Read(ref completedCycles) + " 次循环") : "已停止 · " + failure.Message, failure != null);
                if (closeAfterStop) BeginInvoke(new Action(Close));
            }
        }
        private void RequestStop()
        {
            bool wasCapturing = waitingForPoint;
            CancelMouseCapture();
            if (wasCapturing) SetStatus("已取消取点 · 保留原坐标", false);
            if (cancellation == null && (pendingMouseRelease != null || pendingKeyboardRelease != null)) { TryReleasePendingInput(); return; }
            if (cancellation == null || cancellation.IsCancellationRequested) return;
            stop.Enabled = false;
            pause.Enabled = false;
            SetStatus(mouseTransport != null ? "正在停止并释放按键与鼠标…" : "正在停止并释放按键…", false);
            cancellation.Cancel();
        }
        private void SetRunning(bool running)
        {
            windows.Enabled = refresh.Enabled = actionKey.Enabled = mode.Enabled = toggleKey.Enabled = stopKey.Enabled = pauseKey.Enabled = !running;
            start.Enabled = !running;
            stop.Enabled = running;
            pause.Enabled = running;
            pause.Text = "暂停";
            loopForever.Enabled = !running;
            loopCount.Enabled = !running && !loopForever.Checked;
            SetMouseControlsEnabled(!running);
            if (pendingMouseRelease != null || pendingKeyboardRelease != null) { start.Enabled = false; stop.Enabled = true; }
        }
        private void TogglePause()
        {
            if (mouseTransport != null && cancellation != null && !cancellation.IsCancellationRequested)
            {
                mouseTransport.IsPaused = !mouseTransport.IsPaused;
                pause.Text = mouseTransport.IsPaused ? "继续" : "暂停";
                mouseState = MouseTargetService.Wait(mouseTransport.IsPaused ? MouseRunState.ManualPause : MouseRunState.WaitingForeground,
                    mouseTransport.IsPaused ? "已手动暂停 · 按暂停快捷键或点击继续恢复" : "已继续 · 等待游戏前台");
                SetStatus(mouseState.Message, false);
                return;
            }
            if (cancellation == null || cancellation.IsCancellationRequested || pauseTransport == null) return;
            pauseTransport.IsPaused = !pauseTransport.IsPaused;
            pause.Text = pauseTransport.IsPaused ? "继续" : "暂停";
            SetStatus(pauseTransport.IsPaused ? "已手动暂停 · 按 " + pauseKey.Text + " 或点击继续恢复" : "已继续 · 游戏在前台时发送按键", false);
        }
        private void SetStatus(string text, bool error)
        {
            if (status.Text != text)
            {
                status.Text = text;
                statusTip.SetToolTip(status, text);
            }
            status.ForeColor = error ? Color.FromArgb(140, 32, 34) : PixelTheme.Ink;
            statusHost.Accent = error ? PixelTheme.Red : text.Contains("暂停") || text.Contains("正在停止") ? PixelTheme.Yellow : text.StartsWith("循环中") || text.StartsWith("已继续") ? PixelTheme.Green : PixelTheme.Blue;
        }
        private void RestoreWindow()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }
        private void SaveSettings()
        {
            if (!enableGlobalHotkeys) return;
            settings.VirtualKey = SelectedKey(actionKey);
            settings.Mode = (int)SelectedMode;
            settings.ToggleHotkey = SelectedKey(toggleKey);
            settings.StopHotkey = SelectedKey(stopKey);
            settings.PauseHotkey = SelectedKey(pauseKey);
            settings.LoopForever = loopForever.Checked;
            settings.LoopCount = (int)loopCount.Value;
            SaveMouseSettings();
            var target = windows.SelectedItem as WindowTarget;
            if (target != null) settings.ProcessName = target.ProcessName;
            try { settings.Save(); }
            catch (System.IO.IOException) { SetStatus("参数保存失败；本次仍可运行。", true); }
            catch (UnauthorizedAccessException) { SetStatus("没有保存参数的权限；本次仍可运行。", true); }
        }
        private void OnClosing(object sender, FormClosingEventArgs args)
        {
            if (cancellation == null && !TryReleasePendingInput()) { args.Cancel = true; closing = false; return; }
            closing = true;
            if (cancellation != null)
            {
                args.Cancel = true;
                closeAfterStop = true;
                RequestStop();
                return;
            }
            SaveSettings();
        }
    }
}
