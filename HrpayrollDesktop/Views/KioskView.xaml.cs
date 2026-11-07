using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace HrpayrollDesktop;

/// <summary>One line in the home screen's "Today's activity".</summary>
public sealed record RecentScan(string Name, string Line, Brush AvatarBg, Brush AvatarFg)
{
    public string Initials => PopupHost.Initials(Name);
}

/// <summary>The figures on the home screen.</summary>
public sealed record KioskToday(int AtWork, int OnBreak, int Left, IReadOnlyList<RecentScan> AtWorkPeople);

/// <summary>How the home screen's sync pill looks.</summary>
public enum SyncTone { Good, Busy, Warning, Problem }

/// <summary>The home screen nobody signs in to. The reader listens the whole time; pop-ups show what each scan did.</summary>
public partial class KioskView : UserControl
{
    private const string IdleTitle = "Place your finger on the reader";
    private const string IdleSub = "One scan checks you in, starts a break, brings you back, or checks you out.";
    private const int PromptSeconds = 20;

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _promptTimer = new() { Interval = TimeSpan.FromSeconds(PromptSeconds) };
    private readonly DispatcherTimer _outcomeTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };
    private bool _readerConnected = true;

    public event Action? ManagerLoginRequested;
    public event Action? ManualCheckInRequested;
    public event Action? ScanRequested;
    public event Action? SeeAllRequested;
    public event Action? HelpRequested;
    public event Action? SyncStatusRequested;

    public KioskView()
    {
        InitializeComponent();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();

        _promptTimer.Tick += (_, _) => ShowIdle();
        _outcomeTimer.Tick += (_, _) => ShowIdle();
        // A fixed desktop layout: the sidebar, the scanner, the guide cards and the right column always show.
        // The window's minimum size guarantees they fit, so nothing is hidden or moved on resize.
    }

    // ----- The scanner -----

    /// <summary>Someone pressed "Scan fingerprint": invite a finger for a while. The reader listens either way.</summary>
    public void PromptForFinger()
    {
        StopTimers();
        Scanner.SetState(ScannerState.Ready);
        SetScannerText("Place your finger on the reader now", "Hold still until you see your name, then lift.", "Waiting for a finger", "#0891B2");
        _promptTimer.Start();
    }

    /// <summary>A finger is on the reader and is being matched.</summary>
    public void ShowScanning()
    {
        StopTimers();
        Scanner.SetState(ScannerState.Scanning);
        SetScannerText("Scanning…", "Keep your finger still.", "Reading", "#0891B2");
    }

    /// <summary>What the scan did, for a moment, before the scanner goes back to waiting.</summary>
    public void ShowScanOutcome(bool recognised, string title, string? sub = null)
    {
        StopTimers();
        Scanner.SetState(recognised ? ScannerState.Success : ScannerState.Failure);
        SetScannerText(title, sub ?? string.Empty, recognised ? "Recognised" : "Not recognised", recognised ? "#10B981" : "#EF4444");
        _outcomeTimer.Start();
    }

    private void ShowIdle()
    {
        StopTimers();
        Scanner.SetState(ScannerState.Idle);
        if (_readerConnected) SetScannerText(IdleTitle, IdleSub, "Ready", "#0891B2");
        else SetScannerText("The fingerprint reader is not connected", "Check the reader's USB cable. Meanwhile your Branch Manager can check you in under \"Manual check-in\".", "No reader", "#D18F00");
    }

    private void StopTimers()
    {
        _promptTimer.Stop();
        _outcomeTimer.Stop();
    }

    private void SetScannerText(string title, string sub, string chip, string colour)
    {
        ScanStatusTitle.Text = title;
        ScanStatusSub.Text = sub;
        ScanStatusSub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
        ScannerChipText.Text = chip;

        var c = (Color)ColorConverter.ConvertFromString(colour);
        ScannerChipDot.Fill = new SolidColorBrush(c);
        ScannerChip.Background = new SolidColorBrush(Color.FromArgb(0x1A, c.R, c.G, c.B));
        ScannerChip.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B));
    }

    // ----- Sync -----

    /// <summary>The pill in the top bar: whether scans are reaching the server, and how many are waiting.</summary>
    public void SetSyncStatus(SyncTone tone, string text)
    {
        var (bg, fg, icon) = tone switch
        {
            SyncTone.Good => ("GreenSoftBrush", "GreenBrush", ""),       // cloud_done
            SyncTone.Busy => ("BlueSoftBrush", "BlueBrush", ""),         // sync
            SyncTone.Warning => ("AmberSoftBrush", "AmberInkBrush", ""), // cloud_off
            _ => ("ErrorSoftBrush", "ErrorBrush", ""),                  // sync_problem
        };

        SyncPill.Background = (Brush)FindResource(bg);
        SyncIcon.Foreground = SyncText.Foreground = (Brush)FindResource(fg);
        SyncIcon.Text = icon;
        SyncText.Text = text;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        LiveClockText.Text = now.ToString("h:mm tt", CultureInfo.InvariantCulture);
        LiveDateText.Text = now.ToString("MMMM d, yyyy  •  dddd", CultureInfo.InvariantCulture);
        GreetingText.Text = now.Hour < 12 ? "Good morning!" : now.Hour < 17 ? "Good afternoon!" : "Good evening!";
    }

    public void SetStationLabel(string text) => StationLabelText.Text = text;

    public void SetBranchName(string? name) =>
        BranchNameText.Text = string.IsNullOrWhiteSpace(name) ? "Attendance terminal" : name;

    public void SetDeviceConnected(bool connected)
    {
        ReaderPill.Background = (Brush)FindResource(connected ? "GreenSoftBrush" : "ErrorSoftBrush");
        FooterStatusDot.Fill = (Brush)FindResource(connected ? "GreenBrush" : "ErrorBrush");
        FooterStatusText.Foreground = (Brush)FindResource(connected ? "GreenBrush" : "ErrorBrush");
        FooterStatusText.Text = connected ? "Reader connected" : "Reader not detected";

        _readerConnected = connected;
        if (Scanner.State is ScannerState.Idle) ShowIdle();
    }

    public void SetToday(IReadOnlyList<RecentScan> recent, KioskToday today)
    {
        StatAtWork.Text = today.AtWork.ToString();
        StatOnBreak.Text = today.OnBreak.ToString();
        StatLeft.Text = today.Left.ToString();

        RecentList.ItemsSource = recent;
        RecentEmptyText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        AtWorkAvatars.Children.Clear();
        AtWorkEmpty.Visibility = today.AtWorkPeople.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var person in today.AtWorkPeople.Take(4))
            AtWorkAvatars.Children.Add(Avatar(person.Initials, person.AvatarBg, person.AvatarFg, person.Name));
        if (today.AtWorkPeople.Count > 4)
            AtWorkAvatars.Children.Add(Avatar($"+{today.AtWorkPeople.Count - 4}", (Brush)FindResource("BlueFixedBrush"), (Brush)FindResource("PrimaryBrush"), "More people at work"));
    }

    private Border Avatar(string text, Brush bg, Brush fg, string tip) => new()
    {
        Width = 54,
        Height = 54,
        CornerRadius = new CornerRadius(27),
        Background = bg,
        BorderBrush = (Brush)FindResource("BlueBrush"),
        BorderThickness = new Thickness(2),
        Margin = new Thickness(0, 0, 8, 0),
        ToolTip = tip,
        Child = new TextBlock
        {
            Text = text,
            Foreground = fg,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private void ManagerLoginButton_Click(object sender, RoutedEventArgs e) => ManagerLoginRequested?.Invoke();

    private void ManualCheckInButton_Click(object sender, RoutedEventArgs e) => ManualCheckInRequested?.Invoke();

    private void Scan_Click(object sender, RoutedEventArgs e) => ScanRequested?.Invoke();

    private void SeeAll_Click(object sender, RoutedEventArgs e) => SeeAllRequested?.Invoke();

    private void Help_Click(object sender, RoutedEventArgs e) => HelpRequested?.Invoke();

    private void SyncStatus_Click(object sender, RoutedEventArgs e) => SyncStatusRequested?.Invoke();

    private void Scanner_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => ScanRequested?.Invoke();

    // There is only one page here, so the item simply stays selected.
    private void NavHome_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton button) button.IsChecked = true;
    }
}
