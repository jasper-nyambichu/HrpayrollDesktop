using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HrpayrollDesktop.Auth;
using HrpayrollDesktop.Sync;
using HrpayrollDesktop.Terminal;

namespace HrpayrollDesktop;

/// <summary>
/// First-run setup: the Back Office signs in once, chooses the branch and registers this reader.
/// The token the server returns is saved (DPAPI-protected) and never shown. An administrator-issued
/// token can still be pasted instead.
/// </summary>
public partial class DeviceSetupWindow : Window
{
    private readonly BackendApiClient _apiClient;
    private AuthResponse? _session;
    private bool _busy;

    public DeviceSetupWindow(ITerminalAdapter terminalAdapter, BackendApiClient apiClient)
    {
        InitializeComponent();
        _apiClient = apiClient;

        // The engine has already opened the reader, so just read its serial.
        var serial = terminalAdapter.DeviceSerial;
        DeviceSerialBox.Text = string.IsNullOrWhiteSpace(serial) ? "(reader not detected)" : serial;
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            ShowStatus("Enter both username and password.");
            return;
        }

        SetBusy(true);
        try
        {
            var login = await _apiClient.LoginAsync(username, password, CancellationToken.None);
            if (!login.Succeeded || login.Session is null)
            {
                ShowStatus(login.Error ?? "Login failed.");
                return;
            }

            if (login.Session.Role is not ("BACK_OFFICE" or "ADMIN" or "SUPER_ADMIN"))
            {
                ShowStatus("Only the Back Office or an Administrator can register a reader.");
                return;
            }

            var all = await _apiClient.GetBranchesAsync(login.Session.AccessToken, CancellationToken.None);
            if (all is null)
            {
                ShowStatus("Couldn't load the branches. Check the internet connection and try again.");
                return;
            }

            var branches = all
                .Where(b => !string.Equals(b.Status, "INACTIVE", StringComparison.OrdinalIgnoreCase))
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (branches.Count == 0)
            {
                ShowStatus("No branches were found. Contact your administrator.");
                return;
            }

            _session = login.Session;
            PasswordBox.Password = string.Empty;
            BranchCombo.ItemsSource = branches;
            BranchCombo.SelectedItem = branches.FirstOrDefault(b => b.Id == login.Session.BranchId) ?? branches[0];
            BranchPanel.Visibility = Visibility.Visible;
            ShowStatus(null);
        }
        catch (HttpRequestException)
        {
            ShowStatus("Can't reach the server. Check the internet connection and try again.");
        }
        catch (TaskCanceledException)
        {
            ShowStatus("The server took too long to respond. Please try again.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RegisterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var serial = DeviceSerialBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(serial) || serial.StartsWith("(", StringComparison.Ordinal))
        {
            ShowStatus("The reader isn't detected. Plug it in and restart the app.");
            return;
        }

        if (_session is null || BranchCombo.SelectedItem is not BranchOption branch)
        {
            ShowStatus("Sign in and choose the branch first.");
            return;
        }

        SetBusy(true);
        try
        {
            var (registration, error) = await _apiClient.RegisterDeviceAsync(
                branch.Id, serial, _session.AccessToken, CancellationToken.None);

            if (registration is null)
            {
                ShowStatus(FriendlyMessages.Translate(error) +
                           " If this reader was registered before, use the token option below.");
                return;
            }

            SaveCredentials(registration.DeviceSerial, registration.PlaintextToken);
            System.Windows.MessageBox.Show(this, $"This reader is now registered for {branch.Name}.",
                "Reader registered", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (HttpRequestException)
        {
            ShowStatus("Can't reach the server. The reader was not registered.");
        }
        catch (TaskCanceledException)
        {
            ShowStatus("The server took too long to respond. Please try again.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SaveTokenButton_Click(object sender, RoutedEventArgs e)
    {
        var serial = DeviceSerialBox.Text.Trim();
        var token = TokenBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(serial) || serial.StartsWith("(", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(token))
        {
            ShowStatus("A detected reader and a token are both required.");
            return;
        }

        SaveCredentials(serial, token);
        DialogResult = true;
    }

    private void SaveCredentials(string serial, string token)
    {
        DeviceCredentialStore.Save(new DeviceCredentials(serial, token));
        _apiClient.ConfigureDeviceCredentials(serial, token);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SignInButton.IsEnabled = !busy;
        RegisterButton.IsEnabled = !busy;
        SaveTokenButton.IsEnabled = !busy;
    }

    private void ShowStatus(string? message) => StatusText.Text = message ?? string.Empty;
}