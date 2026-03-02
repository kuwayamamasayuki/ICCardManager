using System;
using System.IO;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// アプリケーションの起動・終了、および DB ファイルの分離を管理するフィクスチャ。
    /// <para>
    /// 使い方:
    /// <code>
    /// using var fixture = AppFixture.Launch();
    /// var mainWindow = fixture.MainWindow;
    /// </code>
    /// </para>
    /// </summary>
    internal sealed class AppFixture : IDisposable
    {
        private static readonly string DefaultExePath = ResolveDefaultExePath();

        private static readonly string DbDirectory =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ICCardManager");

        private static readonly string DbPath = Path.Combine(DbDirectory, "iccard.db");
        private static readonly string DbBackupPath = Path.Combine(DbDirectory, "iccard.db.uitest-backup");

        private readonly Application _app;
        private readonly UIA3Automation _automation;
        private readonly bool _dbBackedUp;
        private bool _disposed;

        private AppFixture(Application app, UIA3Automation automation, bool dbBackedUp)
        {
            _app = app;
            _automation = automation;
            _dbBackedUp = dbBackedUp;
        }

        /// <summary>
        /// アプリケーションを起動し、メインウィンドウが表示されるまで待機する。
        /// </summary>
        public static AppFixture Launch()
        {
            var exePath = Environment.GetEnvironmentVariable("ICCARD_APP_EXE")
                          ?? DefaultExePath;

            if (!File.Exists(exePath))
            {
                throw new FileNotFoundException(
                    $"テスト対象の exe が見つかりません: {exePath}\n" +
                    "先にメインプロジェクトをビルドするか、環境変数 ICCARD_APP_EXE を設定してください。");
            }

            // DB バックアップ（既存 DB がある場合のみ）
            // 前回のテストプロセスが DB を解放するまで少し待つ場合がある
            var dbBackedUp = false;
            if (File.Exists(DbPath))
            {
                dbBackedUp = CopyFileWithRetry(DbPath, DbBackupPath, maxRetries: 5, delayMs: 500);
            }

            var app = Application.Launch(exePath);
            var automation = new UIA3Automation();

            return new AppFixture(app, automation, dbBackedUp);
        }

        /// <summary>
        /// メインウィンドウを取得する（タイムアウト付き）。
        /// </summary>
        public Window MainWindow =>
            _app.GetMainWindow(
                _automation,
                TimeSpan.FromSeconds(TestConstants.AppLaunchTimeoutSeconds))
            ?? throw new TimeoutException(
                $"メインウィンドウが {TestConstants.AppLaunchTimeoutSeconds} 秒以内に表示されませんでした。");

        /// <summary>
        /// UIA3Automation インスタンス。ダイアログ検索等で必要になる場合に使用。
        /// </summary>
        public UIA3Automation Automation => _automation;

        /// <summary>
        /// Application インスタンス。プロセス状態の確認等に使用。
        /// </summary>
        public Application App => _app;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // アプリケーションを終了
            try
            {
                if (!_app.HasExited)
                {
                    _app.Close();
                    // プロセス終了を待機（DB ファイルのロック解放のため）
                    _app.WaitWhileMainHandleIsMissing(TimeSpan.FromSeconds(5));
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                if (!_app.HasExited)
                {
                    _app.Kill();
                }
            }
            catch
            {
                // 既に終了済み
            }

            // プロセス終了後に少し待機（DB ファイルロック解放を待つ）
            System.Threading.Thread.Sleep(500);

            _automation.Dispose();

            // DB リストア
            if (_dbBackedUp && File.Exists(DbBackupPath))
            {
                CopyFileWithRetry(DbBackupPath, DbPath, maxRetries: 3, delayMs: 500);
                try { File.Delete(DbBackupPath); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// ファイルロック対策としてリトライ付きでファイルをコピーする。
        /// </summary>
        private static bool CopyFileWithRetry(string source, string dest, int maxRetries, int delayMs)
        {
            for (var i = 0; i < maxRetries; i++)
            {
                try
                {
                    File.Copy(source, dest, overwrite: true);
                    return true;
                }
                catch (IOException) when (i < maxRetries - 1)
                {
                    System.Threading.Thread.Sleep(delayMs);
                }
            }
            return false;
        }

        /// <summary>
        /// 既定の exe パスを解決する。
        /// テスト DLL と同じビルド構成（Release/Debug）を優先し、
        /// 見つからなければもう一方の構成にフォールバックする。
        /// </summary>
        private static string ResolveDefaultExePath()
        {
            var testAssemblyDir = AppDomain.CurrentDomain.BaseDirectory;
            var (primaryPath, fallbackPath) = ResolveExePathCandidates(testAssemblyDir);

            if (File.Exists(primaryPath))
                return primaryPath;

            return File.Exists(fallbackPath) ? fallbackPath : primaryPath;
        }

        /// <summary>
        /// テスト DLL のディレクトリから exe パスの候補を算出する（純粋なパス計算、I/O なし）。
        /// </summary>
        /// <param name="testAssemblyDir">テスト DLL の BaseDirectory（末尾セパレータあり/なし両対応）。</param>
        /// <returns>primaryPath（テスト構成と同じ）と fallbackPath（もう一方の構成）のタプル。</returns>
        internal static (string primaryPath, string fallbackPath) ResolveExePathCandidates(string testAssemblyDir)
        {
            // tests/ICCardManager.UITests/bin/{Config}/net48/
            //   → src/ICCardManager/bin/{Config}/net48/ICCardManager.exe
            var projectRoot = Path.GetFullPath(
                Path.Combine(testAssemblyDir, "..", "..", "..", "..", ".."));

            // テスト DLL 自身のパスからビルド構成を推定する
            // testAssemblyDir は …/bin/Debug/net48/ または …/bin/Release/net48/ の形式
            var parentOfNet48 = Path.GetFullPath(Path.Combine(testAssemblyDir, ".."));
            var configDir = Path.GetFileName(parentOfNet48);

            var primaryConfig = configDir;
            var fallbackConfig = string.Equals(configDir, "Release", StringComparison.OrdinalIgnoreCase)
                ? "Debug"
                : "Release";

            var primaryPath = Path.Combine(
                projectRoot, "src", "ICCardManager", "bin", primaryConfig, "net48", "ICCardManager.exe");
            var fallbackPath = Path.Combine(
                projectRoot, "src", "ICCardManager", "bin", fallbackConfig, "net48", "ICCardManager.exe");

            return (primaryPath, fallbackPath);
        }
    }
}
