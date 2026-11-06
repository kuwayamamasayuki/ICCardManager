#nullable enable
namespace ICCardManager.Common
{
    /// <summary>
    /// 登録の書き込みが重複以外の理由で失敗したときの案内（Issue #2263 / #2382）
    /// </summary>
    /// <remarks>
    /// <para>
    /// リポジトリの <c>InsertAsync</c> は一過性でない SQLite の失敗（主キーの重複のほか、ディスク満杯・読み取り専用・
    /// 破損・I/O エラー）をすべて <c>false</c> に畳む（#1951 / #2001）。呼び出し側は読み直して行があるかを確かめ、
    /// 行があれば <see cref="ConcurrencyConflictMessage.ForRegister"/>、無ければこの文言で案内する。
    /// </para>
    /// <para>
    /// 職員管理画面（#2263）と交通系ICカード管理画面（#2382）で同じ判断・同じ文言を使うため、ここへ 1 か所に置く（#1763）。
    /// 原因は断定しない。語彙は <c>ExceptionMessageFormatter</c> の SQLite の文言（「データベースの読み書きができませんでした」）にそろえる。
    /// 技術的な詳細はリポジトリ（<c>DbContext.LogNonTransientWriteFailure</c>）がログへ残している。
    /// </para>
    /// </remarks>
    public static class RegisterWriteFailedMessage
    {
        /// <summary>
        /// 案内を組み立てる
        /// </summary>
        /// <param name="target">登録しようとした対象の説明（「職員「…」」「交通系ICカード「…」」）。「何が」に相当する</param>
        public static string Build(string target)
            => $"{target}を登録できませんでした。データベースへの書き込みに失敗しました" +
               "（保存先の空き容量・書き込み権限・共有フォルダーの接続が原因の可能性があります）。" +
               "これらを確認してから、もう一度「保存」を押してください。詳細はログファイルを確認してください。";
    }
}
