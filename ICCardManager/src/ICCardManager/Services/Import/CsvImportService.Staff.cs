#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
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
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
    public partial class CsvImportService
    {
        // === 職員CSVインポート・プレビュー ===

        public virtual async Task<CsvImportResult> ImportStaffAsync(string filePath, bool skipExisting = true)
        {
            var errors = new List<CsvImportError>();
            return await ExecuteImportWithErrorHandlingAsync(
                () => ImportStaffInternalAsync(filePath, skipExisting, errors),
                errors,
                "職員CSVの取り込み").ConfigureAwait(false);
        }

        /// <summary>
        /// 職員CSVインポートの内部処理
        /// </summary>
        private async Task<CsvImportResult> ImportStaffInternalAsync(
            string filePath,
            bool skipExisting,
            List<CsvImportError> errors)
        {
            var importedCount = 0;
            var skippedCount = 0;
            var updatedCount = 0;

            var lines = await ReadCsvFileAsync(filePath, _logger).ConfigureAwait(false);
            if (lines.Count < 2)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ErrorMessage = "CSVファイルにデータがありません（ヘッダー行のみ）"
                };
            }

            // バリデーションパス: まず全データをバリデーション
            // IsRestore: 削除済み職員を復元して更新する場合true
            var validRecords = new List<(int LineNumber, Staff Staff, bool IsUpdate, bool IsRestore)>();

            for (var i = 1; i < lines.Count; i++)
            {
                var lineNumber = i + 1;
                var line = lines[i];

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var fields = ParseCsvLine(line);

                // 最低4列（職員IDm, 氏名, 職員番号, 備考）が必要
                if (!ValidateColumnCount(fields, 4, lineNumber, line, errors))
                {
                    continue;
                }

                var staffIdm = fields[0].Trim().ToUpperInvariant(); // IDmは大文字に正規化
                // Issue #1808: テキスト列はエクスポート由来の先頭 ' を取り除いて自然な値で保存する
                //（式インジェクション対策は sink 側のエクスポート／帳票出力が担う。ReadTextField を参照）
                var name = ReadTextField(fields, 1);
                var number = ReadTextField(fields, 2);
                var note = ReadTextField(fields, 3);

                // バリデーション（共通メソッドを使用）
                if (!ValidateIdm(staffIdm, lineNumber, "職員IDm", line, errors, isStaff: true))
                {
                    continue;
                }

                if (!ValidateRequired(name, lineNumber, "氏名", line, errors))
                {
                    continue;
                }

                // Issue #2324: 「削除済み」列が「1」の行は取り込まない（復元・登録・更新しない。TryReadDeletedFlag の remarks）。
                // 職員の復元は認証できる職員証を増やすので、削除済みとして出力した行を黙って有効に戻さない
                if (!TryReadDeletedFlag(fields, lineNumber, line, errors, out var isDeletedRow))
                {
                    continue;
                }

                if (isDeletedRow)
                {
                    skippedCount++;
                    continue;
                }

                // 書き込む内容（取り込み後の職員）。Issue #2262: 既存の職員（検証で読んだインスタンス）は書き換えない。
                // 書き換えると、操作ログの「変更前」に使う値と「変更後」の値が同じインスタンスを指し得る（#1959）。
                // 取り込み後の職員は is_deleted = 0（削除済みなら復元する）で、Staff の列はこれですべて。
                var importedStaff = new Staff
                {
                    StaffIdm = staffIdm,
                    Name = name,
                    Number = NormalizeOptionalText(number),
                    Note = NormalizeOptionalText(note)
                };

                // 既存チェック（削除済みも含めて検索）
                var existingStaff = await _staffRepository.GetByIdmAsync(staffIdm, includeDeleted: true).ConfigureAwait(false);
                if (existingStaff != null)
                {
                    // 削除済み職員の場合は復元対象として扱う
                    if (existingStaff.IsDeleted)
                    {
                        // 削除済み職員は復元して更新する（skipExistingでもスキップしない）
                        validRecords.Add((lineNumber, importedStaff, true, true)); // isRestore = true
                    }
                    else
                    {
                        // Issue #1376: skipExisting=true でも「全項目一致」のみスキップ。
                        // 備考を含むいずれかのフィールドに差分があれば更新する
                        var changes = new List<FieldChange>();
                        DetectStaffChanges(existingStaff, name, number, note, changes);
                        if (skipExisting && changes.Count == 0)
                        {
                            skippedCount++;
                            continue;
                        }
                        validRecords.Add((lineNumber, importedStaff, true, false)); // isRestore = false
                    }
                }
                else
                {
                    // 新規登録用の職員
                    validRecords.Add((lineNumber, importedStaff, false, false));
                }
            }

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

            // トランザクション内でインポート実行
            using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                foreach (var (lineNumber, staff, isUpdate, isRestore) in validRecords)
                {
                    // Issue #2262: 書き込みと同じトランザクションで、1 行ずつ操作ログを記録する（変更前・変更後）。
                    // 以前は取り込み全体についてファイル名と件数だけを記録しており、誰をどう登録・変更・復元したかが残らなかった
                    bool success;
                    if (isRestore)
                    {
                        success = await RestoreStaffWithAuditLogAsync(staff, scope.Transaction).ConfigureAwait(false);
                    }
                    else if (isUpdate)
                    {
                        success = await UpdateStaffWithAuditLogAsync(staff, scope.Transaction).ConfigureAwait(false);
                    }
                    else
                    {
                        success = await _staffRepository.InsertAsync(staff, scope.Transaction).ConfigureAwait(false);
                        if (success)
                        {
                            await _operationLogger.LogStaffInsertAsync(staff, scope.Transaction).ConfigureAwait(false);
                        }
                    }

                    if (success)
                    {
                        importedCount++;
                        if (isUpdate)
                        {
                            // 更新・復元（既存の職員を書き換えた行）。isRestore の行も isUpdate = true
                            updatedCount++;
                        }
                    }
                    else
                    {
                        // Issue #2262 / #1759: 更新・復元の false は「対象が削除された」「先に復元された」競合
                        // （影響行数 0。WHERE の is_deleted で決まる）。原因を名指しする。登録の false は
                        // InsertAsync が一過性でない SQLite の失敗をすべて畳むため原因を断定できず、従来の文言のまま
                        var target = $"職員「{staff.Name}」";
                        var message = isRestore ? ConcurrencyConflictMessage.ForImportRestore(target, StaffManagementScreenName)
                            : isUpdate ? ConcurrencyConflictMessage.ForImportUpdate(target, StaffManagementScreenName)
                            : "職員の登録に失敗しました";
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = message,
                            Data = staff.StaffIdm
                        });
                    }
                }

                // すべて成功したらコミット
                if (errors.Count == 0)
                {
                    scope.Commit();
                    // コミット後にキャッシュを無効化
                    _cacheService.InvalidateByPrefix(CacheKeys.StaffPrefixForInvalidation);
                }
                else
                {
                    TryRollbackImportTransaction(scope);
                    importedCount = 0;
                    updatedCount = 0;
                }
            }
            catch (SQLiteException ex)
            {
                // ログはロールバックより先に書く（TryRollbackImportTransaction の remarks 参照）
                // Issue #1282: SQLiteException は DatabaseException へラップして詳細を保持
                LogImportTransactionFailure(ex,
                    "職員CSVインポートのトランザクション中に SQLite エラーが発生しロールバック");
                TryRollbackImportTransaction(scope);
                throw MarkLogged(DatabaseException.QueryFailed("CSV import transaction", ex));
            }
            catch (Exception ex)
            {
                // Issue #1282: 想定外の例外（IO例外・DB接続断・仮想テーブル解決失敗等）も
                // 握りつぶさずログに痕跡を残してから再スローする。throw; で
                // スタックトレースを保持したまま呼び出し元に伝搬する。
                LogImportTransactionFailure(ex,
                    "職員CSVインポートのトランザクション中に想定外の例外が発生しロールバック");
                TryRollbackImportTransaction(scope);
                MarkLogged(ex);
                throw;
            }

            return new CsvImportResult
            {
                Success = errors.Count == 0,
                ImportedCount = importedCount,
                UpdatedCount = updatedCount,
                SkippedCount = skippedCount,
                ErrorCount = errors.Count,
                Errors = errors
            };
        }

        /// <summary>
        /// 登録済みの職員を CSV の内容で更新し、変更前・変更後を同じトランザクションで操作ログへ記録する（Issue #2262）
        /// </summary>
        /// <param name="staff">更新後の職員（CSV の行から組み立てたもの）</param>
        /// <param name="transaction">取り込み全体のトランザクション</param>
        /// <returns>更新したら <c>true</c>。対象の職員がいなかった（他 PC や別の操作で削除された）ら <c>false</c></returns>
        /// <remarks>
        /// 変更前は<b>このトランザクションの中で読み直した</b>職員を使う（検証の段階で読んだ値は、書き込むまでの間に
        /// 他 PC が書き換え得る）。操作ログの書き込みが失敗したら例外がそのまま抜け、呼び出し元が取り込み全体を巻き戻す。
        /// </remarks>
        private async Task<bool> UpdateStaffWithAuditLogAsync(Staff staff, SQLiteTransaction transaction)
        {
            // DbContext は接続を 1 本しか持たないため、この読み取りは同じ接続上のトランザクションの内側で行われる
            var before = await _staffRepository.GetByIdmAsync(staff.StaffIdm, includeDeleted: true).ConfigureAwait(false);
            if (before == null || before.IsDeleted)
            {
                // 読み取りが null（削除済み）なら書き込みも行わない（#1760）。UPDATE も同じ条件（is_deleted = 0）で 0 行になる
                return false;
            }

            var updated = await _staffRepository.UpdateAsync(staff, transaction).ConfigureAwait(false);
            if (!updated)
            {
                return false;
            }

            await _operationLogger.LogStaffUpdateAsync(before, staff, transaction).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// 削除済みの職員を復元して CSV の内容で更新し、同じトランザクションで操作ログへ記録する（Issue #2262）
        /// </summary>
        /// <param name="staff">復元・更新後の職員（CSV の行から組み立てたもの）</param>
        /// <param name="transaction">取り込み全体のトランザクション</param>
        /// <returns>
        /// 復元・更新したら <c>true</c>。対象が削除済みでなくなっていた（他 PC が先に復元した）・見つからなかったら <c>false</c>
        /// </returns>
        /// <remarks>
        /// <para>
        /// 記録は 2 行になり得る。復元（<c>RESTORE</c>。職員管理画面の復元と同じ操作種別）と、氏名・職員番号・備考が
        /// 削除前と違っていればその変更（<c>UPDATE</c>。変更前は削除済みの状態）。復元の記録は変更前を持たないため、
        /// 変更の記録を分けないと「削除前は誰として登録されていた職員証か」が操作ログから読めない。
        /// </para>
        /// <para>
        /// 削除済みの職員の復元は、職員管理画面では職員証の認証を経て行う（Issue #2263）。CSV の取り込みも
        /// 呼び出し元（データ入出力画面）が認証してから呼ぶため、記録の操作者は認証した職員になる。
        /// </para>
        /// </remarks>
        private async Task<bool> RestoreStaffWithAuditLogAsync(Staff staff, SQLiteTransaction transaction)
        {
            var before = await _staffRepository.GetByIdmAsync(staff.StaffIdm, includeDeleted: true).ConfigureAwait(false);
            if (before == null)
            {
                return false;
            }

            // 削除済みでなくなっていれば（他 PC が先に復元した）、RestoreAsync の WHERE（is_deleted = 1）が 0 行で false を返す
            var restored = await _staffRepository.RestoreAsync(staff.StaffIdm, transaction).ConfigureAwait(false);
            if (!restored)
            {
                return false;
            }

            var updated = await _staffRepository.UpdateAsync(staff, transaction).ConfigureAwait(false);
            if (!updated)
            {
                return false;
            }

            await _operationLogger.LogStaffRestoreAsync(staff, transaction).ConfigureAwait(false);
            if (!HasSameStaffAttributes(before, staff))
            {
                await _operationLogger.LogStaffUpdateAsync(before, staff, transaction).ConfigureAwait(false);
            }

            return true;
        }

        /// <summary>職員の状態を確かめる画面（取り込みの競合の案内に使う）</summary>
        private const string StaffManagementScreenName = "職員管理画面（F2）";

        /// <summary>
        /// 氏名・職員番号・備考が同じか（職員番号・備考の空欄と null は同じ未設定として比べる。#1808）
        /// </summary>
        private static bool HasSameStaffAttributes(Staff before, Staff after)
            => before.Name == after.Name
               && NormalizeOptionalText(before.Number) == NormalizeOptionalText(after.Number)
               && NormalizeOptionalText(before.Note) == NormalizeOptionalText(after.Note);

        /// <summary>
        /// カードCSVのインポートプレビューを取得
        /// </summary>
        /// <param name="filePath">CSVファイルパス</param>
        /// <param name="skipExisting">既存データをスキップするか（falseの場合は更新）</param>

        public virtual async Task<CsvImportPreviewResult> PreviewStaffAsync(string filePath, bool skipExisting = true)
        {
            var errors = new List<CsvImportError>();
            return await ExecutePreviewWithErrorHandlingAsync(
                () => PreviewStaffInternalAsync(filePath, skipExisting, errors),
                errors,
                "職員CSVの取り込み内容の確認").ConfigureAwait(false);
        }

        /// <summary>
        /// 職員CSVプレビューの内部処理
        /// </summary>
        private async Task<CsvImportPreviewResult> PreviewStaffInternalAsync(
            string filePath,
            bool skipExisting,
            List<CsvImportError> errors)
        {
            var items = new List<CsvImportPreviewItem>();
            var newCount = 0;
            var updateCount = 0;
            var skipCount = 0;

            var lines = await ReadCsvFileAsync(filePath, _logger).ConfigureAwait(false);
            if (lines.Count < 2)
            {
                return new CsvImportPreviewResult
                {
                    IsValid = false,
                    ErrorMessage = "CSVファイルにデータがありません（ヘッダー行のみ）"
                };
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

                if (!ValidateColumnCount(fields, 4, lineNumber, line, errors))
                {
                    continue;
                }

                var staffIdm = fields[0].Trim().ToUpperInvariant(); // IDmは大文字に正規化
                // Issue #1370: プレビューでも備考差分検出のため note を読み取る
                // Issue #1808: インポート本体と同じ ReadTextField（先頭 ' の除去）で読む
                var name = ReadTextField(fields, 1);
                var number = ReadTextField(fields, 2);
                var note = ReadTextField(fields, 3);

                // バリデーション（共通メソッドを使用）
                if (!ValidateIdm(staffIdm, lineNumber, "職員IDm", line, errors, isStaff: true))
                {
                    continue;
                }

                if (!ValidateRequired(name, lineNumber, "氏名", line, errors))
                {
                    continue;
                }

                // Issue #2324: 取り込みと同じく「削除済み」列が「1」の行はスキップと示す
                if (!TryReadDeletedFlag(fields, lineNumber, line, errors, out var isDeletedRow))
                {
                    continue;
                }

                // 既存チェック（削除済みも含めて検索）
                var existingStaff = await _staffRepository.GetByIdmAsync(staffIdm, includeDeleted: true).ConfigureAwait(false);
                ImportAction action;
                var changes = new List<FieldChange>();

                if (isDeletedRow)
                {
                    action = ImportAction.Skip;
                    skipCount++;
                    changes.Add(CreateDeletedRowSkipDisplay());
                }
                else if (existingStaff != null)
                {
                    // 削除済み職員の場合は復元対象として扱う
                    if (existingStaff.IsDeleted)
                    {
                        action = ImportAction.Restore;
                        updateCount++; // 復元+更新なので更新件数に含める
                        // 復元時も変更点を検出
                        DetectStaffChanges(existingStaff, name, number, note, changes);
                        changes.Insert(0, new FieldChange
                        {
                            FieldName = "状態",
                            OldValue = "削除済み",
                            NewValue = "有効"
                        });
                    }
                    else
                    {
                        // Issue #1376: skipExisting の有無に関わらず全項目比較で差分を検出。
                        // 差分あり → Update、差分なしかつ skipExisting=true → Skip、
                        // 差分なしかつ skipExisting=false → Update（no-op 更新）
                        DetectStaffChanges(existingStaff, name, number, note, changes);
                        if (changes.Count == 0 && skipExisting)
                        {
                            action = ImportAction.Skip;
                            skipCount++;
                        }
                        else
                        {
                            action = ImportAction.Update;
                            updateCount++;
                        }
                    }
                }
                else
                {
                    action = ImportAction.Insert;
                    newCount++;
                }

                items.Add(new CsvImportPreviewItem
                {
                    LineNumber = lineNumber,
                    Idm = staffIdm,
                    Name = name,
                    AdditionalInfo = NormalizeOptionalText(number),
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

        /// <summary>
        /// 職員データの変更点を検出
        /// </summary>
        /// <param name="existingStaff">既存の職員</param>
        /// <param name="newName">新しい氏名</param>
        /// <param name="newNumber">新しい職員番号</param>
        /// <param name="newNote">新しい備考</param>
        /// <param name="changes">変更点リスト（検出結果が追加される）</param>
        private static void DetectStaffChanges(
            Staff existingStaff,
            string newName,
            string newNumber,
            string newNote,
            List<FieldChange> changes)
        {
            if (existingStaff.Name != newName)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "氏名",
                    OldValue = existingStaff.Name ?? "(なし)",
                    NewValue = newName
                });
            }

            // Issue #1808: 職員番号は保存時に NormalizeOptionalText（IsNullOrWhiteSpace → null）で
            // 正規化されるため、CSV の空欄 "" と DB の null を生で比較すると常に不一致になり、
            // 職員番号未設定の職員が同一 CSV の再取り込みで毎回「更新」に数えられ、プレビューに
            // 実在しない差分「職員番号: (なし) → （空）」が出ていた。備考（Issue #1370）と同じ
            // 正規化で比較する（保存側と比較側で同じヘルパーを使う）。
            AddOptionalTextChangeIfDiffers("職員番号", existingStaff.Number, newNumber, changes);

            // Issue #1370: 備考の差分検出
            AddOptionalTextChangeIfDiffers("備考", existingStaff.Note, newNote, changes);
        }
    }
}
