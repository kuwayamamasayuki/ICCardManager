#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace ICCardManager.Common.Exceptions
{
    /// <summary>
    /// データベース操作関連の例外
    /// </summary>
    public class DatabaseException : AppException
    {
        /// <summary>
        /// クエリ実行エラー
        /// </summary>
        public static DatabaseException QueryFailed(string? operation = null, Exception? innerException = null)
        {
            var message = string.IsNullOrEmpty(operation)
                ? "Database query failed"
                : $"Database query failed during: {operation}";
            const string userMessage = "データの操作中にエラーが発生しました。再度お試しください。";
            const string errorCode = "DB002";

            return innerException != null
                ? new DatabaseException(message, userMessage, errorCode, innerException)
                : new DatabaseException(message, userMessage, errorCode);
        }

        /// <summary>
        /// TEXT 列の日付が保存形式（西暦の ISO 8601）ではない（Issue #1985）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 既定カレンダーが和暦のロケールで動いた過去のビルドが書いた値
        /// （<c>08-09-01 13:05:07</c> のような和暦年）を検出したときに投げる。
        /// </para>
        /// <para>
        /// <b>黙って読み替えない理由</b>: 柔軟な解析（<c>DateTime.Parse</c> ＋ <c>InvariantCulture</c>）は
        /// この値を <c>MM-dd-yy</c> と解釈して <b>2001-08-09 を返し、例外にならない</b>。
        /// その値を再保存すると、6 年保存の台帳が「表示が狂っていただけの状態」から
        /// 「実際に書き換わった状態」へ悪化する。壊れていることが分かる形で失敗させる
        /// （development-conventions.md #1744「フォールバックが働いたことを呼び出し元が知れるか」）。
        /// </para>
        /// </remarks>
        public static DatabaseException InvalidStoredDate(string storedText, Exception? innerException = null)
        {
            var message = $"Stored date is not in ISO 8601 Gregorian form: {storedText}";
            var userMessage =
                $"保存されている日付「{storedText}」を読み取れませんでした。" +
                "西暦4桁のISO 8601形式（例: 2026-09-01 13:05:07）で保存されているべき値が、" +
                "別の形式（和暦年など）になっています。" +
                "システム管理画面（F6）からバックアップの復元を検討し、管理者に連絡してください。";
            const string errorCode = "DB009";

            return innerException != null
                ? new DatabaseException(message, userMessage, errorCode, innerException)
                : new DatabaseException(message, userMessage, errorCode);
        }

        private DatabaseException(string message, string userFriendlyMessage, string errorCode)
            : base(message, userFriendlyMessage, errorCode)
        {
        }

        private DatabaseException(string message, string userFriendlyMessage, string errorCode, Exception innerException)
            : base(message, userFriendlyMessage, errorCode, innerException)
        {
        }
    }
}
