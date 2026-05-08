using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using ICCardManager.Data.Migrations;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;
using System.Data.SQLite;

namespace ICCardManager.Data
{
    /// <summary>
    /// 接続リース。Disposeでセマフォを解放する。
    /// using文で使用することで、DB操作の直列化を保証する。
    /// </summary>
    public sealed class ConnectionLease : IDisposable
    {
        /// <summary>リースされた接続</summary>
        public SQLiteConnection Connection { get; }

        private readonly Action _onDispose;
        private bool _disposed;

        internal ConnectionLease(SQLiteConnection connection, Action onDispose)
        {
            Connection = connection;
            _onDispose = onDispose;
        }

        /// <summary>リースを解放し、セマフォを返却する</summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _onDispose?.Invoke();
            }
        }
    }

    /// <summary>
    /// トランザクションスコープ。リースとトランザクションを束ね、
    /// Disposeで両方を適切な順序で解放する。
    /// </summary>
    public sealed class TransactionScope : IDisposable
    {
        /// <summary>接続リース</summary>
        public ConnectionLease Lease { get; }

        /// <summary>トランザクション</summary>
        public SQLiteTransaction Transaction { get; }

        private bool _disposed;

        internal TransactionScope(ConnectionLease lease, SQLiteTransaction transaction)
        {
            Lease = lease;
            Transaction = transaction;
        }

        /// <summary>トランザクションをコミットする</summary>
        public void Commit() => Transaction.Commit();

        /// <summary>トランザクションをロールバックする</summary>
        public void Rollback() => Transaction.Rollback();

        /// <summary>トランザクション→リースの順に解放する</summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Transaction?.Dispose();
                Lease?.Dispose();
            }
        }
    }

    /// <summary>
    /// Issue #1166: 接続一時停止スコープ。
    /// リストア中にバックグラウンドタスクが接続を再オープンすることを防止する。
    /// Disposeで自動的に停止を解除する。
    /// </summary>
    public sealed class ConnectionSuspensionScope : IDisposable
    {
        private readonly DbContext _dbContext;
        private bool _disposed;

        internal ConnectionSuspensionScope(DbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _dbContext.ResumeConnections();
            }
        }
    }

/// <summary>
    /// SQLiteデータベース接続管理クラス
    /// </summary>
    public class DbContext : IDisposable, IDatabaseInfo
    {
        private readonly string _connectionString;
        private readonly object _connectionLock = new object();
        private readonly ILogger _logger;
        private SQLiteConnection _connection;
        private bool _disposed;

        /// <summary>
        /// DB操作の直列化用セマフォ。同一接続への並行アクセスを防止する。
        /// </summary>
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// リエントラントカウント。同一非同期フロー内のネスト呼び出しでセマフォ再取得をスキップする。
        /// </summary>
        private readonly AsyncLocal<int> _reentrancyCount = new AsyncLocal<int>();

        /// <summary>
        /// Issue #1166: 接続一時停止フラグ。
        /// リストア中にバックグラウンドタスクが接続を再オープンすることを防止する。
        /// </summary>
        private volatile bool _isSuspended;

        /// <summary>
        /// ジッター生成用の乱数。thundering herd問題を防止するためリトライ待機に使用
        /// </summary>
        private static readonly Random _jitterRandom = new Random();

        /// <summary>
        /// 共有モード（ユーザーがDB保存先を明示的に指定した場合）かどうか
        /// </summary>
        /// <remarks>
        /// UNCパス（\\server\share）だけでなく、ドライブレター形式（D:\share）の
        /// マップドドライブも含む。デフォルトパス以外が指定された場合にtrueとなる。
        /// </remarks>
        public bool IsSharedMode { get; }

        /// <summary>
        /// ローカルモードのbusy_timeout値（ミリ秒）
        /// </summary>
        internal const int LocalBusyTimeoutMs = 5000;

        /// <summary>
        /// 共有モードのbusy_timeout値（ミリ秒）。
        /// SMBのネットワーク遅延と最大約20台の同時アクセスを考慮し、ローカルモードより長く設定
        /// </summary>
        internal const int SharedBusyTimeoutMs = 15000;

        /// <summary>
        /// busy_timeout値（ミリ秒）。モードに応じた値を返す
        /// </summary>
        internal int BusyTimeoutMs => IsSharedMode ? SharedBusyTimeoutMs : LocalBusyTimeoutMs;

        /// <summary>
        /// データベースファイル名
        /// </summary>
        public const string DatabaseFileName = "iccard.db";

        /// <summary>
        /// データベースファイルのパス
        /// </summary>
        public string DatabasePath { get; }

        /// <summary>
        /// Issue #1172: 最後に設定/確認されたSQLiteジャーナルモード（小文字、例: "delete", "truncate", "persist", "unknown"）。
        /// 接続初期化前はnull。ConfigureJournalModeが呼ばれた際にセットされる。
        /// </summary>
        public virtual string CurrentJournalMode { get; private set; }

        /// <summary>
        /// Issue #1172: ジャーナルモードがDELETE以外（クラッシュ耐性が低下した状態）かどうか。
        /// nullまたは"delete"の場合はfalse、それ以外（truncate/persist/unknown等）はtrue。
        /// </summary>
        /// <remarks>
        /// この値がtrueの場合、UI側で警告を表示することを推奨する。
        /// MainViewModel.InitializeAsyncで起動時にチェックされる。
        /// </remarks>
        public virtual bool IsJournalModeDegraded =>
            !string.IsNullOrEmpty(CurrentJournalMode) && CurrentJournalMode != "delete";

        /// <summary>
        /// テスト用のprotectedコンストラクタ
        /// </summary>
        protected DbContext()
        {
            DatabasePath = string.Empty;
            _connectionString = string.Empty;
        }

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="databasePath">データベースファイルのパス（省略時はアプリフォルダ内）</param>
        /// <param name="logger">ロガー（省略時はログ出力なし）</param>
        public DbContext(string databasePath = null, ILogger<DbContext> logger = null)
        {
            _logger = logger;
            DatabasePath = databasePath ?? GetDefaultDatabasePath();

            // SQLiteはバックスラッシュのUNCパス（\\server\share）を開けないため、
            // フォワードスラッシュ（//server/share）に変換する
            var effectivePath = IsUncPath(DatabasePath)
                ? DatabasePath.Replace('\\', '/')
                : DatabasePath;

            // SQLiteConnectionStringBuilderでエスケープし、接続文字列インジェクションを防止
            var builder = new SQLiteConnectionStringBuilder { DataSource = effectivePath };
            _connectionString = builder.ToString();
            // ユーザーが明示的にパスを指定した場合（databasePathがnull以外）は共有モード
            IsSharedMode = databasePath != null;
        }

        /// <summary>
        /// UNCパス（ネットワーク共有）かどうかを判定
        /// </summary>
        internal static bool IsUncPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            try
            {
                return new Uri(path).IsUnc;
            }
            catch (UriFormatException)
            {
                // パスがURI形式でない場合は \\で始まるかを直接チェック
                return path.StartsWith(@"\\", StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// デフォルトのデータベースパスを取得
        /// </summary>
        /// <remarks>
        /// CommonApplicationData（C:\ProgramData）を使用することで、
        /// 異なるユーザーがログインしても同じデータにアクセスできるようにする
        /// </remarks>
        private static string GetDefaultDatabasePath()
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ICCardManager");

            // ディレクトリを作成し、全ユーザーがアクセスできるように権限を設定
            EnsureDirectoryWithPermissions(appDataPath);

            return Path.Combine(appDataPath, DatabaseFileName);
        }

        /// <summary>
        /// ディレクトリを作成する
        /// </summary>
        /// <remarks>
        /// Issue #1455: 旧実装ではランタイムで <c>BUILTIN\Users : FullControl</c> を
        /// <c>AddAccessRule</c> で付与していたが、以下の理由で撤廃した:
        /// (1) <c>FullControl</c> は削除権限まで含むため、一般ユーザーが他ユーザーのファイルを
        ///     削除・差替え可能な過剰権限となっていた（PII 置換攻撃の足掛かり）。
        /// (2) <c>AddAccessRule</c> は冪等ではなく、起動の度に新規 ACE が追加され ACL が累積する。
        /// (3) インストーラー (<c>installer/ICCardManager.iss</c>) が
        ///     <c>{commonappdata}\ICCardManager</c> 配下に <c>Permissions: users-full</c> を
        ///     設定済みのため、ランタイムでの再付与は機能的に冗長。
        /// 共有モード（UNC パス）等インストーラーの管理外パスを使う場合の権限は管理者責任とする。
        /// </remarks>
        /// <param name="directoryPath">ディレクトリパス</param>
        internal static void EnsureDirectoryWithPermissions(string directoryPath)
        {
            // Directory.CreateDirectoryは既存ディレクトリに対しても安全（冪等）
            Directory.CreateDirectory(directoryPath);
        }

        /// <summary>
        /// 接続リースを非同期で取得する。using文で使用すること。
        /// </summary>
        /// <remarks>
        /// <para>
        /// セマフォは取得せず、同一の <see cref="SQLiteConnection"/> インスタンスを返す。
        /// 書き込み操作は <see cref="BeginTransactionAsync"/> でセマフォ保護される。
        /// バックグラウンドスレッドからの同期 DB アクセスには <see cref="LeaseConnection"/>（同期版）を使用すること。
        /// </para>
        /// <para>
        /// <b>重要（Issue #1452）— 並列起動禁止:</b>
        /// 本メソッドはセマフォを取得しないため、複数のリポジトリ呼び出しを <c>Task.WhenAll</c> 等で
        /// 並列起動すると、同一の <see cref="SQLiteConnection"/> 上で <see cref="SQLiteCommand"/> が
        /// 並列実行される。<c>System.Data.SQLite</c> の <see cref="SQLiteConnection"/> は同一接続上の
        /// 並列コマンド実行を保証していないため、<c>SQLITE_MISUSE</c> または不定動作の原因となる。
        /// Service / ViewModel 層は **必ず直列 await** で呼び出すこと。
        /// </para>
        /// <para>
        /// 並列化前提のレビュー観点（誤解されやすいので注記）:
        /// (1) Service 層は <c>ConfigureAwait(false)</c> 規約により継続が ThreadPool に逃げるため、
        ///     UI Dispatcher による暗黙の直列化は成立しない。
        /// (2) ViewModel 層は <c>ConfigureAwait(false)</c> を付けないが、<see cref="SQLiteCommand"/>
        ///     自体は内部で別スレッドにディスパッチされ得るため、UI スレッド単独で実行される保証はない。
        ///     よって ViewModel 経路でも <c>Task.WhenAll</c> での並列起動は同様にリスクがある。
        /// </para>
        /// </remarks>
        public virtual Task<ConnectionLease> LeaseConnectionAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var lease = new ConnectionLease(GetConnectionInternal(), () => { });
            return Task.FromResult(lease);
        }

        /// <summary>
        /// 接続リースを同期で取得する。using文で使用すること。
        /// </summary>
        /// <remarks>
        /// <para>同期メソッド（InitializeDatabase, CleanupOldData等）で使用する。
        /// 同一スレッド内ではリエントラント。</para>
        /// <para>
        /// <b>重要（Issue #1281）:</b> このメソッドは WPF の UI スレッドから呼び出してはならない。
        /// 内部で <c>_semaphore.Wait()</c> により UI スレッドがブロックされるため、
        /// 別スレッドで進行中の非同期トランザクション継続が Dispatcher 経由で UI スレッドに
        /// 戻ろうとする際にデッドロックが発生する。UI 層からは <see cref="LeaseConnectionAsync"/>
        /// を使うか、<c>Task.Run</c> でバックグラウンドスレッドにオフロードすること。
        /// 違反を検出した場合は <see cref="InvalidOperationException"/> をスローする。
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">WPF の UI スレッドから呼び出された場合</exception>
        public ConnectionLease LeaseConnection()
        {
            // Issue #1281: UI スレッドからの呼び出しを検出して拒否する
            ThrowIfOnUiThread();

            if (_reentrancyCount.Value > 0)
            {
                _reentrancyCount.Value++;
                return new ConnectionLease(GetConnectionInternal(), () => _reentrancyCount.Value--);
            }

            _semaphore.Wait();
            _reentrancyCount.Value = 1;
            return new ConnectionLease(GetConnectionInternal(), () =>
            {
                _reentrancyCount.Value--;
                if (_reentrancyCount.Value == 0)
                {
                    try { _semaphore.Release(); }
                    catch (ObjectDisposedException) { /* DbContext.Dispose()後のリース解放 */ }
                }
            });
        }

        /// <summary>
        /// Issue #1281: UI スレッド検出用のフック（AsyncLocal）。
        /// AsyncLocal のため並列に動くテスト間で状態が干渉しない。
        /// 値が null のとき既定検出（<see cref="DefaultIsOnUiThread"/>）を使用する。
        /// テストから差し替え可能（内部 API）。
        /// </summary>
        private static readonly AsyncLocal<Func<bool>?> _isOnUiThreadOverride = new();

        /// <summary>
        /// UI スレッド検出のオーバーライド用プロパティ（テスト専用）。
        /// null 代入で既定検出に戻る。<see cref="AsyncLocal{T}"/> ベースのため
        /// xUnit が並列実行するテスト間で状態が漏れない。
        /// </summary>
        internal static Func<bool> IsOnUiThread
        {
            get => _isOnUiThreadOverride.Value ?? DefaultIsOnUiThread;
            set => _isOnUiThreadOverride.Value = value;
        }

        /// <summary>
        /// 既定の UI スレッド検出: <see cref="SynchronizationContext.Current"/> の型名で判定する。
        /// System.Windows を直接参照せずに WPF Dispatcher スレッドを検出できる。
        /// </summary>
        private static bool DefaultIsOnUiThread()
        {
            var context = SynchronizationContext.Current;
            return context != null &&
                   context.GetType().FullName == "System.Windows.Threading.DispatcherSynchronizationContext";
        }

        /// <summary>
        /// UI スレッドから呼び出されていた場合に <see cref="InvalidOperationException"/> をスローする。
        /// </summary>
        private static void ThrowIfOnUiThread()
        {
            if (IsOnUiThread())
            {
                throw new InvalidOperationException(
                    "DbContext.LeaseConnection() は WPF UI スレッドから呼び出せません。" +
                    "内部の SemaphoreSlim.Wait() が UI スレッドをブロックし、" +
                    "バックグラウンドで進行中の非同期トランザクション継続が Dispatcher 経由で " +
                    "UI スレッドに戻ろうとした際にデッドロックを引き起こす危険があります。" +
                    "UI 層からは LeaseConnectionAsync() を使用するか、" +
                    "Task.Run でバックグラウンドスレッドにオフロードしてから呼び出してください。");
            }
        }

        /// <summary>
        /// リース付きトランザクションを非同期で開始する。using文で使用すること。
        /// </summary>
        /// <remarks>
        /// TransactionScope.Dispose時にトランザクション→リースの順で解放される。
        /// Commit/Rollbackを呼ばずにDisposeした場合、トランザクションは自動ロールバックされる。
        /// </remarks>
        public virtual async Task<TransactionScope> BeginTransactionAsync(CancellationToken ct = default)
        {
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var connection = GetConnectionInternal();
                var lease = new ConnectionLease(connection, () =>
                {
                    try { _semaphore.Release(); }
                    catch (ObjectDisposedException) { /* DbContext.Dispose()後のリース解放 */ }
                });
                var transaction = connection.BeginTransaction();
                return new TransactionScope(lease, transaction);
            }
            catch
            {
                _semaphore.Release();
                throw;
            }
        }

        /// <summary>
        /// データベース接続を取得（非推奨）
        /// </summary>
        [Obsolete("スレッドセーフな LeaseConnectionAsync() または LeaseConnection() を使用してください")]
        public virtual SQLiteConnection GetConnection()
        {
            return GetConnectionInternal();
        }

        /// <summary>
        /// 接続の初期化・再接続ロジック（内部用）
        /// </summary>
        private SQLiteConnection GetConnectionInternal()
        {
            // Issue #1166: 接続一時停止中は新規接続を拒否
            // リストア中にバックグラウンドタスクが接続を再オープンすることを防止
            if (_isSuspended)
            {
                throw new InvalidOperationException(
                    "データベース接続は一時停止中です（リストア処理中）。");
            }

            lock (_connectionLock)
            {
                // ロック取得後に再チェック（ロック待ち中にSuspendされた可能性）
                if (_isSuspended)
                {
                    throw new InvalidOperationException(
                        "データベース接続は一時停止中です（リストア処理中）。");
                }

                if (_connection != null && _connection.State != ConnectionState.Open)
                {
                    // 接続が切断された場合（ネットワーク共有時の瞬断等）は再接続
#if DEBUG
                    System.Diagnostics.Debug.WriteLine("[DbContext] 接続が切断されています。再接続します。");
#endif
                    CloseConnectionInternal();
                }

                if (_connection == null)
                {
                    _connection = new SQLiteConnection(_connectionString);
                    _connection.Open();
                    ConfigurePragmas(_connection);
                }

                return _connection;
            }
        }

        /// <summary>
        /// 接続に対してPRAGMA設定を適用
        /// </summary>
        private void ConfigurePragmas(SQLiteConnection connection)
        {
            // foreign_keysとbusy_timeoutは1コマンドにまとめてラウンドトリップを削減
            // ローカルモードでもbusy_timeoutを設定する（実害なし、コードパス統一）
            var timeout = BusyTimeoutMs;
            using var pragmaCommand = connection.CreateCommand();
            pragmaCommand.CommandText = $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {timeout};";
            pragmaCommand.ExecuteNonQuery();

            // journal_modeは優先順位順にフォールバック試行
            // DELETE: 共有モード推奨。TRUNCATE/PERSIST: DELETEが使えない場合の代替
            // OFF: データ保護なし（絶対に避けたい）
            ConfigureJournalMode(connection);
        }

        /// <summary>
        /// journal_modeを設定（フォールバック付き）
        /// </summary>
        /// <remarks>
        /// Issue #1107: SMB上でDELETEモードが設定できない場合がある（NASの設定等による）。
        /// DELETE → TRUNCATE → PERSIST の順にフォールバックし、
        /// いずれも失敗した場合は警告をログに記録する。
        /// OFFモードはクラッシュ時のデータ保護がないため使用しない。
        /// </remarks>
        internal string ConfigureJournalMode(SQLiteConnection connection)
        {
            // 優先順位: DELETE（推奨）→ TRUNCATE → PERSIST
            var preferredModes = new[] { "DELETE", "TRUNCATE", "PERSIST" };

            foreach (var mode in preferredModes)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"PRAGMA journal_mode = {mode};";
                var result = cmd.ExecuteScalar()?.ToString()?.ToLowerInvariant();

                if (result == mode.ToLowerInvariant())
                {
                    if (mode != "DELETE")
                    {
                        // DELETE以外にフォールバックした場合は警告
                        var message = $"journal_modeをDELETEに設定できなかったため、{mode}を使用します";
                        _logger?.LogWarning(message);
#if DEBUG
                        System.Diagnostics.Debug.WriteLine($"[DbContext] 警告: {message}");
#endif
                    }
                    // Issue #1172: 上位レイヤがdegraded状態をチェックできるようプロパティに保存
                    CurrentJournalMode = result;
                    return result;
                }
            }

            // すべて失敗 — 現在の設定を確認
            using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "PRAGMA journal_mode;";
            var currentMode = checkCmd.ExecuteScalar()?.ToString()?.ToLowerInvariant() ?? "unknown";

            var warningMessage = $"journal_modeの設定に失敗しました（現在: {currentMode}）。" +
                                 "データベースのクラッシュ耐性が低下している可能性があります。";
            _logger?.LogWarning(warningMessage);
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[DbContext] 警告: {warningMessage}");
#endif

            // Issue #1172: 失敗時もプロパティに保存（degraded状態として検出される）
            CurrentJournalMode = currentMode;
            return currentMode;
        }

        /// <summary>
        /// データベース接続を一時的に閉じる（Issue #508: リストア用）
        /// </summary>
        /// <remarks>
        /// リストア処理でDBファイルを置き換える前に呼び出す。
        /// 接続を閉じることでファイルロックを解放する。
        /// その後GetConnection()を呼ぶと自動的に再接続される。
        /// </remarks>
        public void CloseConnection()
        {
            lock (_connectionLock)
            {
                CloseConnectionInternal();
            }
        }

        /// <summary>
        /// Issue #1166: 接続を一時停止し、停止中は新規接続の取得を拒否する。
        /// リストア処理でDBファイルを安全に置き換えるために使用する。
        /// </summary>
        /// <remarks>
        /// 戻り値のConnectionSuspensionScopeをDisposeすると停止が解除される。
        /// using文で使用することで、例外発生時も確実に解除される。
        /// 停止中にGetConnection()を呼ぶとInvalidOperationExceptionがスローされる。
        /// </remarks>
        /// <returns>停止スコープ（Disposeで停止解除）</returns>
        public ConnectionSuspensionScope SuspendConnections()
        {
            lock (_connectionLock)
            {
                _isSuspended = true;
                CloseConnectionInternal();
            }
            _logger?.LogDebug("Issue #1166: DB接続を一時停止しました");
            return new ConnectionSuspensionScope(this);
        }

        /// <summary>
        /// Issue #1166: 接続の一時停止を解除する（ConnectionSuspensionScope.Disposeから呼び出される）
        /// </summary>
        internal void ResumeConnections()
        {
            _isSuspended = false;
            _logger?.LogDebug("Issue #1166: DB接続の一時停止を解除しました");
        }

        /// <summary>
        /// Issue #1166: 接続が一時停止中かどうか
        /// </summary>
        public bool IsConnectionSuspended => _isSuspended;

        /// <summary>
        /// 接続を閉じる内部実装（ロック取得済みの状態で呼び出すこと）
        /// </summary>
        private void CloseConnectionInternal()
        {
            if (_connection != null)
            {
                _connection.Close();
                _connection.Dispose();
                _connection = null;
#if DEBUG
                System.Diagnostics.Debug.WriteLine("[DbContext] 接続を閉じました");
#endif
            }
        }

        /// <summary>
        /// データベースを初期化（マイグレーションを実行）
        /// </summary>
        public void InitializeDatabase()
        {
            // 共有モード時: ネットワーク共有フォルダの存在確認
            if (IsSharedMode)
            {
                var directory = Path.GetDirectoryName(DatabasePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    throw new IOException(
                        $"データベース保存先のネットワーク共有フォルダにアクセスできません: {directory}\n" +
                        "ネットワーク接続を確認するか、設定画面でデータベース保存先を変更してください。");
                }
            }

            // 既存DBのアクセス権限を接続前に修正（旧バージョンで単一ユーザーに制限されている場合の対応）
            // 共有モード時はスキップ（共有フォルダの権限はファイルサーバー側で管理される）
            if (!IsSharedMode)
            {
                SetDatabaseFilePermissions(DatabasePath);
            }

            using var lease = LeaseConnection();
            var connection = lease.Connection;

            // 新規作成されたDBファイルにもアクセス権限を設定
            if (!IsSharedMode)
            {
                SetDatabaseFilePermissions(DatabasePath);
            }

            // HandleLegacyDatabaseはトランザクションを持たないため、
            // BEGIN IMMEDIATEで排他ロックを取得し、複数PCの同時初期化を直列化する。
            // MigrateToLatestは各マイグレーションが独自にトランザクションを持つため外に出す。
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                try
                {
                    HandleLegacyDatabase(connection);
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }

            // マイグレーションを実行（各マイグレーションが独自にトランザクションを管理）
            // 複数PCが同時にマイグレーションを実行しても、schema_migrationsの
            // PRIMARY KEY制約により重複適用が防止される。
            var runner = new MigrationRunner(connection);
            var appliedCount = runner.MigrateToLatest();

            if (appliedCount > 0)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[DbContext] {appliedCount}件のマイグレーションを適用しました");
#endif
            }
        }

        /// <summary>
        /// データベースを初期化（非同期版）。UI スレッドから呼び出しても Issue #1281 の
        /// <see cref="LeaseConnection"/> ガードで例外にならないよう、<see cref="Task.Run(Action)"/>
        /// でバックグラウンドスレッドに確実にオフロードする。
        /// </summary>
        public Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
            => Task.Run((Action)InitializeDatabase, cancellationToken);

        /// <summary>
        /// マイグレーション導入前の既存DBを処理
        /// </summary>
        /// <remarks>
        /// staffテーブルが存在するがschema_migrationsテーブルが存在しない場合、
        /// 既存DBとみなしてバージョン1として記録する
        /// </remarks>
        private void HandleLegacyDatabase(SQLiteConnection connection)
        {
            // staffテーブルの存在確認（既存DBの判定）
            using var checkStaffCmd = connection.CreateCommand();
            checkStaffCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='staff'";
            var hasStaffTable = checkStaffCmd.ExecuteScalar() != null;

            if (!hasStaffTable)
            {
                // 新規DBの場合は何もしない（マイグレーションが実行される）
                return;
            }

            // schema_migrationsテーブルの存在確認
            using var checkMigrationCmd = connection.CreateCommand();
            checkMigrationCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_migrations'";
            var hasMigrationTable = checkMigrationCmd.ExecuteScalar() != null;

            if (hasMigrationTable)
            {
                // 既にマイグレーション管理されている
                return;
            }

            // 既存DBをバージョン1として記録
#if DEBUG
            System.Diagnostics.Debug.WriteLine("[DbContext] 既存DBを検出しました。バージョン1として記録します。");
#endif

            using var createTableCmd = connection.CreateCommand();
            createTableCmd.CommandText = @"CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY,
    description TEXT NOT NULL,
    applied_at TEXT DEFAULT (datetime('now', 'localtime'))
)";
            createTableCmd.ExecuteNonQuery();

            using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = "INSERT INTO schema_migrations (version, description) VALUES (1, '初期スキーマ（既存DB）')";
            insertCmd.ExecuteNonQuery();
        }

        /// <summary>
        /// データベースファイルのアクセス権限を設定（親ディレクトリからの継承を有効化）
        /// </summary>
        /// <remarks>
        /// 旧バージョンでは継承を無効化し現在のユーザーのみにACLを設定していたため、
        /// 他のWindowsユーザーがDBにアクセスできなかった。
        /// 本メソッドは継承を再有効化し、明示的ACLを削除することで、
        /// 親ディレクトリ（インストーラー <c>ICCardManager.iss</c> が
        /// <c>{commonappdata}\ICCardManager</c> に <c>users-full</c> を設定済み）
        /// からの権限継承によりアクセス制御を行う。
        ///
        /// SQLiteの関連ファイル（-wal, -shm, -journal）も同様に処理する。
        /// </remarks>
        /// <param name="dbPath">データベースファイルのパス</param>
        internal static void SetDatabaseFilePermissions(string dbPath)
        {
            // メインDBファイルとSQLite関連ファイルの権限を修正
            EnableInheritance(dbPath);
            EnableInheritance(dbPath + "-wal");
            EnableInheritance(dbPath + "-shm");
            EnableInheritance(dbPath + "-journal");
        }

        /// <summary>
        /// 指定ファイルの継承を有効化し、明示的ACLを削除する
        /// </summary>
        /// <param name="filePath">ファイルパス</param>
        private static void EnableInheritance(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return;
                }

                var fileInfo = new FileInfo(filePath);
                var fileSecurity = fileInfo.GetAccessControl();

                // 既に継承が有効なら何もしない
                if (!fileSecurity.AreAccessRulesProtected)
                {
                    return;
                }

                // 継承を有効化（親ディレクトリからの権限を継承する）
                fileSecurity.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);

                // 明示的ACLを削除（継承ルールに任せる）
                var rules = fileSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier));
                foreach (FileSystemAccessRule rule in rules)
                {
                    fileSecurity.PurgeAccessRules(rule.IdentityReference);
                }

                fileInfo.SetAccessControl(fileSecurity);
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[DbContext] ファイルの継承を有効化: {filePath}");
#endif
            }
            catch (Exception ex)
            {
                _ = ex; // 警告抑制（DEBUGビルドでのみ使用）
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[DbContext] ファイル権限設定に失敗: {filePath} - {ex.Message}");
#endif
            }
        }

        /// <summary>
        /// 現在のデータベースバージョンを取得
        /// </summary>
        public int GetDatabaseVersion()
        {
            using var lease = LeaseConnection();
            var runner = new MigrationRunner(lease.Connection);
            return runner.GetCurrentVersion();
        }

        /// <summary>
        /// 未適用のマイグレーションがあるか確認
        /// </summary>
        public bool HasPendingMigrations()
        {
            using var lease = LeaseConnection();
            var runner = new MigrationRunner(lease.Connection);
            return runner.HasPendingMigrations();
        }

        /// <summary>
        /// 6年経過したデータを削除（ledger + operation_log）
        /// </summary>
        /// <remarks>
        /// 各カラムは 'YYYY-MM-DD HH:MM:SS' 形式で保存されているため、
        /// date()関数で日付部分のみを抽出して比較する必要があります。
        /// また、'localtime'を指定することでローカルタイムゾーンで比較します。
        ///
        /// Issue #1170: 両テーブルの削除を単一トランザクションで実行し、
        /// 片方の削除が成功した直後に SQLITE_BUSY 等が発生してもテーブル間の
        /// 不整合（保存期間の不一致）が発生しないようにする。
        /// SQLITE_BUSY/SQLITE_LOCKED時はリトライする。
        /// </remarks>
        /// <returns>削除件数（ledger件数, operation_log件数）</returns>
        public (int LedgerCount, int OperationLogCount) CleanupOldData()
        {
            // Issue #1170: SQLITE_BUSY/SQLITE_LOCKED時の同期リトライ
            var delays = IsSharedMode ? SharedRetryDelays : LocalRetryDelays;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return CleanupOldDataInternal();
                }
                catch (SQLiteException ex) when (
                    attempt < delays.Length &&
                    (ex.ResultCode == SQLiteErrorCode.Busy || ex.ResultCode == SQLiteErrorCode.Locked))
                {
                    var baseDelay = delays[attempt];
                    var jitter = IsSharedMode ? _jitterRandom.Next(0, baseDelay / 2) : 0;
                    var totalDelay = baseDelay + jitter;

                    _logger?.LogWarning(
                        "CleanupOldDataリトライ（{Attempt}/{MaxRetries}回目、{Delay}ms待機）: {ResultCode}",
                        attempt + 1, delays.Length, totalDelay, ex.ResultCode);
#if DEBUG
                    System.Diagnostics.Debug.WriteLine(
                        $"[DbContext] CleanupOldDataリトライ（{attempt + 1}/{delays.Length}回目、{totalDelay}ms待機）: {ex.ResultCode}");
#endif
                    Thread.Sleep(totalDelay);
                }
            }
        }

        /// <summary>
        /// 6年経過データ削除の非同期版。UI スレッドから呼び出しても Issue #1281 の
        /// <see cref="LeaseConnection"/> ガードで例外にならないよう、<see cref="Task.Run{TResult}(Func{TResult})"/>
        /// でバックグラウンドスレッドに確実にオフロードする。
        /// </summary>
        public Task<(int LedgerCount, int OperationLogCount)> CleanupOldDataAsync(CancellationToken cancellationToken = default)
            => Task.Run(CleanupOldData, cancellationToken);

        /// <summary>
        /// Issue #1170: CleanupOldDataの実体。両テーブルの削除を単一トランザクションで実行する。
        /// </summary>
        private (int LedgerCount, int OperationLogCount) CleanupOldDataInternal()
        {
            using var lease = LeaseConnection();
            var connection = lease.Connection;

            using var transaction = connection.BeginTransaction();
            try
            {
                using var ledgerCommand = connection.CreateCommand();
                ledgerCommand.Transaction = transaction;
                ledgerCommand.CommandText = "DELETE FROM ledger WHERE date(date) < date('now', '-6 years', 'localtime')";
                var ledgerCount = ledgerCommand.ExecuteNonQuery();

                using var logCommand = connection.CreateCommand();
                logCommand.Transaction = transaction;
                logCommand.CommandText = "DELETE FROM operation_log WHERE date(timestamp) < date('now', '-6 years', 'localtime')";
                var logCount = logCommand.ExecuteNonQuery();

                transaction.Commit();
                return (ledgerCount, logCount);
            }
            catch
            {
                // Issue #1170: 片方が失敗したら両方ロールバックして整合性を維持
                try { transaction.Rollback(); } catch { /* ロールバック失敗は無視 */ }
                throw;
            }
        }

        /// <summary>
        /// VACUUMを実行してデータベースを最適化
        /// </summary>
        /// <remarks>
        /// 共有モードでは他PCが接続中の場合、排他ロックを取得できず失敗する可能性がある。
        /// その場合はfalseを返し、次回起動時にリトライする。
        /// </remarks>
        /// <returns>VACUUMが成功した場合true</returns>
        public bool Vacuum()
        {
            try
            {
                using var lease = LeaseConnection();
                using var command = lease.Connection.CreateCommand();
                command.CommandText = "VACUUM";
                command.ExecuteNonQuery();
                return true;
            }
            catch (SQLiteException ex) when (ex.ResultCode == SQLiteErrorCode.Busy || ex.ResultCode == SQLiteErrorCode.Locked)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[DbContext] VACUUM失敗（他の接続がアクティブ）: {ex.Message}");
#endif
                return false;
            }
        }

        /// <summary>
        /// VACUUM 実行の非同期版。UI スレッドから呼び出しても Issue #1281 の
        /// <see cref="LeaseConnection"/> ガードで例外にならないよう、<see cref="Task.Run{TResult}(Func{TResult})"/>
        /// でバックグラウンドスレッドに確実にオフロードする。
        /// </summary>
        public Task<bool> VacuumAsync(CancellationToken cancellationToken = default)
            => Task.Run(Vacuum, cancellationToken);

        /// <summary>
        /// ローカルモードのリトライ待機時間（ミリ秒）
        /// </summary>
        internal static readonly int[] LocalRetryDelays = { 100, 500, 2000 };

        /// <summary>
        /// 共有モードのリトライ待機時間（ミリ秒）。
        /// SMBのネットワーク遅延と最大約20台の同時アクセスを考慮し、回数と待機時間を増加
        /// </summary>
        internal static readonly int[] SharedRetryDelays = { 200, 500, 1000, 2000, 5000 };

        /// <summary>
        /// SQLITE_BUSY/SQLITE_LOCKED時にリトライ付きで非同期操作を実行
        /// </summary>
        /// <remarks>
        /// busy_timeout PRAGMAでカバーできないケース（接続レベルのロック等）に対するセーフティネット。
        /// リトライ回数と待機時間はモードに応じて異なる:
        /// - ローカルモード: 最大3回（100ms, 500ms, 2000ms）
        /// - 共有モード: 最大5回（200ms, 500ms, 1000ms, 2000ms, 5000ms）+ ジッター
        /// ジッターにより、複数PCが同時にリトライするthundering herd問題を緩和する。
        /// </remarks>
        public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
        {
            var delays = IsSharedMode ? SharedRetryDelays : LocalRetryDelays;

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (SQLiteException ex) when (
                    attempt < delays.Length &&
                    (ex.ResultCode == SQLiteErrorCode.Busy || ex.ResultCode == SQLiteErrorCode.Locked))
                {
                    // 基本待機時間 + ジッター（0〜50%の追加遅延）で thundering herd を緩和
                    var baseDelay = delays[attempt];
                    var jitter = IsSharedMode ? _jitterRandom.Next(0, baseDelay / 2) : 0;
                    var totalDelay = baseDelay + jitter;

                    _logger?.LogWarning(
                        "DB操作リトライ（{Attempt}/{MaxRetries}回目、{Delay}ms待機）: {ResultCode}",
                        attempt + 1, delays.Length, totalDelay, ex.ResultCode);
#if DEBUG
                    System.Diagnostics.Debug.WriteLine(
                        $"[DbContext] DB操作リトライ（{attempt + 1}/{delays.Length}回目、{totalDelay}ms待機）: {ex.ResultCode}");
#endif
                    await Task.Delay(totalDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// SQLITE_BUSY/SQLITE_LOCKED時にリトライ付きで非同期操作を実行（戻り値なし）
        /// </summary>
        public async Task ExecuteWithRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
        {
            await ExecuteWithRetryAsync(async () =>
            {
                await operation().ConfigureAwait(false);
                return 0;
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// トランザクションを開始（非推奨）
        /// </summary>
        [Obsolete("スレッドセーフな BeginTransactionAsync() を使用してください")]
        public virtual SQLiteTransaction BeginTransaction()
        {
#pragma warning disable CS0618 // Obsolete
            return GetConnection().BeginTransaction();
#pragma warning restore CS0618
        }

        /// <summary>
        /// DB接続の疎通確認（IDatabaseInfo実装）
        /// </summary>
        /// <returns>接続可能な場合true</returns>
        public bool CheckConnection()
        {
            if (IsConnectionSuspended)
                return true;

            try
            {
                using var lease = LeaseConnection();
                using var command = lease.Connection.CreateCommand();
                // Issue #1110: sqlite_masterからの読み取りで実際のファイルアクセスを強制
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master";
                command.ExecuteScalar();
                return true;
            }
            catch (InvalidOperationException)
            {
                // 接続一時停止中 — ネットワーク切断ではない
                return true;
            }
            catch (Exception ex)
            {
                // Issue #1282: 疎通確認なので「失敗=未到達」を戻り値で通知するのが仕様。
                // ただしサイレント握りつぶしはトラブル時のデバッグを困難にするため、
                // LogDebug で失敗理由を残す。接続断は運用上頻繁に起きる想定のため
                // LogWarning ではなく LogDebug を選択（ログファイルの肥大化を避ける）。
                _logger?.LogDebug(ex,
                    "DB接続疎通確認に失敗。呼び出し元には false を返す（ネットワーク断または読み取りエラー）");
                return false;
            }
        }

        /// <summary>
        /// リソースを解放
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// リソースを解放（内部実装）
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _connection?.Dispose();
                    _semaphore?.Dispose();
                }
                _disposed = true;
            }
        }
    }
}
