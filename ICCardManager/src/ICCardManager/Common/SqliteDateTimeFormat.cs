using System;
using System.Data.SQLite;
using System.Globalization;
using ICCardManager.Common.Exceptions;

namespace ICCardManager.Common
{
    /// <summary>
    /// SQLite の TEXT 列に保存する日付（ISO 8601 <c>yyyy-MM-dd HH:mm:ss</c>）と
    /// <see cref="DateTime"/> の相互変換を担う唯一の手段（Issue #1985）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>なぜ集約するのか</b>: DB の日付は TEXT 型で保存する規約（CLAUDE.md「DB設計原則」）だが、
    /// <see cref="DateTime.ToString(string)"/> / <see cref="DateTime.Parse(string)"/> は
    /// <see cref="CultureInfo.CurrentCulture"/> のカレンダーに従う。既定カレンダーが
    /// <see cref="JapaneseCalendar"/> のロケールでは <c>yyyy</c> が和暦年（令和 8 年 → <c>0008</c>）になり、
    /// <b>6 年保存の台帳の日付が壊れる</b>。SQL 側は <c>date()</c> 関数と文字列比較で範囲を絞るため、
    /// 月次帳票・履歴の期間検索・6 年経過データの削除がすべて狂う。
    /// </para>
    /// <para>
    /// 書式文字列とカルチャの指定を呼び出し元へ配ると、次に列を足す人が配り忘れる
    /// （<c>development-conventions.md</c> #1763「同じ論理的な処理に手段が 2 通りあるか」）。
    /// <b>呼び出し元は書式文字列を直書きせず、本クラスのメソッドを使うこと。</b>
    /// 規約は <c>InvariantCultureDateConventionTests</c> が静的検査で固定する。
    /// </para>
    /// </remarks>
    public static class SqliteDateTimeFormat
    {
        /// <summary>日時列の書式（ISO 8601）。</summary>
        public const string DateTimePattern = "yyyy-MM-dd HH:mm:ss";

        /// <summary>日付のみの書式（SQLite の <c>date()</c> 関数の戻り値と同形）。</summary>
        public const string DatePattern = "yyyy-MM-dd";

        /// <summary>年月キーの書式（月次集計のグループキー）。</summary>
        public const string MonthPattern = "yyyy-MM";

        /// <summary>
        /// 日時を <c>yyyy-MM-dd HH:mm:ss</c> のテキストへ整形する。
        /// </summary>
        public static string ToText(DateTime value)
            => value.ToString(DateTimePattern, CultureInfo.InvariantCulture);

        /// <summary>
        /// 日時を <c>yyyy-MM-dd HH:mm:ss</c> のテキストへ整形する。
        /// <c>null</c> のときは <c>null</c> を返す（テキスト列の値として使う場合は
        /// <see cref="ToTextOrDbNull(DateTime?)"/> を使うこと）。
        /// </summary>
        public static string ToText(DateTime? value)
            => value.HasValue ? ToText(value.Value) : null;

        /// <summary>
        /// 日時を <see cref="SQLiteParameter"/> の値として使えるオブジェクトへ変換する。
        /// <c>null</c> のときは <see cref="DBNull.Value"/> を返す。
        /// </summary>
        public static object ToTextOrDbNull(DateTime? value)
            => value.HasValue ? (object)ToText(value.Value) : DBNull.Value;

        /// <summary>
        /// 日付を <c>yyyy-MM-dd</c> のテキストへ整形する（時刻部分は捨てる）。
        /// </summary>
        public static string ToDateText(DateTime value)
            => value.ToString(DatePattern, CultureInfo.InvariantCulture);

        /// <summary>
        /// 日付を <c>yyyy-MM-dd</c> のテキストへ整形する。<c>null</c> のときは <c>null</c> を返す。
        /// </summary>
        public static string ToDateText(DateTime? value)
            => value.HasValue ? ToDateText(value.Value) : null;

        /// <summary>
        /// 日付を <c>yyyy-MM</c> の年月キーへ整形する。
        /// </summary>
        public static string ToMonthKey(DateTime value)
            => value.ToString(MonthPattern, CultureInfo.InvariantCulture);

        /// <summary>
        /// その日の開始時刻（00:00:00）のテキスト。期間検索の下限に使う。
        /// </summary>
        public static string ToDayStartText(DateTime value)
            => ToText(value.Date);

        /// <summary>
        /// その日の終了時刻（23:59:59）のテキスト。期間検索の上限に使う。
        /// </summary>
        public static string ToDayEndText(DateTime value)
            => ToText(value.Date.AddDays(1).AddSeconds(-1));

        /// <summary>
        /// DB の TEXT 列に入り得る書式（<see cref="ParseStored"/> / <see cref="TryParseStored"/> が受け付ける形）。
        /// </summary>
        /// <remarks>
        /// 本システムが書く <c>yyyy-MM-dd HH:mm:ss</c> のほか、SQLite の <c>date()</c> の戻り値
        /// （<c>yyyy-MM-dd</c>）と、ISO 8601 の <c>T</c> 区切りを許容する。
        /// </remarks>
        private static readonly string[] StoredPatterns =
        {
            DateTimePattern,
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-dd HH:mm",
            DatePattern,
        };

        /// <summary>
        /// DB の TEXT 列の値を <see cref="DateTime"/> へ復元する（<b>書式を限定した厳格版</b>）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>DB から読んだ値は必ずこちらを使う。</b> 柔軟な <see cref="Parse(string)"/> は
        /// <c>InvariantCulture</c> の一般規則で解釈するため、Issue #1985 の欠陥を実際に踏んだ環境が
        /// 書き込んだ和暦テキスト <c>"08-09-01 13:05:07"</c> を <b>例外にせず 2001-08-09 として読む</b>
        /// （invariant は <c>MM-dd-yy</c> と解釈する）。その値を再保存すると、6 年保存の台帳が
        /// 「表示が狂っていただけの状態」から「実際に書き換わった状態」へ悪化する。
        /// </para>
        /// <para>
        /// 厳格版は <c>yyyy</c> に 4 桁を要求する（実測）ため、同じテキストを<b>受け付けず</b>
        /// <see cref="DatabaseException.InvalidStoredDate"/> を投げる。**壊れていることが分かる形で
        /// 失敗させる**のが狙いで、黙って別の日付として読み替えるより望ましい
        /// （development-conventions.md #1744「フォールバックが働いたことを呼び出し元が知れるか」）。
        /// </para>
        /// <para>
        /// この選択は「壊れた台帳を持つ環境で履歴が開けなくなる」という代償を伴う。それでも読み替えを
        /// 選ばないのは、読み替えた値を再保存すると<b>6 年保存の監査台帳が実際に書き換わる</b>ためで、
        /// 復旧手段（バックアップからの復元）は文言で案内する。
        /// </para>
        /// <para>
        /// CSV の利用者入力（<c>2026/09/01</c> 等）や <c>operation_log</c> の JSON
        /// （<c>2026-09-01T13:05:07.1234567</c>）は書式が異なるため、それらは
        /// <see cref="Parse(string)"/> / <see cref="TryParse(string, out DateTime)"/> を使う。
        /// </para>
        /// </remarks>
        /// <exception cref="DatabaseException">
        /// <paramref name="text"/> が <see cref="StoredPatterns"/> のいずれにも一致しない場合。
        /// <see cref="AppException"/> 派生なので、捕捉漏れがあっても
        /// 「予期しないエラー（SYS999）」ではなく整備済みの案内へ倒れる（#1757）。
        /// </exception>
        public static DateTime ParseStored(string text)
            => TryParseStored(text, out var value)
                ? value
                : throw DatabaseException.InvalidStoredDate(text);

        /// <summary>
        /// DB の TEXT 列の値を <see cref="DateTime"/> へ復元する（厳格版）。
        /// 解析できない場合は <c>false</c>。
        /// </summary>
        public static bool TryParseStored(string text, out DateTime value)
            => DateTime.TryParseExact(
                text, StoredPatterns, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

        /// <summary>
        /// ISO 8601 のテキストを <see cref="DateTime"/> へ復元する（書式を限定しない柔軟版）。
        /// 解析できない場合は例外。
        /// </summary>
        /// <remarks>
        /// 利用者が編集し得る CSV や、<c>JsonSerializer</c> が書いたラウンドトリップ書式のように
        /// <see cref="StoredPatterns"/> に収まらない入力向け。
        /// <b>DB の TEXT 列を読むときは <see cref="ParseStored"/> を使うこと</b>（理由はそちらの XML doc）。
        /// </remarks>
        /// <exception cref="FormatException">
        /// <paramref name="text"/> が日時として解析できない場合。
        /// </exception>
        public static DateTime Parse(string text)
            => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None);

        /// <summary>
        /// ISO 8601 のテキストを <see cref="DateTime"/> へ復元する（柔軟版）。
        /// 解析できない場合は <c>false</c>。
        /// </summary>
        public static bool TryParse(string text, out DateTime value)
            => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}
