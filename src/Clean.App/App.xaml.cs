using System.Windows;
using Clean.Core;
using Clean.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Clean.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private Mutex? _instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instanceMutex = new Mutex(true, "Local\\Clean.Desktop." + Environment.UserName, out var created);
        if (!created) { MessageBox.Show("Clean 已在运行，请使用现有窗口。", "Clean"); Shutdown(); return; }
        var state = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clean");
        var services = new ServiceCollection();
        services.AddSingleton(WindowsFileSystem.CreatePolicy(state));
        services.AddSingleton<IFileSystem, WindowsFileSystem>();
        services.AddSingleton<ScanService>(); services.AddSingleton<CleanupService>();
        services.AddSingleton<SystemMetrics>();
        services.AddSingleton<IDiskInfoProvider>(x => x.GetRequiredService<SystemMetrics>());
        services.AddSingleton<IMemoryInfoProvider>(x => x.GetRequiredService<SystemMetrics>());
        services.AddSingleton<ISettingsStore>(new JsonSettingsStore(state));
        services.AddSingleton<ICleanupHistoryStore>(new JsonHistoryStore(state));
        services.AddSingleton<IDiagnosticLog>(new LocalDiagnosticLog(state));
        services.AddSingleton<IRecycleBinService, ShellRecycleBin>();
        services.AddSingleton<IUserDialogs, UserDialogs>();
        services.AddSingleton(new AppLocations(System.IO.Path.GetTempPath(), state));
        services.AddSingleton<MainViewModel>(); services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();
        try
        {
            MainWindow = _services.GetRequiredService<MainWindow>();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            _services.GetRequiredService<IDiagnosticLog>().Write("startup-failed", ex.GetType().Name);
            MessageBox.Show("Clean 启动失败：" + Errors.Describe(ex), "Clean");
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { _services?.Dispose(); _instanceMutex?.Dispose(); base.OnExit(e); }
}
