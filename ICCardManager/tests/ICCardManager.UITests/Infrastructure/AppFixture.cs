using System;
using System.Diagnostics;
using System.IO;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using System.Data.SQLite;

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
        private readonly Process? _dotnetProcess;
        private bool _disposed;

        private AppFixture(Application app, UIA3Automation automation, bool dbBackedUp, Process? dotnetProcess = null)
        {
            _app = app;
            _automation = automation;
            _dbBackedUp = dbBackedUp;
            _dotnetProcess = dotnetProcess;
        }

        /// <summary>
        /// テスト用の職員 1 件を事前投入してアプリを起動する。
        /// </summary>
        /// <remarks>
        /// StaffAuthDialog をトリガーするテスト用。
        /// IDm "FFFF000000000001"（DebugVirtualTouchButton と一致）で職員「テスト職員」を登録する。
        /// 既存 DB がある場合は退避し、新規空 DB を作って職員を投入する。
        /// マイグレーションは一時的にアプリを起動して実行させ、終了後に職員 INSERT を行い、
        /// 再度本起動する。ただし初回起動の Dispose が DB をリストアしないよう、
        /// 初回 Dispose 前に BackupPath を削除してリストアをスキップする。
        /// </remarks>
        public static AppFixture LaunchWithSeededStaff()
        {
            RecoverOrphanBackups();

            // 既存 DB を退避
            if (File.Exists(DbPath))
            {
                CopyFileWithRetry(DbPath, DbBackupPath, maxRetries: 5, delayMs: 500);
            }

            // 空 DB ファイルを削除（アプリ初回起動でマイグレーション実行）
            try { File.Delete(DbPath); } catch { /* ignore */ }

            // アプリを起動して初期マイグレーションを実行させる
            var initialFixture = Launch();
            // メインウィンドウを取得することでアプリが完全初期化（DB マイグレーション完了）を保証
            _ = initialFixture.MainWindow;

            // Dispose 時に DB がリストアされないよう、BackupPath を退避してからリストアを防ぐ
            // ※ initialFixture._dbBackedUp = true だと Dispose 後に BackupPath から DbPath へ復元されるため、
            //    BackupPath を別名に移動しておき、Dispose 後に職員 INSERT → 再起動、最後に元の BackupPath を復元
            var seededBackupPath = DbBackupPath + ".seeded-original";
            if (File.Exists(DbBackupPath))
            {
                CopyFileWithRetry(DbBackupPath, seededBackupPath, maxRetries: 3, delayMs: 300);
                try { File.Delete(DbBackupPath); } catch { /* ignore */ }
            }

            // 初回 Dispose（_dbBackedUp=true だが BackupPath が無いのでリストアされない）
            initialFixture.Dispose();

            // プロセス終了後の DB ファイルロック解放を待つ
            System.Threading.Thread.Sleep(1000);

            // SQLite に職員を直接 INSERT する（テスト用ヘルパ）
            // staff テーブルのスキーマ: staff_idm (PK), name, number, note, is_deleted, deleted_at
            using (var conn = new SQLiteConnection($"Data Source={DbPath};Version=3"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT OR IGNORE INTO staff (staff_idm, name, is_deleted) " +
                    "VALUES ('FFFF000000000001', 'テスト職員', 0)";
                cmd.ExecuteNonQuery();
            }

            // 再度起動（投入済み DB を使う）
            // この時点で BackupPath は存在しない（上で削除済み）ので Launch() 内の dbBackedUp=false
            // → Dispose 時にリストアなし。元の DB は seededBackupPath に保管されているので後で処理が必要。
            // ただし UITest 終了後のリストアは別途考慮が必要。簡略化のため seededBackupPath を BackupPath に戻す。
            if (File.Exists(seededBackupPath))
            {
                CopyFileWithRetry(seededBackupPath, DbBackupPath, maxRetries: 3, delayMs: 300);
                try { File.Delete(seededBackupPath); } catch { /* ignore */ }
            }

            // 再起動（投入済み DB を使う）。Launch() は DbPath が存在するため BackupPath へバックアップする。
            // BackupPath に元の DB（seededBackupPath から復元済み）があるので正しくリストアされる。
            return Launch();
        }

        /// <summary>
        /// アプリケーションを起動し、メインウィンドウが表示されるまで待機する。
        /// </summary>
        public static AppFixture Launch()
        {
            RecoverOrphanBackups();

            // DB バックアップ（既存 DB がある場合のみ）
            // 前回のテストプロセスが DB を解放するまで少し待つ場合がある
            var dbBackedUp = false;
            if (File.Exists(DbPath))
            {
                dbBackedUp = CopyFileWithRetry(DbPath, DbBackupPath, maxRetries: 5, delayMs: 500);
            }

            // dotnet run --no-build でアプリを起動する。
            // SDK-style の .NET Framework 4.8 プロジェクトでは exe の直接起動だと
            // アセンブリ解決に失敗する場合があるため、dotnet CLI 経由で起動する。
            var projectRoot = ResolveProjectRoot();
            var csprojPath = Path.Combine(projectRoot, "src", "ICCardManager", "ICCardManager.csproj");

            if (!File.Exists(csprojPath))
            {
                throw new FileNotFoundException(
                    $"メインプロジェクトの csproj が見つかりません: {csprojPath}\n" +
                    "先にメインプロジェクトをビルドしてください。");
            }

            // dotnet run は子プロセスとして WPF アプリ（ICCardManager.exe）を起動する。
            // FlaUI の Application.Launch(ProcessStartInfo) は dotnet.exe プロセスを追跡するが、
            // WPF ウィンドウは子プロセスに属するため GetMainWindow で見つからない。
            // そのため Process.Start で起動し、子プロセスを Application.Attach で接続する。
            var startTime = DateTime.Now;
            var dotnetProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --no-build --project \"{csprojPath}\"",
                WorkingDirectory = projectRoot,
                UseShellExecute = false
            });

            if (dotnetProcess == null)
            {
                throw new InvalidOperationException("dotnet run プロセスの起動に失敗しました。");
            }

            // 子プロセス（ICCardManager.exe）が出現するのを待つ
            var appProcess = WaitForAppProcess(dotnetProcess, startTime,
                TimeSpan.FromSeconds(TestConstants.AppLaunchTimeoutSeconds));

            var app = Application.Attach(appProcess);
            var automation = new UIA3Automation();

            return new AppFixture(app, automation, dbBackedUp, dotnetProcess);
        }

        /// <summary>
        /// メインウィンドウを取得する（タイムアウト付き）。
        /// プロセスが早期終了した場合は診断情報を含むエラーを返す。
        /// </summary>
        public Window MainWindow
        {
            get
            {
                // プロセスが既に終了していないか確認
                if (_app.HasExited)
                {
                    throw new InvalidOperationException(
                        $"アプリケーションプロセスが起動直後に終了しました（ExitCode: {_app.ExitCode}）。");
                }

                var window = _app.GetMainWindow(
                    _automation,
                    TimeSpan.FromSeconds(TestConstants.AppLaunchTimeoutSeconds));

                if (window != null)
                    return window;

                // タイムアウト時の診断情報
                var exitInfo = _app.HasExited
                    ? $"プロセスは終了済み（ExitCode: {_app.ExitCode}）"
                    : "プロセスは実行中だがウィンドウが見つからない";

                throw new TimeoutException(
                    $"メインウィンドウが {TestConstants.AppLaunchTimeoutSeconds} 秒以内に表示されませんでした。\n{exitInfo}");
            }
        }

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

            // dotnet run の親プロセスも終了させる
            try
            {
                if (_dotnetProcess != null && !_dotnetProcess.HasExited)
                {
                    _dotnetProcess.Kill();
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
        /// dotnet run が起動した子プロセス（ICCardManager.exe）を検索して返す。
        /// </summary>
        private static Process WaitForAppProcess(Process dotnetProcess, DateTime startTime, TimeSpan timeout)
        {
            var deadline = DateTime.Now + timeout;

            while (DateTime.Now < deadline)
            {
                if (dotnetProcess.HasExited)
                {
                    throw new InvalidOperationException(
                        $"dotnet run が早期終了しました（ExitCode: {dotnetProcess.ExitCode}）。\n" +
                        "メインプロジェクトがビルド済みか確認してください。");
                }

                foreach (var proc in Process.GetProcessesByName("ICCardManager"))
                {
                    try
                    {
                        // dotnet run 起動後に開始されたプロセスのみ対象とする
                        if (!proc.HasExited && proc.StartTime >= startTime.AddSeconds(-2))
                        {
                            return proc;
                        }
                    }
                    catch
                    {
                        // アクセス権限エラー等は無視
                    }
                }

                System.Threading.Thread.Sleep(500);
            }

            throw new TimeoutException(
                $"ICCardManager プロセスが {timeout.TotalSeconds} 秒以内に見つかりませんでした。\n" +
                $"dotnet run プロセス状態: {(dotnetProcess.HasExited ? "終了済み" : "実行中")}");
        }

        /// <summary>
        /// 前回テスト実行中にプロセス中断で残ったオーファンバックアップを復元する。
        /// Issue #1509 関連: LaunchWithSeededStaff が中断された場合のユーザー DB 保護。
        /// </summary>
        private static void RecoverOrphanBackups()
        {
            var seededOriginalPath = DbBackupPath + ".seeded-original";
            if (File.Exists(seededOriginalPath))
            {
                // seeded-original が残っている → 信頼できる元 DB として DbPath へ復元する
                CopyFileWithRetry(seededOriginalPath, DbPath, maxRetries: 3, delayMs: 500);
                try { File.Delete(seededOriginalPath); } catch { /* ignore */ }
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
        /// テスト DLL のディレクトリからプロジェクトルート（.sln があるディレクトリ）を解決する。
        /// </summary>
        private static string ResolveProjectRoot()
        {
            var testAssemblyDir = AppDomain.CurrentDomain.BaseDirectory;
            // tests/ICCardManager.UITests/bin/{Config}/net48/ → プロジェクトルート
            return Path.GetFullPath(
                Path.Combine(testAssemblyDir, "..", "..", "..", "..", ".."));
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
