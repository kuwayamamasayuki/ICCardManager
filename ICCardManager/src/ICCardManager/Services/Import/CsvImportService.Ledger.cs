#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services.Import;
using ICCardManager.Services.Import.Parsers;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
    public partial class CsvImportService
    {
        // === 利用履歴CSVインポート・プレビュー ===

        public virtual async Task<CsvImportResult> ImportLedgersAsync(string filePath, bool skipExisting = true, string? targetCardIdm = null)
        {
            var errors = new List<CsvImportError>();
            var importedCount = 0;
            var skippedCount = 0;
            var updatedCount = 0;

            try
            {
                var lines = await ReadCsvFileAsync(filePath, _logger).ConfigureAwait(false);
                if (lines.Count < 2)
                {
                    return new CsvImportResult
                    {
                        Success = false,
                        ErrorMessage = "CSVファイルにデータがありません（ヘッダー行のみ）"
                    };
                }

                // ヘッダー行を解析してID列の有無を判定
                var headerFields = ParseCsvLine(lines[0]);
                var hasIdColumn = headerFields.Count > 0 && headerFields[0].Trim().Equals("ID", StringComparison.OrdinalIgnoreCase);
                var minColumns = hasIdColumn ? 10 : 9;

                // バリデーションパス: まず全データをバリデーション
                // IsUpdate: 既存レコードを更新する場合true
                var validRecords = new List<(int LineNumber, Ledger Ledger, bool IsUpdate)>();
                // Issue #754: 残高整合性チェック用に全レコードを保持（スキップ分を含む）
                var allRecordsForValidation = new List<(int LineNumber, Ledger Ledger, bool IsUpdate)>();
                var existingCardIdms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 全カードのIDmをキャッシュ（パフォーマンス向上）
                var allCards = (await _cardRepository.GetAllIncludingDeletedAsync().ConfigureAwait(false)).ToList();
                foreach (var card in allCards)
                {
                    existingCardIdms.Add(card.CardIdm);
                }

                // Issue #2307: 既存履歴の重複キーをカードごとに 1 回だけ読む。貸出中の行の判定（下のループ）と
                // 新規分の重複チェック（取り込みの段）が同じ読み取り結果を使う
                var existingKeysByCard = new Dictionary<string, HashSet<(string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance)>>(StringComparer.OrdinalIgnoreCase);
                async Task<HashSet<(string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance)>> GetExistingKeysForCardAsync(string cardIdm)
                {
                    if (!existingKeysByCard.TryGetValue(cardIdm, out var keys))
                    {
                        // 検証の段（行ごとのループ）で読むため、この読み取りが例外になると、行エラー・残高の不一致の一覧の代わりに
                        // 例外の文言（メソッド末尾の catch）だけが出る。読み取りの失敗は取り込みを続けられない状態なので、それでよい
                        keys = await _ledgerRepository.GetExistingLedgerKeysAsync(new[] { cardIdm }).ConfigureAwait(false);
                        existingKeysByCard[cardIdm] = keys;
                    }

                    return keys;
                }

                for (var i = 1; i < lines.Count; i++)
                {
                    var lineNumber = i + 1;
                    var line = lines[i];

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var fields = ParseCsvLine(line);
                    var parsed = LedgerCsvRowParser.TryParseRow(
                        fields, lineNumber, line, hasIdColumn, minColumns,
                        existingCardIdms, targetCardIdm, errors);
                    if (parsed == null)
                    {
                        continue;
                    }

                    // 既存レコードの確認（IDがある場合）
                    var isUpdate = false;
                    Ledger? existingLedgerForUpdate = null;
                    var existingLedger = parsed.LedgerId.HasValue
                        ? await _ledgerRepository.GetByIdAsync(parsed.LedgerId.Value).ConfigureAwait(false)
                        : null;
                    if (parsed.LedgerId.HasValue && existingLedger != null)
                    {
                        // Issue #2262: ID が指す履歴と行のカードが食い違う行は取り込まない（下記 BuildLedgerCardMismatchError）
                        var cardMismatch = BuildLedgerCardMismatchError(lineNumber, parsed.LedgerId.Value, existingLedger.CardIdm, parsed.CardIdm, allCards,
                            LedgerCsvCardColumnLabel);
                        if (cardMismatch != null)
                        {
                            errors.Add(cardMismatch);
                            continue;
                        }
                    }

                    // 変更の検出はプレビューと同じ関数で行う（経路ごとに別実装だと、貸出中の行を拒否するかが経路で食い違う。Issue #2307）
                    var detectedChanges = new List<FieldChange>();
                    if (existingLedger != null)
                    {
                        DetectLedgerChanges(existingLedger, parsed.Date, parsed.Summary, parsed.Income, parsed.Expense, parsed.Balance,
                            parsed.StaffName, parsed.Note, parsed.CompanionCount, detectedChanges);
                    }

                    var hasChanges = detectedChanges.Count > 0;
                    var duplicateKeyExists = existingLedger == null && skipExisting &&
                        (await GetExistingKeysForCardAsync(parsed.CardIdm).ConfigureAwait(false))
                            .Contains((parsed.CardIdm, parsed.Date, parsed.Summary, parsed.Income, parsed.Expense, parsed.Balance));

                    // Issue #2307: 貸出中レコードを写した行は、通常の行として取り込まない（LedgerCsvLentRowPolicy の remarks）。
                    // 台帳を変えない行（既存の行と全項目一致・ID の無い行で重複キーが一致してスキップ）は拒否しない
                    var lentRowError = LedgerCsvLentRowPolicy.BuildError(lineNumber, parsed.Summary, existingLedger,
                        leavesLedgerUnchanged: LeavesLedgerUnchanged(existingLedger, hasChanges, duplicateKeyExists));
                    if (lentRowError != null)
                    {
                        errors.Add(lentRowError);
                        continue;
                    }

                    // Issue #2324: 利用履歴 ID が台帳に無い行（削除・統合した行）を新しい履歴として戻さない（BuildLedgerIdNotFoundError の remarks）
                    var idNotFoundError = BuildLedgerIdNotFoundError(lineNumber, parsed.LedgerId, existingLedger, duplicateKeyExists);
                    if (idNotFoundError != null)
                    {
                        errors.Add(idNotFoundError);
                        continue;
                    }

                    if (parsed.LedgerId.HasValue)
                    {
                        if (existingLedger != null)
                        {
                            if (hasChanges)
                            {
                                isUpdate = true;
                                existingLedgerForUpdate = existingLedger;
                            }
                            else if (skipExisting)
                            {
                                var skippedLedger = new Ledger
                                {
                                    Id = parsed.LedgerId.Value,
                                    CardIdm = parsed.CardIdm,
                                    Date = parsed.Date,
                                    Summary = parsed.Summary,
                                    Income = parsed.Income,
                                    Expense = parsed.Expense,
                                    Balance = parsed.Balance
                                };
                                allRecordsForValidation.Add((lineNumber, skippedLedger, false));
                                skippedCount++;
                                continue;
                            }
                            else
                            {
                                isUpdate = true;
                                existingLedgerForUpdate = existingLedger;
                            }
                        }
                    }

                    var ledger = new Ledger
                    {
                        Id = parsed.LedgerId ?? 0,
                        CardIdm = parsed.CardIdm,
                        Date = parsed.Date,
                        Summary = parsed.Summary,
                        Income = parsed.Income,
                        Expense = parsed.Expense,
                        Balance = parsed.Balance,
                        StaffName = string.IsNullOrWhiteSpace(parsed.StaffName) ? null : parsed.StaffName,
                        Note = string.IsNullOrWhiteSpace(parsed.Note) ? null : parsed.Note,
                        // Issue #1906: 列が無い旧形式 CSV では既存値を引き継ぐ（0 で上書きしない。#1726 / #1808）
                        CompanionCount = parsed.CompanionCount ?? existingLedgerForUpdate?.CompanionCount ?? 0,
                        LenderIdm = existingLedgerForUpdate?.LenderIdm,
                        ReturnerIdm = existingLedgerForUpdate?.ReturnerIdm,
                        LentAt = existingLedgerForUpdate?.LentAt,
                        ReturnedAt = existingLedgerForUpdate?.ReturnedAt,
                        IsLentRecord = existingLedgerForUpdate?.IsLentRecord ?? false
                    };

                    validRecords.Add((lineNumber, ledger, isUpdate));
                    allRecordsForValidation.Add((lineNumber, ledger, isUpdate));
                }

                // Issue #907: カードごとにDB上の直前残高を取得（最初の行の整合性チェック用）
                var previousBalanceByCard = await GetPreviousBalanceByCardAsync(allRecordsForValidation
                    .GroupBy(r => r.Ledger.CardIdm, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Min(r => r.Ledger.Date))).ConfigureAwait(false);

                // Issue #754: 残高整合性チェックはスキップ分を含む全レコードで実施
                // （スキップされたレコードを除外すると前後関係が崩れ、誤った前回残高でエラーになる）
                // Issue #907: 最初の行もDB上の直前残高と照合
                ValidateBalanceConsistencyForLedgers(allRecordsForValidation, errors, previousBalanceByCard);

                // バリデーションエラーがあれば中断
                if (errors.Count > 0)
                {
                    return new CsvImportResult
                    {
                        Success = false,
                        ImportedCount = 0,
                        SkippedCount = skippedCount,
                        ErrorCount = errors.Count,
                        Errors = errors
                    };
                }

                // レコードが0件の場合
                if (validRecords.Count == 0)
                {
                    return new CsvImportResult
                    {
                        Success = true,
                        ImportedCount = 0,
                        SkippedCount = skippedCount,
                        ErrorCount = 0
                    };
                }

                // Issue #334: 新規追加分のみ既存履歴の重複チェック用キーを取得
                // Issue #903: skipExisting=falseの場合は重複チェックを行わず全レコードを登録する
                var newRecords = validRecords.Where(r => !r.IsUpdate).ToList();
                var uniqueCardIdms = newRecords.Select(r => r.Ledger.CardIdm).Distinct();
                var existingLedgerKeys = new HashSet<(string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance)>();
                if (skipExisting)
                {
                    foreach (var cardIdm in uniqueCardIdms)
                    {
                        existingLedgerKeys.UnionWith(await GetExistingKeysForCardAsync(cardIdm).ConfigureAwait(false));
                    }
                }

                // Issue #1745: インポート実行はトランザクションで囲み「全か無か」で確定させる。
                // 以前はここだけトランザクションを持たず（カード/職員インポートは Card.cs:157- /
                // Staff.cs:151- で BeginTransactionAsync で囲んでいる）、tx を渡さない
                // InsertAsync(Ledger) / UpdateAsync(Ledger) が autocommit で 1 行ずつ確定していた。
                // 共有モードで他 PC のバックアップ等により途中行が SQLITE_BUSY で失敗すると、
                // それ以前の行だけが台帳に残る。事前の ValidateBalanceConsistencyForLedgers は
                // 「全行が入る前提」で残高チェーンを検証しているため、途中で切れた状態は
                // 誰にも検証されないまま確定してしまう。
                //
                // tx は scope.Transaction を明示的に引き渡す（development-conventions.md の「①」経路）。
                // BeginTransactionAsync は SemaphoreSlim(1,1) を取るため、リポジトリ側で入れ子に
                // 開くと自己デッドロックする（Issue #1575）。HasActiveTransactionScope による
                // 暗黙参加（②）は backstop であって正規手段ではない（Issue #1737）。
                using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
                try
                {
                    // 行ごとの try/catch は持たない（カード/職員インポートと同じ形）。
                    // 例外を握って続行すると、ロックが取れない状況では後続行も同じ理由で失敗し
                    // 同一のエラーが行数分だけ並ぶ。加えて ex.Message を UI のエラー一覧へ
                    // 直接載せることになり、生の例外メッセージを出さない規約に反する（Issue #1614）。
                    // 例外はメソッド冒頭の catch へ抜け、ToUserFacingErrorMessage が
                    // 集約済みの対応表で 3 要素文言へ変換する（Issue #1744）。
                    foreach (var (lineNumber, ledger, isUpdate) in validRecords)
                    {
                        if (isUpdate)
                        {
                            // 既存レコードを更新し、変更前・変更後を同じトランザクションで操作ログへ記録する（Issue #2262）
                            var success = await UpdateLedgerWithAuditLogAsync(ledger, scope.Transaction).ConfigureAwait(false);
                            if (success)
                            {
                                updatedCount++;
                            }
                            else
                            {
                                errors.Add(new CsvImportError
                                {
                                    LineNumber = lineNumber,
                                    // Issue #2262 / #1759: false は「対象の行が無い（他の PC や別の操作で削除された）」競合だけ
                                    // （UpdateLedgerWithAuditLogAsync の読み直しが null、または UPDATE が 0 行）。原因を名指しする
                                    Message = ConcurrencyConflictMessage.ForImportUpdate(
                                        $"利用履歴ID {ledger.Id.ToString(CultureInfo.InvariantCulture)} の履歴", "履歴画面"),
                                    Data = ledger.CardIdm
                                });
                            }
                        }
                        else
                        {
                            // 重複チェック: skipExisting=trueの場合、同じ履歴が既に存在すればスキップ
                            var ledgerKey = (ledger.CardIdm, ledger.Date, ledger.Summary, ledger.Income, ledger.Expense, ledger.Balance);
                            if (existingLedgerKeys.Contains(ledgerKey))
                            {
                                skippedCount++;
                                continue;
                            }

                            // 新規登録
                            var id = await _ledgerRepository.InsertAsync(ledger, scope.Transaction).ConfigureAwait(false);
                            if (id > 0)
                            {
                                importedCount++;
                            }
                            else
                            {
                                errors.Add(new CsvImportError
                                {
                                    LineNumber = lineNumber,
                                    // Issue #2324: 何が（この行の履歴）・なぜ（登録できず全体を取り消した）・どうすれば（取り込み直す）
                                    Message = LedgerInsertFailedMessage,
                                    Data = ledger.CardIdm
                                });
                            }
                        }
                    }

                    // すべて成功したらコミット
                    if (errors.Count == 0)
                    {
                        scope.Commit();
                        // カード/職員インポートはここでキャッシュを無効化するが、履歴は行わない。
                        // LedgerRepository は ICacheService を参照しておらず CacheKeys にも
                        // ledger 系のプレフィックスが無いため、無効化すべきキャッシュが存在しない。
                    }
                    else
                    {
                        TryRollbackImportTransaction(scope);
                        // ImportedCount は importedCount + updatedCount のため両方を戻す
                        importedCount = 0;
                        updatedCount = 0;
                    }
                }
                catch (SQLiteException ex)
                {
                    // ログはロールバックより先に書く。ROLLBACK 自体が失敗すると
                    // その二次例外が本来の失敗要因を置き換えて抜け、この LogError も
                    // DatabaseException へのラップも実行されない（TryRollbackImportTransaction の remarks 参照）。
                    // Issue #1282: SQLiteException は DatabaseException へラップして詳細を保持
                    LogImportTransactionFailure(ex,
                        "履歴CSVインポートのトランザクション中に SQLite エラーが発生しロールバック");
                    TryRollbackImportTransaction(scope);
                    throw MarkLogged(DatabaseException.QueryFailed("CSV import transaction", ex));
                }
                catch (Exception ex)
                {
                    // Issue #1282: 想定外の例外も握りつぶさずログに記録してから再スロー
                    LogImportTransactionFailure(ex,
                        "履歴CSVインポートのトランザクション中に想定外の例外が発生しロールバック");
                    TryRollbackImportTransaction(scope);
                    MarkLogged(ex);
                    throw;
                }

                return new CsvImportResult
                {
                    Success = errors.Count == 0,
                    ImportedCount = importedCount + updatedCount,
                    // Issue #2262: 操作ログが追加と更新を分けて記録できるよう、更新件数を別に返す
                    UpdatedCount = updatedCount,
                    SkippedCount = skippedCount,
                    ErrorCount = errors.Count,
                    Errors = errors
                };
            }
            // 例外 → ユーザー向け文言の対応表は ToUserFacingErrorMessage に集約する。
            // ここで ladder を書き写すと、次に対応表を変えたときにこの経路だけ取り残される
            // （Issue #1744 の FileOperationException 追加で実際に4か所へ同じ catch を書く羽目になった）
            catch (Exception ex)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ErrorMessage = ToUserFacingErrorMessage(ex, "利用履歴CSVの取り込み"),
                    Errors = errors
                };
            }
        }

        /// <summary>
        /// 既存の利用履歴 1 行を CSV の内容で更新し、変更前・変更後を同じトランザクションで操作ログへ記録する（Issue #2262）
        /// </summary>
        /// <param name="ledger">更新後の内容（CSV の行から組み立てたもの）</param>
        /// <param name="transaction">取り込み全体のトランザクション</param>
        /// <returns>更新したら <c>true</c>。対象の行が無かった（他 PC や別の操作で削除された）ら <c>false</c></returns>
        /// <remarks>
        /// <para>
        /// 以前は更新しても操作ログを 1 行も残さず、取り込み全体についてファイル名と件数だけを記録していた。
        /// 履歴画面の「変更」（認証あり・変更前後を記録）と同じ台帳の書き換えが、変更前の値も残らない形で行えた。
        /// </para>
        /// <para>
        /// 変更前は<b>このトランザクションの中で読み直した</b>行を使う。検証の段階で読んだ行（<c>existingLedgerForUpdate</c>）は
        /// トランザクションの外で読んだもので、共有モードでは書き込むまでの間に他 PC が書き換え得る。読み直した行は
        /// 更新後の <paramref name="ledger"/> とは別のインスタンスなので、記録する時点で変更後の値を写すこともない
        /// （db-write-conventions.md「監査ログへ渡す『変更前』は、変更対象と別のインスタンスにする」#1959）。
        /// </para>
        /// <para>
        /// 利用履歴 CSV は明細を持たず、この更新は明細に触れない。変更後の記録に明細が無いと、操作ログの画面・Excel が
        /// 「明細 N 件 → なし」という起きていない変更として表示するため、変更後には変更前の明細の複製を載せる。
        /// </para>
        /// <para>
        /// 操作ログの書き込みが失敗したら例外がそのまま抜け、呼び出し元が取り込み全体を巻き戻す
        /// （記録の無い書き換えを残さない。AuditedWriteTransaction と同じ考え方）。
        /// </para>
        /// </remarks>
        private async Task<bool> UpdateLedgerWithAuditLogAsync(Ledger ledger, SQLiteTransaction transaction)
        {
            // DbContext は接続を 1 本しか持たないため、この読み取りは同じ接続上のトランザクションの内側で行われる
            // （CsvImportService.Detail.cs の ReplaceDetailsAndUpdateParentAsync と同じ）
            var before = await _ledgerRepository.GetByIdAsync(ledger.Id).ConfigureAwait(false);
            if (before == null)
            {
                // 読み取りが null なら書き込みも行わない（#1760）。UPDATE も同じ WHERE で 0 行になる
                return false;
            }

            var updated = await _ledgerRepository.UpdateAsync(ledger, transaction).ConfigureAwait(false);
            if (!updated)
            {
                return false;
            }

            // 履歴のカード（card_idm）は UpdateAsync が SET しない列。検証で一致を確かめているが、
            // 変更後の記録には実際に DB にある値を載せる（書いていない値を「変更後」として記録しない）
            ledger.CardIdm = before.CardIdm;
            ledger.Details = LedgerCloner.Clone(before).Details;
            await _operationLogger.LogLedgerUpdateAsync(before, ledger, transaction).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// CSV の行が台帳を変えないか（Issue #2307。取り込みとプレビューが同じ判定で貸出中の行の拒否を決める）
        /// </summary>
        /// <param name="existingLedger">利用履歴 ID が指す既存の行（ID が空欄・見つからないときは <c>null</c>）</param>
        /// <param name="hasChanges">既存の行との差分があるか（<c>DetectLedgerChanges</c> の結果）</param>
        /// <param name="duplicateKeyExists">
        /// 既存の行が無い行について、スキップ指定ありで重複キー（カード・日付・摘要・金額・残額）が既存の履歴と一致するか
        /// （取り込みはこの行をスキップする）
        /// </param>
        /// <remarks>
        /// 既存の行があれば全項目一致（スキップ・無変更の更新）、無ければ重複キーの一致（スキップ）。
        /// 以前の取り込みで通常の行として混入した「（貸出中）」の行と同じ行の往復を止めないため。
        /// </remarks>
        private static bool LeavesLedgerUnchanged(Ledger? existingLedger, bool hasChanges, bool duplicateKeyExists)
            => existingLedger != null ? !hasChanges : duplicateKeyExists;

        /// <summary>
        /// 利用履歴 CSV で新しい履歴の登録（<c>InsertAsync</c>）が失敗値を返したときの行エラーの文言（Issue #2324）
        /// </summary>
        /// <remarks>
        /// 以前は「履歴の登録に失敗しました」だけで、取り込み全体を取り消したことも次の行動も示さなかった。
        /// 画面は「行N: 」を前に付けるので、「何が」は「この行の履歴」で足りる。
        /// 失敗値は待てば解消する失敗を示すとは限らないため、「しばらく待って」ではなく、取り込み直しても繰り返すときの連絡先を示す。
        /// </remarks>
        internal const string LedgerInsertFailedMessage =
            "この行の履歴を台帳に登録できなかったため、取り込み全体を取り消しました（1件も取り込んでいません）。" +
            "同じCSVをプレビューからもう一度取り込み、同じエラーが繰り返し出る場合は管理者（IT担当者）に連絡してください。";

        /// <summary>
        /// 利用履歴 ID が台帳に無い行のエラー（Issue #2324）
        /// </summary>
        /// <param name="lineNumber">CSV の行番号</param>
        /// <param name="ledgerId">行の利用履歴 ID（空欄なら <c>null</c>）</param>
        /// <param name="existingLedger">ID が指す既存の行（見つからなければ <c>null</c>）</param>
        /// <param name="duplicateKeyExists">
        /// スキップ指定ありで重複キー（カード・日付・摘要・金額・残額）が既存の履歴と一致するか（一致すれば取り込みはスキップする）
        /// </param>
        /// <returns>取り込んでよければ <c>null</c></returns>
        /// <remarks>
        /// <para>
        /// 利用履歴 ID は <c>ledger.id</c>（AUTOINCREMENT。削除した値は再利用されない）を写したもので、ID があるのに台帳に
        /// 行が無いのは、その行が<b>台帳から消えた</b>か、<b>別のデータベースで出力した</b>場合: 履歴画面で削除した行、
        /// 統合で他の行にまとめた統合元、返却で削除された貸出中レコード（<see cref="LedgerCsvLentRowPolicy"/> が先に専用の
        /// 文言で拒否する）、別の PC のデータベースから出力した CSV の行、バックアップからのリストアより前に出力した CSV の行
        /// （リストアで <c>sqlite_sequence</c> も巻き戻るため、リストア後に作られていない ID は台帳に無い）。以前はこれらを新しい履歴として登録しており、昔の CSV を取り込み直すと、
        /// 削除・統合で消した行が別の ID で台帳に戻った（統合元は統合先と二重計上になる）。残高の検証は CSV の中のつながりと
        /// 最初の行の直前残高しか見ないため、この復活は検証を通っていた。
        /// </para>
        /// <para>
        /// 台帳を変えない行は拒否しない（#2307 の <see cref="LeavesLedgerUnchanged"/> と同じ考え方）: スキップ指定ありで
        /// 重複キーが既存の履歴と一致する行は、取り込みもプレビューもスキップする。新しい履歴として登録したいときは、
        /// 利用履歴 ID を空欄にすれば ID の無い行として扱われ、重複キーの照合を経て登録される（意図して戻す手段を残す）。
        /// 判定は取り込み・プレビューが共通のこの関数で行う。
        /// </para>
        /// </remarks>
        private static CsvImportError? BuildLedgerIdNotFoundError(int lineNumber, int? ledgerId, Ledger? existingLedger, bool duplicateKeyExists)
        {
            if (!ledgerId.HasValue || existingLedger != null || duplicateKeyExists)
            {
                return null;
            }

            var idText = ledgerId.Value.ToString(CultureInfo.InvariantCulture);
            return new CsvImportError
            {
                LineNumber = lineNumber,
                Message = $"利用履歴ID {idText} の履歴は台帳にありません（履歴画面で削除した・統合で他の履歴にまとめた履歴か、" +
                          "別のパソコンやバックアップから戻す前のデータベースで出力したCSVの可能性があります）。" +
                          "削除・統合した履歴を取り込みで台帳に戻さないため、この行は取り込めません。" +
                          "不要な行ならCSVから削除し、新しい履歴として登録する場合は利用履歴IDを空欄にしてから、もう一度取り込んでください。",
                Data = idText
            };
        }

        /// <summary>
        /// 利用履歴 CSV で、行のカードを直す場所（カードIDm列が空欄なら取り込み先カードを使う）
        /// </summary>
        private const string LedgerCsvCardColumnLabel = "カードIDm列（または取り込み先カード）";

        /// <summary>
        /// 利用履歴詳細 CSV で、行のカードを直す場所（取り込み先カードの指定は無い）
        /// </summary>
        private const string DetailCsvCardColumnLabel = "カードIDm列";

        /// <summary>
        /// 利用履歴 ID が指す既存の履歴と、行のカード（カードIDm列、空欄なら取り込み先カード）が食い違うときのエラー（Issue #2262）
        /// </summary>
        /// <returns>一致すれば <c>null</c></returns>
        /// <remarks>
        /// 履歴の更新（<c>LedgerRepository.UpdateAsync</c>）はカード（<c>card_idm</c>）を書き換えない。食い違う行を
        /// 更新として受け付けると、別のカードの履歴の摘要・金額・残額を、そのカードの残高チェーンの検証を経ないまま
        /// 書き換え、操作ログの「変更後」には実際には書いていないカードが載る。カードを付け替える取り込みは
        /// 想定していないため、検証エラーにする（プレビューにも同じエラーを出す）。
        /// IDm は画面に出さない（#1986）。登録済みのカードはカード種別と管理番号で名指しする。
        /// 利用履歴詳細 CSV の明細の置き換えも同じ理由で同じ照合を行う（Issue #2324。<c>CsvImportService.Detail.cs</c>）。
        /// </remarks>
        /// <param name="lineNumber">CSV の行番号</param>
        /// <param name="ledgerId">行の利用履歴 ID</param>
        /// <param name="ledgerCardIdm">ID が指す既存の履歴のカード</param>
        /// <param name="rowCardIdm">行のカード</param>
        /// <param name="cards">登録済みのカード（削除済みを含む。名指しに使う）</param>
        /// <param name="cardColumnLabel">
        /// 行のカードをどこで直すか（利用履歴は <see cref="LedgerCsvCardColumnLabel"/>、利用履歴詳細は <see cref="DetailCsvCardColumnLabel"/>）
        /// </param>
        private static CsvImportError? BuildLedgerCardMismatchError(
            int lineNumber, int ledgerId, string ledgerCardIdm, string rowCardIdm, IEnumerable<IcCard> cards, string cardColumnLabel)
        {
            if (string.Equals(ledgerCardIdm, rowCardIdm, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var owner = cards.FirstOrDefault(c => string.Equals(c.CardIdm, ledgerCardIdm, StringComparison.OrdinalIgnoreCase));
            var ownerLabel = owner != null ? $"「{$"{owner.CardType} {owner.CardNumber}".Trim()}」" : "（登録されていないカード）";
            return new CsvImportError
            {
                LineNumber = lineNumber,
                Message = $"利用履歴ID {ledgerId.ToString(CultureInfo.InvariantCulture)} は別の交通系ICカード{ownerLabel}の履歴で、" +
                          $"この行のカードと一致しないため取り込めません。利用履歴IDと{cardColumnLabel}を確認し、" +
                          "新しい履歴として取り込む場合は利用履歴IDを空欄にしてください。",
                Data = ledgerId.ToString(CultureInfo.InvariantCulture)
            };
        }

        /// <summary>
        /// 履歴CSVのインポートプレビューを取得
        /// </summary>
        /// <param name="filePath">CSVファイルパス</param>
        /// <param name="skipExisting">既存データをスキップするか（falseの場合は更新）</param>
        /// <param name="targetCardIdm">CSV内のIDmが空の場合に使用するカードIDm（オプション）</param>
        /// <remarks>Issue #511: CSVのIDm列が空の場合、targetCardIdmが指定されていればそのIDmを使用</remarks>

        public virtual async Task<CsvImportPreviewResult> PreviewLedgersAsync(string filePath, bool skipExisting = true, string? targetCardIdm = null)
        {
            var errors = new List<CsvImportError>();
            var items = new List<CsvImportPreviewItem>();
            var newCount = 0;
            var updateCount = 0;
            var skipCount = 0;

            try
            {
                var lines = await ReadCsvFileAsync(filePath, _logger).ConfigureAwait(false);
                if (lines.Count < 2)
                {
                    return new CsvImportPreviewResult
                    {
                        IsValid = false,
                        ErrorMessage = "CSVファイルにデータがありません（ヘッダー行のみ）"
                    };
                }

                // ヘッダー行を解析してID列の有無を判定
                var headerFields = ParseCsvLine(lines[0]);
                var hasIdColumn = headerFields.Count > 0 && headerFields[0].Trim().Equals("ID", StringComparison.OrdinalIgnoreCase);
                var minColumns = hasIdColumn ? 10 : 9;

                // 全カードのIDmをキャッシュ
                var existingCardIdms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var cardNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var allCards = await _cardRepository.GetAllIncludingDeletedAsync().ConfigureAwait(false);
                foreach (var card in allCards)
                {
                    existingCardIdms.Add(card.CardIdm);
                    cardNameMap[card.CardIdm] = $"{card.CardType} {card.CardNumber}".Trim();
                }

                // 仮バリデーションでカードIDmを収集（重複チェック用）
                var cardIdmsInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var validatedRecords = new List<(int LineNumber, int? LedgerId, string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance, string StaffName, string Note, int? CompanionCount)>();

                for (var i = 1; i < lines.Count; i++)
                {
                    var lineNumber = i + 1;
                    var line = lines[i];

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var fields = ParseCsvLine(line);
                    var parsed = LedgerCsvRowParser.TryParseRow(
                        fields, lineNumber, line, hasIdColumn, minColumns,
                        existingCardIdms, targetCardIdm, errors);
                    if (parsed == null)
                    {
                        continue;
                    }

                    cardIdmsInFile.Add(parsed.CardIdm);
                    validatedRecords.Add((
                        parsed.LineNumber, parsed.LedgerId, parsed.CardIdm,
                        parsed.Date, parsed.Summary, parsed.Income, parsed.Expense,
                        parsed.Balance, parsed.StaffName, parsed.Note, parsed.CompanionCount));
                }

                // Issue #907: カードごとにDB上の直前残高を取得（最初の行の整合性チェック用）
                var previousBalanceByCard = await GetPreviousBalanceByCardAsync(validatedRecords
                    .GroupBy(r => r.CardIdm, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Min(r => r.Date))).ConfigureAwait(false);

                // Issue #428: 金額の整合性チェック（カードごとに残高の連続性を検証）
                // Issue #907: 最初の行もDB上の直前残高と照合
                // 母集団は取り込み（ImportLedgersAsync）と違う: 取り込みは行エラー（カードの食い違い #2262・貸出中の行 #2307）の行を
                // 除いて検証するが、プレビューはそれらの判定（下のループ）より前にパースできた全行で検証する。
                // 行エラーがあればどちらも取り込まないので結果（取り込むか）は変わらず、違うのは残高の不一致エラーが追加で出るかだけ。
                // 揃えるには行エラーの判定（ID ごとの DB 読み取り）をこの前へ移す必要があり、本件の範囲では揃えない
                ValidateBalanceConsistency(validatedRecords, errors, previousBalanceByCard);

                // Issue #334: 既存履歴の重複チェック用キーを取得（新規追加分のみ）
                // Issue #903: skipExisting=falseの場合は重複チェックを行わない
                var existingLedgerKeys = skipExisting
                    ? await _ledgerRepository.GetExistingLedgerKeysAsync(cardIdmsInFile).ConfigureAwait(false)
                    : new HashSet<(string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance)>();

                // プレビューアイテムを生成
                foreach (var (lineNumber, ledgerId, cardIdm, date, summary, income, expense, balance, staffName, note, companionCount) in validatedRecords)
                {
                    ImportAction action;
                    var changes = new List<FieldChange>();

                    // IDがある場合は既存レコードを検索
                    var existingLedger = ledgerId.HasValue
                        ? await _ledgerRepository.GetByIdAsync(ledgerId.Value).ConfigureAwait(false)
                        : null;

                    // Issue #2262: 取り込みと同じ検証（ID が指す履歴と行のカードの食い違い）をプレビューでも行う
                    if (ledgerId.HasValue && existingLedger != null)
                    {
                        var cardMismatch = BuildLedgerCardMismatchError(lineNumber, ledgerId.Value, existingLedger.CardIdm, cardIdm, allCards,
                            LedgerCsvCardColumnLabel);
                        if (cardMismatch != null)
                        {
                            errors.Add(cardMismatch);
                            continue;
                        }
                    }

                    // Issue #639: 金額・日付を含む全フィールドで変更点を検出
                    if (existingLedger != null)
                    {
                        DetectLedgerChanges(existingLedger, date, summary, income, expense, balance, staffName, note, companionCount, changes);
                    }

                    // Issue #2307: 取り込みと同じ検証（貸出中レコードを写した行）をプレビューでも行う。
                    // 台帳を変えない行は拒否しない（取り込み側と同じ関数で判定する）
                    var duplicateKeyExists = existingLedger == null &&
                        existingLedgerKeys.Contains((cardIdm, date, summary, income, expense, balance));
                    var lentRowError = LedgerCsvLentRowPolicy.BuildError(lineNumber, summary, existingLedger,
                        leavesLedgerUnchanged: LeavesLedgerUnchanged(existingLedger, changes.Count > 0, duplicateKeyExists));
                    if (lentRowError != null)
                    {
                        errors.Add(lentRowError);
                        continue;
                    }

                    // Issue #2324: 取り込みと同じ関数で、利用履歴 ID が台帳に無い行を拒否する
                    var idNotFoundError = BuildLedgerIdNotFoundError(lineNumber, ledgerId, existingLedger, duplicateKeyExists);
                    if (idNotFoundError != null)
                    {
                        errors.Add(idNotFoundError);
                        continue;
                    }

                    if (ledgerId.HasValue)
                    {
                        if (existingLedger != null)
                        {
                            if (changes.Count > 0)
                            {
                                // 変更がある場合は更新
                                action = ImportAction.Update;
                                updateCount++;
                            }
                            else if (skipExisting)
                            {
                                // Issue #903: skipExisting=trueの場合のみスキップ
                                action = ImportAction.Skip;
                                skipCount++;
                                // Issue #969: スキップ時もデータ内容を表示
                                changes = CreateLedgerDisplayChanges(date, summary, income, expense, balance, staffName, note, companionCount);
                            }
                            else
                            {
                                // Issue #903: skipExisting=falseの場合、変更がなくても更新扱い
                                action = ImportAction.Update;
                                updateCount++;
                            }
                        }
                        else
                        {
                            // IDが指定されているがレコードが見つからない行は、スキップ指定ありで重複キーが既存の履歴と一致する
                            // （台帳を変えない）場合だけここへ来る。それ以外は上の BuildLedgerIdNotFoundError で拒否済み（Issue #2324）。
                            // 取り込みと同じくスキップと表示する（Issue #2307。以前は常に「追加」と表示し、取り込みはスキップしていた）
                            changes = CreateLedgerDisplayChanges(date, summary, income, expense, balance, staffName, note, companionCount);
                            action = ImportAction.Skip;
                            skipCount++;
                        }
                    }
                    else
                    {
                        // IDがない場合は従来の重複チェック
                        var ledgerKey = (cardIdm, date, summary, income, expense, balance);
                        if (existingLedgerKeys.Contains(ledgerKey))
                        {
                            action = ImportAction.Skip;
                            skipCount++;
                            // Issue #969: スキップ時もデータ内容を表示
                            changes = CreateLedgerDisplayChanges(date, summary, income, expense, balance, staffName, note, companionCount);
                        }
                        else
                        {
                            action = ImportAction.Insert;
                            newCount++;
                            // Issue #969: 追加時もデータ内容を表示
                            changes = CreateLedgerDisplayChanges(date, summary, income, expense, balance, staffName, note, companionCount);
                        }
                    }

                    // Issue #937: カード名も表示する
                    var cardDisplayIdm = cardNameMap.TryGetValue(cardIdm, out var displayName) && !string.IsNullOrEmpty(displayName)
                        ? $"{displayName} ({cardIdm})"
                        : cardIdm;

                    items.Add(new CsvImportPreviewItem
                    {
                        LineNumber = lineNumber,
                        Idm = cardDisplayIdm,
                        Name = summary,
                        AdditionalInfo = SqliteDateTimeFormat.ToText(date),
                        Action = action,
                        Changes = changes
                    });
                }

                return new CsvImportPreviewResult
                {
                    IsValid = errors.Count == 0,
                    NewCount = newCount,
                    UpdateCount = updateCount,
                    SkipCount = skipCount,
                    ErrorCount = errors.Count,
                    Errors = errors,
                    Items = items
                };
            }
            // 対応表の集約については ImportLedgersAsync 側のコメントを参照
            catch (Exception ex)
            {
                return new CsvImportPreviewResult
                {
                    IsValid = false,
                    ErrorMessage = ToUserFacingErrorMessage(ex, "利用履歴CSVの取り込み内容の確認"),
                    Errors = errors
                };
            }
        }

    }
}
