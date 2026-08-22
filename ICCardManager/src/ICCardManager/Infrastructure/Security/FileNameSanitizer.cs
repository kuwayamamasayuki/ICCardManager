using System.Collections.Generic;
using System.IO;

namespace ICCardManager.Infrastructure.Security
{
    /// <summary>
    /// Issue #1703: 帳票ファイル名に埋め込む <c>CardType</c> / <c>CardNumber</c> 等の
    /// ユーザー入力由来の構成要素からパス区切り・不正ファイル名文字を除去し、
    /// 出力フォルダ外へのパストラバーサル（<c>..\\</c> 等）を防ぐサニタイザ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CardType</c> / <c>CardNumber</c> は CSV 取込（非空チェックのみ）や共有モードの
    /// 他 PC による DB 直接書き込み経由でパス区切り文字を含みうる。これらが
    /// <c>string.Format</c> でファイル名に素通しされると <c>Path.Combine</c> +
    /// <c>SaveAs</c> の解決時に選択フォルダを脱出しうるため、名前生成の単一チョークポイント
    /// （<c>ReportFileNameFactory.Build</c>。Issue #1820 で <c>ReportService.GetFiscalYearFileName</c>
    /// から移設）でサニタイズする。入力側検証だけでは
    /// 共有 DB 書き込み経路を塞げないため、sink 側での無害化を主防御とする。
    /// </para>
    /// <para>
    /// 危険文字は置換文字（<c>_</c>）へ置き換える。<c>Path.GetInvalidFileNameChars()</c> は
    /// プラットフォーム依存（Linux では <c>\\</c> を含まない）のため、クロスプラットフォームでの
    /// 堅牢性を確保するべく区切り文字 <c>/</c> <c>\\</c> <c>:</c> を明示的に加える。
    /// </para>
    /// <para>
    /// 本関数は idempotent: 置換文字 <c>_</c> 自体は危険文字ではないため、サニタイズ済みの
    /// 値を再度渡しても変化しない。
    /// </para>
    /// </remarks>
    public static class FileNameSanitizer
    {
        /// <summary>
        /// 危険文字を置き換える文字。ファイル名として常に有効な <c>_</c> を用いる。
        /// </summary>
        public const char ReplacementChar = '_';

        /// <summary>
        /// ファイル名構成要素として無効・危険な文字集合。
        /// <see cref="Path.GetInvalidFileNameChars"/> にパス区切り <c>/</c> <c>\\</c> <c>:</c> を加えたもの。
        /// </summary>
        private static readonly HashSet<char> InvalidChars = BuildInvalidChars();

        private static HashSet<char> BuildInvalidChars()
        {
            var set = new HashSet<char>(Path.GetInvalidFileNameChars())
            {
                '/',
                '\\',
                ':',
            };
            return set;
        }

        /// <summary>
        /// ファイル名の 1 構成要素をサニタイズする。危険文字（パス区切り・制御文字・
        /// 予約文字）を <see cref="ReplacementChar"/> に置換する。
        /// </summary>
        /// <param name="value">サニタイズ対象（null / 空はそのまま返す）</param>
        /// <returns>パス区切りを含まない安全なファイル名構成要素</returns>
        public static string SanitizeComponent(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            char[] buffer = null;
            for (int i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (System.Char.IsControl(c) || InvalidChars.Contains(c))
                {
                    if (buffer == null)
                        buffer = value.ToCharArray();
                    buffer[i] = ReplacementChar;
                }
            }

            return buffer == null ? value : new string(buffer);
        }

        /// <summary>
        /// null セーフ版。null を渡すと空文字列を返す。
        /// </summary>
        public static string SanitizeComponentOrEmpty(string value)
        {
            return SanitizeComponent(value ?? string.Empty);
        }
    }
}
