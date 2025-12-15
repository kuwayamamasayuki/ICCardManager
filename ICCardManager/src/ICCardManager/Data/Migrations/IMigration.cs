using Microsoft.Data.Sqlite;

namespace ICCardManager.Data.Migrations;

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
    void Up(SqliteConnection connection, SqliteTransaction transaction);

    /// <summary>
    /// マイグレーションをロールバック（ダウングレード）
    /// </summary>
    /// <param name="connection">データベース接続</param>
    /// <param name="transaction">トランザクション</param>
    void Down(SqliteConnection connection, SqliteTransaction transaction);
}
