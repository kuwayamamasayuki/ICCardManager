#nullable enable
using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// カード種別＋管理番号のユニークインデックスを、is_deleted が NULL のカードも対象にして作り直すマイグレーション
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2419: Migration_008 の部分ユニークインデックスは <c>WHERE is_deleted = 0</c> のため、
    /// is_deleted が NULL のカードを対象にしない。Issue #2381 以降アプリは NULL を有効なカードとして扱うので、
    /// 外部ツールで is_deleted を空にしたカードと同じ種別・管理番号のカードを重複して登録・編集できた。
    /// </para>
    /// <para>
    /// 1. 旧インデックスを落とし、is_deleted の NULL を 0 へ正規化する（落とす前に正規化すると、
    ///    NULL だったカードが旧インデックスの対象に入った時点で重複が制約違反になる）。
    /// 2. 有効なカードの種別＋管理番号の重複を、Migration_008 と同じ方法（番号をずらす）で解消する。
    ///    旧インデックスの対象外だった NULL のカードを含む重複が残ったままだと、
    ///    インデックスの作成が制約違反で失敗してアプリが起動できなくなる。
    /// 3. 条件を <c>WHERE is_deleted = 0 OR is_deleted IS NULL</c> にしてインデックスを作り直す。
    ///    <c>CREATE UNIQUE INDEX IF NOT EXISTS</c> は同じ名前の既存インデックスの条件を変えないため、1. で DROP しておく必要もある。
    ///    正規化だけでは移行の後に外部ツールで再び NULL にされると同じ形に戻るので、条件の変更と組にする。
    /// </para>
    /// <para>
    /// 冪等性（Issue #1285）: 2 回目は NULL も重複も無く、同じ定義でインデックスを作り直すだけになる。
    /// </para>
    /// </remarks>
    public class Migration_012_CardUniqueIndexCoversNullIsDeleted : IMigration
    {
        internal const string IndexName = "idx_card_type_number_active";

        public int Version => 12;
        public string Description => "カード種別＋管理番号のユニークインデックスを is_deleted が NULL のカードにも適用";

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // is_deleted 列の無いレガシー DB では、Migration_008 が全カードを対象とする
            // （条件なしの）インデックスを作っており、NULL の問題は起きない
            if (!MigrationHelpers.HasColumn(connection, transaction, "ic_card", "is_deleted"))
            {
                return;
            }

            // 旧インデックスを先に落とす。残したまま正規化すると、NULL だったカードが旧インデックス
            // （WHERE is_deleted = 0）の対象へ入った瞬間に重複が制約違反になり、正規化そのものが失敗する
            ExecuteNonQuery(connection, transaction,
                $"DROP INDEX IF EXISTS {IndexName}");

            ExecuteNonQuery(connection, transaction,
                "UPDATE ic_card SET is_deleted = 0 WHERE is_deleted IS NULL");

            // 正規化の後なので、008 の「is_deleted = 0 の重複」は NULL だったカードも含む
            Migration_008_AddCardTypeNumberUniqueIndex.ResolveDuplicates(connection, transaction);

            ExecuteNonQuery(connection, transaction,
                $"CREATE UNIQUE INDEX IF NOT EXISTS {IndexName} ON ic_card(card_type, card_number) WHERE is_deleted = 0 OR is_deleted IS NULL");
        }

        public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            if (!MigrationHelpers.HasColumn(connection, transaction, "ic_card", "is_deleted"))
            {
                return;
            }

            // Migration_008 の定義へ戻す（正規化した NULL は元に戻せないが、0 は有効なカードとして同じ意味）
            ExecuteNonQuery(connection, transaction,
                $"DROP INDEX IF EXISTS {IndexName}");
            ExecuteNonQuery(connection, transaction,
                $"CREATE UNIQUE INDEX IF NOT EXISTS {IndexName} ON ic_card(card_type, card_number) WHERE is_deleted = 0");
        }

        private static void ExecuteNonQuery(SQLiteConnection connection, SQLiteTransaction transaction, string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
