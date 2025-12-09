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

        try
        {
            System.Diagnostics.Debug.WriteLine("アプリケーション起動開始");

            // DIコンテナの設定
            var services = new ServiceCollection();
            ConfigureServices(services);
            ServiceProvider = services.BuildServiceProvider();

            System.Diagnostics.Debug.WriteLine("DIコンテナ構築完了");

            // データベース初期化
            InitializeDatabase();

            System.Diagnostics.Debug.WriteLine("データベース初期化完了");

            // メインウィンドウを表示
            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            System.Diagnostics.Debug.WriteLine("MainWindow取得完了");

            mainWindow.Show();
            System.Diagnostics.Debug.WriteLine("MainWindow表示完了");
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
        services.AddTransient<CardManageViewModel>();

        // Views
        services.AddTransient<MainWindow>();
        services.AddTransient<Views.Dialogs.CardManageDialog>();
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

        // 起動時処理
        PerformStartupTasks(dbContext);
    }

#if DEBUG
    /// <summary>
    /// テストデータを登録（デバッグ用）
    /// </summary>
    private async Task RegisterTestDataAsync()
    {
        var staffRepository = ServiceProvider.GetRequiredService<IStaffRepository>();
        var cardRepository = ServiceProvider.GetRequiredService<ICardRepository>();

        // テスト職員を登録
        var testStaffList = new[]
        {
            new Models.Staff { StaffIdm = "FFFF000000000001", Name = "山田太郎", Number = "001", Note = "テスト職員1" },
            new Models.Staff { StaffIdm = "FFFF000000000002", Name = "鈴木花子", Number = "002", Note = "テスト職員2" },
            new Models.Staff { StaffIdm = "FFFF000000000003", Name = "佐藤一郎", Number = "003", Note = "テスト職員3" },
        };

        foreach (var staff in testStaffList)
        {
            var existing = await staffRepository.GetByIdmAsync(staff.StaffIdm);
            if (existing == null)
            {
                await staffRepository.InsertAsync(staff);
                System.Diagnostics.Debug.WriteLine($"テスト職員を登録: {staff.Name}");
            }
        }

        // テストカードを登録
        var testCardList = new[]
        {
            new Models.IcCard { CardIdm = "07FE112233445566", CardType = "はやかけん", CardNumber = "TEST-001", Note = "テストカード1" },
            new Models.IcCard { CardIdm = "05FE112233445567", CardType = "nimoca", CardNumber = "TEST-002", Note = "テストカード2" },
            new Models.IcCard { CardIdm = "06FE112233445568", CardType = "SUGOCA", CardNumber = "TEST-003", Note = "テストカード3" },
            new Models.IcCard { CardIdm = "01FE112233445569", CardType = "Suica", CardNumber = "TEST-004", Note = "テストカード4" },
        };

        foreach (var card in testCardList)
        {
            var existing = await cardRepository.GetByIdmAsync(card.CardIdm);
            if (existing == null)
            {
                await cardRepository.InsertAsync(card);
                System.Diagnostics.Debug.WriteLine($"テストカードを登録: {card.CardType} {card.CardNumber}");
            }
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
