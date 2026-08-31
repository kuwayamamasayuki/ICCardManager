using System.Linq;
using ICCardManager.Models;

namespace ICCardManager.Common
{
    /// <summary>
    /// 監査ログ用に <see cref="Ledger"/> の深いコピーを作る（Issue #1959）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Services.OperationLogger"/> はエンティティ全体を JSON 化して
    /// <c>BeforeData</c> / <c>AfterData</c> に記録するため、「変更前の状態」を保持する側は
    /// <b>変更対象と別のインスタンス</b>でなければならない。履歴統合（<c>LedgerMergeService</c>）は
    /// 統合先を in-place で書き換えるので、リストの浅いコピー（<c>ledgers.ToList()</c>）では
    /// <c>BeforeData[0]</c> に統合<b>後</b>の値が入り、6 年保存の監査ログから
    /// 「何から何へ変わったのか」が失われていた。
    /// </para>
    /// <para>
    /// <see cref="Ledger.Details"/> も統合の過程で書き換えられる（<c>SyncBusStopsFromSummary</c> による
    /// <c>BusStops</c> の同期、摘要再生成のための <c>SequenceNumber</c> の一時再採番）ため、
    /// 明細まで含めて複製する。スカラー列だけを複製すると「半分だけ正しい監査記録」になり、
    /// 次に読む人が <c>BeforeData</c> を信用してよいか判断できない。
    /// </para>
    /// <para>
    /// コピーの生成手段は本クラスただ 1 つに寄せる（履歴分割 <c>LedgerSplitService</c> も本クラスを使う）。
    /// 手段が 2 通りあると、モデルへ列を足したとき片方だけが更新される
    /// （`.claude/rules/development-conventions.md`「同じ論理的な処理に手段が 2 通りあるか」Issue #1763）。
    /// コピー漏れは静かに古い値を記録するため、<c>LedgerClonerCoverageTests</c> が
    /// リフレクションで全プロパティを走査して検出する。
    /// </para>
    /// </remarks>
    public static class LedgerCloner
    {
        /// <summary>
        /// <see cref="Ledger"/> を明細ごと複製する。
        /// </summary>
        /// <param name="source">複製元。<c>null</c> のときは <c>null</c> を返す。</param>
        public static Ledger Clone(Ledger source)
        {
            if (source == null)
            {
                return null;
            }

            return new Ledger
            {
                Id = source.Id,
                CardIdm = source.CardIdm,
                LenderIdm = source.LenderIdm,
                Date = source.Date,
                Summary = source.Summary,
                Income = source.Income,
                Expense = source.Expense,
                Balance = source.Balance,
                StaffName = source.StaffName,
                CompanionCount = source.CompanionCount,
                Note = source.Note,
                ReturnerIdm = source.ReturnerIdm,
                LentAt = source.LentAt,
                ReturnedAt = source.ReturnedAt,
                IsLentRecord = source.IsLentRecord,
                DetailCount = source.DetailCount,
                // 未取得（null）を空リストへ丸めない。「明細を持たない」と「明細を読んでいない」は
                // 6 年保存の監査記録の中で別の事実であり、丸めると後者が前者に見える。
                Details = source.Details?.Select(CloneDetail).ToList()
            };
        }

        /// <summary>
        /// <see cref="LedgerDetail"/> を複製する。
        /// </summary>
        /// <remarks>
        /// 親への逆参照（<see cref="LedgerDetail.Ledger"/>）は複製しない。監査ログは
        /// <see cref="System.Text.Json.JsonSerializer"/> でシリアライズするため、逆参照を持ち回ると
        /// 親 → 明細 → 親 の循環参照になる。親の情報は <see cref="LedgerDetail.LedgerId"/> で足りる。
        /// </remarks>
        /// <param name="source">複製元。<c>null</c> のときは <c>null</c> を返す。</param>
        public static LedgerDetail CloneDetail(LedgerDetail source)
        {
            if (source == null)
            {
                return null;
            }

            return new LedgerDetail
            {
                LedgerId = source.LedgerId,
                UseDate = source.UseDate,
                EntryStation = source.EntryStation,
                ExitStation = source.ExitStation,
                BusStops = source.BusStops,
                Amount = source.Amount,
                Balance = source.Balance,
                IsCharge = source.IsCharge,
                IsPointRedemption = source.IsPointRedemption,
                IsBus = source.IsBus,
                GroupId = source.GroupId,
                SequenceNumber = source.SequenceNumber,
                RawBytes = source.RawBytes == null ? null : (byte[])source.RawBytes.Clone()
            };
        }
    }
}
