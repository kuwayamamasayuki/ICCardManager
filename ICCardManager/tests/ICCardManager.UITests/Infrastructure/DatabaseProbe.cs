using System;
using System.Data.SQLite;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// アプリが書いたデータベースを、テストから読み返す（Issue #2190）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 画面の表示だけを表明すると「トーストは出たが記録されていない」を見逃す。貸出・返却の回帰テストは
    /// 台帳（<c>ledger</c>）と <c>ic_card</c> を読み返して、記録そのものを表明する。
    /// </para>
    /// <para>
    /// アプリの起動中に別の接続で読む。読み取りは読み取り専用で開き、1 回の問い合わせごとに閉じる（アプリの書き込みを妨げない）。
    /// 例外として <see cref="ExecuteAsAnotherPc"/> だけは書き込み用に開く（他の PC の変更を再現するため。Issue #2474）。
    /// journal_mode は DELETE なので、コミット済みの内容はファイルから読める。
    /// </para>
    /// </remarks>
    internal static class DatabaseProbe
    {
        /// <summary>スカラー値を 1 つ読む。行が無ければ null。</summary>
        public static object? Scalar(string sql, params (string name, object value)[] parameters)
        {
            using var conn = new SQLiteConnection($"Data Source={AppFixture.DatabasePath};Version=3;Read Only=True");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            var result = cmd.ExecuteScalar();
            return result is DBNull ? null : result;
        }

        /// <summary>件数を読む（<c>SELECT COUNT(*) …</c>）。</summary>
        public static long Count(string sql, params (string name, object value)[] parameters) =>
            Convert.ToInt64(Scalar(sql, parameters) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// 他の PC の操作として書き込む（Issue #2474）。影響行数を返す。
        /// </summary>
        /// <remarks>
        /// 共有フォルダーモードで、アプリが職員証の認証を待つ間に他の PC が対象を変更した状況を作るためだけに使う。
        /// 1 回の書き込みごとに開いて閉じる（アプリの接続とロックを取り合わない）。
        /// </remarks>
        public static int ExecuteAsAnotherPc(string sql, params (string name, object value)[] parameters)
        {
            using var conn = new SQLiteConnection($"Data Source={AppFixture.DatabasePath};Version=3");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            return cmd.ExecuteNonQuery();
        }
    }
}
