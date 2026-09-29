using System.Threading;
using System.Windows;
using HrpayrollDesktop.Auth;
using HrpayrollDesktop.Sync;
using HrpayrollDesktop.Terminal;

namespace HrpayrollDesktop;

public partial class DeviceSetupWindow : Window
{
    private readonly ITerminalAdapter _terminalAdapter;
    private readonly BackendApiClient _apiClient;

    public DeviceSetupWindow(ITerminalAdapter terminalAdapter, BackendApiClient apiClient)
    {
        InitializeComponent();
        _terminalAdapter = terminalAdapter;
        _apiClient = apiClient;
        Loaded += async (_, _) => await ConnectAndShowSerialAsync();
    }

    private async System.Threading.Tasks.Task ConnectAndShowSerialAsync()
    {
        DeviceSerialBox.Text = "Connecting to reader...";
        try
        {
            await _terminalAdapter.ConnectAsync(CancellationToken.None);
            DeviceSerialBox.Text = _terminalAdapter.DeviceSerial ?? "(unavailable)";
        }
        catch (System.Exception ex)
        {
            DeviceSerialBox.Text = "(reader not detected)";
            StatusText.Text = $"Could not read device serial: {ex.Message}. Plug in the reader and restart the app.";
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var serial = DeviceSerialBox.Text.Trim();
        var token = TokenBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(serial) || serial.StartsWith("(") || string.IsNullOrWhiteSpace(token))
        {
            StatusText.Text = "A valid device serial and token are both required.";
            return;
        }

        var credentials = new DeviceCredentials(serial, token);
        DeviceCredentialStore.Save(credentials);
        _apiClient.ConfigureDeviceCredentials(credentials.DeviceSerial, credentials.DeviceToken);

        DialogResult = true;
    }
}