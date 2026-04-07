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
using ICCardManager.Views.Dialogs;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

                // データベース初期化（SummaryGenerator等がsettingsテーブルを参照するため、DI解決より先に実行）
                InitializeDatabase();

                // Issue #974: 組織固有設定を静的サービスに注入
                // SummaryGeneratorはDIコンストラクタ経由でConfigureされる（即時解決で静的状態を初期化）
                ServiceProvider.GetRequiredService<SummaryGenerator>();

                _logger.LogDebug("データベース初期化完了");

                // メインウィンドウを表示
                var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
                _logger.LogDebug("MainWindow取得完了");

                mainWindow.Show();
                _logger.LogInformation("アプリケーション起動完了");
            }
            catch (Exception ex)
            {
                // DB関連のエラーの場合、パス情報も含めて表示（トラブルシューティング用）
                var dbPathInfo = "";
                try
                {
                    var configPath = ViewModels.SettingsViewModel.LoadDatabasePathFromConfigFile();
                    if (!string.IsNullOrWhiteSpace(configPath))
                        dbPathInfo = $"\n\nデータベースパス: {configPath}";
                }
                catch { }

#if DEBUG
                var errorMessage = $"起動エラー: {ex.Message}{dbPathInfo}\n\n{ex.StackTrace}";
#else
                var errorMessage = $"起動エラーが発生しました。\n\n{ex.Message}{dbPathInfo}\n\n詳細はログファイルを確認してください。";
#endif

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
            // 設定オプション（Issue #854: ハードコード値の外部化）
            services.Configure<AppOptions>(Configuration.GetSection("AppOptions"));
            services.Configure<CacheOptions>(Configuration.GetSection("CacheOptions"));
            // Issue #974: 組織固有設定の外部化
            services.Configure<OrganizationOptions>(Configuration.GetSection("OrganizationOptions"));
            services.Configure<DatabaseOptions>(Configuration.GetSection("DatabaseOptions"));

            // ProgramData配下の設定ファイルからDBパスを読み込み、DatabaseOptionsを上書き
            // （appsettings.jsonはProgram Files内で書き込み不可のため、別ファイルで管理）
            services.PostConfigure<DatabaseOptions>(dbOptions =>
            {
                var configPath = ViewModels.SettingsViewModel.LoadDatabasePathFromConfigFile();
                if (!string.IsNullOrWhiteSpace(configPath))
                {
                    dbOptions.Path = configPath;
                }
            });

            // 共有モード時はキャッシュTTLを短縮（他PCの変更を素早く反映するため）
            // ※ ローカル操作ではキャッシュが即座に無効化されるため、
            //   TTLは「他PCの操作結果が見えるまでの遅延」のみに影響する。
            //   20台同時接続での負荷を考慮し、過度に短くしない。
            services.PostConfigure<CacheOptions>(cacheOptions =>
            {
                var dbPath = ViewModels.SettingsViewModel.LoadDatabasePathFromConfigFile();
                if (!string.IsNullOrWhiteSpace(dbPath))
                {
                    cacheOptions.CardListSeconds = 15;
                    cacheOptions.LentCardsSeconds = 10;
                    cacheOptions.StaffListSeconds = 30;
                    cacheOptions.SettingsMinutes = 3;
                }
            });

            // ロギングの設定
            services.AddLogging(builder =>
            {
                builder.AddConfiguration(Configuration.GetSection("Logging"));
                builder.AddDebug();
                builder.AddFile();
            });

            // メッセージング（Issue #852: 静的フラグをWeakReferenceMessengerに置換）
            services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

            // Infrastructure層 - キャッシュ
            services.AddSingleton<ICacheService, CacheService>();

            // Data層
            services.AddSingleton<DbContext>(sp =>
            {
                var dbOptions = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
                var path = dbOptions.Path;
                // PostConfigureで設定されなかった場合、database_config.txtから直接読む（フォールバック）
                if (string.IsNullOrWhiteSpace(path))
                {
                    path = ViewModels.SettingsViewModel.LoadDatabasePathFromConfigFile();
                }
                var logger = sp.GetService<ILogger<DbContext>>();
                return new DbContext(string.IsNullOrWhiteSpace(path) ? null : path, logger);
            });
            services.AddSingleton<IStaffRepository, StaffRepository>();
            services.AddSingleton<ICardRepository, CardRepository>();
            services.AddSingleton<ILedgerRepository, LedgerRepository>();
            services.AddSingleton<ISettingsRepository, SettingsRepository>();
            services.AddSingleton<IOperationLogRepository, OperationLogRepository>();

            // Services層
            services.AddSingleton<IValidationService, ValidationService>();
            services.AddSingleton<SummaryGenerator>(sp =>
            {
                var settings = sp.GetRequiredService<ISettingsRepository>().GetAppSettings();
                var orgOptions = sp.GetRequiredService<IOptions<OrganizationOptions>>().Value;
                return new SummaryGenerator(settings.DepartmentType, orgOptions);
            });
            services.AddSingleton<CardLockManager>();
            services.AddSingleton<LendingService>();
            services.AddSingleton<IReportDataBuilder, ReportDataBuilder>();
            services.AddSingleton<ReportService>();
            services.AddSingleton<PrintService>();
            services.AddSingleton<BackupService>();
            services.AddSingleton<OperationLogger>();
            services.AddSingleton<LedgerMergeService>();
            services.AddSingleton<LedgerSplitService>();
            services.AddSingleton<LedgerConsistencyChecker>();
            services.AddSingleton<CsvExportService>();
            services.AddSingleton<OperationLogExcelExportService>();
            services.AddSingleton<CsvImportService>();
            services.AddSingleton<IToastNotificationService, ToastNotificationService>();
            services.AddSingleton<NavigationService>();
            services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
            services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<NavigationService>());
            services.AddSingleton<IStaffAuthService, StaffAuthService>();
            services.AddSingleton<IStationMasterService, StationMasterService>();

            // Infrastructure層
    #if DEBUG
            // デバッグ時はテストデータサービスを登録
            services.AddSingleton<DebugDataService>();
    #endif

    #if DEBUG
            // Issue #640: HybridCardReader で物理カードリーダー（PaSoRi + felicalib）をラップし、
            // 仮想タッチ機能も利用可能にする
            services.AddSingleton<HybridCardReader>(sp => new HybridCardReader(CreateCardReader(sp)));
            services.AddSingleton<ICardReader>(sp => sp.GetRequiredService<HybridCardReader>());
    #else
            // 物理カードリーダー（PaSoRi + felicalib）で FelicaCardReader を使用
            services.AddSingleton<ICardReader>(sp => CreateCardReader(sp));
    #endif
            services.AddSingleton<ISoundPlayer, SoundPlayer>();
            services.AddSingleton<Infrastructure.Timing.ITimerFactory, Infrastructure.Timing.DispatcherTimerFactory>();
            services.AddSingleton<Infrastructure.Timing.IDispatcherService, Infrastructure.Timing.WpfDispatcherService>();

            // ViewModels
            services.AddTransient<MainViewModel>();
            services.AddTransient<CardManageViewModel>();
            services.AddTransient<StaffManageViewModel>();
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<ReportViewModel>();

            services.AddTransient<BusStopInputViewModel>();
            services.AddTransient<PrintPreviewViewModel>();
            services.AddTransient<DataExportImportViewModel>();
            services.AddTransient<OperationLogSearchViewModel>();
            services.AddTransient<LedgerDetailViewModel>();
            services.AddTransient<SystemManageViewModel>();
            services.AddTransient<IncompleteBusStopViewModel>();
            services.AddTransient<LedgerRowEditViewModel>();
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

            services.AddTransient<Views.Dialogs.BusStopInputDialog>();
            services.AddTransient<Views.Dialogs.PrintPreviewDialog>();
            services.AddTransient<Views.Dialogs.DataExportImportDialog>();
            services.AddTransient<Views.Dialogs.OperationLogDialog>();
            services.AddTransient<Views.Dialogs.LedgerDetailDialog>();
            services.AddTransient<Views.Dialogs.SystemManageDialog>();
            services.AddTransient<Views.Dialogs.IncompleteBusStopDialog>();
            services.AddTransient<Views.Dialogs.LedgerRowEditDialog>();
            services.AddTransient<Views.Dialogs.CardTypeSelectionDialog>();
    #if DEBUG
            services.AddTransient<Views.Dialogs.VirtualCardDialog>();
    #endif
        }

        /// <summary>
        /// FelicaCardReader を作成します。
        /// </summary>
        /// <remarks>
        /// 本システムは PaSoRi + felicalib.dll 前提です（交通系ICカードの残高・履歴は
        /// felicalib 経由でしか読み取れないため）。felicalib.dll が存在しない場合は
        /// <see cref="InvalidOperationException"/> を送出します。
        /// </remarks>
        private static ICardReader CreateCardReader(IServiceProvider sp)
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var stationMasterService = sp.GetRequiredService<IStationMasterService>();

            if (!IsFelicaLibAvailable())
            {
                throw new InvalidOperationException(
                    "felicalib.dll が見つかりません。PaSoRi + felicalib 環境でのみ動作します。");
            }

            var logger = loggerFactory.CreateLogger<FelicaCardReader>();
            logger.LogInformation("FelicaCardReader を使用します（残高・履歴読み取り可能）");
            return new FelicaCardReader(logger, stationMasterService);
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

            // インストーラーからの部署設定を適用（Issue #742）
            ApplyDepartmentConfigFromInstaller();

            // 保存済み設定を適用
            ApplySavedSettings();

            // 起動時処理
            PerformStartupTasks(dbContext);
        }

        /// <summary>
        /// インストーラーが書き出した部署設定ファイルを読み取り、DBに適用する（Issue #742）
        /// </summary>
        /// <remarks>
        /// インストーラーは %PROGRAMDATA%\ICCardManager\department_config.txt に
        /// 選択された部署種別（"mayor_office" or "enterprise_account"）を書き出す。
        /// アプリ初回起動時にこのファイルを読み取り、DB設定を上書きしてからファイルを削除する。
        /// ファイルが存在しない場合（手動インストール等）はschema.sqlのデフォルト値が使用される。
        /// </remarks>
        private void ApplyDepartmentConfigFromInstaller()
        {
            try
            {
                var configPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ICCardManager", "department_config.txt");

                if (!System.IO.File.Exists(configPath))
                {
                    return; // 設定ファイルなし（アップグレードまたは手動インストール）
                }

                var departmentValue = System.IO.File.ReadAllText(configPath).Trim();

                // ファイルを削除（次回起動時に再適用しない）
                // インストーラー（管理者権限）が作成したファイルのため、
                // 一般ユーザーでは削除できない場合がある（Issue #1080）
                DeleteOrClearFile(configPath, _logger);

                if (string.IsNullOrEmpty(departmentValue))
                {
                    return;
                }

                var settingsRepository = ServiceProvider.GetRequiredService<ISettingsRepository>();
                Task.Run(() => settingsRepository.SetAsync(
                    SettingsRepository.KeyDepartmentType,
                    departmentValue
                )).GetAwaiter().GetResult();

                // キャッシュを無効化して再取得を強制
                var cacheService = ServiceProvider.GetRequiredService<ICacheService>();
                cacheService.Invalidate(CacheKeys.AppSettings);

                var departmentType = SettingsRepository.ParseDepartmentType(departmentValue);
                _logger?.LogInformation("インストーラー設定: 部署種別を適用 DepartmentType={DepartmentType}", departmentType);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "インストーラーからの部署設定の適用でエラー");
                // エラーが発生してもアプリは起動させる（schema.sqlのデフォルト値が使用される）
            }
        }

        /// <summary>
        /// ファイルを削除する。削除できない場合はファイルの内容をクリアする（Issue #1080）
        /// </summary>
        /// <remarks>
        /// インストーラー（管理者権限）が作成したファイルは、一般ユーザーでは
        /// 削除権限がない場合がある。その場合、ファイル内容を空にすることで
        /// 次回起動時の再適用を防ぐ。内容クリアも失敗した場合は警告ログを出力するが、
        /// 同じ設定値の再適用は実害がないため処理を継続する。
        /// </remarks>
        /// <param name="filePath">削除対象のファイルパス</param>
        /// <param name="logger">ロガー（省略可能）</param>
        /// <returns>削除またはクリアに成功した場合true、両方失敗した場合false</returns>
        internal static bool DeleteOrClearFile(string filePath, ILogger logger = null)
        {
            try
            {
                System.IO.File.Delete(filePath);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // 削除権限がない場合、内容をクリアして次回の再適用を防ぐ
                try
                {
                    System.IO.File.WriteAllText(filePath, string.Empty);
                    logger?.LogInformation("ファイルの削除権限がないため内容をクリアしました: {FilePath}", filePath);
                    return true;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "ファイルの削除・クリアに失敗（同一設定の再適用のため実害なし）: {FilePath}", filePath);
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "ファイルの削除に失敗: {FilePath}", filePath);
                return false;
            }
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
            var dialogIconFontSize = Math.Round(baseFontSize * 2.3); // ダイアログアイコン用（約2.3倍）Issue #719
            var dialogLargeIconFontSize = Math.Round(baseFontSize * 3.4); // ダイアログ大アイコン用（約3.4倍）Issue #719

            // Issue #542: サイドバー幅をフォントサイズに応じて調整
            // 基準: Medium (14) で 350px、差分 × 5 で調整
            // Small(12)→340, Medium(14)→350, Large(16)→360, ExtraLarge(20)→380
            var sidebarWidth = Math.Round(350 + (baseFontSize - 14) * 5);
            // Issue #663: ウィンドウ最小幅はフォントサイズに関係なく固定 愛称追加に伴い拡大（1400px）
            const double windowMinWidth = 1400;

            // アプリケーションリソースを更新
            var resources = Application.Current.Resources;
            resources["BaseFontSize"] = baseFontSize;
            resources["LargeFontSize"] = largeFontSize;
            resources["SmallFontSize"] = smallFontSize;
            resources["TitleFontSize"] = titleFontSize;
            resources["StatusFontSize"] = statusFontSize;
            resources["IconFontSize"] = iconFontSize;
            resources["DialogIconFontSize"] = dialogIconFontSize;
            resources["DialogLargeIconFontSize"] = dialogLargeIconFontSize;
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

                // 古いデータの削除（6年経過分）
                var (ledgerDeleted, logDeleted) = dbContext.CleanupOldData();
                if (ledgerDeleted > 0)
                {
                    _logger?.LogInformation("古い利用履歴を{DeletedCount}件削除しました", ledgerDeleted);
                }
                if (logDeleted > 0)
                {
                    _logger?.LogInformation("古い操作ログを{DeletedCount}件削除しました", logDeleted);
                }

                // VACUUM（月次実行）
                var settingsRepository = ServiceProvider.GetRequiredService<ISettingsRepository>();
                var settings = settingsRepository.GetAppSettings();

                var today = DateTime.Now;
                if (today.Day >= 10)
                {
                    var lastVacuum = settings.LastVacuumDate;
                    if (!lastVacuum.HasValue ||
                        lastVacuum.Value.Year != today.Year ||
                        lastVacuum.Value.Month != today.Month)
                    {
                        if (dbContext.Vacuum())
                        {
                            settings.LastVacuumDate = today;
                            _ = settingsRepository.SaveAppSettingsAsync(settings);
                            _logger?.LogInformation("VACUUM実行完了");
                        }
                        else
                        {
                            _logger?.LogWarning("VACUUM失敗（他の接続がアクティブ）。次回起動時にリトライします。");
                        }
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
