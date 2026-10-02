#nullable enable

using System.Text.Encodings.Web;
using System.Text.Json;

namespace ICCardManager.Common
{
    /// <summary>
    /// 操作ログ（<c>operation_log</c>）の <c>before_data</c> / <c>after_data</c> を書く JSON の書式（Issue #1996 / #2162）
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OperationLogger</c>（業務操作の記録）と <c>MigrationRunner</c>（マイグレーションの記録）の 2 か所が
    /// 同じ <c>operation_log</c> へ書く。書式をそれぞれに書き写すと、次にエスケープ規則を変える人が
    /// 片方を取りこぼすため、ここ 1 か所に置く（#1763）。
    /// </para>
    /// <para>
    /// シリアライズのたびに <see cref="JsonSerializerOptions"/> を作ると、型ごとのメタデータのキャッシュが
    /// 使い回されない（CA1869）。共有する。最初のシリアライズ以降、このインスタンスは変更できない。
    /// </para>
    /// </remarks>
    internal static class OperationLogJson
    {
        /// <summary>
        /// 日本語を <c>\u</c> エスケープせず、改行を入れない書式。
        /// </summary>
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }
}
