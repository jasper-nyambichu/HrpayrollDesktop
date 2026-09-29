using HrpayrollDesktop.Auth;
using HrpayrollDesktop.Sync;
using System.Threading;
using System.Windows;
using System.Windows.Input;

namespace HrpayrollDesktop;

public partial class LoginWindow : Window
{
    private readonly BackendApiClient _apiClient;

    public AuthResponse? Session { get; private set; }

    public LoginWindow(BackendApiClient apiClient)
    {
        InitializeComponent();
        _apiClient = apiClient;
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            ShowError("Enter both username and password.");
            return;
        }

        LoginButton.IsEnabled = false;
        ShowError(null);
        LoadingText.Visibility = Visibility.Visible;    // ADD
        UsernameBox.IsEnabled = false;                  // ADD — prevents editing mid-request
        PasswordBox.IsEnabled = false;                  // ADD

        var result = await _apiClient.LoginAsync(username, password, CancellationToken.None);

        LoadingText.Visibility = Visibility.Collapsed;  // ADD
        UsernameBox.IsEnabled = true;                   // ADD
        PasswordBox.IsEnabled = true;                   // ADD

        if (!result.Succeeded || result.Session is null)
        {
            ShowError(result.Error ?? "Login failed.");
            LoginButton.IsEnabled = true;
            return;
        }

        if (result.Session.Role != "BRANCH_MANAGER")
        {
            ShowError("This device is for Branch Manager login only.");
            LoginButton.IsEnabled = true;
            return;
        }

        Session = result.Session;
        DialogResult = true;
    }

    private void ShowError(string? message)
    {
        StatusText.Text = message ?? string.Empty;
        StatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { MaximizeButton_Click(sender, e); return; }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}