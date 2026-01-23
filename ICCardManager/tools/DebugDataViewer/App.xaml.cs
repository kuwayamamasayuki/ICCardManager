using System;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ICCardManager.Data;
using ICCardManager.Infrastructure.CardReader;

namespace DebugDataViewer
{
    /// <summary>
    /// デバッグデータビューアのアプリケーションエントリポイント
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// DIコンテナ
        /// </summary>
        public IServiceProvider ServiceProvider { get; private set; }

        /// <summary>
        /// 現在のアプリケーションインスタンス
        /// </summary>
        public new static App Current => (App)Application.Current;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                // DIコンテナのセットアップ
                var services = new ServiceCollection();
                ConfigureServices(services);
                ServiceProvider = services.BuildServiceProvider();

                // メインウィンドウを表示
                var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                var errorMessage = $"起動エラーが発生しました:\n\n{ex.Message}\n\n詳細:\n{ex}";

                // クリップボードにコピー
                try
                {
                    Clipboard.SetText(errorMessage);
                }
                catch
                {
                    // クリップボードへのコピーに失敗しても続行
                }

                // エラーログファイルに出力
                try
                {
                    var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
                    File.WriteAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{errorMessage}");
                }
                catch
                {
                    // ログ出力に失敗しても続行
                }

                MessageBox.Show(
                    $"起動エラーが発生しました。\n\nエラー内容はクリップボードにコピーされました。\nまた、error.log ファイルにも出力されています。\n\n{ex.Message}",
                    "DebugDataViewer - エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        /// <summary>
        /// サービスを登録
        /// </summary>
        private void ConfigureServices(IServiceCollection services)
        {
            // ロギング
            services.AddLogging(builder =>
            {
                builder.AddDebug();
                builder.SetMinimumLevel(LogLevel.Debug);
            });

            // データベースコンテキスト
            var dbPath = FindDatabasePath();
            services.AddSingleton(sp => new DbContext(dbPath));

            // カードリーダーの自動選択:
            // 1. felicalib.dll が存在する場合: FelicaCardReader（残高・履歴読み取り可能）
            // 2. それ以外: PcScCardReader（IDm読み取りのみ）
            services.AddSingleton<ICardReader>(sp => CreateCardReader(sp));

            // ViewModel
            services.AddTransient<MainViewModel>();

            // Views
            services.AddTransient<MainWindow>();
        }

        /// <summary>
        /// 利用可能なカードリーダーを自動選択して作成します。
        /// </summary>
        /// <remarks>
        /// 以下の優先順位でカードリーダーを選択します：
        /// 1. FelicaCardReader: felicalib.dll が利用可能な場合（残高・履歴読み取り可能）
        /// 2. PcScCardReader: PC/SC API が利用可能な場合（IDm読み取りのみ）
        /// </remarks>
        private static ICardReader CreateCardReader(IServiceProvider sp)
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            // felicalib.dll の存在を確認
            if (IsFelicaLibAvailable())
            {
                var logger = loggerFactory.CreateLogger<FelicaCardReader>();
                System.Diagnostics.Debug.WriteLine("[DebugDataViewer] FelicaCardReader を使用します（残高・履歴読み取り可能）");
                return new FelicaCardReader(logger);
            }

            // フォールバック: PcScCardReader
            {
                var logger = loggerFactory.CreateLogger<PcScCardReader>();
                System.Diagnostics.Debug.WriteLine("[DebugDataViewer] PcScCardReader を使用します（IDm読み取りのみ、残高・履歴は読み取れません）");
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
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var dllPath = Path.Combine(baseDir, "felicalib.dll");

                return File.Exists(dllPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// データベースファイルのパスを検索
        /// </summary>
        private string FindDatabasePath()
        {
            // 優先順位:
            // 1. コマンドライン引数
            // 2. 実行ファイルと同じディレクトリ
            // 3. メインアプリの標準パス

            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1]))
            {
                return args[1];
            }

            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var localDb = Path.Combine(exeDir, "iccard.db");
            if (File.Exists(localDb))
            {
                return localDb;
            }

            // メインアプリの標準パス
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ICCardManager",
                "iccard.db");
            if (File.Exists(appDataPath))
            {
                return appDataPath;
            }

            // 見つからない場合はデフォルトパスを返す（DbContextが初期化する）
            return localDb;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // クリーンアップ
            if (ServiceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }
            base.OnExit(e);
        }
    }
}
