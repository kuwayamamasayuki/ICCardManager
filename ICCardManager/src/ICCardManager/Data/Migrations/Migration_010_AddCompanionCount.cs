using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
    /// <summary>
    /// 台帳に同行者数カラムを追加するマイグレーション
    /// </summary>
    /// <remarks>
    /// Issue #1906: 複数名が同一経路を 1 枚の交通系ICカードで利用した場合に、
    /// 物品出納簿の氏名欄を「博多 花子 外１名」のようにまとめて記載できるようにする。
    /// companion_count は本人を含まない同行者の人数（既定 0）。
    /// 「外N名」の文字列は staff_name には保存せず、表示・帳票・CSV の各消費側が
    /// <see cref="ICCardManager.Common.StaffNameFormatter"/> で導出する。
    /// </remarks>
    public class Migration_010_AddCompanionCount : IMigration
    {
        public int Version => 10;
        public string Description => "台帳の同行者数カラムの追加（複数名利用の「外N名」表記対応）";

        public void Up(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // AddColumnIfNotExists で冪等化（Issue #1285）
            MigrationHelpers.AddColumnIfNotExists(
                connection, transaction,
                "ledger", "companion_count", "INTEGER NOT NULL DEFAULT 0");
        }

        public void Down(SQLiteConnection connection, SQLiteTransaction transaction)
        {
            // SQLiteでは古いバージョンとの互換性のためカラム削除は行わない
        }
    }
}
