using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Models;

namespace ICCardManager.Services
{
    /// <summary>
    /// 利用履歴 CSV インポート用の検証ロジック（partial 分割、Issue #1284）。
    /// </summary>
    public partial class CsvImportService
    {
        // Issue #1284: CsvImportService.Ledger.cs から以下 4 メソッドを移設
        //   - DetectLedgerChanges
        //   - ValidateBalanceConsistency
        //   - ValidateBalanceConsistencyForLedgers
        //   - GetPreviousBalanceByCardAsync

        /// <summary>
        /// 出納記録の変更点を検出
        /// </summary>
        /// <param name="existingLedger">既存の出納記録</param>
        /// <param name="newDate">新しい日付</param>
        /// <param name="newSummary">新しい摘要</param>
        /// <param name="newIncome">新しい受入金額</param>
        /// <param name="newExpense">新しい払出金額</param>
        /// <param name="newBalance">新しい残額</param>
        /// <param name="newStaffName">新しい利用者名</param>
        /// <param name="newNote">新しい備考</param>
        /// <param name="changes">変更点リスト（検出結果が追加される）</param>
        private static void DetectLedgerChanges(
            Ledger existingLedger,
            DateTime newDate,
            string newSummary,
            int newIncome,
            int newExpense,
            int newBalance,
            string newStaffName,
            string newNote,
            List<FieldChange> changes)
        {
            // Issue #639: 金額・日付フィールドの変更も検出
            if (existingLedger.Date != newDate)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "日時",
                    OldValue = existingLedger.Date.ToString("yyyy-MM-dd HH:mm:ss"),
                    NewValue = newDate.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }

            if (existingLedger.Summary != newSummary)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "摘要",
                    OldValue = existingLedger.Summary ?? "(なし)",
                    NewValue = newSummary
                });
            }

            if (existingLedger.Income != newIncome)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "受入金額",
                    OldValue = $"{existingLedger.Income}円",
                    NewValue = $"{newIncome}円"
                });
            }

            if (existingLedger.Expense != newExpense)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "払出金額",
                    OldValue = $"{existingLedger.Expense}円",
                    NewValue = $"{newExpense}円"
                });
            }

            if (existingLedger.Balance != newBalance)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "残額",
                    OldValue = $"{existingLedger.Balance}円",
                    NewValue = $"{newBalance}円"
                });
            }

            var existingStaffName = existingLedger.StaffName ?? "";
            if (existingStaffName != newStaffName)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "利用者",
                    OldValue = string.IsNullOrEmpty(existingStaffName) ? "(なし)" : existingStaffName,
                    NewValue = string.IsNullOrEmpty(newStaffName) ? "(なし)" : newStaffName
                });
            }

            var existingNote = existingLedger.Note ?? "";
            if (existingNote != newNote)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "備考",
                    OldValue = string.IsNullOrEmpty(existingNote) ? "(なし)" : existingNote,
                    NewValue = string.IsNullOrEmpty(newNote) ? "(なし)" : newNote
                });
            }
        }

        /// <summary>
        /// 利用履歴詳細の変更検出
        /// 既存の詳細リストとインポート対象の詳細リストを比較し、差分を検出する。
        /// </summary>

        internal static void ValidateBalanceConsistency(
            List<(int LineNumber, int? LedgerId, string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance, string StaffName, string Note)> records,
            List<CsvImportError> errors,
            Dictionary<string, int> previousBalanceByCard = null)
        {
            if (records.Count == 0) return;

            // カードごとにグループ化して日時順にソート
            var groupedByCard = records
                .GroupBy(r => r.CardIdm, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Date).ThenBy(r => r.LineNumber).ToList());

            foreach (var kvp in groupedByCard)
            {
                var cardIdm = kvp.Key;
                var cardRecords = kvp.Value;

                // Issue #907: 最初の行をDB上の直前残高と照合
                if (cardRecords.Count > 0 && previousBalanceByCard != null &&
                    previousBalanceByCard.TryGetValue(cardIdm.ToUpperInvariant(), out var prevDbBalance))
                {
                    var firstRecord = cardRecords[0];
                    var expectedBalance = prevDbBalance + firstRecord.Income - firstRecord.Expense;
                    if (expectedBalance != firstRecord.Balance)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = firstRecord.LineNumber,
                            Message = $"残高が一致しません（期待値: {expectedBalance}円、実際: {firstRecord.Balance}円）。" +
                                      $"前回残高（DB）: {prevDbBalance}円 + 受入: {firstRecord.Income}円 - 払出: {firstRecord.Expense}円",
                            Data = cardIdm
                        });
                    }
                }

                for (var i = 1; i < cardRecords.Count; i++)
                {
                    var prevRecord = cardRecords[i - 1];
                    var currentRecord = cardRecords[i];

                    // 期待される残高: 前の残高 + 受入金額 - 払出金額
                    var expectedBalance = prevRecord.Balance + currentRecord.Income - currentRecord.Expense;

                    if (expectedBalance != currentRecord.Balance)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = currentRecord.LineNumber,
                            Message = $"残高が一致しません（期待値: {expectedBalance}円、実際: {currentRecord.Balance}円）。" +
                                      $"前回残高: {prevRecord.Balance}円 + 受入: {currentRecord.Income}円 - 払出: {currentRecord.Expense}円",
                            Data = cardIdm
                        });
                    }
                }
            }
        }

        /// <summary>
        /// 残高整合性チェック（インポート用）
        /// カードごとに日時順で残高の連続性を検証します。
        /// 計算式: 前の残高 + 受入金額 - 払出金額 = 今回の残高
        /// Issue #907: 最初の行もDB上の直前残高と照合します。
        /// </summary>
        /// <param name="records">検証対象レコード（LineNumber, Ledger, IsUpdate）</param>
        /// <param name="errors">エラーリスト</param>
        /// <param name="previousBalanceByCard">カードIDmごとのDB上の直前残高（存在しない場合はキーなし）</param>
        internal static void ValidateBalanceConsistencyForLedgers(
            List<(int LineNumber, Ledger Ledger, bool IsUpdate)> records,
            List<CsvImportError> errors,
            Dictionary<string, int> previousBalanceByCard = null)
        {
            if (records.Count == 0) return;

            // カードごとにグループ化して日時順にソート
            var groupedByCard = records
                .GroupBy(r => r.Ledger.CardIdm, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Ledger.Date).ThenBy(r => r.LineNumber).ToList());

            foreach (var kvp in groupedByCard)
            {
                var cardIdm = kvp.Key;
                var cardRecords = kvp.Value;

                // Issue #907: 最初の行をDB上の直前残高と照合
                if (cardRecords.Count > 0 && previousBalanceByCard != null &&
                    previousBalanceByCard.TryGetValue(cardIdm.ToUpperInvariant(), out var prevDbBalance))
                {
                    var firstRecord = cardRecords[0];
                    var expectedBalance = prevDbBalance + firstRecord.Ledger.Income - firstRecord.Ledger.Expense;
                    if (expectedBalance != firstRecord.Ledger.Balance)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = firstRecord.LineNumber,
                            Message = $"残高が一致しません（期待値: {expectedBalance}円、実際: {firstRecord.Ledger.Balance}円）。" +
                                      $"前回残高（DB）: {prevDbBalance}円 + 受入: {firstRecord.Ledger.Income}円 - 払出: {firstRecord.Ledger.Expense}円",
                            Data = cardIdm
                        });
                    }
                }

                for (var i = 1; i < cardRecords.Count; i++)
                {
                    var prevRecord = cardRecords[i - 1];
                    var currentRecord = cardRecords[i];

                    // 期待される残高: 前の残高 + 受入金額 - 払出金額
                    var expectedBalance = prevRecord.Ledger.Balance + currentRecord.Ledger.Income - currentRecord.Ledger.Expense;

                    if (expectedBalance != currentRecord.Ledger.Balance)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = currentRecord.LineNumber,
                            Message = $"残高が一致しません（期待値: {expectedBalance}円、実際: {currentRecord.Ledger.Balance}円）。" +
                                      $"前回残高: {prevRecord.Ledger.Balance}円 + 受入: {currentRecord.Ledger.Income}円 - 払出: {currentRecord.Ledger.Expense}円",
                            Data = cardIdm
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Issue #907: カードごとにDB上の直前残高を取得する
        /// CSVの各カードの最も古い日付より前のledgerレコードの残高を返します。
        /// DB上に該当レコードがない場合はキーに含まれません。
        /// </summary>
        /// <param name="earliestDateByCard">カードIDmごとのCSV内の最小日付</param>
        /// <returns>カードIDm（大文字）→直前残高のディクショナリ</returns>
        private async Task<Dictionary<string, int>> GetPreviousBalanceByCardAsync(
            Dictionary<string, DateTime> earliestDateByCard)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in earliestDateByCard)
            {
                var cardIdm = kvp.Key;
                var earliestDate = kvp.Value;

                var previousLedger = await _ledgerRepository.GetLatestBeforeDateAsync(cardIdm, earliestDate).ConfigureAwait(false);
                if (previousLedger != null)
                {
                    result[cardIdm.ToUpperInvariant()] = previousLedger.Balance;
                }
            }

            return result;
        }
    }
}
