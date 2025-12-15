using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Logging;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using ICCardManager.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// 設定
    /// </summary>
    public IConfiguration Configuration { get; private set; } = null!;

    /// <summary>
    /// アプリケーションロガー
    /// </summary>
    private ILogger<App>? _logger;

    /// <summary>
    /// 現在のアプリケーションインスタンス
    /// </summary>
    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // グローバル例外ハンドラーを登録
        SetupGlobalExceptionHandlers();

        // 古いログファイルを削除
        ErrorDialogHelper.CleanupOldLogs();

        try
        {
            // 設定ファイルを読み込み
            Configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .Build();

            // DIコンテナの設定
            var services = new ServiceCollection();
            ConfigureServices(services);
            ServiceProvider = services.BuildServiceProvider();

            // ロガーを取得
            _logger = ServiceProvider.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("アプリケーション起動開始");

            _logger.LogDebug("DIコンテナ構築完了");

            // データベース初期化
            InitializeDatabase();

            _logger.LogDebug("データベース初期化完了");

            // メインウィンドウを表示
            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            _logger.LogDebug("MainWindow取得完了");

            mainWindow.Show();
            _logger.LogInformation("アプリケーション起動完了");
        }
        catch (Exception ex)
        {
            var errorMessage = $"起動エラー: {ex.Message}\n\n{ex.StackTrace}";

            // クリップボードにコピー可能なエラーダイアログを表示
            var result = MessageBox.Show(
                $"{errorMessage}\n\n[はい]をクリックするとエラー内容をクリップボードにコピーします。",
                "エラー",
                MessageBoxButton.YesNo,
                MessageBoxImage.Error);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    System.Windows.Clipboard.SetText(errorMessage);
                }
                catch
                {
                    // クリップボードへのコピーに失敗した場合は無視
                }
            }

            Shutdown(1);
        }
    }

    /// <summary>
    /// サービスを登録
    /// </summary>
    private void ConfigureServices(IServiceCollection services)
    {
        // ロギングの設定
        services.AddLogging(builder =>
        {
            builder.AddConfiguration(Configuration.GetSection("Logging"));
            builder.AddDebug();
            builder.AddFile();
        });

        // Infrastructure層 - キャッシュ
        services.AddSingleton<ICacheService, CacheService>();

        // Data層
        services.AddSingleton<DbContext>();
        services.AddSingleton<IStaffRepository, StaffRepository>();
        services.AddSingleton<ICardRepository, CardRepository>();
        services.AddSingleton<ILedgerRepository, LedgerRepository>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<IOperationLogRepository, OperationLogRepository>();

        // Services層
        services.AddSingleton<IValidationService, ValidationService>();
        services.AddSingleton<CardTypeDetector>();
        services.AddSingleton<SummaryGenerator>();
        services.AddSingleton<CardLockManager>();
        services.AddSingleton<LendingService>();
        services.AddSingleton<ReportService>();
        services.AddSingleton<PrintService>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<OperationLogger>();
        services.AddSingleton<CsvExportService>();
        services.AddSingleton<CsvImportService>();
        services.AddSingleton<IToastNotificationService, ToastNotificationService>();

        // Infrastructure層
#if DEBUG
        // デバッグ時はモックを使用
        services.AddSingleton<ICardReader, MockCardReader>();
        services.AddSingleton<DebugDataService>();
#else
        services.AddSingleton<ICardReader, PcScCardReader>();
#endif
        services.AddSingleton<ISoundPlayer, SoundPlayer>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<CardManageViewModel>();
        services.AddTransient<StaffManageViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ReportViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<BusStopInputViewModel>();
        services.AddTransient<PrintPreviewViewModel>();
        services.AddTransient<DataExportImportViewModel>();
        services.AddTransient<OperationLogSearchViewModel>();

        // Views
        services.AddTransient<MainWindow>();
        services.AddTransient<Views.Dialogs.CardManageDialog>();
        services.AddTransient<Views.Dialogs.StaffManageDialog>();
        services.AddTransient<Views.Dialogs.SettingsDialog>();
        services.AddTransient<Views.Dialogs.ReportDialog>();
        services.AddTransient<Views.Dialogs.HistoryDialog>();
        services.AddTransient<Views.Dialogs.BusStopInputDialog>();
        services.AddTransient<Views.Dialogs.PrintPreviewDialog>();
        services.AddTransient<Views.Dialogs.DataExportImportDialog>();
        services.AddTransient<Views.Dialogs.OperationLogDialog>();
    }

    /// <summary>
    /// データベースを初期化
    /// </summary>
    private void InitializeDatabase()
    {
        var dbContext = ServiceProvider.GetRequiredService<DbContext>();
        dbContext.InitializeDatabase();

#if DEBUG
        // デバッグ時はテストデータを登録
        RegisterTestDataAsync().Wait();
#endif

        // 保存済み設定を適用
        ApplySavedSettings();

        // 起動時処理
        PerformStartupTasks(dbContext);
    }

    /// <summary>
    /// 保存済み設定を適用
    /// </summary>
    private void ApplySavedSettings()
    {
        try
        {
            var settingsRepository = ServiceProvider.GetRequiredService<ISettingsRepository>();
            var settings = settingsRepository.GetAppSettingsAsync().Result;

            // 文字サイズを適用
            ApplyFontSize(settings.FontSize);

            _logger?.LogDebug("設定を適用: フォントサイズ={FontSize}", settings.FontSize);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "設定の適用でエラー");
            // デフォルト値を適用
            ApplyFontSize(FontSizeOption.Medium);
        }
    }

    /// <summary>
    /// 文字サイズをアプリケーション全体に適用
    /// </summary>
    public static void ApplyFontSize(FontSizeOption fontSize)
    {
        var baseFontSize = fontSize switch
        {
            FontSizeOption.Small => 12.0,
            FontSizeOption.Medium => 14.0,
            FontSizeOption.Large => 16.0,
            FontSizeOption.ExtraLarge => 20.0,
            _ => 14.0
        };

        // 比率に基づいて他のフォントサイズも計算
        var largeFontSize = Math.Round(baseFontSize * 1.3);
        var smallFontSize = Math.Round(baseFontSize * 0.85);

        // アプリケーションリソースを更新
        var resources = Application.Current.Resources;
        resources["BaseFontSize"] = baseFontSize;
        resources["LargeFontSize"] = largeFontSize;
        resources["SmallFontSize"] = smallFontSize;
    }

#if DEBUG
    /// <summary>
    /// テストデータを登録（デバッグ用）
    /// </summary>
    private async Task RegisterTestDataAsync()
    {
        try
        {
            var debugDataService = ServiceProvider.GetRequiredService<DebugDataService>();
            await debugDataService.RegisterAllTestDataAsync();
            _logger?.LogDebug("テストデータ登録完了");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "テストデータ登録エラー");
        }
    }

    /// <summary>
    /// デバッグ用: テストデータをリセット
    /// </summary>
    public async Task ResetTestDataAsync()
    {
        var debugDataService = ServiceProvider.GetRequiredService<DebugDataService>();
        await debugDataService.ResetTestDataAsync();
    }

    /// <summary>
    /// デバッグ用: 履歴データを生成
    /// </summary>
    public async Task GenerateHistoryAsync(string cardIdm, int days, string staffName)
    {
        var debugDataService = ServiceProvider.GetRequiredService<DebugDataService>();
        await debugDataService.GenerateHistoryAsync(cardIdm, days, staffName);
    }

    /// <summary>
    /// デバッグ用: テストデータ一覧を取得
    /// </summary>
    public static IEnumerable<(string Idm, string Description, bool IsStaff)> GetTestDataList()
    {
        return DebugDataService.GetAllTestIdms();
    }
#endif

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
                _logger?.LogInformation("古いデータを{DeletedCount}件削除しました", deletedCount);
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
                    _logger?.LogInformation("VACUUM実行完了");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "起動時タスクでエラー");
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

    #region グローバル例外ハンドラー

    /// <summary>
    /// グローバル例外ハンドラーを設定
    /// </summary>
    private void SetupGlobalExceptionHandlers()
    {
        // UIスレッド上の未処理例外
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 非UIスレッドの未処理例外
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // Task内の未観測例外
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// UIスレッドの未処理例外ハンドラー
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "UIスレッド未処理例外");

        // AppExceptionの場合はユーザーフレンドリーなメッセージを表示
        if (e.Exception is AppException)
        {
            ErrorDialogHelper.ShowError(e.Exception);
            e.Handled = true;
            return;
        }

        // その他の例外
        ErrorDialogHelper.ShowError(e.Exception, "予期しないエラー");
        e.Handled = true;
    }

    /// <summary>
    /// 非UIスレッドの未処理例外ハンドラー
    /// </summary>
    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new Exception("Unknown error");

        _logger?.LogCritical(exception, "非UIスレッド未処理例外 (IsTerminating={IsTerminating})", e.IsTerminating);

        if (e.IsTerminating)
        {
            // アプリケーション終了を伴う致命的エラー
            ErrorDialogHelper.ShowFatalError(exception);
        }
        else
        {
            // 継続可能なエラー
            Dispatcher.Invoke(() =>
            {
                ErrorDialogHelper.ShowError(exception);
            });
        }
    }

    /// <summary>
    /// Task内の未観測例外ハンドラー
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Task未観測例外 (InnerCount={InnerCount})", e.Exception.InnerExceptions.Count);

        // 例外を観測済みとしてマーク（アプリケーションのクラッシュを防止）
        e.SetObserved();

        // 内部例外もログに記録
        foreach (var innerException in e.Exception.InnerExceptions)
        {
            _logger?.LogError(innerException, "Task未観測例外の内部例外");
        }

        // UIスレッドでエラーダイアログを表示
        Dispatcher.Invoke(() =>
        {
            // 複数の例外がある場合は最初のものを表示
            var displayException = e.Exception.InnerExceptions.FirstOrDefault() ?? e.Exception;
            ErrorDialogHelper.ShowError(displayException, "バックグラウンド処理エラー");
        });
    }

    #endregion
}
