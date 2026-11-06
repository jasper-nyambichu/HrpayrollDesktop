using HrpayrollDesktop.Auth;
using HrpayrollDesktop.Sync;
using HrpayrollDesktop.Terminal;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HrpayrollDesktop;

/// <summary>Who the sign-in is for: the Back Office console, or a Branch Manager's manual check-in list.</summary>
public enum LoginPurpose { BackOffice, ManualCheckIn }

public partial class LoginView : UserControl
{
    private BackendApiClient? _apiClient;
    private ITerminalAdapter? _adapter;
    private bool _busy;

    public event Action<AuthResponse>? LoginSucceeded;
    public event Action? Cancelled;

    /// <summary>This PC's reader got a (new) key from the server: scans saved meanwhile can be sent now.</summary>
    public event Action? ReaderKeyRenewed;

    /// <summary>What the last successful sign-in was for.</summary>
    public LoginPurpose Purpose { get; private set; } = LoginPurpose.BackOffice;

    public LoginView()
    {
        InitializeComponent();
    }

    public void Initialize(BackendApiClient apiClient, ITerminalAdapter adapter)
    {
        _apiClient = apiClient;
        _adapter = adapter;
    }

    public void SetTerminalLabel(string text) => TerminalPillText.Text = text;

    /// <summary>Clears the form for the given purpose; call right before showing the overlay.</summary>
    public void Reset(LoginPurpose purpose = LoginPurpose.BackOffice)
    {
        Purpose = purpose;
        var manager = purpose == LoginPurpose.ManualCheckIn;
        BrandRoleText.Text = manager ? "Branch Manager" : "Back Office";
        BrandHeadlineText.Text = manager ? "Reader not working? Check staff in by hand." : "Your branch, one clear view.";
        BrandBodyText.Text = manager
            ? "Tick the employees who are here and they are checked in at the server's time, under your name."
            : "Attendance, fingerprints, days worked and damages for your branch.";
        FormTitleText.Text = manager ? "Branch Manager sign-in" : "Back Office sign-in";
        FormSubText.Text = manager
            ? "Only a Branch Manager can check employees in manually. Use the login your administrator gave you."
            : "Use the username and password your administrator gave you.";

        UsernameBox.Text = string.Empty;
        ClearPassword(PasswordBox);
        ShowError(null);
        ShowInfo(null);
        LoadingText.Visibility = Visibility.Collapsed;
        SetInputsEnabled(true);
        LeaveReset();

        Dispatcher.BeginInvoke(() => UsernameBox.Focus(), DispatcherPriority.Input);
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _apiClient is null) return;

        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            ShowError("Enter both username and password.");
            return;
        }

        _busy = true;
        ShowError(null);
        ShowInfo(null);
        LoadingText.Visibility = Visibility.Visible;
        SetInputsEnabled(false);

        try
        {
            var result = await _apiClient.LoginAsync(username, password, CancellationToken.None);

            if (!result.Succeeded || result.Session is null)
            {
                ShowError(result.Error ?? "Login failed.");
                return;
            }

            if (Purpose == LoginPurpose.ManualCheckIn)
            {
                if (await CheckManagerAsync(result.Session) is { } refusal)
                {
                    ShowError(refusal);
                    return;
                }

                ClearPassword(PasswordBox);
                LoginSucceeded?.Invoke(result.Session);
                return;
            }

            if (result.Session.Role != "BACK_OFFICE")
            {
                ShowError(result.Session.Role == "BRANCH_MANAGER"
                    ? "Branch Managers use \"Manual check-in\" on the home screen. This sign-in is for the Back Office."
                    : "This device is for Back Office login only.");
                return;
            }

            if (result.Session.BranchId is not long branchId)
            {
                ShowError("This account isn't assigned to a branch yet. Ask your administrator.");
                return;
            }

            // A Back Office works at its own branch only, so it can't use another branch's reader.
            if (_apiClient.Identity is { } identity)
            {
                var readers = await _apiClient.GetDevicesByBranchAsync(branchId, result.Session.AccessToken, CancellationToken.None);
                if (readers is not null && !readers.Exists(r => string.Equals(r.DeviceSerial, identity.Serial, StringComparison.Ordinal)))
                {
                    ShowError("This reader belongs to a different branch. Sign in on your own branch's computer.");
                    return;
                }

                // The server no longer takes this PC's reader key: the reader was set up on another PC since,
                // which issues a new key. Signing in here takes it back, so the scans saved here can be sent.
                if (await _apiClient.CheckDeviceCredentialsAsync(CancellationToken.None) == false)
                {
                    var (renewed, error) = await _apiClient.RegisterDeviceAsync(
                        branchId, identity.Serial, result.Session.AccessToken, CancellationToken.None);
                    if (renewed is null)
                    {
                        ShowError(FriendlyMessages.Translate(error));
                        return;
                    }

                    DeviceCredentialStore.Save(new DeviceCredentials(renewed.DeviceSerial, renewed.PlaintextToken));
                    _apiClient.ConfigureDeviceCredentials(renewed.DeviceSerial, renewed.PlaintextToken);
                    ReaderKeyRenewed?.Invoke();
                }
            }
            else if (!string.IsNullOrWhiteSpace(_adapter?.DeviceSerial))
            {
                // First sign-in on this PC: the reader joins this login's branch. The token is shown only once.
                var (registration, error) = await _apiClient.RegisterDeviceAsync(
                    branchId, _adapter.DeviceSerial, result.Session.AccessToken, CancellationToken.None);
                if (registration is null)
                {
                    ShowError(FriendlyMessages.Translate(error));
                    return;
                }

                DeviceCredentialStore.Save(new DeviceCredentials(registration.DeviceSerial, registration.PlaintextToken));
                _apiClient.ConfigureDeviceCredentials(registration.DeviceSerial, registration.PlaintextToken);
                ReaderKeyRenewed?.Invoke();
            }

            ClearPassword(PasswordBox);
            LoginSucceeded?.Invoke(result.Session);
        }
        catch (HttpRequestException)
        {
            ShowError("Can't reach the server. Check the internet connection and try again.");
        }
        catch (TaskCanceledException)
        {
            ShowError("The server took too long to respond. Please try again.");
        }
        finally
        {
            _busy = false;
            LoadingText.Visibility = Visibility.Collapsed;
            SetInputsEnabled(true);
        }
    }

    // A Branch Manager may check people in only on a reader that is already registered to their own branch.
    // The reader itself does not have to be working: this list is for exactly when it isn't.
    private async Task<string?> CheckManagerAsync(AuthResponse session)
    {
        if (session.Role != "BRANCH_MANAGER")
            return "Only a Branch Manager can check employees in manually. The Back Office can't.";

        if (session.BranchId is not long branchId)
            return "This account isn't assigned to a branch yet. Ask your administrator.";

        if (_apiClient?.Identity is not { } identity)
            return "This computer isn't activated yet. A Back Office must sign in here once to link it to its branch.";

        var readers = await _apiClient.GetDevicesByBranchAsync(branchId, session.AccessToken, CancellationToken.None);
        if (readers is null)
            return "Couldn't confirm which branch this computer belongs to. Check the internet connection and try again.";

        return readers.Exists(r => string.Equals(r.DeviceSerial, identity.Serial, StringComparison.Ordinal))
            ? null
            : "This computer belongs to a different branch. Sign in on your own branch's computer.";
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            LoginButton_Click(sender, e);
        }
    }

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _busy) return;
        e.Handled = true;

        // Escape steps back one screen. The new-password pop-up ignores it, so a stray key can't lose the checked code.
        if (NewPasswordLayer.Visibility == Visibility.Visible) return;
        if (ResetPanel.Visibility == Visibility.Visible)
        {
            LeaveReset();
            UsernameBox.Focus();
            return;
        }
        Cancelled?.Invoke();
    }

    // ------------------------------------------------------------------ forgotten password
    // The same steps as the web sign-in pages: username or email, the emailed 6-digit code, then a pop-up for the
    // new password. Works for whoever signs in here: a Back Office, or a Branch Manager for manual check-in.

    private const int ResendSeconds = 60;
    private string _resetIdentifier = string.Empty;
    private string? _resetToken;
    private int _resendIn;
    private DispatcherTimer? _resendTimer;

    private void Forgot_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ShowError(null);
        ShowInfo(null);
        SignInPanel.Visibility = Visibility.Collapsed;
        ResetPanel.Visibility = Visibility.Visible;
        ShowResetStep(code: false);
        IdentifierBox.Text = UsernameBox.Text.Trim();
        Dispatcher.BeginInvoke(() => IdentifierBox.Focus(), DispatcherPriority.Input);
    }

    private void BackToSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        LeaveReset();
        UsernameBox.Focus();
    }

    private void LeaveReset()
    {
        _resendTimer?.Stop();
        _resendIn = 0;
        _resetToken = null;
        _resetIdentifier = string.Empty;
        IdentifierBox.Text = string.Empty;
        CodeBox.Text = string.Empty;
        ClearPassword(NewPasswordBox);
        ClearPassword(ConfirmPasswordBox);
        NewPasswordLayer.Visibility = Visibility.Collapsed;
        ResetPanel.Visibility = Visibility.Collapsed;
        SignInPanel.Visibility = Visibility.Visible;
        ShowResetError(null);
        ShowResetBusy(null);
    }

    private void ShowResetStep(bool code)
    {
        IdentifierStep.Visibility = code ? Visibility.Collapsed : Visibility.Visible;
        CodeStep.Visibility = code ? Visibility.Visible : Visibility.Collapsed;
        ResetTitleText.Text = code ? "Check your email" : "Forgot your password?";
        ResetSubText.Text = code
            ? "If that account has an email address, a 6-digit code is on its way to it. Type the code here."
            : "Enter your username or email. We will send a 6-digit code to the email on your account.";
        ShowResetError(null);
    }

    private void Identifier_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SendCode_Click(sender, e);
    }

    private async void SendCode_Click(object sender, RoutedEventArgs e)
    {
        var identifier = IdentifierBox.Text.Trim();
        if (identifier.Length == 0)
        {
            ShowResetError("Enter your username or email.");
            return;
        }
        await SendCodeAsync(identifier);
    }

    private async void Resend_Click(object sender, RoutedEventArgs e)
    {
        if (_resendIn > 0 || _resetIdentifier.Length == 0) return;
        await SendCodeAsync(_resetIdentifier);
    }

    private async Task SendCodeAsync(string identifier)
    {
        if (_busy || _apiClient is null) return;

        await RunResetStepAsync("Sending the code…", async () =>
        {
            var error = await _apiClient.RequestPasswordResetCodeAsync(identifier, CancellationToken.None);
            if (error is not null)
            {
                ShowResetError(error);
                return;
            }

            _resetIdentifier = identifier;
            CodeBox.Text = string.Empty;
            ShowResetStep(code: true);
            StartResendCountdown();
        });
        if (CodeStep.Visibility == Visibility.Visible)
            _ = Dispatcher.BeginInvoke(() => CodeBox.Focus(), DispatcherPriority.Input);
    }

    private void StartResendCountdown()
    {
        _resendIn = ResendSeconds;
        _resendTimer ??= CreateResendTimer();
        _resendTimer.Start();
        UpdateResendButton();
    }

    private DispatcherTimer CreateResendTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            _resendIn = Math.Max(0, _resendIn - 1);
            if (_resendIn == 0) timer.Stop();
            UpdateResendButton();
        };
        return timer;
    }

    private void UpdateResendButton()
    {
        ResendButton.Content = _resendIn > 0 ? $"Send a new code in {_resendIn}s" : "Send a new code";
        ResendButton.IsEnabled = _resendIn == 0 && !_busy;
    }

    // Digits only, so a code copied with spaces or a dash still goes in cleanly.
    private void Code_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        foreach (var ch in e.Text)
        {
            if (!char.IsAsciiDigit(ch))
            {
                e.Handled = true;
                return;
            }
        }
    }

    private void Code_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted) return;
        var digits = new string(Array.FindAll(pasted.ToCharArray(), char.IsAsciiDigit));
        CodeBox.Text = digits.Length > 6 ? digits[..6] : digits;
        CodeBox.CaretIndex = CodeBox.Text.Length;
    }

    // All six digits typed: check it straight away, as the web page does.
    private void Code_TextChanged(object sender, TextChangedEventArgs e)
    {
        ShowResetError(null);
        if (CodeBox.Text.Length == 6 && !_busy && ResetPanel.Visibility == Visibility.Visible) Verify_Click(sender, new RoutedEventArgs());
    }

    private void Code_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Verify_Click(sender, e);
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _apiClient is null) return;

        var code = CodeBox.Text.Trim();
        if (code.Length != 6)
        {
            ShowResetError("Enter the 6-digit code from the email.");
            return;
        }

        var verified = false;
        string? refusal = null;
        await RunResetStepAsync("Checking the code…", async () =>
        {
            var (token, error) = await _apiClient.VerifyPasswordResetCodeAsync(_resetIdentifier, code, CancellationToken.None);
            if (token is null)
            {
                refusal = error ?? "That code is wrong or has expired. Check it, or ask for a new one.";
                return;
            }
            _resetToken = token;
            verified = true;
        });

        if (verified)
        {
            OpenNewPasswordPopup();
        }
        else if (refusal is not null)
        {
            CodeBox.Text = string.Empty;   // clears the error too, so it is shown after
            ShowResetError(refusal);
            CodeBox.Focus();
        }
    }

    private void OpenNewPasswordPopup()
    {
        ClearPassword(NewPasswordBox);
        ClearPassword(ConfirmPasswordBox);
        ShowNewPasswordError(null);
        NewPasswordLayer.Visibility = Visibility.Visible;

        if (NewPasswordCard.RenderTransform is ScaleTransform scale)
        {
            var grow = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        NewPasswordCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

        Dispatcher.BeginInvoke(() => FocusPassword(NewPasswordBox), DispatcherPriority.Input);
    }

    private void NewPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (sender == NewPasswordBox || sender == NewPasswordPlain) FocusPassword(ConfirmPasswordBox);
        else SavePassword_Click(sender, e);
    }

    private async void SavePassword_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _apiClient is null || _resetToken is null) return;

        var next = NewPasswordBox.Password;
        var confirm = ConfirmPasswordBox.Password;
        if (PasswordRuleProblem(next) is { } problem)
        {
            ShowNewPasswordError(problem);
            FocusPassword(NewPasswordBox);
            return;
        }
        if (next != confirm)
        {
            ShowNewPasswordError("The two passwords don't match.");
            FocusPassword(ConfirmPasswordBox);
            return;
        }

        _busy = true;
        ShowNewPasswordError(null);
        SetNewPasswordEnabled(false);
        SavePasswordText.Text = "Saving…";
        string? error;
        try
        {
            error = await _apiClient.ResetPasswordAsync(_resetToken, next, CancellationToken.None);
        }
        catch (HttpRequestException)
        {
            error = "Can't reach the server. Check the internet connection and try again.";
        }
        catch (TaskCanceledException)
        {
            error = "The server took too long to respond. Please try again.";
        }
        finally
        {
            _busy = false;
            SetNewPasswordEnabled(true);
            SavePasswordText.Text = "Save new password";
        }

        if (error is not null)
        {
            ShowNewPasswordError(error);
            return;
        }

        // Back to sign-in, ready for the new password.
        var identifier = _resetIdentifier;
        LeaveReset();
        UsernameBox.Text = identifier.Contains('@') ? string.Empty : identifier;
        ClearPassword(PasswordBox);
        ShowInfo("Password changed. Sign in with your new password.");
        Control focus = UsernameBox.Text.Length == 0 ? UsernameBox : PasswordBox;
        _ = Dispatcher.BeginInvoke(() => focus.Focus(), DispatcherPriority.Input);
    }

    private void CancelNewPassword_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        // Start over rather than leave a half-finished reset; the checked token simply expires.
        _resetToken = null;
        NewPasswordLayer.Visibility = Visibility.Collapsed;
        CodeBox.Text = string.Empty;
        ShowResetStep(code: false);
        IdentifierBox.Text = _resetIdentifier;
        IdentifierBox.Focus();
    }

    // The server's rules, checked here first so the message is clear.
    private static string? PasswordRuleProblem(string password)
    {
        if (password.Length < 8) return "Use at least 8 characters.";
        if (password.Length > 72) return "Use 72 characters or fewer.";
        bool upper = false, lower = false, digit = false;
        foreach (var ch in password)
        {
            upper |= char.IsUpper(ch);
            lower |= char.IsLower(ch);
            digit |= char.IsAsciiDigit(ch);
        }
        return upper && lower && digit ? null : "The password needs an uppercase letter, a lowercase letter and a number.";
    }

    private async Task RunResetStepAsync(string busyText, Func<Task> step)
    {
        _busy = true;
        ShowResetError(null);
        ShowResetBusy(busyText);
        SetResetEnabled(false);
        try
        {
            await step();
        }
        catch (HttpRequestException)
        {
            ShowResetError("Can't reach the server. Check the internet connection and try again.");
        }
        catch (TaskCanceledException)
        {
            ShowResetError("The server took too long to respond. Please try again.");
        }
        finally
        {
            _busy = false;
            ShowResetBusy(null);
            SetResetEnabled(true);
        }
    }

    private void SetResetEnabled(bool enabled)
    {
        IdentifierBox.IsEnabled = enabled;
        SendCodeButton.IsEnabled = enabled;
        CodeBox.IsEnabled = enabled;
        VerifyButton.IsEnabled = enabled;
        UpdateResendButton();
    }

    private void SetNewPasswordEnabled(bool enabled)
    {
        NewPasswordBox.IsEnabled = NewPasswordPlain.IsEnabled = NewPasswordEye.IsEnabled = enabled;
        ConfirmPasswordBox.IsEnabled = ConfirmPasswordPlain.IsEnabled = ConfirmPasswordEye.IsEnabled = enabled;
        SavePasswordButton.IsEnabled = enabled;
        CancelPasswordButton.IsEnabled = enabled;
    }

    private void ShowResetError(string? message)
    {
        ResetErrorText.Text = message ?? string.Empty;
        ResetErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowResetBusy(string? message)
    {
        ResetBusyText.Text = message ?? string.Empty;
        ResetBusyText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowNewPasswordError(string? message)
    {
        NewPasswordErrorText.Text = message ?? string.Empty;
        NewPasswordErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowInfo(string? message)
    {
        InfoText.Text = message ?? string.Empty;
        InfoText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetInputsEnabled(bool enabled)
    {
        UsernameBox.IsEnabled = enabled;
        PasswordBox.IsEnabled = PasswordPlain.IsEnabled = PasswordEye.IsEnabled = enabled;
        LoginButton.IsEnabled = enabled;
    }

    private void ShowError(string? message)
    {
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ show / hide password
    // Each password field has a plain text box beside it, swapped in while the eye is on. The PasswordBox always
    // holds the value, kept in step as they type, so everything that reads .Password works either way.

    private (PasswordBox Box, TextBox Plain, Button Eye) RevealPair(PasswordBox box) =>
        box == NewPasswordBox ? (NewPasswordBox, NewPasswordPlain, NewPasswordEye)
        : box == ConfirmPasswordBox ? (ConfirmPasswordBox, ConfirmPasswordPlain, ConfirmPasswordEye)
        : (PasswordBox, PasswordPlain, PasswordEye);

    private PasswordBox BoxNamed(object? tag) => (PasswordBox)FindName((string)tag!);

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        var box = BoxNamed(((Button)sender).Tag);
        SetRevealed(box, RevealPair(box).Plain.Visibility != Visibility.Visible);
        FocusPassword(box);
    }

    private void Plain_TextChanged(object sender, TextChangedEventArgs e)
    {
        var plain = (TextBox)sender;
        if (plain.Visibility == Visibility.Visible) BoxNamed(plain.Tag).Password = plain.Text;
    }

    private void SetRevealed(PasswordBox box, bool show)
    {
        var (_, plain, eye) = RevealPair(box);
        if (show == (plain.Visibility == Visibility.Visible)) return;

        if (show) plain.Text = box.Password;
        plain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        box.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (!show) plain.Text = string.Empty;   // never left lying around in a hidden box
        eye.Content = show ? "" : "";
        eye.ToolTip = show ? "Hide password" : "Show password";
    }

    // Empties a password field and hides it again, so the next person never sees what was typed.
    private void ClearPassword(PasswordBox box)
    {
        SetRevealed(box, false);
        box.Password = string.Empty;
    }

    private void FocusPassword(PasswordBox box)
    {
        var plain = RevealPair(box).Plain;
        if (plain.Visibility == Visibility.Visible)
        {
            plain.Focus();
            plain.CaretIndex = plain.Text.Length;
        }
        else
        {
            box.Focus();
        }
    }
}