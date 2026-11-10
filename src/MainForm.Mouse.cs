using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AutoTools
{
    public sealed partial class MainForm
    {
        private readonly PixelComboBox captureKey = MakeCombo(true);
        private readonly NumericUpDown mouseWidth = MakeNumber(1, 32768);
        private readonly NumericUpDown mouseHeight = MakeNumber(1, 32768);
        private readonly NumericUpDown mouseGap = MakeNumber(0, 3600000);
        private readonly CheckBox extraEnterForChat = new CheckBox { Text = "增幅后出现聊天了勾选我", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly PixelButton checkSize = new PixelButton { Text = "检查尺寸", Width = 100, Height = 36 };
        private readonly PixelButton applySize = new PixelButton { Text = "应用尺寸", Width = 100, Height = 36 };
        private readonly PixelButton beginCapture = new PixelButton { Text = "开始取点", Width = 110, Height = 36 };
        private readonly PixelButton cancelCapture = new PixelButton { Text = "取消取点", Width = 110, Height = 36, Enabled = false };
        private readonly PixelButton confirmPoint = new PixelButton { Text = "确认已存点位", Width = 150, Height = 36 };
        private readonly Label actualSize = new Label { Text = "实际客户区：尚未检查", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label pointInfo = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        private PixelPanel mousePanel;
        private Control actionLabel;
        private TableLayoutPanel keyFields;
        private RowStyle mouseRow;
        private RowStyle settingsRow;
        private MouseTargetService mouseTarget;
        private ForegroundMouseTransport mouseTransport;
        private ForegroundMouseTransport pendingMouseRelease;
        private ForegroundKeyTransport pendingKeyboardRelease;
        private volatile MouseReadiness mouseState;
        private bool waitingForPoint;
        private bool pointConfirmed;
        private bool captureHotkeyRegistered;
        private bool resizingTarget;

        private static NumericUpDown MakeNumber(int min, int max)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Width = 92,
                BackColor = PixelTheme.Paper, ForeColor = PixelTheme.Ink, Font = PixelTheme.BodyFont };
        }
        private Control BuildMousePanel()
        {
            mousePanel = new PixelPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 8, 14, 12), Margin = new Padding(0, 0, 0, 10), Visible = false };
            var rows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0) };
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(rows, new Label { Text = "客户区不含标题栏和边框；优先在游戏内设置窗口模式与分辨率。", Dock = DockStyle.Fill }, 28);
            var dimensions = MouseFlow();
            dimensions.Controls.Add(MouseLabel("宽", 30)); dimensions.Controls.Add(mouseWidth);
            dimensions.Controls.Add(MouseLabel("× 高", 46)); dimensions.Controls.Add(mouseHeight);
            dimensions.Controls.Add(checkSize); dimensions.Controls.Add(applySize);
            AddRow(rows, dimensions, 42);
            AddRow(rows, actualSize, 26);
            AddRow(rows, pointInfo, 28);
            var capture = MouseFlow();
            capture.Controls.Add(beginCapture); capture.Controls.Add(cancelCapture); capture.Controls.Add(confirmPoint);
            AddRow(rows, capture, 42);
            var options = MouseFlow();
            options.Controls.Add(MouseLabel("取点快捷键", 100));
            captureKey.Width = 110; options.Controls.Add(captureKey);
            options.Controls.Add(MouseLabel("下轮等待 ms", 110)); options.Controls.Add(mouseGap);
            AddRow(rows, options, 42);
            AddRow(rows, extraEnterForChat, 34);
            AddRow(rows, new Label { Text = "1→左键 100 ms；左键→回车① 50 ms；①→② 150 ms；勾选后②→③ 50 ms。", Dock = DockStyle.Fill, ForeColor = PixelTheme.Muted }, 28);
            mousePanel.Controls.Add(rows);
            return mousePanel;
        }
        private static FlowLayoutPanel MouseFlow()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        }
        private static Label MouseLabel(string text, int width)
        {
            return new Label { Text = text, Width = width, Height = 32, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 4, 0) };
        }
        private void InitializeMouseControls()
        {
            mouseWidth.Value = Clamp(settings.MouseExpectedWidth, 1, 32768, 1280);
            mouseHeight.Value = Clamp(settings.MouseExpectedHeight, 1, 32768, 720);
            mouseGap.Value = Clamp(settings.MouseLoopGapMilliseconds, 0, 3600000, MouseOptions.DefaultLoopGapMilliseconds);
            extraEnterForChat.Checked = settings.ExtraEnterForChat;
            checkSize.Click += delegate { CheckMouseSize(); };
            applySize.Click += async delegate { await ApplyMouseSizeAsync(); };
            beginCapture.Click += delegate { BeginMouseCapture(); };
            cancelCapture.Click += delegate { CancelMouseCapture(); SetStatus("已取消取点 · 保留原坐标", false); };
            confirmPoint.Click += delegate { ConfirmMousePoint(); };
            mouseWidth.ValueChanged += delegate { MouseDimensionsChanged(); };
            mouseHeight.ValueChanged += delegate { MouseDimensionsChanged(); };
            UpdatePointInfo();
        }
        private static int Clamp(int value, int min, int max, int fallback) { return value < min || value > max ? fallback : value; }
        private Size ExpectedMouseSize { get { return new Size((int)mouseWidth.Value, (int)mouseHeight.Value); } }
        private MouseTargetService GetMouseTarget()
        {
            if (mouseTarget == null) mouseTarget = new MouseTargetService(windows.SelectedItem as WindowTarget);
            return mouseTarget;
        }
        private void MouseTargetChanged()
        {
            CancelMouseCapture();
            mouseTarget = null;
            pointConfirmed = false;
            actualSize.Text = "实际客户区：尚未检查";
            UpdatePointInfo();
            if (!initializing && SelectedMode == MacroMode.Amplification)
                SetStatus(settings.MousePointConfigured ? "目标窗口已变更 · 请重新取点或确认已存点位" : "未取点 · 请先检查客户区尺寸，再开始取点", false);
        }
        private void MouseDimensionsChanged()
        {
            CancelMouseCapture();
            pointConfirmed = false;
            UpdatePointInfo();
            SetStatus("期望客户区尺寸已变更 · 请检查尺寸并重新取点或确认点位", false);
        }
        private void UpdateModeView()
        {
            CancelMouseCapture();
            bool mouse = SelectedMode == MacroMode.Amplification;
            mousePanel.Visible = mouse;
            actionKey.Visible = actionLabel.Visible = !mouse;
            using (var graphics = CreateGraphics())
            {
                float scale = graphics.DpiY / 96F;
                keyFields.RowStyles[1].Height = mouse ? 0 : 44 * scale;
                settingsRow.Height = (mouse ? 268 : 312) * scale;
                mouseRow.Height = mouse ? 308 * scale : 0;
                ClientSize = new Size(ClientSize.Width, Math.Min((int)((mouse ? 880 : 616) * scale), Screen.FromControl(this).WorkingArea.Height - 80));
            }
            SetMouseControlsEnabled(cancellation == null);
            UpdatePointInfo();
        }
        private void SetMouseControlsEnabled(bool enabled)
        {
            enabled = enabled && !resizingTarget;
            mouseWidth.Enabled = mouseHeight.Enabled = mouseGap.Enabled = captureKey.Enabled = checkSize.Enabled = applySize.Enabled = enabled && !waitingForPoint;
            extraEnterForChat.Enabled = enabled && !waitingForPoint;
            beginCapture.Enabled = enabled && !waitingForPoint;
            cancelCapture.Enabled = enabled && waitingForPoint;
            confirmPoint.Enabled = enabled && !waitingForPoint && settings.MousePointConfigured;
            if (enabled) stop.Enabled = cancellation != null || waitingForPoint;
        }
        private void UpdatePointInfo()
        {
            pointInfo.Text = settings.MousePointConfigured
                ? "已取点：(" + settings.MouseClientX + ", " + settings.MouseClientY + ") · 采集尺寸 " + settings.MouseCapturedWidth + " × " + settings.MouseCapturedHeight + (pointConfirmed ? " · 已确认" : " · 需确认或重新取点")
                : "未取点";
            statusTip.SetToolTip(pointInfo, pointInfo.Text);
            confirmPoint.Enabled = settings.MousePointConfigured && cancellation == null && !waitingForPoint && !resizingTarget;
        }
        private void ShowActualSize(Size actual) { actualSize.Text = "实际客户区：" + actual.Width + " × " + actual.Height; }
        private void CheckMouseSize()
        {
            try
            {
                Size actual = GetMouseTarget().CheckSize();
                ShowActualSize(actual);
                SetStatus(actual == ExpectedMouseSize ? "客户区尺寸已核对 · " + actual.Width + " × " + actual.Height : MouseTargetService.SizeMessage(ExpectedMouseSize, actual), actual != ExpectedMouseSize);
            }
            catch (Exception error) { SetStatus(error.Message, true); }
        }
        private async Task ApplyMouseSizeAsync()
        {
            if (cancellation != null || resizingTarget || closing) return;
            try
            {
                MouseTargetService service = GetMouseTarget();
                Size expected = ExpectedMouseSize;
                resizingTarget = true;
                SetRunning(false);
                start.Enabled = windows.Enabled = refresh.Enabled = mode.Enabled = false;
                SetStatus("正在应用客户区尺寸…", false);
                Size actual = await Task.Run(delegate { return service.ApplySize(expected); });
                // Games may asynchronously enforce their own size. Re-read after settling.
                await Task.Delay(150);
                actual = service.CheckSize();
                ShowActualSize(actual);
                SetStatus(actual == expected ? "客户区尺寸已核对 · 请取点或确认已存点位" : MouseTargetService.SizeMessage(expected, actual), actual != expected);
            }
            catch (Exception error) { if (!IsDisposed) SetStatus(error.Message, true); }
            finally
            {
                resizingTarget = false;
                if (!IsDisposed) SetRunning(false);
            }
        }
        private void BeginMouseCapture()
        {
            if (cancellation != null || resizingTarget || SelectedMode != MacroMode.Amplification) return;
            if (!HotkeysAreDistinct()) { SetStatus("控制快捷键和取点快捷键必须不同。", true); return; }
            if (enableGlobalHotkeys && !captureHotkeyRegistered) { SetStatus("取点快捷键注册失败，请更换取点快捷键。", true); return; }
            try
            {
                Size actual = GetMouseTarget().CheckSize();
                ShowActualSize(actual);
                if (actual != ExpectedMouseSize) throw new InvalidOperationException(MouseTargetService.SizeMessage(ExpectedMouseSize, actual));
                waitingForPoint = true;
                SetMouseControlsEnabled(true);
                start.Enabled = mode.Enabled = false;
                SetStatus("等待取点 · 切回所选游戏，将鼠标移到目标位置后按 " + captureKey.Text, false);
            }
            catch (Exception error) { SetStatus(error.Message, true); }
        }
        private void CancelMouseCapture()
        {
            if (!waitingForPoint) return;
            waitingForPoint = false;
            SetMouseControlsEnabled(cancellation == null);
            start.Enabled = mode.Enabled = cancellation == null && !resizingTarget;
        }
        private void CaptureMousePoint()
        {
            if (!waitingForPoint || cancellation != null || SelectedMode != MacroMode.Amplification) return;
            try
            {
                Point client = GetMouseTarget().Capture(ExpectedMouseSize);
                settings.MouseClientX = client.X;
                settings.MouseClientY = client.Y;
                settings.MouseCapturedWidth = ExpectedMouseSize.Width;
                settings.MouseCapturedHeight = ExpectedMouseSize.Height;
                settings.MousePointConfigured = true;
                settings.MousePointProcessName = ((WindowTarget)windows.SelectedItem).ProcessName;
                pointConfirmed = true;
                CancelMouseCapture();
                UpdatePointInfo();
                SetStatus("已取点：(" + client.X + ", " + client.Y + ") · 可按开始快捷键运行", false);
                SaveSettings();
            }
            catch (Exception error) { SetStatus(error.Message + " · 保留原坐标，可重试或取消取点", true); }
        }
        private MouseOptions ReadMouseOptions()
        {
            return new MouseOptions { PointConfigured = settings.MousePointConfigured,
                ClientPoint = new Point(settings.MouseClientX, settings.MouseClientY),
                CapturedSize = new Size(settings.MouseCapturedWidth, settings.MouseCapturedHeight),
                ExpectedSize = ExpectedMouseSize, LoopGapMilliseconds = (int)mouseGap.Value, LoopCount = SelectedLoopLimit,
                ExtraEnterForChat = extraEnterForChat.Checked };
        }
        private void ConfirmMousePoint()
        {
            if (cancellation != null || waitingForPoint || resizingTarget) return;
            try
            {
                MouseOptions options = ReadMouseOptions();
                options.Validate();
                var target = windows.SelectedItem as WindowTarget;
                if (target == null) throw new InvalidOperationException("请先选择目标窗口。");
                if (!string.Equals(target.ProcessName, settings.MousePointProcessName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("已存点位来自其他进程，请在当前目标中重新取点。");
                Size actual = GetMouseTarget().CheckSize();
                ShowActualSize(actual);
                if (actual != options.ExpectedSize) throw new InvalidOperationException(MouseTargetService.SizeMessage(options.ExpectedSize, actual));
                pointConfirmed = true;
                UpdatePointInfo();
                SetStatus("已确认当前窗口使用已存点位 · 请确认游戏内布局与取点时一致", false);
            }
            catch (Exception error) { SetStatus(error.Message, true); }
        }
        private async Task StartMouseLoopAsync(WindowTarget target)
        {
            if (waitingForPoint || resizingTarget) { SetStatus("请先完成或取消取点 / 尺寸操作。", true); return; }
            MouseOptions options;
            MouseTargetService service;
            try
            {
                options = ReadMouseOptions();
                options.Validate();
                if (!pointConfirmed) throw new InvalidOperationException("未确认当前窗口的点位，请重新取点或点击“确认已存点位”。");
                service = GetMouseTarget();
                Size actual = service.CheckSize();
                ShowActualSize(actual);
                if (actual != options.ExpectedSize) throw new InvalidOperationException(MouseTargetService.SizeMessage(options.ExpectedSize, actual));
            }
            catch (Exception error) { SetStatus(error.Message, true); return; }
            SaveSettings();
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            var transport = new ForegroundMouseTransport(service, new NativeMouseInputBackend());
            var keyboard = new ForegroundKeyTransport(target);
            mouseTransport = transport;
            completedCycles = 0;
            runningLoopLimit = options.LoopCount;
            mouseState = MouseTargetService.Wait(MouseRunState.WaitingForeground, "循环已启动 · 等待游戏前台");
            SetRunning(true);
            SetStatus(mouseState.Message, false);
            Exception failure = null;
            bool finished = false;
            try
            {
                runningTask = Task.Run(delegate { return new AmplificationEngine().RunAsync(options, transport, keyboard, delegate(MouseReadiness state) { mouseState = state; }, delegate(long count) { Interlocked.Exchange(ref completedCycles, count); }, token); });
                await runningTask;
                finished = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                failure = error;
                mouseState = MouseTargetService.Wait(error is MouseTargetInvalidException ? MouseRunState.TargetInvalid : error is MouseDpiException ? MouseRunState.DpiUnsupported : MouseRunState.InputFailed, error.Message);
                if (error is MouseTargetInvalidException) pointConfirmed = false;
            }
            finally
            {
                runningTask = null;
                if (transport.HasHeldButton) pendingMouseRelease = transport;
                if (keyboard.HasHeldKey) pendingKeyboardRelease = keyboard;
                mouseTransport = null;
                cancellation.Dispose();
                cancellation = null;
                SetRunning(false);
                UpdatePointInfo();
                tray.Text = "auto-tools · 已停止";
                SetStatus(failure == null ? (finished ? "已完成 " + Interlocked.Read(ref completedCycles) + " 次赌增幅强化" : "已停止 · 已完成 " + Interlocked.Read(ref completedCycles) + " 次循环") : "已停止 · " + failure.Message + (pendingMouseRelease != null || pendingKeyboardRelease != null ? "；按停止重试释放" : ""), failure != null);
                if (closeAfterStop)
                {
                    if (failure == null) BeginInvoke(new Action(Close));
                    else { closeAfterStop = closing = false; RestoreWindow(); }
                }
            }
        }
        private void SaveMouseSettings()
        {
            settings.MouseExpectedWidth = (int)mouseWidth.Value;
            settings.MouseExpectedHeight = (int)mouseHeight.Value;
            settings.MouseCaptureHotkey = SelectedKey(captureKey);
            settings.MouseLoopGapMilliseconds = (int)mouseGap.Value;
            settings.ExtraEnterForChat = extraEnterForChat.Checked;
        }
        private int SelectedLoopLimit { get { return loopForever.Checked ? 0 : (int)loopCount.Value; } }
        private string LoopProgress()
        {
            return " · " + Interlocked.Read(ref completedCycles) + (runningLoopLimit == 0 ? " 次 / 一直循环" : " / " + runningLoopLimit + " 次");
        }
        private string MouseIdleStatus()
        {
            return !settings.MousePointConfigured ? "未取点 · 请检查客户区尺寸，再开始取点" : pointConfirmed ? "已停止 · 当前点位已确认" : "已停止 · 请重新取点或确认已存点位";
        }
        private bool TryReleasePendingInput()
        {
            if (pendingMouseRelease == null && pendingKeyboardRelease == null) return true;
            string failure = "";
            if (pendingMouseRelease != null)
            {
                try { pendingMouseRelease.Release(); pendingMouseRelease = null; }
                catch (Exception error) { failure = "松开鼠标左键失败：" + error.Message; }
            }
            if (pendingKeyboardRelease != null)
            {
                try { pendingKeyboardRelease.ReleaseHeldKey(); pendingKeyboardRelease = null; }
                catch (Exception error) { failure += " 松开按键失败：" + error.Message; }
            }
            SetRunning(false);
            SetStatus(failure.Length == 0 ? "按键和鼠标已释放 · 可以重新开始" : failure + "；按停止重试释放", failure.Length != 0);
            return failure.Length == 0;
        }
    }
}
