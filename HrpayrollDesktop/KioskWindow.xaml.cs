using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace HrpayrollDesktop;

public partial class KioskWindow : System.Windows.Window
{
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _resetTimer;

    public event Action? ManagerLoginRequested;

    public KioskWindow()
    {
        InitializeComponent();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();

        _resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _resetTimer.Tick += (_, _) => { _resetTimer.Stop(); ShowIdle(); };

        ShowIdle();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        LiveClockText.Text = now.ToString("hh:mm:ss tt");
        LiveDateText.Text = now.ToString("dddd, MMMM d, yyyy");
    }

    public void SetDeviceConnected(bool connected)
    {
        FooterStatusDot.Fill = connected ? Brushes.LimeGreen : (Brush)FindResource("ErrorBrush");
        FooterStatusText.Text = connected ? "Reader Connected" : "Reader Not Detected";
    }

    public void ShowIdle()
    {
        _resetTimer.Stop();
        SensorIcon.Text = "\uE928";
        SensorIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xC4, 0xF0));
        SensorCard.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x2C, 0x6F, 0xBB));
        HeadlineText.Text = "Place your finger on the reader";
        SubheadlineText.Text = "to clock in or out";
        ResultBadge.Visibility = Visibility.Collapsed;
    }

    public void ShowScanning()
    {
        _resetTimer.Stop();
        SensorIcon.Text = "\uE928";
        SensorIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
        SensorCard.BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
        HeadlineText.Text = "Scanning...";
        SubheadlineText.Text = "Please hold still on the sensor";
        ResultBadge.Visibility = Visibility.Collapsed;
    }

    public void ShowSuccess(string employeeName, string eventType, DateTime timestamp)
    {
        SensorIcon.Text = "\uE73E"; // checkmark
        SensorIcon.Foreground = Brushes.MediumSeaGreen;
        SensorCard.BorderBrush = Brushes.MediumSeaGreen;
        HeadlineText.Text = $"Welcome, {employeeName}";
        SubheadlineText.Text = "Identity verified successfully";

        var action = eventType == "CLOCK_OUT" ? "Clocked Out" : "Clocked In";
        ResultBadgeText.Text = $"{action} at {timestamp.ToLocalTime():hh:mm tt}";
        ResultBadge.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x4A, 0xDE, 0x80));
        ResultBadge.Visibility = Visibility.Visible;

        RestartResetTimer();
    }

    public void ShowNotRecognized()
    {
        SensorIcon.Text = "\uE783"; // alert glyph
        SensorIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x82, 0x1F));
        SensorCard.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x82, 0x1F));
        HeadlineText.Text = "Fingerprint not recognized";
        SubheadlineText.Text = "Please try again, or see your branch manager";
        ResultBadge.Visibility = Visibility.Collapsed;

        RestartResetTimer();
    }

    private void RestartResetTimer()
    {
        _resetTimer.Stop();
        _resetTimer.Start();
    }

    private void ManagerLoginButton_Click(object sender, RoutedEventArgs e)
    {
        ManagerLoginRequested?.Invoke();
    }
}