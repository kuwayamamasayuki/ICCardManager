using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// カード種別＋管理番号のユニークインデックスを追加するマイグレーション
    /// </summary>
    /// <remarks>
    /// Issue #1106: 共有フォルダモードで複数PCから同一種別のカードを同時に登録すると、
    /// GetNextCardNumberAsync の SELECT MAX + 1 パターンにより同じ管理番号が採番される。
    /// 部分ユニークインデックス（is_deleted = 0）を追加し、有効なカード間での番号重複を防止する。
    /// </remarks>
    public class Migration_008_AddCardTypeNumberUniqueIndex : IMigration
    {
        public int Version => 8;
        public string Description => "カード種別＋管理番号のユニークインデックス追加";

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // 既存データの重複を解消（安全策: 同一種別・同一番号の有効カードが複数あれば番号をずらす）
            ResolveDuplicates(connection, transaction);

            // 部分ユニークインデックスを追加
            // is_deleted = 0 のカードのみ対象（削除済みカードは重複を許容する）
            // レガシーDBではis_deletedカラムが存在しない場合があるが、
            // Migration_001が先に適用されるため通常はis_deletedが存在する
            var hasIsDeleted = HasColumn(connection, transaction, "ic_card", "is_deleted");
            if (hasIsDeleted)
            {
                ExecuteNonQuery(connection, transaction,
                    "CREATE UNIQUE INDEX IF NOT EXISTS idx_card_type_number_active ON ic_card(card_type, card_number) WHERE is_deleted = 0");
            }
            else
            {
                // is_deletedがない場合は全カードを対象とするユニークインデックス
                ExecuteNonQuery(connection, transaction,
                    "CREATE UNIQUE INDEX IF NOT EXISTS idx_card_type_number_active ON ic_card(card_type, card_number)");
            }
        }

        public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            ExecuteNonQuery(connection, transaction,
                "DROP INDEX IF EXISTS idx_card_type_number_active");
        }

        /// <summary>
        /// 既存データに同一種別・同一番号の有効カードが複数ある場合、番号をずらして重複を解消する
        /// </summary>
        private static void ResolveDuplicates(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // is_deletedカラムの存在を確認（レガシーDBでは存在しない場合がある）
            var hasIsDeleted = HasColumn(connection, transaction, "ic_card", "is_deleted");
            var whereClause = hasIsDeleted ? "WHERE is_deleted = 0" : "";

            // 重複しているcard_type + card_numberの組を検出
            using var findCmd = connection.CreateCommand();
            findCmd.Transaction = transaction;
            findCmd.CommandText = $@"SELECT card_type, card_number, COUNT(*) as cnt
FROM ic_card
{whereClause}
GROUP BY card_type, card_number
HAVING cnt > 1";

            var duplicates = new System.Collections.Generic.List<(string cardType, string cardNumber)>();
            using (var reader = findCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    duplicates.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            foreach (var (cardType, cardNumber) in duplicates)
            {
                // 重複カードのIDmを取得（最初の1件は元の番号を保持、残りをリナンバー）
                using var listCmd = connection.CreateCommand();
                listCmd.Transaction = transaction;
                var listWhere = hasIsDeleted
                    ? "WHERE card_type = @cardType AND card_number = @cardNumber AND is_deleted = 0"
                    : "WHERE card_type = @cardType AND card_number = @cardNumber";
                listCmd.CommandText = $@"SELECT card_idm FROM ic_card
{listWhere}
ORDER BY card_idm
LIMIT -1 OFFSET 1";
                listCmd.Parameters.AddWithValue("@cardType", cardType);
                listCmd.Parameters.AddWithValue("@cardNumber", cardNumber);

                var duplicateIdms = new System.Collections.Generic.List<string>();
                using (var reader = listCmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        duplicateIdms.Add(reader.GetString(0));
                    }
                }

                // 重複カードに新しい番号を割り当て
                foreach (var idm in duplicateIdms)
                {
                    // 現在の最大番号を取得して+1
                    using var maxCmd = connection.CreateCommand();
                    maxCmd.Transaction = transaction;
                    maxCmd.CommandText = @"SELECT MAX(CAST(card_number AS INTEGER))
FROM ic_card WHERE card_type = @cardType";
                    maxCmd.Parameters.AddWithValue("@cardType", cardType);
                    var maxResult = maxCmd.ExecuteScalar();
                    var nextNumber = (maxResult == System.DBNull.Value ? 0 : System.Convert.ToInt32(maxResult)) + 1;

                    using var updateCmd = connection.CreateCommand();
                    updateCmd.Transaction = transaction;
                    updateCmd.CommandText = "UPDATE ic_card SET card_number = @newNumber WHERE card_idm = @cardIdm";
                    updateCmd.Parameters.AddWithValue("@newNumber", nextNumber.ToString());
                    updateCmd.Parameters.AddWithValue("@cardIdm", idm);
                    updateCmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// 指定テーブルに指定カラムが存在するかを確認
        /// </summary>
        private static bool HasColumn(SQLiteConnection connection, SQLiteTransaction transaction, string tableName, string columnName)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"PRAGMA table_info({tableName})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1) == columnName)
                    return true;
            }
            return false;
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
