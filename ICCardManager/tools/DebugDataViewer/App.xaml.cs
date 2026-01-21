using System;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
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
                MessageBox.Show(
                    $"起動エラーが発生しました:\n\n{ex.Message}\n\n詳細:\n{ex}",
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
