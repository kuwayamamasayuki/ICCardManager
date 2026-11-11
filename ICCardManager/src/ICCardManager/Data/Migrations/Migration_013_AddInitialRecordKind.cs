#nullable enable
using System.Collections.Generic;
using System.Data.SQLite;
using ICCardManager.Common;
using ICCardManager.Services;

namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// 台帳に導入行の種別（<c>initial_record_kind</c>）を追加し、既存の導入行を摘要から埋めるマイグレーション（Issue #2371）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 導入行（カード登録時に書く「新規購入」「○月から繰越」「前年度より繰越」）は種別を表す列を持たず、
    /// 摘要を<b>現在の</b>組織設定の文言と比べて判定していた。文言を変えると変更前に登録した導入行を認識できず、
    /// 紙の累計の二重計上（#2326）・前年度繰越の二重計上（#2418）・返却時の記録の下限の後退（#2237）を招く。
    /// 貸出中レコードの <c>is_lent_record</c>（#2044）と同じく、行自身に種別を持たせる。
    /// </para>
    /// <para>
    /// 既存の行は <see cref="InitialRecordSummaryMatcher"/> で摘要から 1 回だけ埋める。照合する文言は
    /// 起動時の組織設定（<see cref="CurrentSummaryText"/>。<c>DbContext</c> が <c>MigrationRunner</c> 経由で渡す）と
    /// 設定の既定値の両方。貸出中レコードは埋めない。<c>NULL</c> の行だけを更新するので、2 回目以降の実行は何もしない（冪等）。
    /// 埋めた後に摘要を書き換えても種別は変わらない。
    /// </para>
    /// </remarks>
    public class Migration_013_AddInitialRecordKind : IMigration
    {
        public int Version => 13;
        public string Description => "台帳の導入行の種別カラムの追加（新規購入・○月から繰越・前年度より繰越を摘要ではなく列で判定する）";

        /// <summary>
        /// 起動時の組織設定の摘要テキスト。<c>null</c>（テスト・設定を渡さない経路）なら既定の文言だけと照合する。
        /// </summary>
        public SummaryTextOptions? CurrentSummaryText { get; set; }

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // AddColumnIfNotExists で冪等化（Issue #1285）
            MigrationHelpers.AddColumnIfNotExists(
                connection, transaction,
                "ledger", "initial_record_kind", "INTEGER DEFAULT NULL");

            // 摘要の照合（○月から繰越は組織設定の正規表現）は SQL で書けないので、候補を読んで C# で判定する
            var updates = new List<(long Id, int Kind)>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                // 貸出中レコードは導入行ではない。is_lent_record を持たない旧形式の台帳（Migration_001 より前に作られた DB）では全行が対象
                select.CommandText = MigrationHelpers.HasColumn(connection, transaction, "ledger", "is_lent_record")
                    ? "SELECT id, summary FROM ledger WHERE initial_record_kind IS NULL AND (is_lent_record = 0 OR is_lent_record IS NULL)"
                    : "SELECT id, summary FROM ledger WHERE initial_record_kind IS NULL";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var summary = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var kind = InitialRecordSummaryMatcher.Infer(summary, CurrentSummaryText);
                    if (kind.HasValue)
                    {
                        updates.Add((reader.GetInt64(0), (int)kind.Value));
                    }
                }
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE ledger SET initial_record_kind = @kind WHERE id = @id AND initial_record_kind IS NULL";
            var kindParameter = update.Parameters.Add("@kind", System.Data.DbType.Int32);
            var idParameter = update.Parameters.Add("@id", System.Data.DbType.Int64);
            foreach (var (id, kind) in updates)
            {
                kindParameter.Value = kind;
                idParameter.Value = id;
                update.ExecuteNonQuery();
            }
        }

        public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // SQLiteでは古いバージョンとの互換性のためカラム削除は行わない
        }
    }
}
