using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
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

        /// <summary>
        /// <c>dotnet run --no-build</c> に渡すビルド構成。環境変数 <c>ICCARDMANAGER_UITEST_CONFIGURATION</c> で
        /// 上書きでき、未設定なら <c>Debug</c>（従来どおり。dotnet run の既定と一致）。
        /// マニュアル用スクリーンショット（Issue #2016）は DEBUG 限定の仮想タッチパネルが写り込まないよう
        /// <c>Release</c> で起動する。
        /// </summary>
        internal static string LaunchConfiguration => ResolveLaunchConfiguration(
            Environment.GetEnvironmentVariable("ICCARDMANAGER_UITEST_CONFIGURATION"));

        /// <summary>
        /// 環境変数の値からビルド構成名を解決する（純粋関数）。<c>Release</c>（大文字小文字を無視）のときだけ
        /// <c>Release</c>、それ以外はすべて <c>Debug</c>。想定外の値で <c>dotnet run</c> が別ディレクトリを探しに行かないよう
        /// 2 値へ丸める。
        /// </summary>
        internal static string ResolveLaunchConfiguration(string? environmentValue) =>
            string.Equals(environmentValue, "Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

        private readonly Application _app;
        private readonly UIA3Automation _automation;

        /// <summary>
        /// 終了時に DB を復元するガード。入れ子の起動（<see cref="LaunchWithSeed"/> のマイグレーション用起動）は
        /// 復元の責任を持たないため null。
        /// </summary>
        private readonly UiTestDatabaseGuard? _ownedDatabaseGuard;
        private readonly Process? _dotnetProcess;

        /// <summary>
        /// アプリ本体（ICCardManager.exe）のプロセス。終了を待って DB ファイルのロック解放を確かめるために持つ（Issue #2108）。
        /// </summary>
        private readonly Process _appProcess;
        private bool _disposed;

        /// <summary>
        /// 終了処理でプロセスの終了を待つ上限。通常は終了の要求から数百 ms で終わる。
        /// </summary>
        private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(10);

        private AppFixture(Application app, UIA3Automation automation, UiTestDatabaseGuard? ownedDatabaseGuard, Process appProcess, Process? dotnetProcess)
        {
            _app = app;
            _automation = automation;
            _ownedDatabaseGuard = ownedDatabaseGuard;
            _appProcess = appProcess;
            _dotnetProcess = dotnetProcess;
        }

        /// <summary>
        /// テスト用の職員 1 件を事前投入してアプリを起動する。
        /// </summary>
        /// <remarks>
        /// StaffAuthDialog をトリガーするテスト用。
        /// IDm "FFFF000000000001"（DebugVirtualTouchButton と一致）で職員「テスト職員」を登録する。
        /// 既存 DB の退避・復元は <see cref="LaunchWithSeed"/> と同じ。
        /// </remarks>
        public static AppFixture LaunchWithSeededStaff()
        {
            return LaunchWithSeed(conn =>
            {
                // SQLite に職員を直接 INSERT する（テスト用ヘルパ）
                // staff テーブルのスキーマ: staff_idm (PK), name, number, note, is_deleted, deleted_at
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT OR IGNORE INTO staff (staff_idm, name, is_deleted) " +
                    $"VALUES ('{SeededStaffIdm}', 'テスト職員', 0)";
                cmd.ExecuteNonQuery();
            });
        }

        /// <summary>
        /// テスト用職員の IDm。DEBUG ビルドの仮想タッチボタン（<see cref="TestConstants.DebugVirtualTouchButtonName"/>）が
        /// 送出する IDm と一致させる。
        /// </summary>
        public const string SeededStaffIdm = "FFFF000000000001";

        /// <summary>
        /// 任意のデータを事前投入してアプリを起動する。
        /// </summary>
        /// <param name="seed">
        /// マイグレーション済みの空 DB に対して実行する投入処理。
        /// 接続は開いた状態で渡され、戻った後に閉じられる。
        /// </param>
        /// <remarks>
        /// 既存 DB を退避 → 空 DB でアプリを一度起動してマイグレーション → 終了後に <paramref name="seed"/> を実行 →
        /// 再起動、という手順を取る。退避は入口で 1 回だけ行い、返したフィクスチャの Dispose で元の DB を復元する。
        /// 途中で失敗した場合も復元してから例外を投げる（Issue #2062）。
        /// </remarks>
        public static AppFixture LaunchWithSeed(Action<SQLiteConnection> seed) =>
            LaunchWithSeed(seed, environment: null);

        /// <summary>
        /// 任意のデータを事前投入し、アプリへ環境変数を渡して起動する（Issue #2190）。
        /// </summary>
        /// <param name="seed"><see cref="LaunchWithSeed(Action{SQLiteConnection})"/> と同じ。</param>
        /// <param name="environment">
        /// 起動するアプリ（<c>dotnet run</c> とその子プロセス）へ渡す環境変数。null なら渡さない。
        /// マイグレーションのための起動にも同じ値を渡す（起動のたびに挙動が変わらないように）。
        /// </param>
        /// <remarks>
        /// テストプロセスの環境変数を書き換えずに済むよう、<see cref="ProcessStartInfo.EnvironmentVariables"/> で渡す。
        /// 書き換えると同じプロセスで後から走る別のテストへ漏れる。
        /// </remarks>
        public static AppFixture LaunchWithSeed(Action<SQLiteConnection> seed, IReadOnlyDictionary<string, string>? environment)
        {
            if (seed == null)
            {
                throw new ArgumentNullException(nameof(seed));
            }

            return LaunchWithSeedCore(
                DbDirectory,
                UiTestDatabaseGuard.UiTestBackupDirectory,
                launchForMigration: () =>
                {
                    var initialFixture = StartApplication(ownedDatabaseGuard: null, environment);
                    try
                    {
                        // メインウィンドウを取得することでアプリが完全初期化（DB マイグレーション完了）を保証
                        _ = initialFixture.MainWindow;
                        return initialFixture;
                    }
                    catch
                    {
                        initialFixture.Dispose();
                        throw;
                    }
                },
                seedDatabase: dbPath =>
                {
                    using var conn = new SQLiteConnection($"Data Source={dbPath};Version=3");
                    conn.Open();
                    seed(conn);
                },
                launchOwningGuard: guard => StartApplication(guard, environment));
        }

        /// <summary>
        /// 起動時のテストデータ自動登録（DEBUG ビルドの <c>RegisterTestDataAsync</c>）を止める環境変数（Issue #2190）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 本体の撮影モード（<c>App.ScreenshotModeEnvironmentVariable</c>。Issue #2019）を流用する。撮影モードの効果は
        /// ①DEBUG パネルを透明にする（UIA の Invoke は効く）②テストデータ登録を止める、の 2 つで、
        /// 回帰テストが必要とするのは②だけだが、①は UIA 経由の操作を妨げないので害は無い。
        /// </para>
        /// <para>
        /// テストデータ登録を止めないと、仮想タッチ用のカード（<see cref="ScreenshotSeedData.VirtualTouchCardIdm"/>）に
        /// デバッグ用の履歴が足され、DB を読んで表明する値が投入データだけで決まらなくなる。
        /// </para>
        /// </remarks>
        public static readonly IReadOnlyDictionary<string, string> SuppressDebugTestData =
            new Dictionary<string, string> { ["ICCARDMANAGER_SCREENSHOT_MODE"] = "1" };

        /// <summary>
        /// アプリが開くデータベースファイルのパス（Issue #2190）。テストが投入結果・操作結果を読み返すために使う。
        /// </summary>
        public static string DatabasePath => Path.Combine(DbDirectory, UiTestDatabaseGuard.DatabaseFileName);

        /// <summary>
        /// <see cref="LaunchWithSeed"/> の手順（退避・バックアップ先の隔離・マイグレーション・投入・本起動・失敗時の復元）。
        /// アプリの起動を差し替えて、実際に起動せずに DB の退避・復元を検証できるよう切り出している（Issue #2062）。
        /// </summary>
        /// <param name="dbDirectory">DB を置くフォルダー。</param>
        /// <param name="backupDirectory">
        /// テストのアプリが自動バックアップを作るフォルダー（Issue #2423）。マイグレーションのための起動より前に DB へ書くので、
        /// どちらの起動も既定のバックアップ先に触れない。
        /// </param>
        /// <param name="launchForMigration">
        /// マイグレーションのための起動。完了するまで待ってから返すこと。返した値は投入の前に Dispose する。
        /// DB の復元はしない（ガードを渡さない）。
        /// </param>
        /// <param name="seedDatabase">マイグレーション済み DB のパスを受け取り、データを投入する。</param>
        /// <param name="launchOwningGuard">本起動。受け取ったガードで、自身の Dispose 時に DB を復元すること。</param>
        /// <param name="maxAttempts">ファイルが使用中のときの試行回数（<see cref="UiTestDatabaseGuard.Acquire"/> へ渡す）。</param>
        internal static TFixture LaunchWithSeedCore<TFixture>(
            string dbDirectory,
            string backupDirectory,
            Func<IDisposable> launchForMigration,
            Action<string> seedDatabase,
            Func<UiTestDatabaseGuard, TFixture> launchOwningGuard,
            int maxAttempts = UiTestDatabaseGuard.DefaultMaxAttempts)
        {
            var guard = UiTestDatabaseGuard.Acquire(dbDirectory, maxAttempts);
            try
            {
                // 空の状態からマイグレーションさせる
                guard.DeleteWorkingDatabase();

                // Issue #2423: マイグレーションのための起動も起動時の自動バックアップを作るので、その前に書く。
                // 投入（seedDatabase）の中で書くと、1 回目の起動が既定のバックアップ先へ書き、その日の実 DB のバックアップを押し出す
                guard.IsolateBackupFolder(backupDirectory);

                // Issue #2108: Dispose がプロセスの終了まで待つので、DB ファイルのロックは解放済み。
                // 固定時間の待機（旧: 1 秒）は要らない
                using (launchForMigration())
                {
                }

                seedDatabase(guard.DatabasePath);

                // 本起動は退避し直さない。旧実装はここで投入済み DB を退避ファイルへ上書きし、元の DB を失っていた
                return launchOwningGuard(guard);
            }
            catch
            {
                // 起動の失敗の経路では既定のバックアップ先を確かめない（確かめて投げると本来の原因を上書きする）。
                // 途中まで進んだアプリが既定の保存先へ書いていても、この経路では検出しない。確かめるのは起動できた回の Dispose（FinishOwnedGuard）
                guard.Restore();
                throw;
            }
        }

        /// <summary>
        /// アプリケーションを起動し、メインウィンドウが表示されるまで待機する。
        /// 既存 DB を退避し、Dispose で復元する。
        /// </summary>
        /// <remarks>
        /// 投入はしないが、バックアップ先の設定（Issue #2423）だけは DB へ書く（開発機の DB があればその DB に。退避済みなので終了後に戻る）。
        /// </remarks>
        public static AppFixture Launch() =>
            LaunchCore(
                DbDirectory,
                UiTestDatabaseGuard.UiTestBackupDirectory,
                launchOwningGuard: guard => StartApplication(guard, environment: null));

        /// <summary>
        /// <see cref="Launch"/> の手順（退避・バックアップ先の隔離・起動・失敗時の復元）。アプリを起動せずに検証できるよう切り出している（Issue #2423）。
        /// </summary>
        /// <param name="dbDirectory">DB を置くフォルダー。</param>
        /// <param name="backupDirectory">テストのアプリが自動バックアップを作るフォルダー。</param>
        /// <param name="launchOwningGuard">起動。受け取ったガードで、自身の Dispose 時に DB を復元すること。</param>
        /// <param name="maxAttempts">ファイルが使用中のときの試行回数（<see cref="UiTestDatabaseGuard.Acquire"/> へ渡す）。</param>
        internal static TFixture LaunchCore<TFixture>(
            string dbDirectory,
            string backupDirectory,
            Func<UiTestDatabaseGuard, TFixture> launchOwningGuard,
            int maxAttempts = UiTestDatabaseGuard.DefaultMaxAttempts)
        {
            var guard = UiTestDatabaseGuard.Acquire(dbDirectory, maxAttempts);
            try
            {
                guard.IsolateBackupFolder(backupDirectory);
                return launchOwningGuard(guard);
            }
            catch
            {
                // 起動の失敗の経路では既定のバックアップ先を確かめない（確かめて投げると本来の原因を上書きする）。
                // 途中まで進んだアプリが既定の保存先へ書いていても、この経路では検出しない。確かめるのは起動できた回の Dispose（FinishOwnedGuard）
                guard.Restore();
                throw;
            }
        }

        /// <summary>
        /// アプリケーションを起動する。DB の退避は行わない（呼び出し元が <see cref="UiTestDatabaseGuard"/> を取得済み）。
        /// </summary>
        /// <param name="ownedDatabaseGuard">Dispose 時に復元するガード。復元の責任を持たない起動では null。</param>
        /// <param name="environment">アプリへ渡す環境変数。null なら渡さない。</param>
        private static AppFixture StartApplication(UiTestDatabaseGuard? ownedDatabaseGuard, IReadOnlyDictionary<string, string>? environment)
        {
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
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --no-build --configuration {LaunchConfiguration} --project \"{csprojPath}\"",
                WorkingDirectory = projectRoot,
                UseShellExecute = false
            };
            if (environment != null)
            {
                foreach (var pair in environment)
                {
                    startInfo.EnvironmentVariables[pair.Key] = pair.Value;
                }
            }

            var dotnetProcess = Process.Start(startInfo);

            if (dotnetProcess == null)
            {
                throw new InvalidOperationException("dotnet run プロセスの起動に失敗しました。");
            }

            // 子プロセス（ICCardManager.exe）が出現するのを待つ
            Process appProcess;
            try
            {
                appProcess = WaitForAppProcess(dotnetProcess, startTime,
                    TimeSpan.FromSeconds(TestConstants.AppLaunchTimeoutSeconds));
            }
            catch
            {
                // 呼び出し元が DB を復元する前に、DB を掴み得るプロセスを止める
                try { if (!dotnetProcess.HasExited) { dotnetProcess.Kill(); } } catch { /* 既に終了済み */ }
                throw;
            }

            var app = Application.Attach(appProcess);
            var automation = new UIA3Automation();

            return new AppFixture(app, automation, ownedDatabaseGuard, appProcess, dotnetProcess);
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
                {
                    return window;
                }

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
            if (_disposed)
            {
                return;
            }

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

            // Issue #2108: 固定時間の待機（旧: 500ms）ではなく、プロセスの終了そのものを待つ。
            // 終了したプロセスのファイルハンドルは OS が閉じているので、DB を復元できる
            WaitForExit(_appProcess);
            WaitForExit(_dotnetProcess);

            _automation.Dispose();

            // テスト本体が失敗している最中にここで例外を投げると、using の Dispose の例外がテスト本体の例外を上書きする。
            // 両方が同時に起きるのは隔離が外れた（またはピッすいが DB を掴んでいる）ときだけで、どちらも直すべき失敗なので許容する
            FinishOwnedGuard(_ownedDatabaseGuard);
        }

        /// <summary>
        /// 起動を所有するフィクスチャの後始末のうち、ガードに関わる部分（DB の復元と、既定のバックアップ先の確認）。
        /// アプリの終了を待った後に呼ぶ。GUI 不要で検証できるよう <see cref="Dispose"/> から切り出している（Issue #2423）。
        /// </summary>
        /// <param name="ownedDatabaseGuard">所有するガード。入れ子の起動（null）では何もしない。</param>
        internal static void FinishOwnedGuard(UiTestDatabaseGuard? ownedDatabaseGuard)
        {
            if (ownedDatabaseGuard == null)
            {
                return;
            }

            FinishOwnedGuard(
                // DB を復元する（失敗したら退避ファイルは残り、次回の起動が回復する）
                restore: ownedDatabaseGuard.Restore,
                // Issue #2423: テストのアプリが既定のバックアップ先へ書いていない（本体が UI テスト専用の保存先を拒んで黙って戻っていない）こと
                checkDefaultBackupFolder: ownedDatabaseGuard.ThrowIfDefaultBackupFolderChanged);
        }

        /// <summary>
        /// <see cref="FinishOwnedGuard(UiTestDatabaseGuard)"/> の本体。復元と確認を互いの成否と独立に走らせ、失敗はすべて伝える。
        /// 確認の失敗の種類（一覧を読めない IOException 等）を差し替えて検証できるよう、処理を受け取る形にしている。
        /// </summary>
        /// <remarks>
        /// 確認は復元の成否と独立に走らせる。次回の <see cref="UiTestDatabaseGuard.Acquire"/> はその時点の一覧を記録し直すので、
        /// ここで見なければこの回の書き込みは二度と検出できない。どちらの失敗も型で絞らずに受ける — 確認が一覧を読めずに
        /// IOException / UnauthorizedAccessException で失敗したときに、復元の失敗を失わないため。
        /// </remarks>
        internal static void FinishOwnedGuard(Action restore, Action checkDefaultBackupFolder)
        {
            Exception? restoreFailure = null;
            try
            {
                restore();
            }
            catch (Exception ex)
            {
                restoreFailure = ex;
            }

            Exception? checkFailure = null;
            try
            {
                checkDefaultBackupFolder();
            }
            catch (Exception ex)
            {
                checkFailure = ex;
            }

            if (restoreFailure != null && checkFailure != null)
            {
                // net48 の AggregateException.Message は内側の例外の文言を含まないので、両方の文言を自分で並べる
                throw new AggregateException(
                    $"UI テストの後始末で 2 つの失敗がありました。DB の復元: {restoreFailure.Message} / 既定のバックアップ先: {checkFailure.Message}",
                    restoreFailure,
                    checkFailure);
            }

            if (restoreFailure != null)
            {
                ExceptionDispatchInfo.Capture(restoreFailure).Throw();
            }

            if (checkFailure != null)
            {
                ExceptionDispatchInfo.Capture(checkFailure).Throw();
            }
        }

        /// <summary>
        /// プロセスの終了を上限付きで待つ。終了済み・取得不能なら何もしない。
        /// </summary>
        private static void WaitForExit(Process? process)
        {
            try
            {
                if (process != null && !process.WaitForExit((int)ProcessExitTimeout.TotalMilliseconds))
                {
                    // 黙って復元へ進まない。DB を掴んだままなら復元が失敗し、退避ファイルは次回の起動が回復する
                    System.Diagnostics.Trace.WriteLine(
                        $"[AppFixture] プロセス {process.Id} が {ProcessExitTimeout.TotalSeconds} 秒以内に終了しませんでした。DB の復元に失敗する可能性があります。");
                }
            }
            catch
            {
                // 既に終了済み・権限不足。復元は続ける（失敗したら退避ファイルが残り、次回の起動が回復する）
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
        /// 既定の exe パスを解決する。
        /// テスト DLL と同じビルド構成（Release/Debug）を優先し、
        /// 見つからなければもう一方の構成にフォールバックする。
        /// </summary>
        private static string ResolveDefaultExePath()
        {
            var testAssemblyDir = AppDomain.CurrentDomain.BaseDirectory;
            var (primaryPath, fallbackPath) = ResolveExePathCandidates(testAssemblyDir);

            if (File.Exists(primaryPath))
            {
                return primaryPath;
            }

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
