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
using System.Windows.Threading;

namespace HrpayrollDesktop;

public partial class LoginView : UserControl
{
    private BackendApiClient? _apiClient;
    private ITerminalAdapter? _adapter;
    private bool _busy;

    public event Action<AuthResponse>? LoginSucceeded;
    public event Action? Cancelled;

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

    /// <summary>Clears the form; call right before showing the overlay.</summary>
    public void Reset()
    {
        UsernameBox.Text = string.Empty;
        PasswordBox.Password = string.Empty;
        ShowError(null);
        LoadingText.Visibility = Visibility.Collapsed;
        SetInputsEnabled(true);

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

            if (result.Session.Role != "BACK_OFFICE")
            {
                ShowError("This device is for Back Office login only.");
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
            }

            PasswordBox.Password = string.Empty;
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
        if (e.Key == Key.Escape && !_busy)
        {
            e.Handled = true;
            Cancelled?.Invoke();
        }
    }

    private void SetInputsEnabled(bool enabled)
    {
        UsernameBox.IsEnabled = enabled;
        PasswordBox.IsEnabled = enabled;
        LoginButton.IsEnabled = enabled;
    }

    private void ShowError(string? message)
    {
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}