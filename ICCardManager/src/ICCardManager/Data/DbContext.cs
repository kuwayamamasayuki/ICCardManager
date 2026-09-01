using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
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
    /// <remarks>
    /// Issue #1809: 一時停止中は <see cref="DbContext"/> の接続セマフォを保持し続ける
    /// （進行中のリースを待ってから閉じ、停止中に到着した書き込みは解除まで待たせる）。
    /// Dispose で停止解除と同時にセマフォも返す。
    /// </remarks>
    public sealed class ConnectionSuspensionScope : IDisposable
    {
        private readonly DbContext _dbContext;
        private readonly bool _releaseSemaphore;
        private bool _disposed;

        internal ConnectionSuspensionScope(DbContext dbContext, bool releaseSemaphore)
        {
            _dbContext = dbContext;
            _releaseSemaphore = releaseSemaphore;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _dbContext.ResumeConnections(_releaseSemaphore);
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
        /// <remarks>
        /// 取得するのは <see cref="LeaseConnection"/>（同期）・<see cref="BeginTransactionAsync"/>・
        /// <see cref="SuspendConnections"/>（Issue #1809）。<see cref="LeaseConnectionAsync"/> は取得しない
        /// （代わりに <see cref="_activeAsyncLeaseCount"/> で進行中件数だけを数える）。
        /// </remarks>
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Issue #1809: 進行中の <see cref="LeaseConnectionAsync"/> リース数。
        /// セマフォを取らない読み取り経路の接続を、<see cref="SuspendConnections"/> が
        /// 他スレッドから閉じてしまわないよう、解放を待つための計数。<see cref="Interlocked"/> で更新する。
        /// </summary>
        private int _activeAsyncLeaseCount;

        /// <summary>
        /// Issue #1809: <see cref="SuspendConnections"/> が進行中の非同期リースの解放を待つ上限の既定値。
        /// </summary>
        /// <remarks>
        /// 上限を設けるのは、リースの解放漏れが 1 か所でもあるとリストアが永久に止まるのを避けるため。
        /// 上限に達した場合は警告ログを残して従来どおり接続を閉じる（進行中の読み取りが失敗し得る
        /// 従来の挙動へ退化するだけで、データは壊れない）。
        /// </remarks>
        internal static readonly TimeSpan DefaultAsyncLeaseDrainTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Issue #1809: <see cref="SuspendConnections"/> が進行中の非同期リースの解放を待つ上限。
        /// 既定は <see cref="DefaultAsyncLeaseDrainTimeout"/>。テストから短縮するために設定可能。
        /// </summary>
        internal TimeSpan AsyncLeaseDrainTimeout { get; set; } = DefaultAsyncLeaseDrainTimeout;

        /// <summary>
        /// リエントラントカウント。同一非同期フロー内のネスト呼び出しでセマフォ再取得をスキップする。
        /// </summary>
        private readonly AsyncLocal<int> _reentrancyCount = new AsyncLocal<int>();

        /// <summary>
        /// Issue #1575: 現在 <see cref="BeginTransactionAsync"/> のスコープが開かれているか（0 or 1）。
        /// <see cref="_semaphore"/>（容量 1）で取得が直列化されるため、値は 0 か 1 のみ。
        /// </summary>
        private int _activeTransactionCount;

        /// <summary>
        /// Issue #1575: 現在 <see cref="BeginTransactionAsync"/> のスコープ内にいるかを示す。
        /// Repository が自前で BeginTransactionAsync を開く前にこれが true なら、
        /// 外側 tx に暗黙参加する経路（<see cref="LeaseConnectionAsync"/>）を取り、
        /// セマフォ再取得によるデッドロックを防ぐ。
        /// </summary>
        /// <remarks>
        /// 読み取り側にロックはないが、トランザクション取得が常に 1 並列であるため
        /// false→true は単純に「外側スコープが直近に開いた」を意味する。
        /// 一瞬の race（読み取った瞬間に値が変わる）が起きても、誤って <see cref="LeaseConnectionAsync"/> 経路を選ぶと
        /// autocommit で動作する／誤って <see cref="BeginTransactionAsync"/> 経路を選ぶとセマフォで待たされるだけで、
        /// いずれも安全側に倒れる。
        /// </remarks>
        public bool HasActiveTransactionScope => _activeTransactionCount > 0;

        /// <summary>
        /// Issue #1984: 保守処理（<see cref="CleanupOldData"/>）が共有接続上でトランザクションを
        /// 開いている間、セマフォを取らない <see cref="LeaseConnectionAsync"/> 経由の書き込みが
        /// そのトランザクションへ暗黙参加しないよう新規リースを止めるゲート。
        /// </summary>
        /// <remarks>
        /// <para>
        /// SQLite のトランザクションは接続単位で、<see cref="DbContext"/> はプロセス全体で
        /// <see cref="SQLiteConnection"/> を 1 本しか持たない。したがって保守トランザクションが
        /// 開いている間に <see cref="LeaseConnectionAsync"/> 経由の単文書き込み（
        /// <c>LedgerRepository.InsertAsync</c> / <c>OperationLogRepository.InsertAsync</c> 等）が
        /// 実行されると、その書き込みは保守トランザクションの内側に潜り込み、保守側が
        /// SQLITE_BUSY 等でロールバックすると一緒に消える（Issue #1737 と同型）。
        /// </para>
        /// <para>
        /// <b><see cref="_activeTransactionCount"/> へ載せない理由:</b> Issue #1984 の受け入れ条件は
        /// 「保守トランザクションを <see cref="HasActiveTransactionScope"/> に反映すること」を挙げるが、
        /// これは<b>害になる</b>。Repository の 3 分岐（Issue #1724）は
        /// <see cref="HasActiveTransactionScope"/> が true のとき分岐②（<c>transaction: null</c> で
        /// 接続だけ借りて外側スコープに commit/rollback を委ねる）を選ぶ。保守トランザクションは
        /// その「外側スコープ」ではないため、②を選んだ複数文の書き込み（<c>ReplaceDetailsAsync</c> の
        /// DELETE + INSERT 等）は commit の主体を失って autocommit で 1 文ずつ確定し、
        /// 原子性が失われる。現在③（自前の <see cref="BeginTransactionAsync"/>）を通っている経路は
        /// セマフォ待ちで既に安全なので、②へ倒す変更は保護を外す方向にしかならない。
        /// よって計数は分けたうえで、危険な経路（セマフォを取らない <see cref="LeaseConnectionAsync"/>）
        /// そのものを止める。
        /// </para>
        /// </remarks>
        private readonly SemaphoreSlim _maintenanceTransactionGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Issue #1984: 保守トランザクション（<see cref="CleanupOldData"/>）が開かれている間 true。
        /// <see cref="HasActiveTransactionScope"/> とは別の計数であることが重要（理由は
        /// <see cref="_maintenanceTransactionGate"/> の remarks）。
        /// </summary>
        /// <remarks>
        /// <b>本番の消費者は無く、<see cref="OnMaintenanceTransactionOpened"/> と対になるテスト用の継ぎ目である。</b>
        /// 「保守トランザクション中も <see cref="HasActiveTransactionScope"/> は false のまま」という設計判断は、
        /// その状態が観測できて初めてテストで表明できる（false だけを見ると、保守トランザクションが
        /// そもそも開いていない実装でも緑になる）。診断・ログへ載せる要件が出たらそのとき公開面を見直すこと
        /// （使われないメソッドを先に増やさない ＝ #1726）。
        /// </remarks>
        internal bool HasActiveMaintenanceTransaction => Volatile.Read(ref _activeMaintenanceTransactionCount) > 0;

        private int _activeMaintenanceTransactionCount;

        /// <summary>
        /// Issue #1166: 接続一時停止フラグ。
        /// リストア中にバックグラウンドタスクが接続を再オープンすることを防止する。
        /// </summary>
        private volatile bool _isSuspended;

        /// <summary>
        /// Issue #1716: 進行中の疎通確認。上限時間で打ち切っても下位の呼び出しは中断できないため、
        /// 同時に走る確認を 1 本に限定してブロック済みスレッドの累積を防ぐ。
        /// </summary>
        private Task<bool> _pendingConnectionCheck;

        /// <summary>
        /// <see cref="_pendingConnectionCheck"/> の生成・差し替えを直列化するロック
        /// </summary>
        private readonly object _connectionCheckLock = new object();

        /// <summary>
        /// 共有モード（UNCパス または マップドネットワークドライブ指定時）かどうか
        /// </summary>
        /// <remarks>
        /// UNCパス（\\server\share）と、ドライブレター形式（Z:\share）でも
        /// DriveInfo.DriveType == DriveType.Network のマップドネットワークドライブを
        /// 共有モード扱いとする。ローカルフルパス指定は共有モードにしない（Issue #1559）。
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
            : this(databasePath, logger, forceSharedMode: null)
        {
        }

        /// <summary>
        /// テスト用 internal コンストラクタ（Issue #1559）。
        /// ローカル一時ファイルを使いながら共有モード挙動（busy_timeout / リトライ回数 等）を検証するために
        /// IsSharedMode を強制的に指定する用途。production コードからは使用しない。
        /// </summary>
        /// <param name="databasePath">データベースファイルのパス</param>
        /// <param name="logger">ロガー</param>
        /// <param name="forceSharedMode">true/falseで IsSharedMode を強制指定。null の場合は通常の判定ロジック（UNC/マップドドライブ）を使用</param>
        internal DbContext(string databasePath, ILogger<DbContext> logger, bool? forceSharedMode)
        {
            _logger = logger;
            DatabasePath = databasePath ?? GetDefaultDatabasePath();

            _connectionString = BuildConnectionString(DatabasePath);

            // Issue #1559: UNCパス または マップドネットワークドライブ指定時のみ共有モード
            // （ローカルフルパス指定では共有モードにしない）。テスト時は forceSharedMode で上書き可能
            IsSharedMode = forceSharedMode ?? IsSharedModePath(databasePath);
        }

        /// <summary>
        /// SQLite の接続文字列を組み立てる（Issue #1924 で本メソッドへ集約）
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>SQLite はバックスラッシュの UNC パス（<c>\\server\share\x.db</c>）を開けない</b>。
        /// <c>Data Source</c> にそのまま渡すと <c>SQLiteException: unable to open database file</c>
        /// （SQLITE_CANTOPEN）になる。フォワードスラッシュ（<c>//server/share/x.db</c>）へ変換すると
        /// 開くことができ、<c>SQLiteConnection.FileName</c> は元の UNC 形式に解決される。
        /// この差は <c>SQLiteConnectionStringBuilder</c> を経由しても変わらない
        /// （実機 <c>\\DESKTOP-4HFLR8J\tmp</c> で確認）。
        /// </para>
        /// <para>
        /// Issue #1924: この変換は本クラスのコンストラクタにだけ存在し、
        /// <c>BackupService.CopyDatabaseTo</c> がコピー先の接続を
        /// <c>$"Data Source={destinationPath}"</c> で自前に組み立てていたため、
        /// <b>共有フォルダーを指定するとバックアップだけが必ず失敗していた</b>
        /// （DB 本体は本メソッド経由なので開ける）。接続文字列の組み立て手段を 2 つ持つと、
        /// 片方だけが知識を持つ状態が生まれる
        /// （<c>.claude/rules/development-conventions.md</c>「同じ論理的な処理に手段が 2 通りあるか」）。
        /// 新たに SQLite 接続を開く箇所は必ず本メソッドを通すこと
        /// （回帰は <c>SqliteConnectionStringConventionTests</c> が静的検査で固定する）。
        /// </para>
        /// <para>
        /// <c>SQLiteConnectionStringBuilder</c> を通すのは接続文字列インジェクションの防止のため。
        /// </para>
        /// </remarks>
        /// <param name="databasePath">データベースファイルのパス（UNC 可）</param>
        /// <returns>SQLite の接続文字列</returns>
        internal static string BuildConnectionString(string databasePath)
        {
            var effectivePath = IsUncPath(databasePath)
                ? databasePath.Replace('\\', '/')
                : databasePath;

            var builder = new SQLiteConnectionStringBuilder { DataSource = effectivePath };
            return builder.ToString();
        }

        /// <summary>
        /// 指定パスが共有モード（複数PC共有）扱いになるかを判定する（Issue #1559 / #1597）。
        /// </summary>
        /// <remarks>
        /// 共有モードの単一判定ロジック。DbContext のコンストラクタ（journal_mode / busy_timeout 等の切替）と
        /// App.xaml.cs のキャッシュTTL短縮設定の両方がこのメソッドを参照することで、判定基準の二重管理を防ぐ。
        /// UNCパス（\\server\share）またはマップドネットワークドライブ（DriveType.Network）のみ true。
        /// null・空・ローカルフルパスは false（Issue #1597: パス指定の有無だけで共有モード扱いにしない）。
        /// </remarks>
        internal static bool IsSharedModePath(string path)
        {
            return IsSharedModePath(path, DefaultDriveTypeResolver);
        }

        /// <summary>
        /// 共有モード判定の本体。マップドネットワークドライブ判定の DriveType 解決を注入可能にした
        /// オーバーロード（Issue #1605）。
        /// </summary>
        /// <param name="path">判定対象パス</param>
        /// <param name="driveTypeResolver">ドライブルート（例: <c>Z:\</c>）から <see cref="DriveType"/> を
        /// 解決する関数。テストではモックを注入して「ネットワークドライブ → 共有モード有効」の正方向を検証する。</param>
        internal static bool IsSharedModePath(string path, Func<string, DriveType> driveTypeResolver)
        {
            return path != null && (IsUncPath(path) || IsNetworkDrive(path, driveTypeResolver));
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
        /// マップドネットワークドライブ（例: Z:\share）かどうかを判定（Issue #1559）
        /// </summary>
        /// <remarks>
        /// Path.GetPathRoot でルートを取得し、DriveInfo.DriveType が Network かを判定する。
        /// パスが不正・null・未マウントドライブ等で例外発生時は false（ローカル扱い）にフォールバック。
        /// UNCパスは IsUncPath 側で判定するため、ここでは早期 false を返す。
        /// </remarks>
        internal static bool IsNetworkDrive(string path)
        {
            return IsNetworkDrive(path, DefaultDriveTypeResolver);
        }

        /// <summary>
        /// マップドネットワークドライブ判定の本体。DriveType の解決を注入可能にしたオーバーロード（Issue #1605）。
        /// </summary>
        /// <param name="path">判定対象パス</param>
        /// <param name="driveTypeResolver">ドライブルート（例: <c>Z:\</c>）から <see cref="DriveType"/> を
        /// 解決する関数。既定では実 <see cref="DriveInfo"/> を用いる（<see cref="DefaultDriveTypeResolver"/>）が、
        /// テストではモックを注入して正方向（<see cref="DriveType.Network"/> → 共有モード有効）を検証できる。</param>
        /// <remarks>
        /// 実ネットワークドライブなしには正方向（true を返すケース）をテストできなかった問題（Issue #1605）に
        /// 対応するための internal seam。本番経路は <see cref="IsNetworkDrive(string)"/> 経由で
        /// <see cref="DefaultDriveTypeResolver"/> を使用するため挙動は不変。
        /// resolver が例外を投げた場合（不正ルート等）は catch でローカル扱い（false）にフォールバックする。
        /// </remarks>
        internal static bool IsNetworkDrive(string path, Func<string, DriveType> driveTypeResolver)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root))
                    return false;

                // UNCはここで除外（IsUncPathで判定する責務分離）
                if (root.StartsWith(@"\\", StringComparison.Ordinal))
                    return false;

                return driveTypeResolver(root) == DriveType.Network;
            }
            catch
            {
                // 不正パス・未マウントドライブ等はローカル扱い
                return false;
            }
        }

        /// <summary>
        /// 既定の DriveType 解決関数。実 <see cref="DriveInfo"/> を用いてドライブ種別を取得する（Issue #1605）。
        /// </summary>
        private static DriveType DefaultDriveTypeResolver(string root)
        {
            return new DriveInfo(root).DriveType;
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

            // ディレクトリを作成（権限はインストーラーが設定済み、Issue #1455 / #1499）
            EnsureDirectoryExists(appDataPath);

            return Path.Combine(appDataPath, DatabaseFileName);
        }

        /// <summary>
        /// ディレクトリが存在することを保証する（なければ作成する）。
        /// </summary>
        /// <remarks>
        /// Issue #1455 / #1499:
        /// 旧名は <c>EnsureDirectoryWithPermissions</c> で、当時はランタイムで
        /// <c>BUILTIN\Users : FullControl</c> を <c>AddAccessRule</c> で付与していたが、以下の理由で撤廃した:
        /// (1) <c>FullControl</c> は削除権限まで含むため、一般ユーザーが他ユーザーのファイルを
        ///     削除・差替え可能な過剰権限となっていた（PII 置換攻撃の足掛かり）。
        /// (2) <c>AddAccessRule</c> は冪等ではなく、起動の度に新規 ACE が追加され ACL が累積する。
        /// (3) インストーラー (<c>installer/ICCardManager.iss</c>) が
        ///     <c>{commonappdata}\ICCardManager</c> 配下に <c>Permissions: users-full</c> を
        ///     設定済みのため、ランタイムでの再付与は機能的に冗長。
        /// 共有モード（UNC パス）等インストーラーの管理外パスを使う場合の権限は管理者責任とする。
        /// 実体が <c>Directory.CreateDirectory</c> の薄いラッパーとなったため、Issue #1499 で
        /// メソッド名を <c>EnsureDirectoryExists</c> に変更し、命名と挙動の乖離を解消した。
        /// </remarks>
        /// <param name="directoryPath">ディレクトリパス</param>
        internal static void EnsureDirectoryExists(string directoryPath)
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
        /// <b>Issue #1984:</b> セマフォを取らないため、保守処理（<see cref="CleanupOldData"/>）が
        /// 同一接続上で開いたトランザクションへ暗黙参加し得る。これを防ぐため、本メソッドは
        /// <see cref="_maintenanceTransactionGate"/> を通過してからリースを払い出す
        /// （保守トランザクションが開いている間は待機する）。
        /// </para>
        /// <para>
        /// <b>この待機には上限が無く、読み取りのリースも同じく待たされる。</b> リースが書き込みに使われるか
        /// 読み取りに使われるかを本メソッドは知り得ないため、区別せずに止めている。
        /// 現在この代償が問題にならないのは、<see cref="CleanupOldData"/> の呼び出し元が
        /// <c>StartupTaskRunner</c> だけで、メイン画面の表示前（＝カードリーダー稼働前）に完了するためである。
        /// <b>保守トランザクションを開く経路を増やすときは、この前提が成り立つかを確かめること</b>
        /// （カードタッチと並走する位置に置くと、職員証・カードの照合がこの待機の後ろに並ぶ）。
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
        public virtual async Task<ConnectionLease> LeaseConnectionAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Issue #1984: 保守トランザクション（CleanupOldData）が開いている間は新規リースを通さない。
            // ゲートを保持したまま計数を増やすことで、保守側が「ゲートを閉じてから進行中リースの
            // ドレインを待つ」だけで「もう誰も同一接続へ文を発行しない」ことを保証できる
            // （ゲート通過後に計数するとその隙間が残る）。
            await _maintenanceTransactionGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Issue #1809: 接続を取る前に計数する。取った後に増やすと、その隙間に SuspendConnections が
                // 「進行中リース 0 件」と判定して接続を閉じ得る（_isSuspended の設定順と合わせて閉塞のない順序）。
                Interlocked.Increment(ref _activeAsyncLeaseCount);
            }
            finally
            {
                _maintenanceTransactionGate.Release();
            }

            SQLiteConnection connection;
            try
            {
                connection = GetConnectionInternal();
            }
            catch
            {
                Interlocked.Decrement(ref _activeAsyncLeaseCount);
                throw;
            }

            return new ConnectionLease(connection, () => Interlocked.Decrement(ref _activeAsyncLeaseCount));
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
            ThrowIfOnUiThread(
                nameof(LeaseConnection),
                "UI 層からは LeaseConnectionAsync() を使用するか、" +
                "Task.Run でバックグラウンドスレッドにオフロードしてから呼び出してください。");

            if (_reentrancyCount.Value > 0)
            {
                // Issue #1809: 接続の確立に失敗したときカウントを増やしたままにしない（先に取得してから増やす）
                var nestedConnection = GetConnectionInternal();
                _reentrancyCount.Value++;
                return new ConnectionLease(nestedConnection, () => _reentrancyCount.Value--);
            }

            _semaphore.Wait();
            try
            {
                // Issue #1809: 接続の確立（Open / PRAGMA）に失敗したら取得済みのセマフォを必ず返す。
                // 返さないと以後の BeginTransactionAsync（全書き込み）が永久に待機する。
                // BeginTransactionAsync には同じ catch が元からあり、こちらだけ欠けていた。
                var connection = GetConnectionInternal();
                _reentrancyCount.Value = 1;
                return new ConnectionLease(connection, () =>
                {
                    _reentrancyCount.Value--;
                    if (_reentrancyCount.Value == 0)
                    {
                        try { _semaphore.Release(); }
                        catch (ObjectDisposedException) { /* DbContext.Dispose()後のリース解放 */ }
                    }
                });
            }
            catch
            {
                _semaphore.Release();
                throw;
            }
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
        /// <param name="methodName">セマフォを同期取得するメソッド名（文言の「何が」）</param>
        /// <param name="alternative">呼び出し元が取れる代替手段（文言の「どうすれば」）</param>
        private static void ThrowIfOnUiThread(string methodName, string alternative)
        {
            if (IsOnUiThread())
            {
                throw new InvalidOperationException(
                    $"DbContext.{methodName}() は WPF UI スレッドから呼び出せません。" +
                    "内部の SemaphoreSlim.Wait() が UI スレッドをブロックし、" +
                    "バックグラウンドで進行中の非同期トランザクション継続が Dispatcher 経由で " +
                    "UI スレッドに戻ろうとした際にデッドロックを引き起こす危険があります。" +
                    alternative);
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
                // Issue #1575: 外側 tx カウンタを増加（セマフォで直列化されているので 1 になる）。Lease 解放で必ず減らす。
                _activeTransactionCount++;
                var lease = new ConnectionLease(connection, () =>
                {
                    _activeTransactionCount = Math.Max(0, _activeTransactionCount - 1);
                    try { _semaphore.Release(); }
                    catch (ObjectDisposedException) { /* DbContext.Dispose()後のリース解放 */ }
                });
                var transaction = connection.BeginTransaction();
                return new TransactionScope(lease, transaction);
            }
            catch
            {
                // 例外時はカウンタも戻す（実体は 0 or 1 だが防御的に Max でガード）
                _activeTransactionCount = Math.Max(0, _activeTransactionCount - 1);
                _semaphore.Release();
                throw;
            }
        }

        // Issue #1988: 非推奨の GetConnection()（生の共有 SQLiteConnection をそのまま返す）を削除した。
        // 本番の呼び出し元は 1 つも無く、セマフォ（_semaphore）・トランザクション計数
        // （_activeTransactionCount）・保守トランザクションのゲート（_maintenanceTransactionGate、
        // Issue #1984）・進行中リースの計数（_activeAsyncLeaseCount、Issue #1809）のいずれも通らない。
        // GetConnection().BeginTransaction() は Issue #1984 で削除した BeginTransaction() の本体そのもので、
        // 残しておくと「計数に載らない取得口」が別の綴りで再生する。
        // 接続は LeaseConnectionAsync() / LeaseConnection() から、トランザクションは
        // BeginTransactionAsync() から取ること。
        // 回帰は DbContextConnectionAccessConventionTests がリフレクションで固定する
        // （メソッド名ではなく「SQLiteConnection を外へ出す公開面が無いこと」を資源で数える）。

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
                    // Issue #1809: PRAGMA の適用まで済んでから _connection へ代入する。
                    // 先に代入すると、ConfigurePragmas が throw（共有モードの SQLITE_BUSY 等）しても
                    // Open 済みの接続がフィールドに残り、次回以降 busy_timeout 未設定（既定 0）／
                    // journal_mode 未確認のまま同じ接続がプロセス全体で再利用され続ける。
                    // 失敗した接続は破棄して null のままにし、次回の取得で再構成させる。
                    var connection = new SQLiteConnection(_connectionString);
                    try
                    {
                        connection.Open();
                        ConfigurePragmas(connection);
                    }
                    catch
                    {
                        connection.Dispose();
                        throw;
                    }
                    _connection = connection;
                }

                return _connection;
            }
        }

        /// <summary>
        /// 接続に対してPRAGMA設定を適用
        /// </summary>
        /// <remarks>
        /// <c>protected virtual</c> なのは、PRAGMA 適用の失敗（共有モードの SQLITE_BUSY 等）を
        /// テストから注入するための継ぎ目（<c>ProbeDatabaseFileReachable</c> と同じ設計、Issue #1809）。
        /// 例外を投げた場合、呼び出し元の <see cref="GetConnectionInternal"/> はその接続を破棄して再スローする。
        /// </remarks>
        protected virtual void ConfigurePragmas(SQLiteConnection connection)
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
        /// その後 <see cref="LeaseConnectionAsync"/> / <see cref="LeaseConnection"/> /
        /// <see cref="BeginTransactionAsync"/> を呼ぶと、いずれも内部の GetConnectionInternal が
        /// 閉じられた接続を検出して自動的に再接続する。
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
        /// <para>
        /// 戻り値のConnectionSuspensionScopeをDisposeすると停止が解除される。
        /// using文で使用することで、例外発生時も確実に解除される。
        /// 停止中に <see cref="LeaseConnectionAsync"/> を呼ぶと InvalidOperationException が
        /// スローされる（<see cref="LeaseConnection"/> / <see cref="BeginTransactionAsync"/> は
        /// 接続を取る前にセマフォを待つため、失敗せず解除後に成功する。下の Issue #1809 の項を参照）。
        /// <b>ただし同一フローが既にリースを保持している場合は別</b>: <see cref="LeaseConnection"/> の
        /// リエントラント経路はセマフォを取らずに接続を取りに行くため、停止中は同じく例外になる。
        /// </para>
        /// <para>
        /// <b>Issue #1809 — 使用中の接続を閉じない:</b> 本メソッドは接続を閉じる前に
        /// (1) 接続セマフォを取得して進行中の <see cref="BeginTransactionAsync"/> / <see cref="LeaseConnection"/>
        /// の完了を待ち、(2) セマフォを取らない <see cref="LeaseConnectionAsync"/> の進行中リースが
        /// 解放されるまで（上限 <see cref="AsyncLeaseDrainTimeout"/>）待つ。セマフォはスコープの
        /// Dispose まで保持し続けるため、停止中に到着した <see cref="BeginTransactionAsync"/> /
        /// <see cref="LeaseConnection"/> は失敗せず、解除後に成功する。
        /// </para>
        /// <para>
        /// <b>UI スレッドから呼ばないこと:</b> セマフォを同期取得するため <see cref="LeaseConnection"/> と
        /// 同じデッドロック経路（Issue #1281）になる。UI 層からは
        /// <c>BackupService.RestoreFromBackupAsync</c>（Task.Run でオフロード）を使う。
        /// 同一非同期フロー内で <see cref="LeaseConnection"/> のリースを保持したまま呼んだ場合は
        /// リエントラント扱い（セマフォを再取得しない）だが、そのリースの接続は閉じられる。
        /// </para>
        /// </remarks>
        /// <returns>停止スコープ（Disposeで停止解除）</returns>
        /// <exception cref="InvalidOperationException">WPF の UI スレッドから呼び出された場合</exception>
        public ConnectionSuspensionScope SuspendConnections()
        {
            ThrowIfOnUiThread(
                nameof(SuspendConnections),
                "BackupService.RestoreFromBackupAsync() のように " +
                "Task.Run でバックグラウンドスレッドにオフロードしてから呼び出してください。");

            // 同一フローが LeaseConnection() のセマフォを既に保持しているならリエントラント扱い
            var ownsSemaphore = _reentrancyCount.Value == 0;
            if (ownsSemaphore)
            {
                _semaphore.Wait();
            }

            try
            {
                // 新規リースの取得を止めてから進行中の非同期リースの解放を待つ。
                // 逆順だと待っている間に新しいリースが入り続けて件数が 0 にならない。
                _isSuspended = true;
                // 「フラグの書き込み → 件数の読み取り」と、LeaseConnectionAsync 側の
                // 「件数の増加（Interlocked）→ フラグの読み取り」が交差しないよう全順序バリアを置く
                // （volatile 書き込みの後ろへ後続の読み取りが追い越すと、双方が「相手はまだ」と判断して
                // 接続の取得と Close が同時に成立し得る）。
                Interlocked.MemoryBarrier();
                if (!TryWaitForAsyncLeasesToDrain())
                {
                    // Issue #1716 と同じ判断: 障害調査で「進行中の読み取りを巻き込んだかもしれない」と
                    // 分かる痕跡が要るため Warning（LogDebug は本番のログファイルに残らない）
                    _logger?.LogWarning(
                        "Issue #1809: 進行中の非同期リース {RemainingLeases} 件が {Timeout} 以内に解放されなかったため、" +
                        "接続を閉じて一時停止を続行します。進行中の読み取りが失敗する可能性があります。{DatabasePath}",
                        Volatile.Read(ref _activeAsyncLeaseCount),
                        AsyncLeaseDrainTimeout,
                        DatabasePath);
                }

                lock (_connectionLock)
                {
                    CloseConnectionInternal();
                }
            }
            catch
            {
                _isSuspended = false;
                if (ownsSemaphore)
                {
                    _semaphore.Release();
                }
                throw;
            }

            _logger?.LogDebug("Issue #1166: DB接続を一時停止しました");
            return new ConnectionSuspensionScope(this, releaseSemaphore: ownsSemaphore);
        }

        /// <summary>
        /// Issue #1809: 進行中の <see cref="LeaseConnectionAsync"/> リースが解放されるまで待つ（上限付き）。
        /// </summary>
        /// <remarks>
        /// Issue #1984: 呼び出し元によって「上限に達したとき何をすべきか」が異なるため、
        /// ログはここで出さず結果だけを返す（一時停止は従来どおり続行、保守処理は当回をスキップ）。
        /// </remarks>
        /// <returns>進行中リースが 0 件になった場合 true、上限に達した場合 false</returns>
        private bool TryWaitForAsyncLeasesToDrain()
            => SpinWait.SpinUntil(() => Volatile.Read(ref _activeAsyncLeaseCount) == 0, AsyncLeaseDrainTimeout);

        /// <summary>
        /// Issue #1166: 接続の一時停止を解除する（ConnectionSuspensionScope.Disposeから呼び出される）
        /// </summary>
        /// <param name="releaseSemaphore">
        /// Issue #1809: <see cref="SuspendConnections"/> が接続セマフォを取得していた場合 true。
        /// 停止解除（<c>_isSuspended = false</c>）の<b>後</b>に返すことで、待機していた
        /// <see cref="BeginTransactionAsync"/> / <see cref="LeaseConnection"/> が解除済みの状態で再開する。
        /// </param>
        internal void ResumeConnections(bool releaseSemaphore)
        {
            _isSuspended = false;
            if (releaseSemaphore)
            {
                try { _semaphore.Release(); }
                catch (ObjectDisposedException) { /* DbContext.Dispose()後のスコープ解放 */ }
            }
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
                    // Issue #1831: 素の Rollback() を呼ばない（詳細は SafeRollback の XML doc）
                    SafeRollback.TryRollback(
                        () => transaction.Rollback(), _logger, "既存データベースの移行");
                    throw;
                }
            }

            // マイグレーションを実行（各マイグレーションが独自にトランザクションを管理）
            // 複数PCが同時にマイグレーションを実行した場合、後発PCは陳腐化したバージョンの
            // スナップショットに基づいて同じマイグレーションを再適用する。Up()は規約により
            // 冪等（.claude/rules/migrations.md）で、適用記録のINSERTもINSERT OR IGNOREのため、
            // 後発PCも例外なく起動できる（Issue #1738）。
            // schema_migrationsのPRIMARY KEY制約が防ぐのは記録の重複であって、
            // 重複適用そのものではない点に注意。
            var runner = new MigrationRunner(connection);

            // Issue #1687: DBのスキーマがこのアプリの対応範囲より新しい場合、
            // 旧バージョンのアプリからの書き込みはデータ不整合を招くためブロックする
            EnsureSchemaVersionCompatibility(runner, connection);

            var appliedCount = runner.MigrateToLatest();

            if (appliedCount > 0)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[DbContext] {appliedCount}件のマイグレーションを適用しました");
#endif
            }

            // Issue #1687: このDBを開くために必要なアプリバージョンの目安を記録する。
            // 旧バージョンのアプリをブロックした際、「どのバージョン以上に更新すべきか」を
            // メッセージに表示するために使用する（引き上げのみで、下げることはない）
            RecordMinimumAppVersion(connection);
        }

        /// <summary>
        /// settings テーブルの「要求アプリバージョン」キー（Issue #1687）。
        /// このDBを最後に更新した最も新しいアプリバージョンが記録される。
        /// </summary>
        public const string MinAppVersionSettingKey = "min_app_version";

        /// <summary>
        /// DBのスキーマバージョンがこのアプリの対応範囲内であることを確認する（Issue #1687）
        /// </summary>
        /// <exception cref="DatabaseVersionMismatchException">
        /// DBのスキーマバージョンがこのアプリの把握する最大マイグレーションバージョンを
        /// 超えている場合（＝より新しいバージョンのアプリで更新されたDBの場合）
        /// </exception>
        private static void EnsureSchemaVersionCompatibility(MigrationRunner runner, SQLiteConnection connection)
        {
            var databaseVersion = runner.GetCurrentVersion();
            var appVersion = runner.GetLatestKnownVersion();

            if (databaseVersion <= appVersion)
                return;

            // ブロックメッセージ用に、DBを更新した側のアプリバージョンを取得する。
            // settings テーブルやキーが存在しない場合は null（フォールバック文言になる）
            string requiredAppVersion = null;
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM settings WHERE key = @key";
                command.Parameters.AddWithValue("@key", MinAppVersionSettingKey);
                requiredAppVersion = command.ExecuteScalar() as string;
            }
            catch (SQLiteException)
            {
                // settings テーブルが読めなくてもブロック自体は成立させる
            }

            throw new DatabaseVersionMismatchException(databaseVersion, appVersion, requiredAppVersion);
        }

        /// <summary>
        /// settings の min_app_version を自バージョンで引き上げ記録する（Issue #1687）
        /// </summary>
        /// <remarks>
        /// 保存値が自バージョン以上の場合は何もしない（複数PC運用で、より新しい
        /// バージョンのPCが記録した値を古いPCが下書きしないため）。
        /// 記録の失敗は起動を阻害しない（次回起動時に再試行される）。
        /// </remarks>
        private void RecordMinimumAppVersion(SQLiteConnection connection)
        {
            try
            {
                string storedValue = null;
                using (var selectCommand = connection.CreateCommand())
                {
                    selectCommand.CommandText = "SELECT value FROM settings WHERE key = @key";
                    selectCommand.Parameters.AddWithValue("@key", MinAppVersionSettingKey);
                    storedValue = selectCommand.ExecuteScalar() as string;
                }

                if (AppVersionInfo.TryParseNormalized(storedValue, out var storedVersion) &&
                    storedVersion >= AppVersionInfo.Current)
                {
                    return;
                }

                using var upsertCommand = connection.CreateCommand();
                upsertCommand.CommandText = @"INSERT INTO settings (key, value) VALUES (@key, @value)
ON CONFLICT(key) DO UPDATE SET value = excluded.value";
                upsertCommand.Parameters.AddWithValue("@key", MinAppVersionSettingKey);
                upsertCommand.Parameters.AddWithValue("@value", AppVersionInfo.CurrentString);
                upsertCommand.ExecuteNonQuery();
            }
            catch (SQLiteException ex)
            {
                _logger?.LogWarning(ex, "min_app_version の記録に失敗しました（次回起動時に再試行されます）");
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

            BackfillLegacyMigrationVersion1(connection);
        }

        /// <summary>
        /// レガシーDB（マイグレーション機構導入前のDB）を <c>schema_migrations</c> のバージョン1として
        /// 追記する。共有モードで複数 PC が同時起動した場合の TOCTOU 競合に備え、
        /// テーブル作成・行挿入ともに冪等な SQL を使用する（Issue #1484）。
        /// </summary>
        internal static void BackfillLegacyMigrationVersion1(SQLiteConnection connection)
        {
            using var createTableCmd = connection.CreateCommand();
            createTableCmd.CommandText = @"CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY,
    description TEXT NOT NULL,
    applied_at TEXT DEFAULT (datetime('now', 'localtime'))
)";
            createTableCmd.ExecuteNonQuery();

            using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = "INSERT OR IGNORE INTO schema_migrations (version, description) VALUES (1, '初期スキーマ（既存DB）')";
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
                    attempt < delays.Length && IsTransientLockError(ex))
                {
                    var baseDelay = delays[attempt];
                    var jitter = IsSharedMode ? RetryJitter.GetJitter(baseDelay) : 0;
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
        /// <remarks>
        /// Issue #1737: 起動時タスクの実行順・直列性を <see cref="Services.StartupTaskRunner"/> の
        /// 単体テストで固定するため virtual（テストダブルで差し替える）。
        /// </remarks>
        public virtual Task<(int LedgerCount, int OperationLogCount)> CleanupOldDataAsync(CancellationToken cancellationToken = default)
            => Task.Run(CleanupOldData, cancellationToken);

        /// <summary>
        /// Issue #1984: 保守トランザクションを開始できる状態にする。
        /// ゲートを閉じて新規の <see cref="LeaseConnectionAsync"/> を止め、進行中のリースが
        /// 解放されるまで待つ。
        /// </summary>
        /// <returns>
        /// 保守トランザクションを開いてよい場合はガード（Dispose でゲートを開く）。
        /// 進行中リースが <see cref="AsyncLeaseDrainTimeout"/> 以内に空にならなかった場合は null。
        /// </returns>
        private MaintenanceTransactionGuard TryBeginMaintenanceTransaction()
        {
            _maintenanceTransactionGate.Wait();
            try
            {
                // ゲートの閉鎖（セマフォ取得）と進行中件数の読み取りが交差しないよう全順序バリアを置く
                // （SuspendConnections と同じ理由）。
                Interlocked.MemoryBarrier();
                if (!TryWaitForAsyncLeasesToDrain())
                {
                    // Issue #1716 / #1730: 「なぜ古いデータが削除されなかったか」を障害調査で追えるよう
                    // 本番のログファイルへ出力されるレベルで記録する（LogDebug は Release で出ない）。
                    // 呼び出し元（StartupTaskRunner）は削除件数 0 を「対象なし」と区別できないため、
                    // 「なぜ削除されなかったか」はこの 1 行だけが伝える。原因の候補まで載せる（#1730）。
                    // なお本経路は次回起動で再試行されるが、リースの解放漏れ（Dispose 漏れ）があると
                    // 恒久的に削除できなくなる。その状態は SuspendConnections（リストア）も同じく
                    // 上限に達するため、この Warning が毎回出るなら解放漏れを疑うこと。
                    _logger?.LogWarning(
                        "Issue #1984: 進行中の非同期リース {RemainingLeases} 件が {Timeout} 以内に解放されなかったため、" +
                        "6年経過データの削除を今回はスキップしました（次回起動時に再試行します）。" +
                        "毎回スキップされる場合は接続リースの解放漏れを疑ってください。{DatabasePath}",
                        Volatile.Read(ref _activeAsyncLeaseCount),
                        AsyncLeaseDrainTimeout,
                        DatabasePath);
                    _maintenanceTransactionGate.Release();
                    return null;
                }

                Interlocked.Increment(ref _activeMaintenanceTransactionCount);
                return new MaintenanceTransactionGuard(this);
            }
            catch
            {
                _maintenanceTransactionGate.Release();
                throw;
            }
        }

        /// <summary>
        /// Issue #1984: 保守トランザクションを開いた直後に呼ばれる割り込み点（既定は何もしない）。
        /// </summary>
        /// <remarks>
        /// 巻き添えロールバックの回帰テストはスレッドを競争させず、この割り込み点を派生クラスで
        /// 上書きして確定的に再現する（Issue #1919 と同じ作法）。
        /// </remarks>
        internal virtual void OnMaintenanceTransactionOpened()
        {
        }

        /// <summary>
        /// Issue #1984: 保守トランザクションの保持を表すガード。Dispose でゲートを開く。
        /// </summary>
        private sealed class MaintenanceTransactionGuard : IDisposable
        {
            private readonly DbContext _owner;
            private bool _disposed;

            internal MaintenanceTransactionGuard(DbContext owner) => _owner = owner;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;

                Interlocked.Decrement(ref _owner._activeMaintenanceTransactionCount);
                try { _owner._maintenanceTransactionGate.Release(); }
                catch (ObjectDisposedException) { /* DbContext.Dispose() 後の解放 */ }
            }
        }

        /// <summary>
        /// Issue #1170: CleanupOldDataの実体。両テーブルの削除を単一トランザクションで実行する。
        /// </summary>
        private (int LedgerCount, int OperationLogCount) CleanupOldDataInternal()
        {
            using var lease = LeaseConnection();
            var connection = lease.Connection;

            // Issue #1984: 保守トランザクションを開く前に、セマフォを取らない LeaseConnectionAsync 経由の
            // 書き込みを止めて進行中のリースを空にする。これをしないと、貸出・返却の台帳 INSERT や
            // 操作ログの INSERT が同一接続上でこのトランザクションへ暗黙参加し、下の Rollback で
            // 一緒に消える（UI は返却成功を表示済み。Issue #1737 と同型）。
            using var maintenanceGuard = TryBeginMaintenanceTransaction();
            if (maintenanceGuard == null)
            {
                // 進行中のリースが上限時間内に空にならなかった。ここで従来どおり続行すると
                // 本 Issue が消そうとしている巻き添えロールバックの窓がそのまま残るため、
                // 当回は削除せずに戻る（6 年経過データの削除は次回起動で改めて試行される）。
                return (0, 0);
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                OnMaintenanceTransactionOpened();

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
                // Issue #1831: 巻き戻しの手段は SafeRollback へ寄せる
                SafeRollback.TryRollback(() => transaction.Rollback(), _logger, "古いデータの削除");
                throw;
            }
        }

        /// <summary>
        /// VACUUMを実行してデータベースを最適化
        /// </summary>
        /// <remarks>
        /// <para>
        /// 共有モードでは他PCが接続中の場合、排他ロックを取得できず失敗する可能性がある。
        /// その場合はfalseを返し、次回起動時にリトライする。
        /// </para>
        /// <para>
        /// <b>Issue #1737:</b> SQLite 由来の失敗は理由を問わず false（当月スキップ）に畳む。
        /// 呼び出し元（<see cref="Services.StartupTaskRunner"/>）は先勝ち CAS ロック（Issue #1482）を
        /// <b>消費してから</b>ここへ来るため、例外を投げ返してもロックは戻らず当月は誰も再試行しない。
        /// それでいて呼び出し元のログには VACUUM が原因だと分からない形でしか痕跡が残らなかった。
        /// Busy / Locked のみを対象にしていると、同一接続に未完了のステートメントがある場合の
        /// "cannot VACUUM - SQL statements in progress"（<see cref="SQLiteErrorCode.Error"/>）や
        /// 書き込み不可・容量不足が素通りする。
        /// </para>
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
            catch (SQLiteException ex)
            {
                // Issue #1716 / #1730: 障害調査で「なぜ VACUUM が飛ばされたか」を追えるよう、
                // 本番のログファイルへ出力されるレベルで記録する（LogDebug は Release で出力されない）。
                // 原因候補を 1 つに絞れるのは ResultCode だけなので必ず載せる。
                //
                // Issue #1737: レベルは「待てば直るか」で分ける。呼び出し元は false を
                // 「当月スキップ・来月再試行」として扱うが、書き込み不可・容量不足・DB 破損は
                // 待っても直らないため、毎月 Warning 1 行が出るだけでは管理者に届かない。
                if (IsPermanentVacuumFailure(ex.ResultCode))
                {
                    _logger?.LogError(ex,
                        "VACUUM に失敗しました（ResultCode={ResultCode}）。" +
                        "データベースファイルが書き込み不可・容量不足・破損のいずれかの可能性があります。" +
                        "月次の再試行では解消しないため、データベースの保存先を確認してください。",
                        ex.ResultCode);
                }
                else
                {
                    // 他の接続がアクティブな場合など。失敗の理由はモードによって異なる
                    // （共有モード=他PCの接続、ローカルモード=自プロセス内の別処理）ため原因は断定しない。
                    _logger?.LogWarning(ex, "VACUUM をスキップしました（ResultCode={ResultCode}）", ex.ResultCode);
                }
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[DbContext] VACUUM失敗（ResultCode={ex.ResultCode}）: {ex.Message}");
#endif
                return false;
            }
        }

        /// <summary>
        /// VACUUM の失敗が「待っても解消しない」種類かを判定する（Issue #1737）。
        /// </summary>
        /// <remarks>
        /// 月次 VACUUM は先勝ち CAS ロック（Issue #1482）を消費してから実行されるため、
        /// 失敗しても当月は誰も再試行しない。恒久的な失敗をログレベルで区別しないと、
        /// 書き込み不可・破損した DB が「来月再試行します」の Warning に紛れて放置される。
        /// </remarks>
        internal static bool IsPermanentVacuumFailure(SQLiteErrorCode resultCode)
        {
            switch (resultCode)
            {
                case SQLiteErrorCode.ReadOnly:  // 共有先の権限が読み取り専用になった
                case SQLiteErrorCode.Full:      // 保存先の空き容量不足
                case SQLiteErrorCode.Corrupt:   // DB ファイルの破損
                case SQLiteErrorCode.NotADb:    // DB ファイルではない／暗号化されている
                case SQLiteErrorCode.CantOpen:  // ファイルを開けない（パス・権限）
                case SQLiteErrorCode.Perm:      // アクセス権限がない
                    return true;
                default:
                    // Busy / Locked / Error（"SQL statements in progress" 等）は一時的とみなす
                    return false;
            }
        }

        /// <summary>
        /// VACUUM 実行の非同期版。UI スレッドから呼び出しても Issue #1281 の
        /// <see cref="LeaseConnection"/> ガードで例外にならないよう、<see cref="Task.Run{TResult}(Func{TResult})"/>
        /// でバックグラウンドスレッドに確実にオフロードする。
        /// </summary>
        /// <remarks>
        /// Issue #1737: 起動時タスクの実行順・直列性を <see cref="Services.StartupTaskRunner"/> の
        /// 単体テストで固定するため virtual（テストダブルで差し替える）。
        /// </remarks>
        public virtual Task<bool> VacuumAsync(CancellationToken cancellationToken = default)
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
        /// 一過性のロック競合（SQLITE_BUSY / SQLITE_LOCKED）かどうかを判定する（Issue #1951）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 「同じ操作をもう一度実行すれば成功し得る失敗」の唯一の定義。
        /// <see cref="ExecuteWithRetryAsync{T}"/> / <see cref="CleanupOldData"/> のリトライ判定と、
        /// リポジトリ側の「この例外を <c>false</c> へ畳んでよいか」の判定が
        /// <b>同じ述語を見ている</b>ことが正しさの前提になる。
        /// </para>
        /// <para>
        /// 判定を各所へ書き写すと、片方だけが新しい ResultCode を知っている状態が生まれる
        /// （<c>.claude/rules/development-conventions.md</c> #1763「同じ論理的な処理に手段が 2 通りあるか」）。
        /// </para>
        /// </remarks>
        internal static bool IsTransientLockError(SQLiteException ex)
        {
            return ex != null &&
                   (ex.ResultCode == SQLiteErrorCode.Busy || ex.ResultCode == SQLiteErrorCode.Locked);
        }

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
                    attempt < delays.Length && IsTransientLockError(ex))
                {
                    // 基本待機時間 + ジッター（0〜50%の追加遅延）で thundering herd を緩和
                    var baseDelay = delays[attempt];
                    var jitter = IsSharedMode ? RetryJitter.GetJitter(baseDelay) : 0;
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

        // Issue #1984: 非推奨の BeginTransaction()（GetConnection() から直接トランザクションを開く）を削除した。
        // 呼び出し元は 1 つも無く、セマフォも _activeTransactionCount も通らないため、
        // 復活すると本 Issue が塞いだ「計数に載らないトランザクション」がそのまま再生する。
        // トランザクションは BeginTransactionAsync() から取ること。
        // Issue #1988: 同じ抜け道を再現できた GetConnection() も削除済み（上記の注記を参照）。

        /// <summary>
        /// DB接続の疎通確認（IDatabaseInfo実装）
        /// </summary>
        /// <remarks>
        /// <para>
        /// SQLite クエリの成功だけでは切断を検知できない（Issue #1716）。本クラスは接続を
        /// 開きっぱなしの単一接続として保持しており、疎通確認に使う
        /// <c>SELECT COUNT(*) FROM sqlite_master</c> はスキーマ情報が SQLite のページキャッシュに
        /// 載っているため、SMB へ一切出て行かずに応答され得る。実機の共有モードでネットワークを
        /// 切断しても本メソッドが true を返し続け、「到達性 正常／書込権限 異常」という
        /// 自己矛盾した接続診断結果になることを確認している
        /// （Issue #1110 の「sqlite_master 読み取りでファイルアクセスを強制」という意図は達成できていなかった）。
        /// </para>
        /// <para>
        /// そのためクエリの成否に加えて <see cref="ProbeDatabaseFileReachable"/> による
        /// ファイルシステムへの実問い合わせを併用し、SMB のラウンドトリップを強制する。
        /// プローブはリース解放後に実行し、死んだ UNC パスへの問い合わせが SMB タイムアウトまで
        /// ブロックする間、接続セマフォを掴んだままにしない。
        /// </para>
        /// <para>
        /// <b>確認全体に上限時間を設ける</b>: 疎通確認は「接続リース取得 → 接続オープン
        /// （切断後は再オープン）→ クエリ → ファイル到達確認」と複数のネットワーク待ちが
        /// 直列に並び、<b>どの区間も下位の TCP/SMB タイムアウトまで戻ってこない</b>。
        /// 区間ごとに上限を設けても塞ぎ残しが生じるため、確認全体をバックグラウンドで実行し
        /// <see cref="ConnectionCheckTimeout"/> を超えたら「接続なし」とみなす。
        /// 実機ログでは接続オープン〜クエリの区間だけで 82.3 秒ブロックし、切断トーストが
        /// 90 秒以上遅れた（Issue #1716）。
        /// </para>
        /// <para>
        /// 呼び出し元は UI スレッド以外から呼ぶこと。上限までの待機（最大
        /// <see cref="ConnectionCheckTimeout"/>）が呼び出しスレッドをブロックする。
        /// 既存の呼び出し元（<c>SharedModeMonitor</c> / <c>ConnectionDiagnosticsService</c>）は
        /// いずれも <c>Task.Run</c> で退避済み。
        /// </para>
        /// </remarks>
        /// <returns>接続可能な場合true</returns>
        public bool CheckConnection()
        {
            if (IsConnectionSuspended)
                return true;

            Task<bool> check;
            lock (_connectionCheckLock)
            {
                // 上限で打ち切っても下位の呼び出し自体は中断できないため、進行中の確認は 1 本に限定する。
                // 打ち切るたびに新しい確認を始めると、ブロックしたスレッドが 15 秒ごとに積み上がり、
                // さらに接続セマフォ待ちの行列を作って本来のDB操作まで巻き添えにする
                if (_pendingConnectionCheck == null || _pendingConnectionCheck.IsCompleted)
                {
                    _pendingConnectionCheck = Task.Run(() =>
                    {
                        // 想定外の例外は「接続なし」に丸め、Task を faulted にしない
                        try { return ExecuteConnectionCheck(); }
                        catch { return false; }
                    });
                }

                check = _pendingConnectionCheck;
            }

            var timeout = ConnectionCheckTimeout;
            if (!check.Wait(timeout))
            {
                _logger?.LogInformation(
                    "DB接続疎通確認: {TimeoutSeconds}秒以内に完了しないため「接続なし」とみなします（{DatabasePath}）。" +
                    "ネットワークが切断されている可能性があります。",
                    timeout.TotalSeconds, DatabasePath);
                return false;
            }

            return check.Result;
        }

        /// <summary>
        /// 疎通確認の本体（Issue #1716）。上限時間の管理は <see cref="CheckConnection"/> が行う
        /// </summary>
        /// <returns>接続可能な場合true</returns>
        private bool ExecuteConnectionCheck()
        {
            // Issue #1716: 「どの段階に何ミリ秒かかったか」を残す。
            // 遅延の原因を段階まで絞れないと調査できない（実機で 82.3 秒の内訳特定に必要だった）
            var queryStopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var lease = LeaseConnection())
                using (var command = lease.Connection.CreateCommand())
                {
                    // Issue #1110: sqlite_masterからの読み取り。ただしページキャッシュ応答があり得るため
                    // これ単独では到達性の証明にならない（下のファイル到達確認と併用する / Issue #1716）
                    command.CommandText = "SELECT COUNT(*) FROM sqlite_master";
                    command.ExecuteScalar();
                }

                queryStopwatch.Stop();
            }
            catch (InvalidOperationException)
            {
                // 接続一時停止中 — ネットワーク切断ではない
                return true;
            }
            catch (Exception ex)
            {
                queryStopwatch.Stop();

                // Issue #1282: 疎通確認なので「失敗=未到達」を戻り値で通知するのが仕様。
                // ただしサイレント握りつぶしはトラブル時のデバッグを困難にするため、
                // LogDebug で失敗理由（例外の詳細）を残す。接続断は運用上頻繁に起きる想定のため
                // LogWarning ではなく LogDebug を選択（ログファイルの肥大化を避ける）。
                _logger?.LogDebug(ex,
                    "DB接続疎通確認に失敗。呼び出し元には false を返す（ネットワーク断または読み取りエラー）");

                // Issue #1716: 上の LogDebug は本番の既定フィルタ（Information）では出力されないため、
                // 障害調査に必要な「いつ・どの段階で・何ミリ秒かけて失敗したか」は Information でも残す
                LogConnectionCheckOutcome("クエリ", queryStopwatch.ElapsedMilliseconds, probeMs: null, isConnected: false);
                return false;
            }

            // Issue #1716: クエリはキャッシュ応答し得るため、ファイルへ実際に届くことも要求する
            var probeStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var isReachable = ProbeDatabaseFileReachable();
            probeStopwatch.Stop();

            LogConnectionCheckOutcome(
                isReachable ? null : "ファイル到達確認",
                queryStopwatch.ElapsedMilliseconds,
                probeStopwatch.ElapsedMilliseconds,
                isReachable);

            return isReachable;
        }

        /// <summary>
        /// 疎通確認の結果と段階ごとの所要時間を記録する（Issue #1716）
        /// </summary>
        /// <remarks>
        /// 失敗時、または正常でも <see cref="SlowConnectionCheckThresholdMs"/> を超えて時間がかかった場合のみ
        /// 出力する。正常かつ高速な通常運用（15秒ごと）ではログに何も残らないため肥大化しない。
        /// レベルに Information を選ぶ理由は <c>.claude/rules/development-conventions.md</c>「ロギング」を参照。
        /// </remarks>
        /// <param name="failedStage">失敗した段階の名称。成功時は null</param>
        /// <param name="queryMs">接続リース取得〜クエリ完了までの所要ミリ秒</param>
        /// <param name="probeMs">ファイル到達確認の所要ミリ秒。クエリ段階で失敗した場合は null</param>
        /// <param name="isConnected">疎通確認の結果</param>
        private void LogConnectionCheckOutcome(string failedStage, long queryMs, long? probeMs, bool isConnected)
        {
            if (_logger == null)
                return;

            var isSlow = queryMs >= SlowConnectionCheckThresholdMs
                         || (probeMs ?? 0) >= SlowConnectionCheckThresholdMs;

            if (isConnected && !isSlow)
                return;

            _logger.LogInformation(
                "DB接続疎通確認: 結果={IsConnected}{FailedStage}（クエリ {QueryMs}ms、ファイル到達確認 {ProbeMs}）{DatabasePath}",
                isConnected ? "接続あり" : "接続なし",
                failedStage == null ? string.Empty : $"／失敗段階={failedStage}",
                queryMs,
                probeMs.HasValue ? $"{probeMs.Value}ms" : "未実施",
                DatabasePath);
        }

        /// <summary>
        /// 疎通確認が「遅い」とみなすしきい値（ミリ秒）。正常なローカル/LAN では数ミリ秒で完了する
        /// </summary>
        private const int SlowConnectionCheckThresholdMs = 1000;

        /// <summary>
        /// 疎通確認全体の上限時間（Issue #1716）。テストから短縮できるよう仮想メンバーにしている
        /// </summary>
        protected virtual TimeSpan ConnectionCheckTimeout =>
            TimeSpan.FromSeconds(AppConstants.DatabaseConnectionCheckTimeoutSeconds);

        /// <summary>
        /// データベースファイルそのものへ到達できるかを実測する（Issue #1716）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 到達できない理由（ネットワーク切断・ファイル削除・アクセス権喪失）は区別せず、
        /// いずれも「到達できない」として扱う。<c>File.Exists</c> は例外を投げず、
        /// 到達不能・権限なしのいずれでも false を返す。
        /// </para>
        /// <para>
        /// インメモリDB（<c>:memory:</c>）とパス未設定（テスト用 protected コンストラクタ）は
        /// ファイルを持たないため、常に到達可能として扱う。ここを除外しないと、
        /// インメモリDBを使う多数の単体テストが一斉に「接続不可」になる。
        /// </para>
        /// <para>
        /// テストから到達不能状態を再現できるよう <c>protected virtual</c> の継ぎ目にしている
        /// （<c>ConnectionDiagnosticsService.ProbeDatabaseFileReachable</c> と同じ設計）。
        /// </para>
        /// </remarks>
        /// <returns>ファイルへ到達できる場合true（インメモリDB・パス未設定は常にtrue）</returns>
        protected virtual bool ProbeDatabaseFileReachable()
        {
            if (IsInMemoryDatabasePath(DatabasePath))
                return true;

            return File.Exists(DatabasePath);
        }

        /// <summary>
        /// ファイル実体を持たないDBパス（インメモリDB・パス未設定）かどうかを判定する（Issue #1716）
        /// </summary>
        internal static bool IsInMemoryDatabasePath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                || path.Equals(InMemoryDatabasePath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// インメモリDBを指すDataSource値。単体テストが <c>new DbContext(":memory:")</c> で使用する
        /// </summary>
        internal const string InMemoryDatabasePath = ":memory:";

        /// <summary>
        /// DBへの書き込み可否確認（IDatabaseInfo実装、Issue #1686）
        /// </summary>
        /// <remarks>
        /// user_version（本アプリでは未使用のDBヘッダ領域）への実書き込みをトランザクション内で
        /// 行い、即座に ROLLBACK する。値もデータも変化しない。
        /// BEGIN IMMEDIATE（書き込みロック取得）だけでは読み取り専用ファイルでも成功してしまう
        /// （SQLITE_READONLY は実際のページ書き込みで初めて発生する）ことを実測で確認済みのため、
        /// 実書き込みを伴うプローブとしている。読み取り専用のファイル属性や共有フォルダの
        /// アクセス権不足（変更権限なし）をこの方法で検出できる。
        /// </remarks>
        /// <returns>書き込み可能な場合true</returns>
        public bool CheckWritable()
        {
            if (IsConnectionSuspended)
                return true;

            // 別の書き込みトランザクションが進行中の場合、BEGIN が二重開始で失敗して
            // 誤って「書込不可」と報告してしまう。書き込みが現に機能している状況なので true を返す
            if (HasActiveTransactionScope)
                return true;

            try
            {
                using var lease = LeaseConnection();
                using var command = lease.Connection.CreateCommand();

                command.CommandText = "PRAGMA user_version;";
                var currentVersion = Convert.ToInt64(command.ExecuteScalar());

                command.CommandText = "BEGIN IMMEDIATE;";
                command.ExecuteNonQuery();
                try
                {
                    // +1 した値を書いて実書き込みを強制する（ROLLBACK で元に戻る）
                    command.CommandText = $"PRAGMA user_version = {currentVersion + 1};";
                    command.ExecuteNonQuery();
                    return true;
                }
                finally
                {
                    command.CommandText = "ROLLBACK;";
                    command.ExecuteNonQuery();
                }
            }
            catch (InvalidOperationException)
            {
                // 接続一時停止中 — 書込権限の問題ではない
                return true;
            }
            catch (Exception ex)
            {
                // CheckConnection と同じ方針: 「失敗=書込不可」を戻り値で通知しつつ LogDebug で痕跡を残す。
                // 例外オブジェクトごと渡すのはこちらだけ（スタックトレースは開発時のみ必要）
                _logger?.LogDebug(ex,
                    "DB書込可否確認に失敗。呼び出し元には false を返す（読み取り専用アクセス権または接続エラー）");

                // Issue #1730: 上の LogDebug は本番の既定フィルタ（Information）では出力されないため、
                // 障害調査に必要な「なぜ書き込めなかったか」は Information でも残す
                LogWritableCheckFailure(ex);
                return false;
            }
        }

        /// <summary>
        /// 書込可否確認の失敗理由を記録する（Issue #1730）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 失敗時のみ出力する。<see cref="CheckWritable"/> の本番呼び出し元は接続診断（F6→接続診断）の
        /// オンデマンド 1 経路のみのため、ログ肥大化は起きない。
        /// </para>
        /// <para>
        /// <b>SQLite の結果コードが調査の核心。</b> 接続診断の画面は書込不可の原因候補として
        /// 「読み取り専用属性／アクセス権不足／他プログラムによる占有」を並べるが、そのどれであるかは
        /// 示せない（<see cref="CheckWritable"/> は bool しか返さない）。結果コード
        /// （<c>ReadOnly</c> / <c>Full</c> / <c>Busy</c> / <c>Locked</c> 等）は候補を 1 つに絞れる唯一の材料で、
        /// これがログに無いと管理者はログを回収しても切り分けられない。
        /// </para>
        /// <para>
        /// レベルに Information を選ぶ理由は <c>.claude/rules/development-conventions.md</c>「ロギング」を参照
        /// （Issue #1716 で <see cref="ExecuteConnectionCheck"/> に入れたのと同じ対応）。
        /// </para>
        /// </remarks>
        /// <param name="ex">書込可否確認で捕捉した例外</param>
        private void LogWritableCheckFailure(Exception ex)
        {
            if (_logger == null)
                return;

            var resultCode = ex is SQLiteException sqliteException
                ? sqliteException.ResultCode.ToString()
                : "なし";

            _logger.LogInformation(
                "DB書込可否確認: 結果=書込不可（例外={ExceptionType}、SQLite結果コード={SqliteResultCode}、メッセージ={ExceptionMessage}）{DatabasePath}",
                ex.GetType().Name,
                resultCode,
                ex.Message,
                DatabasePath);
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
                    _maintenanceTransactionGate?.Dispose();
                }
                _disposed = true;
            }
        }
    }
}
