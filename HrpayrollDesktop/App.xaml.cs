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

namespace HrpayrollDesktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

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
                services.AddHttpClient("BackendApi");
                services.AddSingleton<BackendApiClient>(sp =>
                {
                    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("BackendApi");
                    var settings = sp.GetRequiredService<IOptions<AgentSettings>>();
                    var logger = sp.GetRequiredService<ILogger<BackendApiClient>>();
                    return new BackendApiClient(httpClient, settings, logger);
                });

                services.AddSingleton<AttendanceEngine>();
                services.AddHostedService(sp => sp.GetRequiredService<AttendanceEngine>());
            })
            .Build();

        var apiClient = _host.Services.GetRequiredService<BackendApiClient>();

        // TEMPORARY: config-file credentials until the DPAPI device setup is wired in.
        var deviceConfig = _host.Services.GetRequiredService<IConfiguration>().GetSection("Device");
        var deviceSerial = deviceConfig["DeviceSerial"];
        var deviceToken = deviceConfig["DeviceToken"];

        if (!string.IsNullOrWhiteSpace(deviceSerial) && !string.IsNullOrWhiteSpace(deviceToken))
        {
            apiClient.ConfigureDeviceCredentials(deviceSerial, deviceToken);
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
            "HR Payroll Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var logPath = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(logPath, $"{DateTime.UtcNow:O} [{source}] {ex}\n\n");
        }
        catch
        {
        }
    }
}