using System.Windows;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using ICCardManager.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ICCardManager;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// サービスプロバイダー
    /// </summary>
    public IServiceProvider ServiceProvider { get; private set; } = null!;

    /// <summary>
    /// 現在のアプリケーションインスタンス
    /// </summary>
    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // DIコンテナの設定
        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();

        // データベース初期化
        InitializeDatabase();

        // メインウィンドウを表示
        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    /// <summary>
    /// サービスを登録
    /// </summary>
    private void ConfigureServices(IServiceCollection services)
    {
        // Data層
        services.AddSingleton<DbContext>();
        services.AddSingleton<IStaffRepository, StaffRepository>();
        services.AddSingleton<ICardRepository, CardRepository>();
        services.AddSingleton<ILedgerRepository, LedgerRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<IOperationLogRepository, OperationLogRepository>();

        // Services層
        services.AddSingleton<CardTypeDetector>();
        services.AddSingleton<SummaryGenerator>();
        services.AddSingleton<LendingService>();
        services.AddSingleton<ReportService>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<OperationLogger>();

        // Infrastructure層
#if DEBUG
        // デバッグ時はモックを使用
        services.AddSingleton<ICardReader, MockCardReader>();
#else
        services.AddSingleton<ICardReader, PcScCardReader>();
#endif
        services.AddSingleton<ISoundPlayer, SoundPlayer>();

        // ViewModels
        services.AddTransient<MainViewModel>();

        // Views
        services.AddTransient<MainWindow>();
    }

    /// <summary>
    /// データベースを初期化
    /// </summary>
    private void InitializeDatabase()
    {
        var dbContext = ServiceProvider.GetRequiredService<DbContext>();
        dbContext.InitializeDatabase();

        // 起動時処理
        PerformStartupTasks(dbContext);
    }

    /// <summary>
    /// 起動時タスクを実行
    /// </summary>
    private void PerformStartupTasks(DbContext dbContext)
    {
        try
        {
            // 自動バックアップ
            var backupService = ServiceProvider.GetRequiredService<BackupService>();
            _ = backupService.ExecuteAutoBackupAsync();

            // 古いデータの削除
            var deletedCount = dbContext.CleanupOldData();
            if (deletedCount > 0)
            {
                System.Diagnostics.Debug.WriteLine($"古いデータを{deletedCount}件削除しました");
            }

            // VACUUM（月次実行）
            var settingsRepository = ServiceProvider.GetRequiredService<ISettingsRepository>();
            var settings = settingsRepository.GetAppSettingsAsync().Result;

            var today = DateTime.Now;
            if (today.Day >= 10)
            {
                var lastVacuum = settings.LastVacuumDate;
                if (!lastVacuum.HasValue ||
                    lastVacuum.Value.Year != today.Year ||
                    lastVacuum.Value.Month != today.Month)
                {
                    dbContext.Vacuum();
                    settings.LastVacuumDate = today;
                    _ = settingsRepository.SaveAppSettingsAsync(settings);
                    System.Diagnostics.Debug.WriteLine("VACUUM実行完了");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"起動時タスクでエラー: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // リソースの解放
        if (ServiceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnExit(e);
    }
}
