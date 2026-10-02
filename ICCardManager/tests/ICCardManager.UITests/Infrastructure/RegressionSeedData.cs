using System;
using System.Data.SQLite;
using System.Globalization;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// 手動確認を置き換える回帰テスト（Issue #2192 以降）用の投入データ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 撮影用の <see cref="ScreenshotSeedData"/> を土台にして、回帰テストだけが必要とするデータを足す。
    /// <see cref="ScreenshotSeedData"/> 自体へ足さないのは、あのファイルが撮影対応表の <c>common</c> に載っており、
    /// 変えるとマニュアルの全画像が撮り直しの対象になるため（画像の内容は変わらないのに）。
    /// </para>
    /// <para>
    /// UITests は本体を参照しないため、列名・書式はリテラルで持つ（<see cref="ScreenshotSeedData"/> と同じ方針）。
    /// </para>
    /// </remarks>
    internal static class RegressionSeedData
    {
        /// <summary>明細を付ける履歴の摘要（<see cref="ScreenshotSeedData.Seed"/> の通常カードの最後の利用）。</summary>
        public const string LedgerWithDetailsSummary = "鉄道（博多～貝塚）";

        /// <summary>乗り換えた駅（明細 2 件の境目）。</summary>
        public const string TransferStation = "中洲川端";

        /// <summary>
        /// <see cref="ScreenshotSeedData.Seed"/> に加えて、通常カードの「鉄道（博多～貝塚）」340 円に
        /// 乗り継ぎの明細 2 件（博多→中洲川端 210 円、中洲川端→貝塚 130 円）を付ける。
        /// </summary>
        /// <remarks>
        /// 利用履歴詳細ダイアログの「すべて統合」「分割線の切り替え」は明細が 2 件以上ないと何もしない（未保存の変更が立たない）。
        /// 明細の残額は履歴の残高チェーンと合わせる（直前の残額 11,830 円 → 11,620 円 → 11,490 円）。合わないと
        /// 整合性チェックの警告が出て、テストの前提にない画面が割り込む。明細を持つ履歴はこの 1 行だけなので、
        /// 履歴一覧で有効な「詳細」ボタンもこの行だけになる（「詳細」は明細を持つ行でだけ有効）。
        /// </remarks>
        public static void SeedWithLedgerDetails(SQLiteConnection conn)
        {
            ScreenshotSeedData.Seed(conn);

            using var tx = conn.BeginTransaction();

            long ledgerId;
            string date;
            int balance;
            int expense;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, date, balance, expense FROM ledger WHERE card_idm = @card AND summary = @summary";
                cmd.Parameters.AddWithValue("@card", ScreenshotSeedData.NormalCardIdm);
                cmd.Parameters.AddWithValue("@summary", LedgerWithDetailsSummary);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    throw new InvalidOperationException(
                        $"明細を付ける履歴「{LedgerWithDetailsSummary}」が投入データにありません（ScreenshotSeedData.Seed を変えたら本メソッドも直すこと）。");
                }

                ledgerId = reader.GetInt64(0);
                date = reader.GetString(1);
                balance = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
                expense = Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
            }

            const int firstLegAmount = 210;
            var secondLegAmount = expense - firstLegAmount;
            var before = balance + expense;
            InsertDetail(conn, ledgerId, date, "博多", TransferStation, firstLegAmount, before - firstLegAmount);
            InsertDetail(conn, ledgerId, date, TransferStation, "貝塚", secondLegAmount, balance);

            tx.Commit();
        }

        /// <summary>
        /// 仮想タッチ用のカード（DEBUG パネルの「交通系ICカード」が模擬する IDm）を払戻済にした投入データ（Issue #2194。ST-007 M5）。
        /// </summary>
        /// <remarks>
        /// 払い戻しは <c>is_deleted</c> を 0 のまま <c>is_refunded</c> を立てる（#530）。払戻済カードは貸出対象外で、
        /// タッチするとエラートーストが出る。
        /// </remarks>
        public static void SeedWithRefundedVirtualTouchCard(SQLiteConnection conn)
        {
            ScreenshotSeedData.SeedForVirtualTouch(conn);

            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE ic_card SET is_refunded = 1, refunded_at = @at WHERE card_idm = @card";
                cmd.Parameters.AddWithValue("@at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@card", ScreenshotSeedData.VirtualTouchCardIdm);
                if (cmd.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException("仮想タッチ用のカードが投入データにありません（ScreenshotSeedData.SeedForVirtualTouch を確認すること）。");
                }
            }

            tx.Commit();
        }

        private static void InsertDetail(
            SQLiteConnection conn, long ledgerId, string date, string entry, string exit, int amount, int balance)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO ledger_detail (ledger_id, use_date, entry_station, exit_station, bus_stops, amount, balance, " +
                "is_charge, is_bus, is_point_redemption, group_id) " +
                "VALUES (@ledger, @date, @entry, @exit, NULL, @amount, @balance, 0, 0, 0, NULL)";
            cmd.Parameters.AddWithValue("@ledger", ledgerId);
            cmd.Parameters.AddWithValue("@date", date);
            cmd.Parameters.AddWithValue("@entry", entry);
            cmd.Parameters.AddWithValue("@exit", exit);
            cmd.Parameters.AddWithValue("@amount", amount);
            cmd.Parameters.AddWithValue("@balance", balance);
            cmd.ExecuteNonQuery();
        }
    }
}
