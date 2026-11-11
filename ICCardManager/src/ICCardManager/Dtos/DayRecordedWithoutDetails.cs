#nullable enable
using System;

namespace ICCardManager.Dtos
{
    /// <summary>
    /// 明細（ledger_detail）を持たない台帳行（履歴の「追加」で手入力した行など）がある日と、その日の行が記録している
    /// 履歴の種類（Issue #2372）。
    /// </summary>
    /// <remarks>
    /// 返却時、貸出中に手入力した行と同じ日・同じ種類の履歴を「記録済み」とみなして取り込みから除くために使う
    /// （<see cref="Services.LendingService.ExcludeUsageOnDaysRecordedWithoutDetails"/>）。
    /// 同じ日に複数の行があれば、種類はそれらを合わせたもの。
    /// </remarks>
    public sealed class DayRecordedWithoutDetails
    {
        public DayRecordedWithoutDetails(DateTime date, bool hasIncome, bool hasExpense)
        {
            Date = date.Date;
            HasIncome = hasIncome;
            HasExpense = hasExpense;
        }

        /// <summary>日付（時刻を含まない）</summary>
        public DateTime Date { get; }

        /// <summary>受入のある行（チャージ・ポイント還元を手入力した行）がある</summary>
        public bool HasIncome { get; }

        /// <summary>払出のある行（利用を手入力した行）がある</summary>
        public bool HasExpense { get; }

        /// <summary>
        /// 受入・払出のどちらも無い行だけの日（金額から種類を判定できない）。
        /// 残高不足の特殊処理でぴったりチャージした利用（払出 0 円でチャージと利用をまとめた行）を手入力した形があり得るので、
        /// その日の履歴は種類を問わず記録済みとみなす。
        /// </summary>
        public bool IsKindUnknown => !HasIncome && !HasExpense;
    }
}
