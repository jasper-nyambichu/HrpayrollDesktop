using HrpayrollDesktop.Config;
using HrpayrollDesktop.Local;
using HrpayrollDesktop.OfflineQueue;
using HrpayrollDesktop.Sync;
using HrpayrollDesktop.Terminal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using HrpayrollDesktop.Auth;

namespace HrpayrollDesktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    // One copy per PC: two would fight over the reader and the scan databases. The installer also uses this name.
    private const string SingleInstanceName = @"Global\DhwarshHR.Attendance";
    private static System.Threading.Mutex? _singleInstance;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _singleInstance = new System.Threading.Mutex(true, SingleInstanceName, out var firstCopy);
        if (!firstCopy)
        {
            // Already running (it starts with Windows): bring that window forward instead of opening a second one.
            var me = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var other in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
            {
                if (other.Id == me.Id || other.MainWindowHandle == IntPtr.Zero) continue;
                if (IsIconic(other.MainWindowHandle)) ShowWindow(other.MainWindowHandle, 9);   // SW_RESTORE, back to maximized
                SetForegroundWindow(other.MainWindowHandle);
                break;
            }
            Shutdown();
            return;
        }

        Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(config =>
            {
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: false);
            })
            .ConfigureServices((context, services) =>
            {
                services.Configure<AgentSettings>(context.Configuration.GetSection("Agent"));
                services.AddSingleton<OfflineQueueStore>();
                services.AddSingleton<LocalAttendanceStore>();
                services.AddSingleton<ITerminalAdapter, SecuGenTerminalAdapter>();

                // One shared BackendApiClient (AddHttpClient<T> would register it
                // transient and each consumer would get an unconfigured copy).
                services.AddSingleton<StaffTokenStore>();
                services.AddTransient<StaffTokenRefreshHandler>();
                services.AddHttpClient("BackendApi").AddHttpMessageHandler<StaffTokenRefreshHandler>();
                services.AddSingleton<BackendApiClient>(sp =>
                {
                    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("BackendApi");
                    var settings = sp.GetRequiredService<IOptions<AgentSettings>>();
                    var logger = sp.GetRequiredService<ILogger<BackendApiClient>>();
                    return new BackendApiClient(httpClient, settings, logger, sp.GetRequiredService<StaffTokenStore>());
                });

                services.AddSingleton<AttendanceEngine>();
                services.AddHostedService(sp => sp.GetRequiredService<AttendanceEngine>());
            })
            .Build();

        var apiClient = _host.Services.GetRequiredService<BackendApiClient>();

        // Device identity: each PC registers its own reader the first time a Back Office signs in, which ties
        // it to that branch. The app ships with no credentials, or every branch would be the same device.
        var saved = DeviceCredentialStore.TryLoad();
        if (saved is not null)
        {
            apiClient.ConfigureDeviceCredentials(saved.DeviceSerial, saved.DeviceToken);
        }

        var engine = _host.Services.GetRequiredService<AttendanceEngine>();
        var terminalAdapter = _host.Services.GetRequiredService<ITerminalAdapter>();
        var localStore = _host.Services.GetRequiredService<LocalAttendanceStore>();
        localStore.Initialize(); // idempotent; guarantees tables exist before any window reads them

        // No login gate: the kiosk is the default screen and capture runs all day.
        // The shell subscribes to engine events in its constructor, BEFORE the
        // host starts, so no early event is missed.
        var shell = new ShellWindow(engine, terminalAdapter, apiClient, localStore);
        MainWindow = shell;
        shell.Show();

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        try
        {
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            LogCrash("Host start", ex);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("UI thread", e.Exception);
        System.Windows.MessageBox.Show(
            "A display error occurred, but attendance tracking is still running in the background. Please restart the app when convenient.",
            "Kisiimatt Management System", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogCrash("AppDomain", e.ExceptionObject as Exception ?? new Exception("Unknown fatal error"));
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("Background task", e.Exception);
        e.SetObserved();
    }

    private void LogCrash(string source, Exception ex)
    {
        try
        {
            var logPath = Path.Combine(HrpayrollDesktop.Config.AppPaths.DataDirectory, "crash.log");
            File.AppendAllText(logPath, $"{DateTime.UtcNow:O} [{source}] {ex}\n\n");
        }
        catch
        {
        }
    }
}