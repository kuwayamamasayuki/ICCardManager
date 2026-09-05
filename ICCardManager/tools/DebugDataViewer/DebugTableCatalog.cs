using System;
using System.Collections.Generic;
using System.Linq;

namespace DebugDataViewer
{
    /// <summary>
    /// DebugDataViewer が閲覧できるテーブル名の一覧（Issue #2012）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ComboBox の選択肢と、SQL 組み立て前のサニタイズは<b>同じ一覧</b>でなければならない。
    /// 以前は <c>MainViewModel</c> の 2 か所に同じ 6 個が別々に書かれており、
    /// テーブルが増えたときに片方だけ更新される形になっていた
    /// （<c>error-messages.md</c>「対応表は 1 か所に集約する」#1744）。
    /// </para>
    /// <para>
    /// 回帰は <c>DebugTableCatalogTests</c> が、実際に <c>InitializeDatabase()</c> した
    /// DB の <c>sqlite_master</c> から導出した集合との一致で固定する。
    /// 値を並べただけのテストは、テーブルが増えたときに追随できない（#1764）。
    /// </para>
    /// </remarks>
    public static class DebugTableCatalog
    {
        private static readonly string[] Tables =
        {
            "staff",
            "ic_card",
            "ledger",
            "ledger_detail",
            "ledger_merge_history",
            "operation_log",
            "settings",
            "schema_migrations"
        };

        /// <summary>閲覧できるテーブル名（表示順）</summary>
        public static IReadOnlyList<string> TableNames => Tables;

        /// <summary>
        /// テーブル名が一覧に含まれるかを判定する（SQL インジェクション対策のサニタイズ）。
        /// </summary>
        public static bool IsKnownTable(string tableName)
        {
            return !string.IsNullOrEmpty(tableName)
                && Tables.Contains(tableName, StringComparer.Ordinal);
        }
    }
}
