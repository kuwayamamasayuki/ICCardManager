using System;
using System.Data.SQLite;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// アプリのデータベースに排他ロックを掛け、アプリの読み書きを待たせる（Issue #2196）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「処理中」の状態を UI テストから作るために使う。処理は通常数十ミリ秒で終わり、その間に閉じる操作を送り込むことはできない。
    /// 別の接続で <c>BEGIN EXCLUSIVE</c> を取っておくと、アプリの DB アクセスは busy_timeout の間待たされる。
    /// </para>
    /// <para>
    /// <b>待たせる処理は、別スレッドで DB にアクセスするものを選ぶこと</b>（実測で判明）。UI スレッドの上で DB を待つ処理
    /// （例: 設定の保存）を待たせると UI スレッドごと止まり、UIA の問い合わせが <c>COMException</c>（タイムアウト）になる。
    /// UI スレッドが止まっている間は閉じる操作もそもそも処理されないので、処理中のガードを検査したことにならない。
    /// </para>
    /// <para>
    /// ロックの取得・解放は SQL（<c>BEGIN EXCLUSIVE</c> / <c>ROLLBACK</c>）で直接行う。<see cref="Dispose"/> でロックを外す。
    /// </para>
    /// </remarks>
    internal sealed class DatabaseExclusiveLock : IDisposable
    {
        private readonly SQLiteConnection _connection;
        private bool _held;

        private DatabaseExclusiveLock(SQLiteConnection connection)
        {
            _connection = connection;
            _held = true;
        }

        /// <summary>排他ロックを取る。</summary>
        public static DatabaseExclusiveLock Acquire()
        {
            var connection = new SQLiteConnection($"Data Source={AppFixture.DatabasePath};Version=3");
            connection.Open();
            try
            {
                Execute(connection, "BEGIN EXCLUSIVE");
                return new DatabaseExclusiveLock(connection);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        /// <summary>ロックを外す（ロールバック）。二度呼んでもよい。</summary>
        public void Release()
        {
            if (!_held)
            {
                return;
            }

            _held = false;
            Execute(_connection, "ROLLBACK");
        }

        public void Dispose()
        {
            try
            {
                Release();
            }
            finally
            {
                _connection.Dispose();
            }
        }

        private static void Execute(SQLiteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
