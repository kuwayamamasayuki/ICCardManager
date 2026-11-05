#nullable enable
using System;
using System.Threading.Tasks;
using ICCardManager.Common;

namespace ICCardManager.ViewModels
{
    /// <summary>
    /// 登録の書き込みが <c>false</c> になったときに原因を切り分けて案内を決める（Issue #2263 / #2382）
    /// </summary>
    /// <remarks>
    /// <para>
    /// リポジトリの <c>InsertAsync</c> は一過性でない SQLite の失敗（主キーの重複のほか、ディスク満杯・読み取り専用・
    /// 破損・I/O エラー）をすべて <c>false</c> に畳む（#1951 / #2001）。職員管理画面と交通系ICカード管理画面は
    /// 同じ判断（読み直して行があるか・削除済みか・一覧を再読込できたか）で案内を決めるので、ここへ 1 か所に置く（#1763）。
    /// </para>
    /// <para>
    /// 一覧の再読込（<c>ObservableCollection</c> の更新）を UI スレッドで続けるため、<c>ConfigureAwait(false)</c> を付けない
    /// ViewModel 層に置く（<c>.claude/rules/async-configureawait.md</c>）。
    /// </para>
    /// </remarks>
    internal static class RegisterFailureNotice
    {
        /// <summary>
        /// 読み直しの結果から案内を決める
        /// </summary>
        /// <typeparam name="T">登録しようとした行の型（職員・カード）</typeparam>
        /// <param name="attemptedLabel">登録しようとした入力内容の表示名（行が無いときの「何が」）</param>
        /// <param name="reread">同じ IDm の行を削除済みも含めて読み直す</param>
        /// <param name="isDeleted">読み直した行が削除済みか</param>
        /// <param name="labelOf">
        /// 読み直した行の表示名。行があるときは<b>入力した値ではなく行の値で</b>名指しする
        /// （他の PC が別の番号・氏名で登録していれば、入力した値は一覧のどこにも無い）
        /// </param>
        /// <param name="reloadList">一覧を再読込する</param>
        /// <param name="listName">一覧の名称（「カード一覧」「職員一覧」）</param>
        /// <param name="logFailure">読み直し・再読込の例外を記録する（案内は失わない）</param>
        /// <returns>ステータス欄に出す案内</returns>
        public static async Task<string> ResolveAsync<T>(
            string attemptedLabel,
            Func<Task<T?>> reread,
            Func<T, bool> isDeleted,
            Func<T, string> labelOf,
            Func<Task> reloadList,
            string listName,
            Action<Exception, string> logFailure)
            where T : class
        {
            T? existing;
            try
            {
                existing = await reread();
            }
            catch (Exception ex)
            {
                // 書き込みが失敗する原因（共有フォルダの切断・DB のロック）は、直後の読み直しでも例外になり得る。
                // 例外を通すと案内そのものが失われ、未処理例外のダイアログになる。書き込めなかったことは確実なので、
                // 原因を断定しない「書き込めなかった」の案内に倒す
                logFailure(ex, "登録できなかった原因を調べるための読み直しに失敗しました。書き込みの失敗として案内します。");
                return RegisterWriteFailedMessage.Build(attemptedLabel);
            }

            if (existing == null)
            {
                // 行が無い＝重複ではない。原因（書き込めなかった理由）はリポジトリがログに残している
                return RegisterWriteFailedMessage.Build(attemptedLabel);
            }

            var existingLabel = labelOf(existing);
            if (isDeleted(existing))
            {
                // 削除済みの行は一覧に出ないので、一覧での確認は案内できない。
                // もう一度保存すると、登録前の確認が削除済みと判定して復元するかを尋ねる
                return ConcurrencyConflictMessage.ForRegisterFoundDeleted(existingLabel);
            }

            try
            {
                // 案内が「再読み込みしました」と述べるので、先に一覧を再読込する（#1759）
                await reloadList();
                return ConcurrencyConflictMessage.ForRegister(existingLabel, listName);
            }
            catch (Exception ex)
            {
                // 成功時の再読込（#1727）と同じく握って案内は続ける。ただし「再読み込みしました」は事実にならない
                logFailure(ex, "登録の競合を案内する前の一覧の再読込に失敗しました。一覧は古い可能性があります。");
                return ConcurrencyConflictMessage.ForRegisterReloadFailed(existingLabel, listName);
            }
        }
    }
}
