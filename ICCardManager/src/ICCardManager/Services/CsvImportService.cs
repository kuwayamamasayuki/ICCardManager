using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using Microsoft.Extensions.Logging;
using System.Data.SQLite;

namespace ICCardManager.Services
{
/// <summary>
    /// CSVインポート結果
    /// </summary>
    public class CsvImportResult
    {
        /// <summary>成功したか</summary>
        public bool Success { get; set; }

        /// <summary>インポートした件数</summary>
        public int ImportedCount { get; set; }

        /// <summary>スキップした件数（既存データ）</summary>
        public int SkippedCount { get; set; }

        /// <summary>エラー件数</summary>
        public int ErrorCount { get; set; }

        /// <summary>エラー詳細リスト</summary>
        public List<CsvImportError> Errors { get; set; } = new();

        /// <summary>エラーメッセージ</summary>
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// CSVインポートエラー詳細
    /// </summary>
    public class CsvImportError
    {
        /// <summary>行番号</summary>
        public int LineNumber { get; set; }

        /// <summary>エラー内容</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>対象データ</summary>
        public string Data { get; set; }
    }

    /// <summary>
    /// CSVインポートプレビュー結果
    /// </summary>
    public class CsvImportPreviewResult
    {
        /// <summary>プレビューが有効か（エラーがないか）</summary>
        public bool IsValid { get; set; }

        /// <summary>新規追加予定件数</summary>
        public int NewCount { get; set; }

        /// <summary>更新予定件数</summary>
        public int UpdateCount { get; set; }

        /// <summary>スキップ予定件数</summary>
        public int SkipCount { get; set; }

        /// <summary>エラー件数</summary>
        public int ErrorCount { get; set; }

        /// <summary>エラー詳細リスト</summary>
        public List<CsvImportError> Errors { get; set; } = new();

        /// <summary>プレビューアイテムリスト</summary>
        public List<CsvImportPreviewItem> Items { get; set; } = new();

        /// <summary>エラーメッセージ</summary>
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// CSVインポートプレビューアイテム
    /// </summary>
    public class CsvImportPreviewItem
    {
        /// <summary>行番号</summary>
        public int LineNumber { get; set; }

        /// <summary>IDm</summary>
        public string Idm { get; set; } = string.Empty;

        /// <summary>名前（カード種別または氏名）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>追加情報（管理番号または職員番号）</summary>
        public string AdditionalInfo { get; set; }

        /// <summary>アクション（新規/更新/スキップ）</summary>
        public ImportAction Action { get; set; }

        /// <summary>変更点リスト（更新時および新規追加時）</summary>
        public List<FieldChange> Changes { get; set; } = new();

        /// <summary>変更点があるか</summary>
        public bool HasChanges => Changes.Count > 0;

        /// <summary>変更点のサマリ文字列</summary>
        public string ChangesSummary => HasChanges
            ? string.Join("、", Changes.Select(c => c.FieldName))
            : string.Empty;

        /// <summary>詳細セクションのヘッダー（アクションに応じて変化）</summary>
        public string ChangesHeader => Action == ImportAction.Insert
            ? "追加する内容:"
            : Action == ImportAction.Skip
                ? "スキップするデータ:"
                : "変更内容の詳細:";
    }

    /// <summary>
    /// フィールド変更情報
    /// </summary>
    public class FieldChange
    {
        /// <summary>フィールド名</summary>
        public string FieldName { get; set; } = string.Empty;

        /// <summary>変更前の値</summary>
        public string OldValue { get; set; } = string.Empty;

        /// <summary>変更後の値</summary>
        public string NewValue { get; set; } = string.Empty;

        /// <summary>表示専用フラグ（追加・スキップ時のデータ表示用）</summary>
        public bool IsDisplayOnly { get; set; }

        /// <summary>変更内容の表示文字列</summary>
        public string DisplayText => IsDisplayOnly
            ? $"{FieldName}: {NewValue ?? "(なし)"}"
            : $"{FieldName}: {OldValue ?? "(なし)"} → {NewValue ?? "(なし)"}";
    }

    /// <summary>
    /// インポートアクション
    /// </summary>
    public enum ImportAction
    {
        /// <summary>新規追加</summary>
        Insert,

        /// <summary>更新</summary>
        Update,

        /// <summary>スキップ</summary>
        Skip,

        /// <summary>削除済みを復元して更新</summary>
        Restore
    }

    /// <summary>
    /// CSVインポートサービス
    /// </summary>
    public partial class CsvImportService
    {
        private readonly ICardRepository _cardRepository;
        private readonly IStaffRepository _staffRepository;
        private readonly ILedgerRepository _ledgerRepository;
        private readonly IValidationService _validationService;
        private readonly DbContext _dbContext;
        private readonly ICacheService _cacheService;
        private readonly ISettingsRepository _settingsRepository;
        private readonly ILogger<CsvImportService>? _logger;

        /// <summary>
        /// 7 引数オーバーロード（既存テストの Moq プロキシ生成互換性のため維持）
        /// </summary>
        public CsvImportService(
            ICardRepository cardRepository,
            IStaffRepository staffRepository,
            ILedgerRepository ledgerRepository,
            IValidationService validationService,
            DbContext dbContext,
            ICacheService cacheService,
            ISettingsRepository settingsRepository)
            : this(cardRepository, staffRepository, ledgerRepository, validationService, dbContext, cacheService, settingsRepository, logger: null)
        {
        }

        /// <summary>
        /// Issue #1282: ILogger を受け取るコンストラクタ
        /// </summary>
        public CsvImportService(
            ICardRepository cardRepository,
            IStaffRepository staffRepository,
            ILedgerRepository ledgerRepository,
            IValidationService validationService,
            DbContext dbContext,
            ICacheService cacheService,
            ISettingsRepository settingsRepository,
            ILogger<CsvImportService>? logger)
        {
            _cardRepository = cardRepository;
            _staffRepository = staffRepository;
            _ledgerRepository = ledgerRepository;
            _validationService = validationService;
            _dbContext = dbContext;
            _cacheService = cacheService;
            _settingsRepository = settingsRepository;
            _logger = logger;
        }

        /// <summary>
        /// 摘要の再生成に使う <see cref="SummaryGenerator"/> を、DB に保存された部署種別で組み立てる。
        /// </summary>
        /// <remarks>
        /// <para>
        /// Issue #1955: 明細 CSV の取込は <c>new SummaryGenerator()</c>（既定 ＝
        /// <see cref="DepartmentType.MayorOffice"/>）で摘要を作り直していたため、企業会計部局に
        /// 設定した組織でもチャージ行が「役務費によりチャージ」で 6 年保存の台帳へ書き込まれ、
        /// そのまま物品出納簿に印字されていた（設定が効く経路と効かない経路が混在する状態。
        /// <c>.claude/rules/development-conventions.md</c> #1820 と同 family）。
        /// </para>
        /// <para>
        /// DI に登録済みの <see cref="SummaryGenerator"/> シングルトンを注入せず、毎回 DB の設定から
        /// 組み立てるのは、<b>共有モードで他 PC が変更した部署種別まで拾うため</b>。
        /// シングルトンは Issue #1975 で自 PC の保存に追従するようになった（<c>ApplyDepartmentType</c>）が、
        /// それは<b>自 PC の設定画面（F5）を経た変更に限られる</b>。
        /// <c>BusStopInputViewModel.PersistBusStopsAsync</c> が毎回設定を読み直しているのと同じ判断。
        /// なお <c>GetAppSettingsAsync</c> はキャッシュ経由（既定 TTL 3 分）なので、他 PC の変更は
        /// 最長で TTL 分だけ古い値を見る（自 PC の保存はキャッシュを無効化する）。
        /// </para>
        /// <para>
        /// 組織文言（<c>ChargeSummaryEnterprise</c> 等）は静的な <c>CurrentOptions</c> から引かれるため、
        /// ここで渡すのは部署種別だけでよい（2 引数コンストラクタは静的状態を書き換えるので使わない）。
        /// </para>
        /// </remarks>
        private async Task<SummaryGenerator> CreateSummaryGeneratorAsync()
        {
            var settings = await _settingsRepository.GetAppSettingsAsync().ConfigureAwait(false);
            return new SummaryGenerator(settings.DepartmentType);
        }

        // === 共通ユーティリティ ===

        /// <summary>
        /// CSVファイルを読み込み、行のリストとして返す
        /// </summary>
        /// <param name="filePath">CSVファイルパス</param>
        /// <param name="logger">判別結果を記録するロガー（省略可）</param>
        /// <remarks>
        /// <para>
        /// Issue #1744: 文字コードは <see cref="TextEncodingDetector"/> で判別する。
        /// 従来は BOM 無しファイルを <see cref="Encoding.UTF8"/>（置換フォールバック）固定で復号していたため、
        /// 日本語版 Excel が「CSV（コンマ区切り）」として保存し直した Shift_JIS ファイルの日本語が
        /// U+FFFD へ置換され、化けたまま staff / ic_card / ledger へ書き込まれていた。
        /// IDm・日付・金額は ASCII のため全バリデーションを素通りし、検出手段が無かった。
        /// </para>
        /// <para>
        /// 判別できない場合は <see cref="FileOperationException.UndecidableEncoding"/> を、
        /// BOM が示す文字コードとして読めない（＝破損）場合は
        /// <see cref="FileOperationException.UnreadableDeclaredEncoding"/> を投げて中断する。
        /// 呼び出し元（<c>ExecuteImportWithErrorHandlingAsync</c> 等）がユーザー向け文言へ変換する。
        /// </para>
        /// </remarks>
        /// <exception cref="FileOperationException">文字コードを判別できない、または宣言された文字コードとして読めない場合</exception>
        internal static async Task<List<string>> ReadCsvFileAsync(string filePath, ILogger logger = null)
        {
            var bytes = await ReadAllBytesAsync(filePath).ConfigureAwait(false);

            var decoded = TextEncodingDetector.Decode(bytes);
            if (!decoded.IsDecoded)
            {
                throw CreateEncodingFailureException(decoded, filePath, logger);
            }

            // 障害調査で「どの文字コードとして読んだか」が分からないと、
            // 文字化けの相談を受けたときに切り分けられない（LogDebug は本番で出力されない。Issue #1716）。
            // BOM の有無は表示名（「UTF-8（BOM付き）」等）が示すため、ここで断定しない
            // （UTF-16 / UTF-32 は BOM があるからこそ判別できており、「BOM無し」は事実に反する）
            if (decoded.Encoding != DetectedTextEncoding.Utf8WithBom && decoded.Encoding != DetectedTextEncoding.Utf8)
            {
                logger?.LogInformation(
                    "CSVインポート: 文字コードを {Encoding} と判別しました。File={FilePath}",
                    TextEncodingDetector.GetDisplayName(decoded.Encoding), filePath);
            }

            // StringReader.ReadLine は StreamReader と同じく CR / LF / CRLF のいずれも行区切りとして扱う
            var lines = new List<string>();
            using (var reader = new StringReader(decoded.Text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.Add(line);
                }
            }

            return lines;
        }

        /// <summary>
        /// ファイル全体をバイト列として読み込む
        /// </summary>
        /// <remarks>
        /// FileShare.ReadWrite で開くことで、他プロセス（Excel等）がファイルを使用中でも読み込める。
        /// **長さぶんのバッファを1つだけ確保する** — `MemoryStream` + `ToArray()` は倍々のバッファと
        /// 連続配列の二重確保になり、x86（`PlatformTarget`）の 2GB 空間で数十MB の台帳CSVを
        /// 取り込む際に不要なピークを生む（Issue #1744 コードレビュー指摘）。
        /// 他プロセスが読み取り中にファイルを縮めた場合に備え、実際に読めた長さへ切り詰める。
        /// </remarks>
        private static async Task<byte[]> ReadAllBytesAsync(string filePath)
        {
            using (var fileStream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true))
            {
                var buffer = new byte[fileStream.Length];
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = await fileStream
                        .ReadAsync(buffer, offset, buffer.Length - offset).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    offset += read;
                }

                if (offset == buffer.Length)
                {
                    return buffer;
                }

                var truncated = new byte[offset];
                Buffer.BlockCopy(buffer, 0, truncated, 0, offset);
                return truncated;
            }
        }

        /// <summary>
        /// 文字コードの復号失敗を、ログを残したうえでユーザー向け例外へ変換する
        /// </summary>
        /// <remarks>
        /// **中断こそログが要る**。成功時（Shift_JIS 判別）だけ Information を出して失敗時が無言だと、
        /// 「インポートできない」という問い合わせに対してログから何も分からない
        /// （Issue #1744 コードレビュー指摘）。
        /// <para>
        /// Issue #1991 以降、呼び出し元の共通ハンドラー（<see cref="LogImportFailure"/>）も
        /// 同じ失敗を Warning で 1 行残す。ここでは<b>判別に用いた文字コードとファイル</b>を、
        /// 向こうでは<b>どの取り込みだったか</b>を記録しており、片方だけでは切り分けられないため
        /// <see cref="MarkLogged{TException}"/> で抑止せず両方残す
        /// （「二重に記録しない」規約の例外。ここに理由を書く）。
        /// </para>
        /// </remarks>
        private static FileOperationException CreateEncodingFailureException(
            TextDecodeResult decoded, string filePath, ILogger logger)
        {
            if (decoded.Failure == TextDecodeFailure.DeclaredEncodingUnreadable)
            {
                var encodingName = TextEncodingDetector.GetDisplayName(decoded.Encoding);
                logger?.LogWarning(
                    "CSVインポートを中断: BOM は {Encoding} を示していますが、その文字コードとして読み取れないデータが含まれています（破損・切り詰めの可能性）。File={FilePath}",
                    encodingName, filePath);
                return FileOperationException.UnreadableDeclaredEncoding(encodingName, filePath);
            }

            logger?.LogWarning(
                "CSVインポートを中断: 文字コードを判別できません（UTF-8・Shift_JIS のいずれとしても復号できませんでした）。File={FilePath}",
                filePath);
            return FileOperationException.UndecidableEncoding(filePath);
        }

        #region 共通処理基盤

        /// <summary>
        /// CSVインポート処理を標準的な例外ハンドリングで実行
        /// </summary>
        /// <param name="operation">実行する処理</param>
        /// <param name="errors">エラーリスト（処理中にエラーが追加される場合に使用）</param>
        /// <param name="operationName">
        /// ユーザー視点の操作名（Issue #1991）。エラー文言の「何が」部分とログの識別子に用いる。
        /// <b>既定値を持たせない</b> — 省略できると全経路が同じ汎用名のままになり、
        /// 職員一覧・カード一覧・明細のどれが失敗したのか文言から区別できなくなる（#1956 / #1820）。
        /// </param>
        /// <returns>インポート結果</returns>
        private async Task<CsvImportResult> ExecuteImportWithErrorHandlingAsync(
            Func<Task<CsvImportResult>> operation,
            List<CsvImportError> errors,
            string operationName)
        {
            errors ??= new List<CsvImportError>();

            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ErrorMessage = ToUserFacingErrorMessage(ex, operationName),
                    Errors = errors
                };
            }
        }

        /// <summary>
        /// インポートのトランザクションをロールバックする（ロールバック自体の失敗を外へ漏らさない）
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>ロールバックは失敗し得る</b>。<c>COMMIT</c> が SQLITE_BUSY 等で失敗すると
        /// SQLite 側は既に自動ロールバック済みで <c>SQLiteTransaction</c> が無効化されており、
        /// 続けて <c>Rollback()</c> を呼ぶと <see cref="InvalidOperationException"/>
        /// （"No transaction is active on this connection"）になる。接続断でも同様に失敗する。
        /// </para>
        /// <para>
        /// <c>catch</c> の中で素の <c>scope.Rollback()</c> を呼ぶと、この二次例外が
        /// <b>本来の失敗要因を置き換えて</b> 抜けてしまい、(1) その catch に書いた
        /// <c>LogError</c> が実行されず障害調査の手掛かりが残らない、(2)
        /// <see cref="DatabaseException"/> へのラップも飛ばされるため
        /// <see cref="ToUserFacingErrorMessage"/> の <c>default</c> 分岐に落ちて
        /// 生の英語メッセージが UI へ出る（Issue #1614 違反）、という二重の害がある。
        /// </para>
        /// <para>
        /// 握りつぶしても<b>データが確定することはない</b>。書き込みが確定するのは
        /// <c>Commit()</c> が成功したときだけで、未コミットのトランザクションは
        /// <c>TransactionScope.Dispose()</c>（＝接続リースの解放）で必ず巻き戻る。
        /// </para>
        /// </remarks>
        /// <param name="scope">巻き戻すトランザクションスコープ</param>
        private void TryRollbackImportTransaction(TransactionScope scope)
        {
            // Issue #1831: 巻き戻しの手段は SafeRollback へ寄せる（クラスごとに同じヘルパーを
            // 増やすと、次に規約を変える人が一部を取りこぼす）
            SafeRollback.TryRollback(() => scope.Rollback(), _logger, "CSVインポート");
        }

        /// <summary>
        /// インポート／プレビューで捕捉した例外をユーザー向けの文言へ変換する
        /// </summary>
        /// <remarks>
        /// <para>
        /// **例外 → 文言の対応表はここ 1 か所に置く**（Issue #1744 コードレビュー指摘）。
        /// 従来はこの ladder が共通ハンドラー 2 つと利用履歴の Import / Preview に計 4 回
        /// 書き写されており、Issue #1744 の <see cref="FileOperationException"/> 追加でも
        /// 4 か所すべてに同じ catch を足す必要があった。**次に対応表を変える人が
        /// 利用履歴経路を取りこぼす**（＝この Issue が直したのと同じ形の欠陥）ため集約する。
        /// </para>
        /// <para>
        /// <see cref="AppException"/>（<see cref="FileOperationException"/> /
        /// <see cref="DatabaseException"/> 等）は整備済みの <see cref="AppException.UserFriendlyMessage"/>
        /// を使い、生の <c>ex.Message</c> を UI へ出さない（Issue #1614）。
        /// </para>
        /// </remarks>
        private string ToUserFacingErrorMessage(Exception ex, string operation)
        {
            // Issue #1991: 是正前は IOException / default の 2 分岐が生の ex.Message を返しており、
            // それが技術的詳細の**唯一の出口**だった（この catch 群はログを出していない）。
            // 文言だけ差し替えると失敗の原因がどこにも残らないため、ログと対で行う（#1817）。
            LogImportFailure(ex, operation);

            switch (ex)
            {
                // 「何が」は経路ごとの操作名で述べる（#1956 / #1820）。
                // ここを付け忘れると、最も起きやすい失敗（ファイルの選び間違い）でこそ
                // カード／職員／利用履歴／明細のどれが失敗したのか分からない
                // ― 操作名を必須引数にした意味が無くなる（コードレビューで検出）。
                case FileNotFoundException _:
                    return $"{ExceptionMessageFormatter.DescribeOperation(operation)}に失敗しました。指定されたファイルが見つかりません。"
                           + "ファイルが移動・削除されていないか確認し、もう一度選び直してください。";
                case UnauthorizedAccessException _:
                    return $"{ExceptionMessageFormatter.DescribeOperation(operation)}に失敗しました。ファイルへのアクセス権限がありません。"
                           + "ファイルの読み取り権限を確認するか、管理者に連絡してください。";
                case AppException appException:
                    return appException.UserFriendlyMessage;
                default:
                    // 対応表は ExceptionMessageFormatter ただ 1 つに寄せる（#1744）。
                    // IOException の「他のプログラムで開かれていないか確認し」は、Excel で開いたままの
                    // CSV を取り込もうとした場合そのまま実行できる行動指示であり、この経路に適合する
                    // （#1817「寄せる前に、その分岐で取れる行動が実際に実行できるかを確認する」）。
                    // SQLiteException（共有モードの Busy / Locked）は #1986 で新設した分岐が名指しする。
                    return ExceptionMessageFormatter.ToUserMessage(ex, operation);
            }
        }

        /// <summary>
        /// 取込・プレビューの失敗の技術的詳細をログへ残す（Issue #1991）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>ILogger</c> は省略可能な注入（Issue #1282）のため、未注入時は
        /// <see cref="ErrorDialogHelper.LogException"/> の既存ファイルログ機構へ委譲する
        /// （ロガーが無いことを「ログを出さない理由」にしない。<c>development-conventions.md</c> #1819）。
        /// </para>
        /// <para>
        /// <b>レベルは「システムの不具合か、利用者の入力の問題か」で分ける</b>
        /// （<c>development-conventions.md</c> #1716「障害調査でこの行が無いと困るか」）。
        /// ファイルが見つからない・権限が無い・文字コードが読めない
        /// （<see cref="FileOperationException"/>）・入力値が不正（<see cref="ValidationException"/>）は
        /// 職員が選び直せば解決する<b>想定内の失敗</b>で、その都度 <c>Error</c> を積むと
        /// 本当の不具合が埋もれる。痕跡は残しつつ <c>Warning</c> にする（本番のログには出力される）。
        /// </para>
        /// <para>
        /// <b>「<see cref="AppException"/> なら想定内」とは数えない</b>（コードレビューで検出）。
        /// <see cref="DatabaseException"/> / <see cref="DatabaseVersionMismatchException"/> も
        /// <see cref="AppException"/> の派生であり、共有モードのロック競合や保存値の破損といった
        /// <b>システムの不具合</b>である。想定内の側は「職員が入力を選び直せば解決するか」で列挙する。
        /// </para>
        /// </remarks>
        private void LogImportFailure(Exception ex, string operation)
        {
            // 二重に記録しない（#1817）。カード／職員／利用履歴の各インポートは
            // トランザクション内の catch が「ロールバックより先に」ログを書いてから再スローする
            // （#1745。TryRollbackImportTransaction の remarks を参照）ため、
            // その例外がここへ到達した時点で痕跡は既に残っている。
            if (WasAlreadyLogged(ex))
            {
                return;
            }

            // 「想定内」は例外の基底型ではなく「職員が選び直せば解決するか」で列挙する。
            var isExpectedInputProblem =
                ex is FileOperationException
                || ex is ValidationException
                || ex is FileNotFoundException
                || ex is UnauthorizedAccessException;

            WriteImportFailureLog(
                ex,
                operation,
                isExpectedInputProblem ? LogLevel.Warning : LogLevel.Error);
        }

        /// <summary>
        /// 取込の失敗をログへ書く唯一の手段（Issue #1991）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b><c>MarkLogged</c> を付けてよいのは、実際に書けたときだけ</b>（コードレビューで検出）。
        /// トランザクション内の <c>catch</c> は <c>_logger?.LogError(...)</c> で書いていたため、
        /// ロガー未注入のオーバーロード（<see cref="CsvImportService"/> の 7 引数版）では
        /// <b>1 行も書かないまま「記録済み」の印だけが付き</b>、
        /// <see cref="LogImportFailure"/> が <see cref="ErrorDialogHelper.LogException"/> への
        /// フォールバック（#1819）ごと飛ばしていた ― 是正前（生の <c>ex.Message</c> が
        /// 少なくとも UI へ出ていた）より痕跡が減る。書き込みをこのメソッドへ寄せ、
        /// ロガーの有無にかかわらず必ずどちらかの出口へ届くようにする（#1763）。
        /// </para>
        /// </remarks>
        private void WriteImportFailureLog(Exception ex, string operation, LogLevel level)
        {
            if (_logger != null)
            {
                _logger.Log(level, ex, "CSV import failed: {Operation}", operation);
            }
            else
            {
                // フォールバックは水準を保持できない（ErrorDialogHelper は常に ERROR で書く）。
                // 本番の DI は 8 引数のコンストラクタを選ぶため、ここを通るのは
                // ロガーを渡さない 7 引数版（Moq プロキシ互換のために残している）だけ。
                ErrorDialogHelper.LogException(ex, operation);
            }
        }

        /// <summary>
        /// トランザクション内の失敗を、ロールバックより先にログへ残す（Issue #1745 / #1991）。
        /// </summary>
        /// <remarks>
        /// 呼び出し側は直後に <see cref="MarkLogged{TException}"/> で「記録済み」の印を付けて再スローする。
        /// その印が嘘にならないよう、書き込みは必ず <see cref="WriteImportFailureLog"/> を通す
        /// （ロガー未注入でも <see cref="ErrorDialogHelper.LogException"/> へ落ちる）。
        /// </remarks>
        /// <param name="ex">捕捉した例外</param>
        /// <param name="operation">どのインポートのどの局面で落ちたか（ログの識別子）</param>
        private void LogImportTransactionFailure(Exception ex, string operation) =>
            WriteImportFailureLog(ex, operation, LogLevel.Error);

        /// <summary>
        /// 「この例外は既にログへ記録済み」という印を付ける（Issue #1991）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 印は<b>例外インスタンス自身</b>（<see cref="Exception.Data"/>）へ置く。
        /// サービスのフィールドに持つと、シングルトンである本サービスで
        /// 別のインポートが並走したときに互いの印を見てしまう。
        /// </para>
        /// <para>
        /// <b>ラップするときは新しい例外の側へ付ける</b>。<c>SQLiteException</c> を
        /// <see cref="DatabaseException.QueryFailed"/> で包む経路では、
        /// 外側の <c>catch</c> へ届くのは包んだ側であり、元の例外の印は見られない。
        /// </para>
        /// <para>
        /// 再スローは必ず <c>throw;</c> で行う（<c>throw ex;</c> はスタックトレースを消す）。
        /// そのため本メソッドは戻り値を使わず<b>文として</b>呼ぶ形も許す。
        /// </para>
        /// </remarks>
        /// <typeparam name="TException">例外の型</typeparam>
        /// <param name="exception">印を付ける例外</param>
        /// <returns>引数と同じ例外（<c>throw MarkLogged(...)</c> と書けるようにするため）</returns>
        private static TException MarkLogged<TException>(TException exception)
            where TException : Exception
        {
            // Exception.Data は一部の例外で null／読み取り専用になり得る（リモーティング等）。
            // 印を付けられないときは「未記録」として扱う（多く出るほうへ倒す ―
            // 痕跡が残らないより二重に残るほうが障害調査では害が小さい）。
            //
            // 本メソッドは `catch` の中から呼ばれる（`throw MarkLogged(...)` / `MarkLogged(ex); throw;`）。
            // ここで二次例外が出ると**本来の失敗要因を置き換えて抜け**、
            // DatabaseException へのラップも、`DbContext.ExecuteWithRetryAsync` の
            // `when (ex.ResultCode == Busy || Locked)` によるリトライも丸ごと外れる
            // （development-conventions.md #1745「catch の中の後始末は、それ自体が失敗し得ることを前提に書く」）。
            try
            {
                if (exception?.Data != null && !exception.Data.IsReadOnly)
                {
                    exception.Data[LoggedMarkerKey] = true;
                }
            }
            catch (Exception)
            {
                // 印が付かないだけ（＝二重に記録される）で、データにも制御フローにも影響しない。
            }

            return exception;
        }

        /// <summary>この例外が既にログへ記録済みか。</summary>
        private static bool WasAlreadyLogged(Exception exception) =>
            exception?.Data != null && exception.Data.Contains(LoggedMarkerKey);

        /// <summary><see cref="MarkLogged{TException}"/> が使う印のキー。</summary>
        private const string LoggedMarkerKey = "ICCardManager.CsvImport.AlreadyLogged";

        /// <summary>
        /// CSVプレビュー処理を標準的な例外ハンドリングで実行
        /// </summary>
        /// <param name="operation">実行する処理</param>
        /// <param name="errors">エラーリスト（処理中にエラーが追加される場合に使用）</param>
        /// <param name="operationName">
        /// ユーザー視点の操作名（Issue #1991）。エラー文言の「何が」部分とログの識別子に用いる。
        /// <b>既定値を持たせない</b>（理由は <see cref="ExecuteImportWithErrorHandlingAsync"/> を参照）。
        /// </param>
        /// <returns>プレビュー結果</returns>
        private async Task<CsvImportPreviewResult> ExecutePreviewWithErrorHandlingAsync(
            Func<Task<CsvImportPreviewResult>> operation,
            List<CsvImportError> errors,
            string operationName)
        {
            errors ??= new List<CsvImportError>();

            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new CsvImportPreviewResult
                {
                    IsValid = false,
                    ErrorMessage = ToUserFacingErrorMessage(ex, operationName),
                    Errors = errors
                };
            }
        }

        /// <summary>
        /// IDmのバリデーションを実行し、エラーがあればリストに追加
        /// </summary>
        /// <param name="idm">検証するIDm</param>
        /// <param name="lineNumber">行番号</param>
        /// <param name="fieldName">フィールド名（エラーメッセージ用）</param>
        /// <param name="line">元の行データ</param>
        /// <param name="errors">エラーリスト</param>
        /// <param name="isStaff">職員IDmかどうか</param>
        /// <returns>バリデーション成功の場合true</returns>
        private bool ValidateIdm(
            string idm,
            int lineNumber,
            string fieldName,
            string line,
            List<CsvImportError> errors,
            bool isStaff = false)
        {
            if (string.IsNullOrWhiteSpace(idm))
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = $"{fieldName}は必須です",
                    Data = line
                });
                return false;
            }

            var validation = isStaff
                ? _validationService.ValidateStaffIdm(idm)
                : _validationService.ValidateCardIdm(idm);

            if (!validation.IsValid)
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = validation.ErrorMessage ?? $"{fieldName}の形式が不正です",
                    Data = idm
                });
                return false;
            }

            return true;
        }

        /// <summary>
        /// 必須フィールドのバリデーションを実行し、エラーがあればリストに追加
        /// </summary>
        /// <param name="value">検証する値</param>
        /// <param name="lineNumber">行番号</param>
        /// <param name="fieldName">フィールド名</param>
        /// <param name="line">元の行データ</param>
        /// <param name="errors">エラーリスト</param>
        /// <returns>バリデーション成功の場合true</returns>
        private static bool ValidateRequired(
            string value,
            int lineNumber,
            string fieldName,
            string line,
            List<CsvImportError> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = $"{fieldName}は必須です",
                    Data = line
                });
                return false;
            }
            return true;
        }

        /// <summary>
        /// CSV行の列数をバリデーション
        /// </summary>
        /// <param name="fields">パースされたフィールド</param>
        /// <param name="minColumns">最低列数</param>
        /// <param name="lineNumber">行番号</param>
        /// <param name="line">元の行データ</param>
        /// <param name="errors">エラーリスト</param>
        /// <returns>バリデーション成功の場合true</returns>
        private static bool ValidateColumnCount(
            List<string> fields,
            int minColumns,
            int lineNumber,
            string line,
            List<CsvImportError> errors)
        {
            if (fields.Count < minColumns)
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = "列数が不足しています",
                    Data = line
                });
                return false;
            }
            return true;
        }

        /// <summary>
        /// CSV のテキスト列を読み取り、DB へ保存する自然な値へ正規化する（Issue #1808）。
        /// </summary>
        /// <param name="fields">パース済みのフィールド</param>
        /// <param name="index">列インデックス（範囲外なら空文字列）</param>
        /// <returns>前後の空白を除き、エクスポート由来の先頭 <c>'</c> を取り除いた値</returns>
        /// <remarks>
        /// <para>
        /// <see cref="CsvExportService"/> は式インジェクション対策（Issue #1267）として全テキスト列に
        /// <see cref="Infrastructure.Security.FormulaInjectionSanitizer.Sanitize"/> を適用し、
        /// <c>=</c> / <c>+</c> / <c>-</c> / <c>@</c> 等で始まる値の先頭に <c>'</c> を付ける。
        /// 取り込み側が同じ <c>Sanitize</c> を掛けていた頃は、UI から入力した <c>-異動予定</c> が
        /// エクスポート→再取り込みで <c>'-異動予定</c> に恒久変化し、管理者マニュアル §5.6.5 が
        /// 推奨する「エクスポート CSV を編集して取り込む」運用がそのまま汚染経路になっていた。
        /// </para>
        /// <para>
        /// 取り込み側では <see cref="Infrastructure.Security.FormulaInjectionSanitizer.Unsanitize"/> で
        /// <c>'</c> を取り除き、DB には UI 入力と同じ自然な値を保存する。式インジェクションの防御は
        /// sink 側（CSV／Excel エクスポート・帳票・操作ログ・ダッシュボード出力）が全て自前で
        /// <c>Sanitize</c> しており、UI 入力は元々サニタイズしないため、取り込み側の <c>Sanitize</c> は
        /// 実効防御ではなく往復非対称の原因でしかなかった。
        /// </para>
        /// <para>
        /// エクスポートが全テキスト列を <c>Sanitize</c> する以上、取り込みも<b>備考だけでなく
        /// エクスポート対象のテキスト列すべて</b>で本メソッドを使うこと（往復対称性）。
        /// </para>
        /// </remarks>
        internal static string ReadTextField(List<string> fields, int index)
        {
            if (index < 0 || index >= fields.Count)
            {
                return string.Empty;
            }

            return Infrastructure.Security.FormulaInjectionSanitizer.Unsanitize(fields[index].Trim());
        }

        /// <summary>
        /// 任意入力のテキスト列（職員番号・備考など）を、保存時と同じ規則で正規化する
        /// （空白のみ・空文字は null）。保存時の <c>Number = …</c> / <c>Note = …</c> と
        /// <c>Detect*Changes</c> の比較で同じ式を使うため（Issue #1808 の「幻の差分」再発防止）。
        /// </summary>
        internal static string NormalizeOptionalText(string value)
            => string.IsNullOrWhiteSpace(value) ? null : value;

        /// <summary>
        /// 任意入力のテキスト列を <see cref="NormalizeOptionalText"/> で揃えて比較し、
        /// 差があれば <see cref="FieldChange"/> を追加する（Issue #1370 / #1808）。
        /// </summary>
        private static void AddOptionalTextChangeIfDiffers(
            string fieldName, string existingValue, string newValue, List<FieldChange> changes)
        {
            var existing = NormalizeOptionalText(existingValue);
            var incoming = NormalizeOptionalText(newValue);
            if (existing != incoming)
            {
                changes.Add(new FieldChange
                {
                    FieldName = fieldName,
                    OldValue = existing ?? "(なし)",
                    NewValue = incoming ?? "(なし)"
                });
            }
        }

        /// <summary>
        /// CSV行をパースし、フィールドのリストとして返す（ダブルクォート対応）
        /// </summary>
        /// <param name="line">CSV行文字列</param>
        private static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var currentField = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        // エスケープされたダブルクォート
                        currentField.Append('"');
                        i++; // 次の文字をスキップ
                    }
                    else
                    {
                        // クォートの開始/終了
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    // フィールドの区切り
                    fields.Add(currentField.ToString());
                    currentField.Clear();
                }
                else
                {
                    currentField.Append(c);
                }
            }

            // 最後のフィールドを追加
            fields.Add(currentField.ToString());

            return fields;
        }

        #endregion
    }
}
