using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Data.SQLite;

namespace ICCardManager.Data.Migrations
{
/// <summary>
    /// マイグレーションインターフェース
    /// </summary>
    public interface IMigration
    {
        /// <summary>
        /// マイグレーションバージョン番号
        /// </summary>
        int Version { get; }

        /// <summary>
        /// マイグレーションの説明
        /// </summary>
        string Description { get; }

        /// <summary>
        /// マイグレーションを適用（アップグレード）
        /// </summary>
        /// <param name="connection">データベース接続</param>
        /// <param name="transaction">トランザクション</param>
        void Up(SQLiteConnection connection, SQLiteTransaction transaction);

        /// <summary>
        /// マイグレーションをロールバック（ダウングレード）
        /// </summary>
        /// <param name="connection">データベース接続</param>
        /// <param name="transaction">トランザクション</param>
        void Down(SQLiteConnection connection, SQLiteTransaction transaction);
    }
}
