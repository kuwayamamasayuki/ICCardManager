#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ICCardManager.Data
{
    /// <summary>
    /// データベースのスキーマのうち、本システムが作成しない「書き込みを黙って変える仕組み」を列挙する（Issue #2269）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 次のものを検出する。本システムのスキーマ（<c>Data/Migrations</c> の全マイグレーション）はいずれも作成しない
    /// （列挙の結果を <see cref="AllowedObjects"/> と照合する。現在は空）。
    /// </para>
    /// <list type="bullet">
    /// <item>トリガー・ビュー（<c>trigger:名前</c> / <c>view:名前</c>）。トリガーは以後の書き込みを黙って変えられる
    /// （例: <c>operation_log</c> への INSERT を <c>RAISE(IGNORE)</c> で捨てると、監査ログが痕跡なく欠落する）</item>
    /// <item>表・索引の定義にある制約の衝突解決句 <c>ON CONFLICT IGNORE</c> / <c>ON CONFLICT REPLACE</c>
    /// （<c>conflict:table:名前</c>）。トリガーが無くても、<c>x INTEGER DEFAULT 1 UNIQUE ON CONFLICT IGNORE</c>
    /// のような列を持つ表は 2 行目以降の INSERT を黙って捨てる（REPLACE は既存の行を黙って消す）。
    /// 本システムの書き込みが使う <c>INSERT … ON CONFLICT(key) DO UPDATE</c>（UPSERT）は DML であり、
    /// <c>sqlite_master</c> の定義には現れない</item>
    /// <item>仮想表（<c>virtual-table:名前</c>）と、定義文から種類を判別できないもの（<c>unknown:種類:名前</c>）</item>
    /// <item>生成列（<c>generated:表.列</c>）。参照動作を付けた生成列で、参照先の行の削除に合わせて行を黙って消せる</item>
    /// <item><c>pragma_</c> / <c>sqlite_</c> で始まる名前（<c>reserved-name:種類:名前</c>。SQLite 内部の正規のものは除く。<see cref="IsReservedName"/>）</item>
    /// <item>本システムのマイグレーションが作らない外部キーの参照動作（<c>CASCADE</c> / <c>SET NULL</c> / <c>SET DEFAULT</c>。
    /// <c>fk-action:…</c>。正規の組は <see cref="AllowedForeignKeyActions"/>）</item>
    /// </list>
    /// <para>
    /// 種類は <c>type</c> 列ではなく定義文の先頭で判定する（理由は <see cref="InspectSchema"/>）。
    /// </para>
    /// <para>
    /// 表の定義を丸ごと照合することはしない。古い経路（マイグレーション機構の導入前・途中の版）で作られた正当なバックアップは、
    /// 現在のマイグレーションが作る定義と文字列が一致しないことがあり、照合すると正当なリストアを塞ぐ。
    /// したがって、上記以外の定義の改変（列の既定値・CHECK 制約等）は本検査の対象外である。
    /// </para>
    /// <para>
    /// <b>この検査の限界</b>: 既知の「書き込みを黙って捨てる・消す」仕組み（トリガー・ビュー・衝突解決句・生成列・想定外の参照動作）を
    /// 拒否するもので、スキーマ全体の照合ではない。また、DB ファイルへ直接書き込める者は、同じことをリストアを経ずにできる（Issue #2261）。
    /// </para>
    /// <para>
    /// 判定はリストア（<c>BackupService.PrepareRestoreSource</c>）と起動時（<c>DbContext.InitializeDatabase</c>）の
    /// 2 か所から使う。判定を 1 か所へ寄せるのは、将来マイグレーションでこれらを作るようになったとき、
    /// 許可リストを 1 か所直せば両方が追随するようにするため（Issue #1763）。
    /// </para>
    /// </remarks>
    internal static class SchemaObjectInspector
    {
        /// <summary>
        /// 本システムが作成するもの（<see cref="FindUnexpectedObjects"/> が返す形式。現在は無し）
        /// </summary>
        /// <remarks>
        /// マイグレーションでトリガー・ビュー・衝突解決句を追加したら、ここへ同じ形式で加えること。
        /// 加えないと、そのマイグレーションを適用した DB のバックアップがリストアで拒否される。
        /// </remarks>
        internal static readonly IReadOnlyCollection<string> AllowedObjects =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex SilentConflictClausePattern = new(
            @"\bON\s+CONFLICT\s+(IGNORE|REPLACE)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// 定義文の先頭（<c>CREATE [TEMP|TEMPORARY] [UNIQUE] TRIGGER|VIEW|TABLE|INDEX</c> / <c>CREATE VIRTUAL TABLE</c>）
        /// </summary>
        private static readonly Regex DefinitionKindPattern = new(
            @"\ACREATE\s+(?:(?:TEMP|TEMPORARY)\s+)?(?:(?<virtual>VIRTUAL)\s+TABLE|(?:UNIQUE\s+)?(?<kind>TRIGGER|VIEW|TABLE|INDEX))\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// 本システムの DB であることの印にする表（どちらか一方があればよい）
        /// </summary>
        /// <remarks>
        /// <c>staff</c> はマイグレーション機構の導入前の DB にもある（<c>DbContext.HandleLegacyDatabase</c> の判定と同じ）。
        /// <c>schema_migrations</c> は導入後の DB にある。
        /// </remarks>
        private static readonly HashSet<string> ApplicationMarkerTables =
            new(StringComparer.OrdinalIgnoreCase) { "staff", "schema_migrations" };

        /// <summary>
        /// スキーマの検査結果（Issue #2269）
        /// </summary>
        internal sealed class SchemaInspection
        {
            internal SchemaInspection(
                IReadOnlyList<string> unexpectedObjects,
                bool hasApplicationTables,
                IReadOnlyCollection<string> tableNames)
            {
                UnexpectedObjects = unexpectedObjects;
                HasApplicationTables = hasApplicationTables;
                TableNames = tableNames;
            }

            /// <summary>
            /// SQLite が実際に持っている表の名前（<c>PRAGMA table_list</c>。<c>sqlite_master</c> の列の値には依存しない。大文字・小文字を区別しない）
            /// </summary>
            internal IReadOnlyCollection<string> TableNames { get; }

            /// <summary>本システムが作成しない仕組み（<see cref="FindUnexpectedObjects"/> と同じ形式）</summary>
            internal IReadOnlyList<string> UnexpectedObjects { get; }

            /// <summary>本システムの DB の印になる表（<c>staff</c> か <c>schema_migrations</c>）があるか</summary>
            internal bool HasApplicationTables { get; }
        }

        /// <summary>
        /// 本システムが作成しないトリガー・ビュー・衝突解決句等を列挙する
        /// </summary>
        /// <param name="connection">開いている接続</param>
        /// <returns>見つかったものの <c>trigger:名前</c> / <c>view:名前</c> / <c>virtual-table:名前</c> /
        /// <c>conflict:table:名前</c> / <c>unknown:種類:名前</c>（名前順）。無ければ空</returns>
        internal static IReadOnlyList<string> FindUnexpectedObjects(SQLiteConnection connection)
            => InspectSchema(connection).UnexpectedObjects;

        /// <summary>
        /// <c>sqlite_master</c> の全行を読み、本システムが作成しない仕組みと、本システムの DB の印になる表の有無を調べる
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>種類は <c>type</c> 列ではなく定義文（<c>sql</c>）の先頭で判定する</b>（コードレビューで検出）。SQLite はスキーマを読み込むとき
        /// オブジェクトの種類を定義文から決め、<c>type</c> 列は大文字・小文字を区別せずに照合するだけである。
        /// <c>PRAGMA writable_schema</c> で <c>type</c> を <c>'TRIGGER'</c> や <c>'Table'</c> へ書き換えても、トリガーは働き続けるのに、
        /// <c>type = 'trigger'</c> で絞る検査は素通りする。そこで <c>sqlite_master</c> は種類で絞らずに全行を読み、
        /// 定義文が表・索引のものだけを「本システムも作り得るもの」として扱い、それ以外（トリガー・ビュー・仮想表・判別できないもの）は
        /// すべて想定外とする。衝突解決句の走査は定義文のある全行に掛ける。
        /// </para>
        /// <para>
        /// 定義文の無い行は、SQLite が UNIQUE / PRIMARY KEY 制約のために自動で作る索引（<c>sqlite_autoindex_…</c>）だけなので、
        /// <c>type</c> が索引（大文字・小文字を問わない）なら通し、それ以外は想定外とする。
        /// </para>
        /// </remarks>
        internal static SchemaInspection InspectSchema(SQLiteConnection connection)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            var found = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT type, name, sql FROM main.sqlite_master ORDER BY name";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var type = reader.IsDBNull(0) ? string.Empty : Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    var name = reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    var sql = reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture);

                    if (IsReservedName(name))
                    {
                        found.Add($"reserved-name:{type}:{name}");
                    }

                    var kind = ClassifyDefinition(sql, type);
                    switch (kind)
                    {
                        case "table":
                        case "index":
                            break;
                        case "trigger":
                        case "view":
                        case "virtual-table":
                            found.Add($"{kind}:{name}");
                            break;
                        default:
                            found.Add($"unknown:{type}:{name}");
                            break;
                    }

                    if (HasSilentConflictClause(sql))
                    {
                        found.Add($"conflict:{(kind == "index" ? "index" : "table")}:{name}");
                    }
                }
            }

            // 表の実体は sqlite_master の列（type / name）ではなく、SQLite が解釈した結果（pragma）から得る。
            // sqlite_master の列は PRAGMA writable_schema で書き換えられ、SQLite は表の種類・名前を定義文から決めるため。
            // pragma は表値関数の形（SELECT … FROM pragma_table_list）で呼ばない。同じ名前の普通の表
            // （CREATE TABLE pragma_table_list(…)。writable_schema も要らない）があると SQLite は表を関数より先に解決し、
            // 一覧が偽装される（コードレビューで検出）。PRAGMA 文の形は表で影を付けられない
            var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA main.table_list";
                using var reader = command.ExecuteReader();
                var nameOrdinal = reader.GetOrdinal("name");
                var typeOrdinal = reader.GetOrdinal("type");
                while (reader.Read())
                {
                    var name = reader.GetString(nameOrdinal);
                    var type = reader.GetString(typeOrdinal);
                    if (string.Equals(type, "table", StringComparison.OrdinalIgnoreCase))
                    {
                        tableNames.Add(name);
                    }
                    else if (!string.Equals(type, "shadow", StringComparison.OrdinalIgnoreCase))
                    {
                        // view / virtual（定義文の判定でも拾うが、SQLite の解釈でも拾う）
                        found.Add($"{(type == "virtual" ? "virtual-table" : type)}:{name}");
                    }
                }
            }

            // SQLite 内部の表（sqlite_sequence・sqlite_stat1〜4。それ以外の sqlite_ 名は上で予約名として拒否済み）は生成列・参照動作の検査から外す。
            // 列を足すことはできず、仮に参照動作を付けても消えるのはその表自身の行で、監査ログ等の業務の表には影響しない
            foreach (var table in tableNames.Where(t => !t.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)))
            {
                found.AddRange(FindGeneratedColumns(connection, table));
                found.AddRange(FindUnexpectedForeignKeyActions(connection, table));
            }

            var hasApplicationTables = ApplicationMarkerTables.Any(tableNames.Contains);

            return new SchemaInspection(
                found.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(o => !AllowedObjects.Contains(o))
                    .OrderBy(o => o, StringComparer.Ordinal)
                    .ToList(),
                hasApplicationTables,
                tableNames);
        }

        /// <summary>
        /// 生成列（<c>GENERATED ALWAYS AS (…)</c>）を列挙する（<c>generated:表.列</c>）
        /// </summary>
        /// <remarks>
        /// 本システムは生成列を作らない。生成列に外部キーの参照動作（<c>ON DELETE CASCADE</c> 等）を付けると、
        /// 参照先の行の削除で行が黙って消える（例: <c>operation_log</c> に <c>ledger(id)</c> を参照する生成列を足すと、
        /// 台帳の行の削除で監査ログが痕跡なく消える）。判定は字句ではなく SQLite の解釈（<c>PRAGMA table_xinfo</c> の
        /// <c>hidden</c> が 2＝仮想・3＝格納の生成列）で行う。
        /// </remarks>
        private static IEnumerable<string> FindGeneratedColumns(SQLiteConnection connection, string table)
        {
            var found = new List<string>();
            using var command = connection.CreateCommand();
            // 表値関数の形は影を付けられるので PRAGMA 文の形で呼ぶ（InspectSchema の注記）。PRAGMA の引数は引数束縛できないため、識別子として引用する
            command.CommandText = $"PRAGMA main.table_xinfo({QuoteIdentifier(table)})";
            using var reader = command.ExecuteReader();
            var nameOrdinal = reader.GetOrdinal("name");
            var hiddenOrdinal = reader.GetOrdinal("hidden");
            while (reader.Read())
            {
                var hidden = Convert.ToInt64(reader.GetValue(hiddenOrdinal), System.Globalization.CultureInfo.InvariantCulture);
                if (hidden == 2 || hidden == 3)
                {
                    found.Add($"generated:{table}.{reader.GetString(nameOrdinal)}");
                }
            }

            return found;
        }

        /// <summary>
        /// 本システムのマイグレーションが作らない外部キーの参照動作（<c>CASCADE</c> / <c>SET NULL</c> / <c>SET DEFAULT</c>）を列挙する
        /// </summary>
        /// <remarks>
        /// 参照動作は、参照先の行の削除・更新に合わせて参照元の行を黙って消す・書き換える。本システムが作るのは
        /// <see cref="AllowedForeignKeyActions"/> の組だけ（<c>NO ACTION</c> / <c>RESTRICT</c> は書き込みを失敗させるので対象外）。
        /// 判定は字句ではなく SQLite の解釈（<c>PRAGMA foreign_key_list</c>）で行う。
        /// </remarks>
        private static IEnumerable<string> FindUnexpectedForeignKeyActions(SQLiteConnection connection, string table)
        {
            var found = new List<string>();
            using var command = connection.CreateCommand();
            // 表値関数の形は影を付けられるので PRAGMA 文の形で呼ぶ（InspectSchema の注記）
            command.CommandText = $"PRAGMA main.foreign_key_list({QuoteIdentifier(table)})";
            using var reader = command.ExecuteReader();
            string Read(string column)
            {
                var ordinal = reader.GetOrdinal(column);
                return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
            }

            while (reader.Read())
            {
                var referenced = Read("table");
                var from = Read("from");
                var to = Read("to");
                foreach (var (clause, action) in new[]
                {
                    ("on_update", Read("on_update")),
                    ("on_delete", Read("on_delete")),
                })
                {
                    if (IsSilentForeignKeyAction(action))
                    {
                        found.Add(FormatForeignKeyAction(table, from, referenced, to, clause, action));
                    }
                }
            }

            return found.Where(f => !AllowedForeignKeyActions.Contains(f));
        }

        /// <summary>
        /// SQLite の識別子として二重引用符で囲む（中の二重引用符は 2 つ重ねる）
        /// </summary>
        internal static string QuoteIdentifier(string identifier)
            => "\"" + identifier.Replace("\"", "\"\"") + "\"";

        /// <summary>
        /// SQLite が内部で作る正規の名前（<c>sqlite_sequence</c>・<c>sqlite_stat1</c>〜<c>4</c>・<c>sqlite_autoindex_…</c>）
        /// </summary>
        private static readonly HashSet<string> InternalSqliteNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "sqlite_sequence", "sqlite_stat1", "sqlite_stat2", "sqlite_stat3", "sqlite_stat4",
        };

        /// <summary>
        /// <c>pragma_</c> / <c>sqlite_</c> で始まる、利用者が作るべきでない名前か（SQLite 内部の正規のものは除く）
        /// </summary>
        /// <remarks>
        /// <c>pragma_</c> で始まる表は、表値関数の形の pragma（<c>pragma_table_list</c> 等）に影を付けて結果を偽装できる
        /// （コードレビューで検出）。本検査は PRAGMA 文の形で呼ぶので影響を受けないが、本システムが作らない名前であり、
        /// 他の検査・ツールを欺く目的でしか置かれないので拒否する。<c>sqlite_</c> は SQLite の予約名で、
        /// <c>writable_schema</c> を使わない限り作れない。
        /// </remarks>
        internal static bool IsReservedName(string name)
        {
            if (name.StartsWith("pragma_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !InternalSqliteNames.Contains(name)
                && !name.StartsWith("sqlite_autoindex_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSilentForeignKeyAction(string action)
            => string.Equals(action, "CASCADE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "SET NULL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "SET DEFAULT", StringComparison.OrdinalIgnoreCase);

        internal static string FormatForeignKeyAction(
            string table, string from, string referencedTable, string to, string clause, string action)
            => $"fk-action:{table}.{from}->{referencedTable}.{to}:{clause} {action}";

        /// <summary>
        /// 本システムのマイグレーションが作る外部キーの参照動作（<see cref="FormatForeignKeyAction"/> の形式）
        /// </summary>
        /// <remarks>
        /// <c>ledger_detail.ledger_id</c> → <c>ledger(id)</c> の <c>ON DELETE CASCADE</c> だけ（Migration_001 / 002 / 003 / 011。
        /// 途中の版の作り直しでも同じ組で、旧 DB も同じ）。マイグレーションで参照動作を足したら、ここへ同じ形式で加えること。
        /// </remarks>
        internal static readonly IReadOnlyCollection<string> AllowedForeignKeyActions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                FormatForeignKeyAction("ledger_detail", "ledger_id", "ledger", "id", "on_delete", "CASCADE"),
            };

        /// <summary>
        /// 定義文の先頭から種類（<c>table</c> / <c>index</c> / <c>trigger</c> / <c>view</c> / <c>virtual-table</c>）を判定する。判別できなければ null
        /// </summary>
        /// <param name="sql">定義文（null なら自動索引として <paramref name="type"/> で判定する）</param>
        /// <param name="type"><c>sqlite_master.type</c>（定義文が無いときの補助）</param>
        internal static string? ClassifyDefinition(string? sql, string type)
        {
            if (sql is null)
            {
                return string.Equals(type, "index", StringComparison.OrdinalIgnoreCase) ? "index" : null;
            }

            var match = DefinitionKindPattern.Match(StripLiteralsAndComments(sql).TrimStart());
            if (!match.Success)
            {
                return null;
            }

            if (match.Groups["virtual"].Success)
            {
                return "virtual-table";
            }

            return match.Groups["kind"].Value.ToLowerInvariant();
        }

        /// <summary>
        /// 定義文に、書き込みを黙って捨てる・消す制約の衝突解決句（<c>ON CONFLICT IGNORE</c> / <c>ON CONFLICT REPLACE</c>）があるか
        /// </summary>
        /// <remarks>
        /// 大文字・小文字と空白（改行を含む）のゆれを許す。文字列リテラル・引用符で囲んだ識別子・コメントの中は見ない
        /// （<c>DEFAULT 'ON CONFLICT IGNORE'</c> のような値で誤って拒否しないため）。
        /// <c>ROLLBACK</c> / <c>ABORT</c> / <c>FAIL</c> は書き込みを失敗させる（黙らない）ので対象外。
        /// </remarks>
        internal static bool HasSilentConflictClause(string? sql)
        {
            if (sql is null || sql.Length == 0)
            {
                return false;
            }

            return SilentConflictClausePattern.IsMatch(StripLiteralsAndComments(sql));
        }

        /// <summary>
        /// 文字列リテラル・引用符で囲んだ識別子（<c>"…"</c> / <c>[…]</c> / <c>`…`</c>）・コメントを空白に置き換える
        /// </summary>
        private static string StripLiteralsAndComments(string sql)
        {
            var result = new StringBuilder(sql.Length);
            var i = 0;
            while (i < sql.Length)
            {
                var c = sql[i];
                if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                {
                    var end = sql.IndexOf('\n', i);
                    i = end < 0 ? sql.Length : end;
                    result.Append(' ');
                }
                else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                {
                    var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? sql.Length : end + 2;
                    result.Append(' ');
                }
                else if (c == '\'' || c == '"' || c == '`')
                {
                    i = SkipQuoted(sql, i, c);
                    result.Append(' ');
                }
                else if (c == '[')
                {
                    var end = sql.IndexOf(']', i + 1);
                    i = end < 0 ? sql.Length : end + 1;
                    result.Append(' ');
                }
                else
                {
                    result.Append(c);
                    i++;
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// 引用符で始まる区間を読み飛ばし、閉じ引用符の次の位置を返す（引用符 2 つ重ねはエスケープ）
        /// </summary>
        private static int SkipQuoted(string sql, int start, char quote)
        {
            var i = start + 1;
            while (i < sql.Length)
            {
                if (sql[i] == quote)
                {
                    if (i + 1 < sql.Length && sql[i + 1] == quote)
                    {
                        i += 2;
                        continue;
                    }

                    return i + 1;
                }

                i++;
            }

            return sql.Length;
        }
    }
}
