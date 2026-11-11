#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// 台帳の残高チェーン（前行の残額 ＋ 受入 − 払出 ＝ 当行の残額）を、カードごとに確かめる（Issue #2425）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 撮影用のサンプルデータ（<see cref="ScreenshotSeedData"/>）と、撮影の操作（仮想タッチの返却）が作った台帳が、
    /// 履歴画面の不整合警告（本体の <c>LedgerConsistencyChecker</c>）に掛かる形になっていないかを確かめる。
    /// マニュアルの返却完了の画像が「払出 200 なのに残高が変わらない」台帳を写していた（#2425）のは、
    /// 仮想タッチのダイアログの「現在残高」を DB の最新残高のまま返却したため。シードの単体テストだけでは
    /// 撮影の操作が作る行を見られないので、撮影の後にも同じ検査を掛ける。
    /// </para>
    /// <para>
    /// 台帳の行についての規則は <c>LedgerConsistencyChecker.CheckConsistency</c> の親レコードの検査と同じ: 並べた行の 2 行目以降について、
    /// 前行の残額 ＋ 受入 − 払出 が当行の残額と一致すること。母集団も同じく貸出中レコード（受入・払出 0）を含める。
    /// <b>本体との差分</b>: ①台帳の行（<c>ledger</c>）だけを見る。本体は明細（<c>ledger_detail</c>）の残高チェーンも検査するが、
    /// ここでは見ない。②同じ日の通常の行を残高チェーンで並べ替えず、id の順のまま並べる（下記）。
    /// UITests は本体を参照しない（csproj の方針）ため、ここに書き写している。本体の規則を変えたら追随すること。
    /// </para>
    /// <para>
    /// 並びは本体（<c>LedgerRepository.GetByDateRangeAsync</c> → <c>LedgerOrderHelper.ReorderByBalanceChain</c>）と同じく、
    /// 日付（日単位）→ 同じ日の中では新規購入・○月から繰越を先頭 → id の順。本体は同じ日の通常の行を残高チェーンで
    /// 並べ替えるが、ここでは id（投入・記録の順）のままにする。シードも返却も時系列の順に書くので結果は同じで、
    /// 並びが食い違う形はかえって不整合として見える（緩める方向にはずれない）。
    /// </para>
    /// </remarks>
    internal static class LedgerBalanceChain
    {
        /// <summary>残高チェーンの検査に使う台帳 1 行。</summary>
        internal sealed class Row
        {
            public Row(long id, string cardIdm, DateTime date, string summary, int income, int expense, int balance, long? initialRecordKind = null)
            {
                InitialRecordKind = initialRecordKind;
                Id = id;
                CardIdm = cardIdm;
                Date = date;
                Summary = summary;
                Income = income;
                Expense = expense;
                Balance = balance;
            }

            public long Id { get; }
            public string CardIdm { get; }
            public DateTime Date { get; }
            public string Summary { get; }
            public int Income { get; }
            public int Expense { get; }
            public int Balance { get; }

            /// <summary>導入行の種別（<c>ledger.initial_record_kind</c>。1＝新規購入、2＝○月から繰越、3＝前年度より繰越、null＝導入行でない）</summary>
            public long? InitialRecordKind { get; }
        }

        /// <summary>DB の全カードの台帳を読み、残高チェーンが切れている箇所を説明する文を返す（無ければ空）。</summary>
        internal static IReadOnlyList<string> FindBreaks(SQLiteConnection conn) =>
            FindBreaks(ReadRows(conn));

        /// <summary>行の集合から、カードごとに残高チェーンが切れている箇所を説明する文を返す（無ければ空）。</summary>
        internal static IReadOnlyList<string> FindBreaks(IEnumerable<Row> rows)
        {
            var breaks = new List<string>();
            foreach (var card in rows.GroupBy(r => r.CardIdm, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var ordered = Order(card).ToList();
                for (var i = 1; i < ordered.Count; i++)
                {
                    var previous = ordered[i - 1];
                    var current = ordered[i];
                    var expected = previous.Balance + current.Income - current.Expense;
                    if (current.Balance != expected)
                    {
                        breaks.Add(string.Format(CultureInfo.InvariantCulture,
                            "カード {0} の {1:yyyy-MM-dd}「{2}」（id {3}）: 前行の残額 {4:N0} ＋ 受入 {5:N0} − 払出 {6:N0} ＝ {7:N0} のはずが、残額は {8:N0}",
                            current.CardIdm, current.Date, current.Summary, current.Id,
                            previous.Balance, current.Income, current.Expense, expected, current.Balance));
                    }
                }
            }

            return breaks;
        }

        /// <summary>1 枚のカードの台帳を、本体の残高チェーンの検査と同じ順に並べる（貸出中レコードを含む）。</summary>
        internal static IReadOnlyList<Row> ReadCard(SQLiteConnection conn, string cardIdm) =>
            Order(ReadRows(conn).Where(r => string.Equals(r.CardIdm, cardIdm, StringComparison.Ordinal))).ToList();

        private static IEnumerable<Row> Order(IEnumerable<Row> rows) =>
            rows.OrderBy(r => r.Date.Date)
                .ThenBy(r => IsSpecialRecord(r) ? 0 : 1)
                .ThenBy(r => r.Id);

        /// <summary>
        /// 同じ日の中で先頭に置く行か（本体の <c>LedgerOrderHelper.IsSpecialRecord</c> ＝ <c>Ledger.IsCarryover</c> と同じ:
        /// 導入行の種別が新規購入・○月から繰越。Issue #2371 で本体が摘要ではなく種別の列で判定するようになったので追随した）。
        /// </summary>
        private static bool IsSpecialRecord(Row row) =>
            row.InitialRecordKind is 1 or 2;

        private static List<Row> ReadRows(SQLiteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, card_idm, date, summary, income, expense, balance, initial_record_kind FROM ledger";
            using var reader = cmd.ExecuteReader();
            var rows = new List<Row>();
            while (reader.Read())
            {
                rows.Add(new Row(
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    reader.GetString(1),
                    ToDate(reader.GetValue(2)),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    ToInt(reader.GetValue(4)),
                    ToInt(reader.GetValue(5)),
                    ToInt(reader.GetValue(6)),
                    reader.IsDBNull(7) ? null : Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture)));
            }

            return rows;
        }

        /// <summary>列の宣言型によって System.Data.SQLite が DateTime で返すことがあるので、両方を受ける。</summary>
        private static DateTime ToDate(object value) =>
            value is DateTime date
                ? date
                : DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.None);

        private static int ToInt(object value) =>
            value is DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}
