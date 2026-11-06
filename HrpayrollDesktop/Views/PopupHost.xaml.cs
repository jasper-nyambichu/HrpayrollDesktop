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

/// <summary>What the person at the reader chose. Reason and Minutes are only set for a temporary check-out.</summary>
public enum ScanChoice { Lunch, Temporary, ClockOut, Cancelled }

/// <summary>One scan in the sync card.</summary>
public sealed record SyncScanRow(string Name, string What, string When, string Note, Brush NoteFg, Brush RowBg, Brush AvatarBg, Brush AvatarFg)
{
    public string Initials => PopupHost.Initials(Name);
}

/// <summary>Everything the sync card shows.</summary>
public sealed record SyncOverview(
    SyncTone Tone,
    string Title,
    string Body,
    int Waiting,
    int Rejected,
    string LastSent,
    IReadOnlyList<SyncScanRow> RejectedRows,
    IReadOnlyList<SyncScanRow> WaitingRows);

/// <summary>
/// Every pop-up in the application. Reader pop-ups sit on their own layer above Back Office dialogs, so someone
/// scanning at the reader never disturbs an officer's unfinished question.
/// </summary>
public partial class PopupHost : UserControl
{
    private const int MinScanMs = 900;      // keep "Scanning" visible at least this long, so it doesn't flicker
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

    private Action<ScanChoice, string?, int?>? _choiceHandler;
    private DateTime _choiceScannedAtUtc;
    private TaskCompletionSource<bool>? _confirm;
    private Action? _syncNow;

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
        ScanPrint.SetState(ScannerState.Ready);

        StartCountdown(ScanPromptSeconds, null, CloseScanLayer);
    }

    /// <summary>
    /// A usable fingerprint was captured and matching has started. With <paramref name="showCard"/> false the
    /// home screen's own scanner shows the scanning, and this only times the result so it doesn't flash past.
    /// </summary>
    public void BeginScan(bool showCard = true)
    {
        if (IsChoosing) return;

        _deliver.Stop();
        _deliverAction = null;
        _countdown.Stop();
        _scanStartedUtc = DateTime.UtcNow;

        if (!showCard)
        {
            if (ScanCard.Visibility == Visibility.Visible) CloseScanLayer();
            _scanStartedUtc = DateTime.UtcNow;
            return;
        }

        _scanWatchdog.Stop();
        _scanWatchdog.Start();

        ScanTitle.Text = "Scanning…";
        ScanBody.Text = "Matching your fingerprint. Keep your finger still.";
        ScanCancelButton.Visibility = Visibility.Collapsed;
        ShowScanCard(ScanCard);
        ScanPrint.SetState(ScannerState.Scanning);
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
    public void ShowChoice(string name, string? detail, DateTime scannedAtUtc, DateTime? clockedInAtUtc, DateTime lunchBackAtUtc,
        Action<ScanChoice, string?, int?> handler, bool lunchTaken = false)
    {
        // Set straight away: another finger arriving now must not cancel this person's turn.
        _choiceHandler = handler;
        _choiceScannedAtUtc = scannedAtUtc;
        _scanWatchdog.Stop();

        AfterMinScan(() =>
        {
            _scanStartedUtc = DateTime.MinValue;

            ChoiceInitials.Text = Initials(name);
            ChoiceTitle.Text = $"Hello, {FirstName(name)}";
            var since = clockedInAtUtc is { } at ? $"At work since {Clock(at)}" : "You are at work";
            ChoiceSub.Text = string.IsNullOrWhiteSpace(detail) ? since : $"{since}  •  {detail}";
            TemporarySub.Text = name;
            // Lunch is once a day.
            LunchOptionButton.IsEnabled = !lunchTaken;
            LunchOptionButton.Opacity = lunchTaken ? 0.5 : 1;
            LunchOptionDetail.Text = lunchTaken
                ? "You have already had your lunch break today."
                : $"30 minutes. Be back by {Clock(lunchBackAtUtc)}, or the time you are late is recorded.";

            OpenChoice();
            Play(SystemSounds.Asterisk);
        });
    }

    private void OpenChoice()
    {
        ShowScanCard(ChoiceCard);

        void Label() => ChoiceCancelText.Text = $"Cancel ({_remaining}s)";
        StartCountdown(ChoiceSeconds, Label, () => FinishChoice(ScanChoice.Cancelled, null, null));
        Label();
        ChoiceCard.Focus();
    }

    private void FinishChoice(ScanChoice choice, string? reason, int? minutes)
    {
        _countdown.Stop();
        var handler = _choiceHandler;
        _choiceHandler = null;
        CloseScanLayer();
        handler?.Invoke(choice, reason, minutes);
    }

    private void ChooseLunch_Click(object sender, RoutedEventArgs e) => FinishChoice(ScanChoice.Lunch, null, null);

    private void ChooseClockOut_Click(object sender, RoutedEventArgs e) => FinishChoice(ScanChoice.ClockOut, null, null);

    private void ChoiceCancel_Click(object sender, RoutedEventArgs e) => FinishChoice(ScanChoice.Cancelled, null, null);

    private void ChooseTemporary_Click(object sender, RoutedEventArgs e)
    {
        foreach (var chip in ReasonChips.Children.OfType<RadioButton>()) chip.IsChecked = false;
        ReasonBox.Text = string.Empty;
        MinutesBox.Text = string.Empty;
        TemporaryError.Visibility = Visibility.Collapsed;
        UpdateMinutesPreview();

        ShowScanCard(TemporaryCard);
        StartCountdown(TemporaryIdleSeconds, null, () => FinishChoice(ScanChoice.Cancelled, null, null));
    }

    // The temporary check-out must end the same day it started (the server refuses anything later).
    private int MaxMinutesToday()
    {
        var scanned = _choiceScannedAtUtc.ToLocalTime();
        return Math.Max(0, (int)Math.Floor((scanned.Date.AddDays(1) - scanned).TotalMinutes) - 1);
    }

    private DateTime BackAtLocal(int minutes) => _choiceScannedAtUtc.ToLocalTime().AddMinutes(minutes);

    private static string Length(int minutes) =>
        minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes} minute{(minutes == 1 ? "" : "s")}";

    private void UpdateMinutesPreview()
    {
        MinutesPlaceholder.Visibility = MinutesBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!int.TryParse(MinutesBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes == 0)
        {
            MinutesPreview.Text = "Numbers only. You will see when you are expected back.";
            MinutesPreview.Foreground = Res("MutedBrush");
            return;
        }

        if (minutes > MaxMinutesToday())
        {
            MinutesPreview.Text = $"That goes past midnight. Today you can be away for at most {MaxMinutesToday()} minutes.";
            MinutesPreview.Foreground = Res("ErrorBrush");
            return;
        }

        MinutesPreview.Text = $"{Length(minutes)} away. Expected back at {BackAtLocal(minutes).ToString("h:mm tt", CultureInfo.InvariantCulture)}.";
        MinutesPreview.Foreground = Res("PrimaryBrush");
    }

    // Only digits can go in, whether typed, pasted or dropped.
    private void MinutesBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = e.Text.Length == 0 || !e.Text.All(char.IsAsciiDigit);

    private void MinutesBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) e.Handled = true;   // a space never goes through PreviewTextInput
    }

    private void MinutesBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        var text = e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText, true)
            ? e.SourceDataObject.GetData(DataFormats.UnicodeText) as string
            : null;
        if (string.IsNullOrEmpty(text) || !text.All(char.IsAsciiDigit)) e.CancelCommand();
    }

    private void MinutesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        KeepAlive();
        TemporaryError.Visibility = Visibility.Collapsed;
        UpdateMinutesPreview();
    }

    private void ReasonChip_Checked(object sender, RoutedEventArgs e)
    {
        KeepAlive();
        if (sender is not RadioButton { Tag: string reason }) return;

        ReasonBox.Text = reason;
        if (reason.Length == 0) ReasonBox.Focus();
        else MinutesBox.Focus();
    }

    private void ReasonBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        KeepAlive();
        ReasonPlaceholder.Visibility = ReasonBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
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

        var typed = MinutesBox.Text.Trim();
        if (typed.Length == 0)
        {
            MinutesBox.Focus();
            ShowTemporaryError("Please type how many minutes you need, in numbers.");
            return;
        }

        if (!typed.All(char.IsAsciiDigit) || !int.TryParse(typed, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            MinutesBox.Focus();
            ShowTemporaryError("Use numbers only for the minutes, for example 45.");
            return;
        }

        if (minutes < 1)
        {
            MinutesBox.Focus();
            ShowTemporaryError("The minutes must be at least 1.");
            return;
        }

        if (minutes > MaxMinutesToday())
        {
            MinutesBox.Focus();
            ShowTemporaryError($"You must be back today. The most you can enter now is {MaxMinutesToday()} minutes.");
            return;
        }

        FinishChoice(ScanChoice.Temporary, reason, minutes);
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
        ScanPrint.SetState(ScannerState.Idle);

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

    // ----- Sync status -----

    /// <summary>True while the sync card is open, so the caller knows to keep it up to date.</summary>
    public bool IsSyncOpen => DialogLayer.Visibility == Visibility.Visible && SyncCard.Visibility == Visibility.Visible;

    /// <summary>Shows what has reached the server, what is waiting, and what the server refused and why.</summary>
    public void ShowSync(SyncOverview overview, Action syncNow)
    {
        _syncNow = syncNow;
        FillSync(overview);
        SyncNowButton.IsEnabled = true;
        SyncNowText.Text = "Send now";
        ShowDialogCard(SyncCard);
    }

    /// <summary>Refreshes the open sync card in place.</summary>
    public void UpdateSync(SyncOverview overview)
    {
        if (!IsSyncOpen) return;
        FillSync(overview);
        SyncNowButton.IsEnabled = true;
        SyncNowText.Text = "Send now";
    }

    private void FillSync(SyncOverview o)
    {
        var (halo, dot, icon) = o.Tone switch
        {
            SyncTone.Good => ("GreenSoftBrush", "GreenBrush", "\uE2BF"),
            SyncTone.Busy => ("BlueSoftBrush", "BlueBrush", "\uE627"),
            SyncTone.Warning => ("AmberSoftBrush", "AmberBrush", "\uE2C1"),
            _ => ("ErrorSoftBrush", "ErrorBrush", "\uE629"),
        };
        SyncHalo.Fill = Res(halo);
        SyncDot.Fill = Res(dot);
        SyncIcon.Text = icon;
        SyncTitle.Text = o.Title;
        SyncBody.Text = o.Body;

        SyncWaitingCount.Text = o.Waiting.ToString(CultureInfo.InvariantCulture);
        SyncRejectedCount.Text = o.Rejected.ToString(CultureInfo.InvariantCulture);
        SyncRejectedCount.Foreground = Res(o.Rejected > 0 ? "ErrorBrush" : "InkBrush");
        SyncLastSent.Text = o.LastSent;

        SyncRejectedList.ItemsSource = o.RejectedRows;
        SyncWaitingList.ItemsSource = o.WaitingRows;
        SyncRejectedTitle.Visibility = SyncRejectedList.Visibility = o.RejectedRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncWaitingTitle.Visibility = SyncWaitingList.Visibility = o.WaitingRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncAllClear.Visibility = o.RejectedRows.Count == 0 && o.WaitingRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        SyncNowButton.IsEnabled = false;
        SyncNowText.Text = "Sending…";
        _syncNow?.Invoke();
    }

    private void SyncClose_Click(object sender, RoutedEventArgs e) => CloseDialogLayer();

    private void ShowDialogCard(FrameworkElement card)
    {
        foreach (var c in new FrameworkElement[] { ConfirmCard, ListCard, SyncCard })
            c.Visibility = ReferenceEquals(c, card) ? Visibility.Visible : Visibility.Collapsed;
        DialogLayer.Visibility = Visibility.Visible;
        Appear(card);
    }

    private void CloseDialogLayer()
    {
        ConfirmCard.Visibility = Visibility.Collapsed;
        ListCard.Visibility = Visibility.Collapsed;
        SyncCard.Visibility = Visibility.Collapsed;
        DialogLayer.Visibility = Visibility.Collapsed;
        _syncNow = null;
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
