using System;
using System.Collections.Generic;
using System.Linq;
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

namespace ICCardManager
{
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
        private ILogger<App> _logger;

        /// <summary>
        /// 現在のアプリケーションインスタンス
        /// </summary>
        public static new App Current => (App)Application.Current;

        /// <summary>
        /// 職員証登録モードが有効かどうか（MainViewModelでの未登録カード処理を抑制するため）
        /// </summary>
        public static bool IsStaffCardRegistrationActive { get; set; }

        /// <summary>
        /// ICカード登録モードが有効かどうか（MainViewModelでの未登録カード処理を抑制するため）
        /// </summary>
        public static bool IsCardRegistrationActive { get; set; }

        /// <summary>
        /// 職員証認証モードが有効かどうか（MainViewModelでのカード処理を抑制するため）
        /// Issue #429: 重要な操作の前に職員証タッチを必須とする
        /// </summary>
        public static bool IsAuthenticationActive { get; set; }

        /// <summary>
        /// DEBUGビルドかどうか（XAMLからデバッグ用UIの表示制御に使用: Issue #289）
        /// </summary>
        public static bool IsDebugBuild
        {
            get
            {
#if DEBUG
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// アプリケーションのバージョン番号（XAMLからバインド可能: Issue #475）
        /// </summary>
        public static string AppVersion
        {
            get
            {
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                // Major.Minor.Build 形式で返す（Revisionは省略）
                return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "不明";
            }
        }

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
            services.AddSingleton<LedgerMergeService>();
            services.AddSingleton<LedgerSplitService>();
            services.AddSingleton<CsvExportService>();
            services.AddSingleton<CsvImportService>();
            services.AddSingleton<IToastNotificationService, ToastNotificationService>();
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IStaffAuthService, StaffAuthService>();

            // Infrastructure層
    #if DEBUG
            // デバッグ時はテストデータサービスを登録
            services.AddSingleton<DebugDataService>();
    #endif

    #if DEBUG
            // Issue #640: デバッグ時は MockCardReader を使用（仮想タッチ機能のため）
            services.AddSingleton<MockCardReader>();
            services.AddSingleton<ICardReader>(sp => sp.GetRequiredService<MockCardReader>());
    #else
            // カードリーダーの自動選択:
            // 1. felicalib.dll が存在する場合: FelicaCardReader（残高・履歴読み取り可能）
            // 2. それ以外: PcScCardReader（IDm読み取りのみ）
            services.AddSingleton<ICardReader>(sp => CreateCardReader(sp));
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
            services.AddTransient<LedgerEditViewModel>();
            services.AddTransient<LedgerDetailViewModel>();
            services.AddTransient<SystemManageViewModel>();
    #if DEBUG
            // Issue #640: 仮想タッチ設定ダイアログ
            services.AddTransient<VirtualCardViewModel>();
    #endif

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
            services.AddTransient<Views.Dialogs.LedgerDetailDialog>();
            services.AddTransient<Views.Dialogs.LedgerEditDialog>();
            services.AddTransient<Views.Dialogs.SystemManageDialog>();
    #if DEBUG
            services.AddTransient<Views.Dialogs.VirtualCardDialog>();
    #endif
        }

        /// <summary>
        /// 利用可能なカードリーダーを自動選択して作成します。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 以下の優先順位でカードリーダーを選択します：
        /// </para>
        /// <list type="number">
        /// <item><description>FelicaCardReader: felicalib.dll が利用可能な場合（残高・履歴読み取り可能）</description></item>
        /// <item><description>PcScCardReader: PC/SC API が利用可能な場合（IDm読み取りのみ）</description></item>
        /// </list>
        /// <para>
        /// FelicaCardReader を使用するには、Sony NFCポートソフトウェアがインストールされている必要があります。
        /// </para>
        /// </remarks>
        private static ICardReader CreateCardReader(IServiceProvider sp)
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            // felicalib.dll の存在を確認
            if (IsFelicaLibAvailable())
            {
                var logger = loggerFactory.CreateLogger<FelicaCardReader>();
                logger.LogInformation("FelicaCardReader を使用します（残高・履歴読み取り可能）");
                return new FelicaCardReader(logger);
            }

            // フォールバック: PcScCardReader
            {
                var logger = loggerFactory.CreateLogger<PcScCardReader>();
                logger.LogInformation("PcScCardReader を使用します（IDm読み取りのみ、残高・履歴は読み取れません）");
                return new PcScCardReader(logger);
            }
        }

        /// <summary>
        /// felicalib.dll が利用可能かどうかを確認します。
        /// </summary>
        /// <remarks>
        /// プロジェクトはx86（32ビット）でビルドされるため、32ビット版のfelicalib.dllのみをチェックします。
        /// </remarks>
        private static bool IsFelicaLibAvailable()
        {
            try
            {
                // felicalib.dll の存在を確認（x86固定ビルドのため32ビット版のみ）
                var baseDir = AppContext.BaseDirectory;
                var dllPath = System.IO.Path.Combine(baseDir, "felicalib.dll");

                return System.IO.File.Exists(dllPath);
            }
            catch
            {
                return false;
            }
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
            // Task.Runで別スレッドに移動し、UIスレッドのデッドロックを防止
            Task.Run(() => RegisterTestDataAsync()).GetAwaiter().GetResult();
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
                // 同期版メソッドを使用してデッドロックを防止
                var settings = settingsRepository.GetAppSettings();

                // 文字サイズを適用
                ApplyFontSize(settings.FontSize);

                // トースト位置を適用
                ApplyToastPosition(settings.ToastPosition);

                _logger?.LogDebug("設定を適用: フォントサイズ={FontSize}, トースト位置={ToastPosition}", settings.FontSize, settings.ToastPosition);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "設定の適用でエラー");
                // デフォルト値を適用
                ApplyFontSize(FontSizeOption.Medium);
                ApplyToastPosition(ToastPosition.TopRight);
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

            // タイトル・ステータス・アイコン用のフォントサイズも比率で計算
            var titleFontSize = Math.Round(baseFontSize * 1.6);      // タイトル用（約1.6倍）
            var statusFontSize = Math.Round(baseFontSize * 2.0);     // ステータスメッセージ用（約2倍）
            var iconFontSize = Math.Round(baseFontSize * 5.0);       // アイコン用（約5倍）

            // Issue #542: サイドバー幅をフォントサイズに応じて調整
            // 基準: Medium (14) で 350px、差分 × 5 で調整
            // Small(12)→340, Medium(14)→350, Large(16)→360, ExtraLarge(20)→380
            var sidebarWidth = Math.Round(350 + (baseFontSize - 14) * 5);
            // ウィンドウ最小幅はフォントサイズに関係なく固定（1200px）
            const double windowMinWidth = 1200;

            // アプリケーションリソースを更新
            var resources = Application.Current.Resources;
            resources["BaseFontSize"] = baseFontSize;
            resources["LargeFontSize"] = largeFontSize;
            resources["SmallFontSize"] = smallFontSize;
            resources["TitleFontSize"] = titleFontSize;
            resources["StatusFontSize"] = statusFontSize;
            resources["IconFontSize"] = iconFontSize;
            resources["SidebarWidth"] = sidebarWidth;
            resources["WindowMinWidth"] = windowMinWidth;
        }

        /// <summary>
        /// トースト通知の表示位置をアプリケーション全体に適用
        /// </summary>
        public static void ApplyToastPosition(ToastPosition position)
        {
            Views.ToastNotificationWindow.CurrentPosition = position;
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
        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
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
}
