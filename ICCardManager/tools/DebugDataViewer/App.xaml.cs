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

            // カードリーダー
            services.AddSingleton<ICardReader, PcScCardReader>();

            // ViewModel
            services.AddTransient<MainViewModel>();

            // Views
            services.AddTransient<MainWindow>();
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
