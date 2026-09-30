using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HrpayrollDesktop;

public partial class KioskView : UserControl
{

    // Matching is near-instant; hold "Scanning" briefly so it doesn't flash.
    private const int MinScanMs = 700;

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _resetTimer;
    private readonly DispatcherTimer _deliverTimer;

    private Action? _deliverAction;
    private DateTime _scanStartedUtc = DateTime.MinValue;
    private int _remaining;
    private Run? _countdownRun;

    public event Action? ManagerLoginRequested;

    public KioskView()
    {
        InitializeComponent();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();

        _resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _resetTimer.Tick += ResetTimer_Tick;

        _deliverTimer = new DispatcherTimer();
        _deliverTimer.Tick += (_, _) =>
        {
            _deliverTimer.Stop();
            var action = _deliverAction;
            _deliverAction = null;
            action?.Invoke();
        };

        Loaded += (_, _) => StartIdleAnimation();
        ShowIdle();
    }

    // ===== Header / footer =====

    private void UpdateClock()
    {
        var now = DateTime.Now;
        LiveClockText.Text = now.ToString("hh:mm:ss", CultureInfo.InvariantCulture);
        LiveAmPmText.Text = now.ToString("tt", CultureInfo.InvariantCulture).ToUpperInvariant();
        LiveDateText.Text = now.ToString("dddd, d MMMM yyyy");
    }

    public void SetStationLabel(string text) => StationLabelText.Text = text;

    public void SetDeviceConnected(bool connected)
    {
        FooterStatusDot.Fill = connected ? Brushes.LimeGreen : (Brush)FindResource("ErrorBrush");
        FooterStatusText.Text = connected ? "Reader Connected" : "Reader Not Detected";
    }

    private void ManagerLoginButton_Click(object sender, RoutedEventArgs e) => ManagerLoginRequested?.Invoke();

    private void TryAgainButton_Click(object sender, RoutedEventArgs e) => ShowIdle();

    // ===== Public state API =====

    public void ShowIdle()
    {
        _resetTimer.Stop();
        _deliverTimer.Stop();
        _deliverAction = null;
        _scanStartedUtc = DateTime.MinValue;
        ShowPanel(IdlePanel);
    }

    /// <summary>A usable fingerprint image was captured; matching is starting.</summary>
    public void BeginScan()
    {
        _resetTimer.Stop();
        _deliverTimer.Stop();
        _deliverAction = null;
        _scanStartedUtc = DateTime.UtcNow;

        ShowPanel(ScanningPanel);
        StartScanAnimations();
    }

    public void ShowSuccess(string employeeName, string? detailLine, string eventType, DateTime timestampUtc)
        => AfterMinScan(() => RenderResult(employeeName, detailLine, eventType, timestampUtc, alreadyRecorded: false));

    public void ShowAlreadyRecorded(string employeeName, string? detailLine, string lastEventType, DateTime lastTimestampUtc)
        => AfterMinScan(() => RenderResult(employeeName, detailLine, lastEventType, lastTimestampUtc, alreadyRecorded: true));

    public void ShowNotRecognized()
        => AfterMinScan(() =>
        {
            _scanStartedUtc = DateTime.MinValue;
            ShowPanel(NotRecognizedPanel);
            StartAmberAnimation();
            StartReset(8, NotRecCountdownRun, null);
        });

    // ===== Rendering =====

    private void RenderResult(string name, string? detailLine, string eventType, DateTime timestampUtc, bool alreadyRecorded)
    {
        _scanStartedUtc = DateTime.MinValue;
        var clockOut = eventType == "CLOCK_OUT";

        // Palette: green for clock-in, blue for clock-out / already-recorded info.
        var green = !clockOut && !alreadyRecorded;
        SuccessHalo.Background = green ? Lin("#0E8A54", "#10B981", "#84CC16") : Lin("#00497E", "#1C6EB4", "#9FCAFF");
        SuccessInner.Background = green ? Lin("#10B981", "#047857") : Lin("#1C6EB4", "#0B4A80");
        SuccessDashRing.Stroke = Br(green ? "#66B7F568" : "#669FCAFF");
        SuccessGlow.Fill = new RadialGradientBrush(
            (Color)ColorConverter.ConvertFromString(green ? "#3310B981" : "#331C6EB4"),
            (Color)ColorConverter.ConvertFromString(green ? "#0010B981" : "#001C6EB4"));
        SuccessIcon.Text = clockOut || alreadyRecorded ? "\uE73E" : "\uE930";

        StatusPill.Background = Br(green ? "#33B7F568" : "#33D2E4FF");
        StatusPill.BorderBrush = Br(green ? "#66B7F568" : "#66D2E4FF");
        StatusDot.Fill = Br(green ? "#10B981" : "#9FCAFF");
        StatusLabelText.Foreground = Br(green ? "#B7F568" : "#D2E4FF");
        LedgerIcon.Foreground = Br(green ? "#B7F568" : "#D2E4FF");

        var action = alreadyRecorded
            ? (clockOut ? "ALREADY CLOCKED OUT" : "ALREADY CLOCKED IN")
            : (clockOut ? "CLOCKED OUT" : "CLOCKED IN");
        StatusLabelText.Text = action;
        StatusTimeText.Text = $"at {timestampUtc.ToLocalTime().ToString("hh:mm tt", CultureInfo.InvariantCulture)}";

        NameHeadlineText.Text = alreadyRecorded ? name : (clockOut ? $"Goodbye, {name}" : $"Welcome, {name}");

        DetailLineText.Text = detailLine ?? string.Empty;
        DetailLineText.Visibility = string.IsNullOrWhiteSpace(detailLine) ? Visibility.Collapsed : Visibility.Visible;

        LedgerText.Text = alreadyRecorded
            ? "Your attendance is already recorded • No need to scan again"
            : clockOut
                ? "Shift ended and recorded • Rest well!"
                : "Attendance recorded • Have a productive shift!";

        ShowPanel(SuccessPanel);
        StartReset(5, SuccessCountdownRun, ResetBarScale);
    }

    private void ShowPanel(UIElement panel)
    {
        foreach (var p in new UIElement[] { IdlePanel, ScanningPanel, SuccessPanel, NotRecognizedPanel })
            p.Visibility = ReferenceEquals(p, panel) ? Visibility.Visible : Visibility.Collapsed;

        if (!ReferenceEquals(panel, ScanningPanel)) StopScanAnimations();
        if (!ReferenceEquals(panel, NotRecognizedPanel)) StopAmberAnimation();
    }

    private void AfterMinScan(Action show)
    {
        if (_scanStartedUtc == DateTime.MinValue)
        {
            show();
            return;
        }

        var waitMs = MinScanMs - (DateTime.UtcNow - _scanStartedUtc).TotalMilliseconds;
        if (waitMs <= 0)
        {
            show();
            return;
        }

        _deliverTimer.Stop();
        _deliverAction = show;
        _deliverTimer.Interval = TimeSpan.FromMilliseconds(waitMs);
        _deliverTimer.Start();
    }

    // ===== Auto-reset countdown =====

    private void StartReset(int seconds, Run countdownRun, ScaleTransform? bar)
    {
        _resetTimer.Stop();
        _remaining = seconds;
        _countdownRun = countdownRun;
        countdownRun.Text = $"{seconds}s";

        if (bar is not null)
            bar.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(seconds)));

        _resetTimer.Start();
    }

    private void ResetTimer_Tick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            ShowIdle();
            return;
        }

        if (_countdownRun is not null)
            _countdownRun.Text = $"{_remaining}s";
    }

    // ===== Animations (code-driven so they can be stopped when hidden) =====

    private static DoubleAnimation Loop(double from, double to, double seconds, bool autoReverse = false, double beginSeconds = 0)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = autoReverse,
            BeginTime = TimeSpan.FromSeconds(beginSeconds),
        };
        if (autoReverse)
            animation.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
        return animation;
    }

    private void StartIdleAnimation()
    {
        IdlePulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, Loop(1, 1.04, 2.4, true));
        IdlePulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, Loop(1, 1.04, 2.4, true));
    }

    private void StartScanAnimations()
    {
        OrbitRotate.BeginAnimation(RotateTransform.AngleProperty, Loop(0, 360, 3.5));
        PrecisionRotate.BeginAnimation(RotateTransform.AngleProperty, Loop(360, 0, 5));

        SweepTranslate.BeginAnimation(TranslateTransform.YProperty, Loop(0, 150, 1.8, true));
        ProgressTranslate.BeginAnimation(TranslateTransform.XProperty, Loop(-90, 256, 1.4));

        Wave1Scale.BeginAnimation(ScaleTransform.ScaleXProperty, Loop(1, 1.43, 2.2));
        Wave1Scale.BeginAnimation(ScaleTransform.ScaleYProperty, Loop(1, 1.43, 2.2));
        Wave1.BeginAnimation(OpacityProperty, Loop(0.8, 0, 2.2));
        Wave2Scale.BeginAnimation(ScaleTransform.ScaleXProperty, Loop(1, 1.43, 2.2, false, 0.75));
        Wave2Scale.BeginAnimation(ScaleTransform.ScaleYProperty, Loop(1, 1.43, 2.2, false, 0.75));
        Wave2.BeginAnimation(OpacityProperty, Loop(0.8, 0, 2.2, false, 0.75));

        Dot1.BeginAnimation(OpacityProperty, Loop(0.2, 1, 0.6, true, 0));
        Dot2.BeginAnimation(OpacityProperty, Loop(0.2, 1, 0.6, true, 0.25));
        Dot3.BeginAnimation(OpacityProperty, Loop(0.2, 1, 0.6, true, 0.5));
    }

    private void StopScanAnimations()
    {
        OrbitRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        PrecisionRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        SweepTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ProgressTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        Wave1Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Wave1Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Wave1.BeginAnimation(OpacityProperty, null);
        Wave2Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Wave2Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Wave2.BeginAnimation(OpacityProperty, null);
        Dot1.BeginAnimation(OpacityProperty, null);
        Dot2.BeginAnimation(OpacityProperty, null);
        Dot3.BeginAnimation(OpacityProperty, null);
    }

    private void StartAmberAnimation()
    {
        AmberWaveScale.BeginAnimation(ScaleTransform.ScaleXProperty, Loop(1, 1.25, 2.4));
        AmberWaveScale.BeginAnimation(ScaleTransform.ScaleYProperty, Loop(1, 1.25, 2.4));
        AmberWave.BeginAnimation(OpacityProperty, Loop(0.9, 0, 2.4));
    }

    private void StopAmberAnimation()
    {
        AmberWaveScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        AmberWaveScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AmberWave.BeginAnimation(OpacityProperty, null);
    }

    // ===== Brush helpers =====

    private static SolidColorBrush Br(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    private static LinearGradientBrush Lin(string a, string b, string? c = null)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(a), 0));
        if (c is null)
        {
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(b), 1));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(b), 0.5));
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(c), 1));
        }
        return brush;
    }
}