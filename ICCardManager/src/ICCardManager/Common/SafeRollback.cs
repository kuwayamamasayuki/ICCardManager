using System;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Common
{
    /// <summary>
    /// <c>catch</c> の中の後始末（ロールバック）を、二次例外で本来の失敗要因を潰さずに行う
    /// （Issue #1745 / #1763 / #1831）
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ロールバックは失敗し得る</b>。<c>COMMIT</c> が SQLITE_BUSY 等で失敗すると SQLite 側は
    /// 既に自動ロールバック済みで <c>SQLiteTransaction</c> が無効化されており、続けて
    /// <c>Rollback()</c> を呼ぶと <see cref="InvalidOperationException"/>
    /// （"No transaction is active on this connection"）になる。接続断でも同様に失敗する。
    /// </para>
    /// <para>
    /// <c>catch</c> の中で素の <c>Rollback()</c> を呼ぶと、この二次例外が
    /// <b>本来の失敗要因を置き換えて</b> 抜けてしまい、次の三重の害になる。
    /// </para>
    /// <list type="number">
    /// <item>その <c>catch</c> に書いた <c>LogError</c> が実行されず、障害調査の手掛かりが残らない</item>
    /// <item>ドメイン例外へのラップが飛ばされ、既定分岐から生の <c>ex.Message</c> が UI へ出る（Issue #1614 違反）</item>
    /// <item>
    /// <c>DbContext.ExecuteWithRetryAsync</c> の
    /// <c>catch (SQLiteException ex) when (ex.ResultCode == Busy || Locked)</c> に一致せず、
    /// <b>共有モード対策のリトライが丸ごと効かなくなる</b>
    /// </item>
    /// </list>
    /// <para>
    /// 握りつぶしても<b>データが確定することはない</b>。書き込みが確定するのは <c>Commit()</c> が
    /// 成功したときだけで、未コミットのトランザクションは <c>TransactionScope.Dispose()</c>
    /// （＝接続リースの解放）または <c>SQLiteTransaction.Dispose()</c> で必ず巻き戻る（二重の巻き戻し）。
    /// </para>
    /// <para>
    /// <b>この型を経由することが唯一の正規手段</b>。クラスごとに同じ private ヘルパーを増やすと、
    /// 次に規約を変える人が一部を取りこぼす（#1763「同じ論理的な書き込みに手段が 2 通りあるか」）。
    /// 規約の遵守は <c>SafeRollbackConventionTests</c> がソーステキストの静的検査で固定する。
    /// </para>
    /// </remarks>
    public static class SafeRollback
    {
        /// <summary>
        /// ロールバックを試み、ロールバック自体の失敗は呼び出し元へ漏らさない
        /// </summary>
        /// <param name="rollback">
        /// 巻き戻し操作（<c>() =&gt; scope.Rollback()</c> / <c>() =&gt; transaction.Rollback()</c>）
        /// </param>
        /// <param name="logger">
        /// ログ出力先。<c>null</c> の場合は <see cref="ErrorDialogHelper.LogException"/> へ委譲する
        /// （<c>ILogger</c> を注入していない層向け。error-messages.md #1817）
        /// </param>
        /// <param name="operationName">
        /// ログに載せる操作名（「返却の記録」「マイグレーションの適用」等）。
        /// 障害調査を先に進める値を載せること（development-conventions.md #1730）
        /// </param>
        public static void TryRollback(Action rollback, ILogger logger, string operationName)
        {
            if (rollback == null)
            {
                throw new ArgumentNullException(nameof(rollback));
            }

            try
            {
                rollback();
            }
            catch (Exception rollbackException)
            {
                // 本来の失敗要因は呼び出し元の catch が記録する（あるいは再スローされて上位が記録する）。
                // ここは補足情報。LogDebug は本番のファイルに出力されないため Warning
                // （development-conventions.md「ロギング」#1716 / #1730）
                if (logger != null)
                {
                    logger.LogWarning(rollbackException,
                        "{Operation}のロールバックに失敗しました（未コミットのためデータは確定しません）",
                        operationName);
                }
                else
                {
                    // ILogger を注入していない層（リポジトリ・マイグレーション等）でも
                    // 痕跡を残す。既存のファイルログ機構を再利用する（ダイアログは出さない）
                    ErrorDialogHelper.LogException(
                        rollbackException, $"{operationName}のロールバック");
                }
            }
        }
    }
}
