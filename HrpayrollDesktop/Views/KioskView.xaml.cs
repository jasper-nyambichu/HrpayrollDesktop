using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HrpayrollDesktop;

/// <summary>One line in the home screen's "Today's activity".</summary>
public sealed record RecentScan(string Name, string Line, Brush AvatarBg, Brush AvatarFg)
{
    public string Initials => PopupHost.Initials(Name);
}

/// <summary>The figures on the home screen.</summary>
public sealed record KioskToday(int AtWork, int OnBreak, int Left, IReadOnlyList<RecentScan> AtWorkPeople);

/// <summary>The home screen nobody signs in to. The reader listens the whole time; pop-ups show what each scan did.</summary>
public partial class KioskView : UserControl
{
    private readonly DispatcherTimer _clockTimer;

    public event Action? ManagerLoginRequested;
    public event Action? ScanRequested;
    public event Action? SeeAllRequested;
    public event Action? HelpRequested;

    public KioskView()
    {
        InitializeComponent();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();

        Loaded += (_, _) => StartFloating();
        SizeChanged += (_, _) =>
        {
            var wide = ActualWidth >= 1000;
            SidePanel.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            SideColumn.Width = wide ? new GridLength(270) : new GridLength(0);
        };
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        LiveClockText.Text = now.ToString("h:mm:ss tt", CultureInfo.InvariantCulture);
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

    private void StartFloating()
    {
        DoubleAnimation Float(double by, double seconds) => new(0, by, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };

        FloatA.BeginAnimation(TranslateTransform.YProperty, Float(-7, 2.6));
        FloatB.BeginAnimation(TranslateTransform.YProperty, Float(6, 3.1));
    }

    private void ManagerLoginButton_Click(object sender, RoutedEventArgs e) => ManagerLoginRequested?.Invoke();

    private void Scan_Click(object sender, RoutedEventArgs e) => ScanRequested?.Invoke();

    private void SeeAll_Click(object sender, RoutedEventArgs e) => SeeAllRequested?.Invoke();

    private void Help_Click(object sender, RoutedEventArgs e) => HelpRequested?.Invoke();

    // There is only one page here, so the item simply stays selected.
    private void NavHome_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton button) button.IsChecked = true;
    }
}
