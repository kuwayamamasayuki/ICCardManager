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
        private readonly ILogger<CsvImportService>? _logger;

        /// <summary>
        /// 6 引数オーバーロード（既存テストの Moq プロキシ生成互換性のため維持）
        /// </summary>
        public CsvImportService(
            ICardRepository cardRepository,
            IStaffRepository staffRepository,
            ILedgerRepository ledgerRepository,
            IValidationService validationService,
            DbContext dbContext,
            ICacheService cacheService)
            : this(cardRepository, staffRepository, ledgerRepository, validationService, dbContext, cacheService, logger: null)
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
            ILogger<CsvImportService>? logger)
        {
            _cardRepository = cardRepository;
            _staffRepository = staffRepository;
            _ledgerRepository = ledgerRepository;
            _validationService = validationService;
            _dbContext = dbContext;
            _cacheService = cacheService;
            _logger = logger;
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
        /// （呼び出し元の catch は結果オブジェクトへ文言を写すだけで、ログは書かない。Issue #1744 コードレビュー指摘）。
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
        /// <returns>インポート結果</returns>
        private async Task<CsvImportResult> ExecuteImportWithErrorHandlingAsync(
            Func<Task<CsvImportResult>> operation,
            List<CsvImportError> errors = null)
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
                    ErrorMessage = ToUserFacingErrorMessage(ex),
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
            try
            {
                scope.Rollback();
            }
            catch (Exception rollbackException)
            {
                // 本来の失敗要因は呼び出し元の catch が既にログ済み。ここは補足情報として残す
                // （LogDebug は本番で出力されないため Warning。development-conventions.md 参照）
                _logger?.LogWarning(rollbackException,
                    "インポートのロールバックに失敗（未コミットのためデータは確定しない）");
            }
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
        private static string ToUserFacingErrorMessage(Exception ex)
        {
            switch (ex)
            {
                case FileNotFoundException _:
                    return "指定されたファイルが見つかりません。";
                case UnauthorizedAccessException _:
                    return "ファイルへのアクセス権限がありません。";
                case AppException appException:
                    return appException.UserFriendlyMessage;
                case IOException _:
                    return $"ファイルの読み込みエラー: {ex.Message}";
                default:
                    return $"予期しないエラーが発生しました: {ex.Message}";
            }
        }

        /// <summary>
        /// CSVプレビュー処理を標準的な例外ハンドリングで実行
        /// </summary>
        /// <param name="operation">実行する処理</param>
        /// <param name="errors">エラーリスト（処理中にエラーが追加される場合に使用）</param>
        /// <returns>プレビュー結果</returns>
        private async Task<CsvImportPreviewResult> ExecutePreviewWithErrorHandlingAsync(
            Func<Task<CsvImportPreviewResult>> operation,
            List<CsvImportError> errors = null)
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
                    ErrorMessage = ToUserFacingErrorMessage(ex),
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
