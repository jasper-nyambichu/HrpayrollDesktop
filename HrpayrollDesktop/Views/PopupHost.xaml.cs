using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HrpayrollDesktop;

public enum PopupKind { Success, Info, Warning, Error }

/// <summary>One line in a "See all" list.</summary>
public sealed record PopupListRow(string Name, string Initials, string Detail, string Time, Brush AvatarBg, Brush AvatarFg);

/// <summary>What the person at the reader chose. Reason and ReturnAtLocal are only set for a temporary check-out.</summary>
public enum ScanChoice { Lunch, Temporary, ClockOut, Cancelled }

/// <summary>
/// Every pop-up in the application. Reader pop-ups sit on their own layer above Back Office dialogs, so someone
/// scanning at the reader never disturbs an officer's unfinished question.
/// </summary>
public partial class PopupHost : UserControl
{
    private const int MinScanMs = 700;      // keep "Scanning" visible at least this long, so it doesn't flicker
    private const int ChoiceSeconds = 30;   // someone who walks away from the choice records nothing
    private const int TemporaryIdleSeconds = 120;
    private const int ScanPromptSeconds = 20;

    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _deliver = new();
    private readonly DispatcherTimer _scanWatchdog = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _hintTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private Action? _deliverAction;
    private DateTime _scanStartedUtc = DateTime.MinValue;
    private int _remaining;
    private Action? _onCountdownTick;
    private Action? _onCountdownDone;

    private Action<ScanChoice, string?, DateTime?>? _choiceHandler;
    private TaskCompletionSource<bool>? _confirm;

    public PopupHost()
    {
        InitializeComponent();

        _countdown.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0)
            {
                _countdown.Stop();
                var done = _onCountdownDone;
                _onCountdownDone = null;
                done?.Invoke();
                return;
            }
            _onCountdownTick?.Invoke();
        };

        _deliver.Tick += (_, _) =>
        {
            _deliver.Stop();
            var action = _deliverAction;
            _deliverAction = null;
            action?.Invoke();
        };

        // If a scan never produces a result, don't stay on "Scanning" forever.
        _scanWatchdog.Tick += (_, _) =>
        {
            _scanWatchdog.Stop();
            if (ScanCard.Visibility == Visibility.Visible && ScanCancelButton.Visibility != Visibility.Visible) CloseScanLayer();
        };

        _hintTimer.Tick += (_, _) => { _hintTimer.Stop(); HintToast.Visibility = Visibility.Collapsed; };
    }

    /// <summary>The pop-ups of the open window, so any screen can ask a question without passing this around.</summary>
    public static PopupHost? Current { get; private set; }

    public void MakeCurrent() => Current = this;

    /// <summary>The person at the reader is partway through choosing, so other reader activity must wait.</summary>
    public bool IsChoosing => _choiceHandler is not null;

    // ===================================================================
    //  At the reader
    // ===================================================================

    /// <summary>The button on the home screen: invites a finger. The reader is always listening, with or without this.</summary>
    public void ShowScanPrompt(bool readerConnected)
    {
        if (IsChoosing) return;

        if (!readerConnected)
        {
            ShowMessage(PopupKind.Error, "Fingerprint reader not found",
                "Check the reader's USB cable, then try again. If it keeps happening, please see the Back Office.");
            return;
        }

        ScanTitle.Text = "Place your finger on the reader";
        ScanBody.Text = "Hold still until you see your name, then lift.";
        ScanCancelButton.Visibility = Visibility.Visible;
        ShowScanCard(ScanCard);
        StartPulse();

        StartCountdown(ScanPromptSeconds, null, CloseScanLayer);
    }

    /// <summary>A usable fingerprint was captured and matching has started.</summary>
    public void BeginScan()
    {
        if (IsChoosing) return;

        _deliver.Stop();
        _deliverAction = null;
        _countdown.Stop();
        _scanStartedUtc = DateTime.UtcNow;

        ScanTitle.Text = "Scanning…";
        ScanBody.Text = "Matching your fingerprint. Keep your finger still.";
        ScanCancelButton.Visibility = Visibility.Collapsed;
        ShowScanCard(ScanCard);
        StartPulse();

        _scanWatchdog.Stop();
        _scanWatchdog.Start();
    }

    public void ShowMessage(PopupKind kind, string title, string body, string? note = null, int autoCloseSeconds = 5, string closeText = "Close")
    {
        AfterMinScan(() =>
        {
            if (IsChoosing) return;

            _scanWatchdog.Stop();
            _scanStartedUtc = DateTime.MinValue;

            var (halo, dot, icon, noteBg, noteFg) = kind switch
            {
                PopupKind.Success => ("BlueSoftBrush", "BlueBrush", "", "BlueSoftBrush", "PrimaryBrush"),
                PopupKind.Warning => ("AmberSoftBrush", "AmberBrush", "", "AmberSoftBrush", "AmberInkBrush"),
                PopupKind.Error => ("ErrorSoftBrush", "ErrorBrush", "", "ErrorSoftBrush", "ErrorBrush"),
                _ => ("BlueSoftBrush", "BlueBrush", "", "BlueSoftBrush", "PrimaryBrush"),
            };

            MessageHalo.Fill = Res(halo);
            MessageDot.Fill = Res(dot);
            MessageIcon.Text = icon;
            MessageTitle.Text = title;
            MessageBody.Text = body;

            MessageNote.Visibility = string.IsNullOrWhiteSpace(note) ? Visibility.Collapsed : Visibility.Visible;
            MessageNote.Background = Res(noteBg);
            MessageNoteText.Foreground = Res(noteFg);
            MessageNoteText.Text = note ?? string.Empty;

            ShowScanCard(MessageCard);
            StopPulse();

            void Label() => MessageCloseButton.Content = autoCloseSeconds > 0 ? $"{closeText} ({_remaining}s)" : closeText;
            if (autoCloseSeconds > 0)
            {
                StartCountdown(autoCloseSeconds, Label, CloseScanLayer);
            }
            Label();

            Play(kind switch
            {
                PopupKind.Success => SystemSounds.Asterisk,
                PopupKind.Info => SystemSounds.Beep,
                _ => SystemSounds.Exclamation,
            });
        });
    }

    /// <summary>The reader has seen the finger lift after a failed match.</summary>
    public void NotifyFingerRemoved()
    {
        if (MessageCard.Visibility == Visibility.Visible && MessageNote.Visibility == Visibility.Visible
            && MessageNoteText.Text.StartsWith("Lift", StringComparison.Ordinal))
        {
            MessageNoteText.Text = "Ready. Place your finger again.";
        }
    }

    public void ShowHint(string text)
    {
        if (ScanLayer.Visibility == Visibility.Visible) return;

        HintText.Text = text;
        HintToast.Visibility = Visibility.Visible;
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    // ----- The choice after someone at work scans -----

    /// <summary>Asks where someone at work is going. The scan records nothing until they answer.</summary>
    public void ShowChoice(string name, string? detail, DateTime? clockedInAtUtc, DateTime lunchBackAtUtc,
        Action<ScanChoice, string?, DateTime?> handler)
    {
        // Set straight away: another finger arriving now must not cancel this person's turn.
        _choiceHandler = handler;
        _scanWatchdog.Stop();

        AfterMinScan(() =>
        {
            _scanStartedUtc = DateTime.MinValue;

            ChoiceInitials.Text = Initials(name);
            ChoiceTitle.Text = $"Hello, {FirstName(name)}";
            var since = clockedInAtUtc is { } at ? $"At work since {Clock(at)}" : "You are at work";
            ChoiceSub.Text = string.IsNullOrWhiteSpace(detail) ? since : $"{since}  •  {detail}";
            TemporarySub.Text = name;
            LunchOptionDetail.Text = $"30 minutes. Be back by {Clock(lunchBackAtUtc)}, or the time you are late is recorded.";

            OpenChoice();
            Play(SystemSounds.Asterisk);
        });
    }

    private void OpenChoice()
    {
        ShowScanCard(ChoiceCard);
        StopPulse();

        void Label() => ChoiceCancelText.Text = $"Cancel ({_remaining}s)";
        StartCountdown(ChoiceSeconds, Label, () => FinishChoice(ScanChoice.Cancelled, null, null));
        Label();
        ChoiceCard.Focus();
    }

    private void FinishChoice(ScanChoice choice, string? reason, DateTime? returnAtLocal)
    {
        _countdown.Stop();
        var handler = _choiceHandler;
        _choiceHandler = null;
        CloseScanLayer();
        handler?.Invoke(choice, reason, returnAtLocal);
    }

    private void ChooseLunch_Click(object sender, RoutedEventArgs e) => FinishChoice(ScanChoice.Lunch, null, null);

    private void ChooseClockOut_Click(object sender, RoutedEventArgs e) => FinishChoice(ScanChoice.ClockOut, null, null);

    private void ChoiceCancel_Click(object sender, RoutedEventArgs e) => FinishChoice(ScanChoice.Cancelled, null, null);

    private void ChooseTemporary_Click(object sender, RoutedEventArgs e)
    {
        foreach (var chip in ReasonChips.Children.OfType<RadioButton>()) chip.IsChecked = false;
        ReasonBox.Text = string.Empty;
        TemporaryError.Visibility = Visibility.Collapsed;
        FillReturnTimes();

        ShowScanCard(TemporaryCard);
        StartCountdown(TemporaryIdleSeconds, null, () => FinishChoice(ScanChoice.Cancelled, null, null));
    }

    // Quarter-hours from now until the end of today: anything earlier or on another day can't be picked.
    private void FillReturnTimes()
    {
        var now = DateTime.Now;
        var next = now.Date.AddHours(now.Hour).AddMinutes((now.Minute / 15 + 1) * 15);
        if (next - now < TimeSpan.FromMinutes(5)) next = next.AddMinutes(15);

        var times = new List<ReturnTime>();
        for (var t = next; t.Date == now.Date; t = t.AddMinutes(15))
            times.Add(new ReturnTime(t));

        ReturnTimeCombo.ItemsSource = times;
        ReturnTimeCombo.SelectedIndex = -1;
    }

    private sealed record ReturnTime(DateTime At)
    {
        public override string ToString()
        {
            var away = At - DateTime.Now;
            var length = away.TotalHours >= 1 ? $"{(int)away.TotalHours}h {away.Minutes:00}m" : $"{Math.Max(1, (int)Math.Round(away.TotalMinutes))}m";
            return $"{At.ToString("h:mm tt", CultureInfo.InvariantCulture)}   (in {length})";
        }
    }

    private void ReasonChip_Checked(object sender, RoutedEventArgs e)
    {
        KeepAlive();
        if (sender is not RadioButton { Tag: string reason }) return;

        ReasonBox.Text = reason;
        if (reason.Length == 0) ReasonBox.Focus();
    }

    private void ReasonBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        KeepAlive();
        ReasonPlaceholder.Visibility = ReasonBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TemporaryError.Visibility = Visibility.Collapsed;
    }

    private void ReturnTimeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        KeepAlive();
        TemporaryError.Visibility = Visibility.Collapsed;
    }

    private void KeepAlive()
    {
        if (TemporaryCard.Visibility == Visibility.Visible) _remaining = TemporaryIdleSeconds;
    }

    private void TemporaryBack_Click(object sender, RoutedEventArgs e) => OpenChoice();

    private void TemporaryConfirm_Click(object sender, RoutedEventArgs e)
    {
        var reason = ReasonBox.Text.Trim();
        if (reason.Length == 0)
        {
            ShowTemporaryError("Please give a reason for leaving.");
            return;
        }

        if (ReturnTimeCombo.SelectedItem is not ReturnTime back)
        {
            ShowTemporaryError("Please choose what time you will be back.");
            return;
        }

        if (back.At <= DateTime.Now)
        {
            FillReturnTimes();
            ShowTemporaryError("That time has passed. Please choose a later one.");
            return;
        }

        FinishChoice(ScanChoice.Temporary, reason, back.At);
    }

    private void ShowTemporaryError(string text)
    {
        TemporaryError.Text = text;
        TemporaryError.Visibility = Visibility.Visible;
        Play(SystemSounds.Exclamation);
    }

    // ----- Plumbing for the reader layer -----

    private void ShowScanCard(FrameworkElement card)
    {
        foreach (var c in new[] { ScanCard, MessageCard, ChoiceCard, TemporaryCard })
            c.Visibility = ReferenceEquals(c, card) ? Visibility.Visible : Visibility.Collapsed;

        HintToast.Visibility = Visibility.Collapsed;
        ScanLayer.Visibility = Visibility.Visible;
        Appear(card);
    }

    private void CloseScanLayer()
    {
        _countdown.Stop();
        _deliver.Stop();
        _deliverAction = null;
        _scanWatchdog.Stop();
        _scanStartedUtc = DateTime.MinValue;
        StopPulse();

        foreach (var c in new FrameworkElement[] { ScanCard, MessageCard, ChoiceCard, TemporaryCard })
            c.Visibility = Visibility.Collapsed;
        ScanLayer.Visibility = Visibility.Collapsed;
    }

    private void StartCountdown(int seconds, Action? onTick, Action onDone)
    {
        _countdown.Stop();
        _remaining = seconds;
        _onCountdownTick = onTick;
        _onCountdownDone = onDone;
        _countdown.Start();
    }

    private void AfterMinScan(Action show)
    {
        var wait = _scanStartedUtc == DateTime.MinValue ? 0 : MinScanMs - (DateTime.UtcNow - _scanStartedUtc).TotalMilliseconds;
        if (wait <= 0)
        {
            show();
            return;
        }

        _deliver.Stop();
        _deliverAction = show;
        _deliver.Interval = TimeSpan.FromMilliseconds(wait);
        _deliver.Start();
    }

    private void ScanCancel_Click(object sender, RoutedEventArgs e) => CloseScanLayer();

    private void MessageClose_Click(object sender, RoutedEventArgs e) => CloseScanLayer();

    private void StartPulse()
    {
        var pulse = new DoubleAnimation(0.82, 1, TimeSpan.FromSeconds(0.9))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        ScanPulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        ScanPulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    private void StopPulse()
    {
        ScanPulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ScanPulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    // ===================================================================
    //  Back Office dialogs
    // ===================================================================

    /// <summary>The application's own version of a yes-or-no box.</summary>
    public Task<bool> ConfirmAsync(string title, string body, string yesText, bool dangerous = false)
    {
        _confirm?.TrySetResult(false);
        _confirm = new TaskCompletionSource<bool>();

        ConfirmTitle.Text = title;
        ConfirmBody.Text = body;
        ConfirmYesButton.Content = yesText;
        ConfirmYesButton.Style = (Style)FindResource(dangerous ? "DangerBtn" : "PrimaryBtn");
        ConfirmHalo.Fill = Res(dangerous ? "ErrorSoftBrush" : "BlueSoftBrush");
        ConfirmDot.Fill = Res(dangerous ? "ErrorBrush" : "BlueBrush");
        ConfirmIcon.Text = dangerous ? "" : "";

        ShowDialogCard(ConfirmCard);
        return _confirm.Task;
    }

    public void ShowList(string title, string subtitle, IReadOnlyList<PopupListRow> rows, string emptyText)
    {
        ListTitle.Text = title;
        ListSub.Text = subtitle;
        ListItems.ItemsSource = rows;
        ListEmpty.Text = emptyText;
        ListEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowDialogCard(ListCard);
    }

    private void ShowDialogCard(FrameworkElement card)
    {
        ConfirmCard.Visibility = ReferenceEquals(ConfirmCard, card) ? Visibility.Visible : Visibility.Collapsed;
        ListCard.Visibility = ReferenceEquals(ListCard, card) ? Visibility.Visible : Visibility.Collapsed;
        DialogLayer.Visibility = Visibility.Visible;
        Appear(card);
    }

    private void CloseDialogLayer()
    {
        ConfirmCard.Visibility = Visibility.Collapsed;
        ListCard.Visibility = Visibility.Collapsed;
        DialogLayer.Visibility = Visibility.Collapsed;
    }

    private void AnswerConfirm(bool answer)
    {
        CloseDialogLayer();
        var pending = _confirm;
        _confirm = null;
        pending?.TrySetResult(answer);
    }

    private void ConfirmYes_Click(object sender, RoutedEventArgs e) => AnswerConfirm(true);

    private void ConfirmNo_Click(object sender, RoutedEventArgs e) => AnswerConfirm(false);

    private void ListClose_Click(object sender, RoutedEventArgs e) => CloseDialogLayer();

    /// <summary>Clears anything left open when the Back Office locks.</summary>
    public void CloseDialogs()
    {
        if (ConfirmCard.Visibility == Visibility.Visible) AnswerConfirm(false);
        CloseDialogLayer();
    }

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (ScanLayer.Visibility == Visibility.Visible)
        {
            if (IsChoosing) FinishChoice(ScanChoice.Cancelled, null, null);
            else CloseScanLayer();
            e.Handled = true;
        }
        else if (DialogLayer.Visibility == Visibility.Visible)
        {
            if (ConfirmCard.Visibility == Visibility.Visible) AnswerConfirm(false);
            else CloseDialogLayer();
            e.Handled = true;
        }
    }

    // ===================================================================
    //  Helpers
    // ===================================================================

    private static void Appear(FrameworkElement card)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        if (card.RenderTransform is ScaleTransform scale && !scale.IsFrozen)
        {
            var grow = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private static void Play(SystemSound sound)
    {
        try { sound.Play(); } catch { /* sound is a nicety; never disturb clock-in */ }
    }

    public static string Clock(DateTime utc) => utc.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);

    public static string FirstName(string fullName) =>
        fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? fullName;

    public static string Initials(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][0].ToString().ToUpperInvariant();
        return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }
}
