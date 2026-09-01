using System;
using System.Data.SQLite;
using System.IO;
using ICCardManager.Common.Exceptions;

namespace ICCardManager.Common
{
    /// <summary>
    /// 例外を、エラーメッセージ品質ガイドライン（Issue #1275、<c>.claude/rules/error-messages.md</c>）
    /// に準拠した「何が／なぜ／どうすれば」3要素のユーザー向け文言へ変換する（Issue #1614）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 生の <c>ex.Message</c> は英語・技術用語を含みうるため、ITリテラシーの高くない職員には
    /// 解読不能になりやすい。本クラスは UI に出してよい文言のみを返し、技術的詳細
    /// （<c>ex.Message</c>・スタックトレース）はログ（<see cref="ErrorDialogHelper.LogException"/>
    /// または注入済み <c>ILogger</c>）にのみ残すという責務分離を担う。
    /// </para>
    /// <para>
    /// ファイル I/O 等の副作用を持たない純粋関数として実装し、単体テスト
    /// （<c>ExceptionMessageFormatterTests</c>）で品質基準を固定する。
    /// </para>
    /// </remarks>
    public static class ExceptionMessageFormatter
    {
        /// <summary>
        /// 例外を「何が／なぜ／どうすれば」3要素を満たすユーザー向け文言へ変換する。
        /// </summary>
        /// <param name="exception">
        /// 発生した例外。<see cref="AppException"/> の場合は整備済みの
        /// <see cref="AppException.UserFriendlyMessage"/> を尊重する。<c>null</c> の場合は汎用文言を返す。
        /// </param>
        /// <param name="operation">
        /// ユーザー視点の操作名（例:「台帳の保存」「エクスポート」「リストア」）。
        /// 文言の「何が」部分（<c>"〇〇に失敗しました。"</c>）に用いる。
        /// <c>null</c>／空白の場合は「処理」を用いる。
        /// </param>
        /// <returns>
        /// <c>"〇〇に失敗しました。{なぜ}{どうすれば}"</c> 形式の、行動指示（～してください）で終わる文言。
        /// </returns>
        public static string ToUserMessage(Exception exception, string operation)
        {
            var op = string.IsNullOrWhiteSpace(operation) ? "処理" : operation.Trim();

            // AppException は整備済みのユーザー向け文言を持つため、それをそのまま尊重する。
            if (exception is AppException appException &&
                !string.IsNullOrWhiteSpace(appException.UserFriendlyMessage))
            {
                return appException.UserFriendlyMessage;
            }

            var (reason, action) = GetReasonAndAction(exception);
            return $"{op}に失敗しました。{reason}{action}";
        }

        /// <summary>
        /// 例外種別に応じた「なぜ（理由）」だけを返す（Issue #1991）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>他の文の中へ埋め込むとき</b>に使う。<see cref="ToUserMessage"/> は
        /// <c>"〇〇に失敗しました。"</c> という「何が」と行動指示を含む<b>完全な文</b>を返すため、
        /// 既に「何が」と「どうすれば」を述べている文へ埋め込むと、
        /// ①「何が」が二重になって<b>矛盾する</b>（「明細は置き換えました」の直後に
        /// 「明細の取り込みに失敗しました」）、②<b>両立しない行動指示</b>が並ぶ
        /// （「再度実行してください」と「履歴画面で修正してください」）、という害がある。
        /// </para>
        /// <para>
        /// <see cref="AppException"/> は整備済みの <see cref="AppException.UserFriendlyMessage"/> を
        /// そのまま返す（3 要素が 1 文に畳まれているため理由だけを取り出せない）。
        /// これは是正前から <c>DatabaseException.QueryFailed(...).UserFriendlyMessage</c> を
        /// 埋め込んでいた箇所と同じ挙動である。
        /// </para>
        /// </remarks>
        /// <param name="exception">発生した例外</param>
        /// <returns>「なぜ」だけの文（行動指示を含まない）</returns>
        public static string ToReason(Exception exception)
        {
            if (exception is AppException appException &&
                !string.IsNullOrWhiteSpace(appException.UserFriendlyMessage))
            {
                return appException.UserFriendlyMessage;
            }

            return GetReasonAndAction(exception).Reason;
        }

        /// <summary>
        /// 例外種別に応じた「なぜ（理由）」と「どうすれば（対処）」の組を返す。
        /// </summary>
        private static (string Reason, string Action) GetReasonAndAction(Exception exception)
        {
            switch (exception)
            {
                // Issue #1986: SQLite の失敗（共有モードの Busy / Locked、UNC 断）は
                // `default` の「予期しない問題」へ落ちていた。DB 障害はこの対応表で最も起こりやすい
                // 部類であり、原因が名指しできないと職員は次の行動を選べない。
                // 文言はモード中立（ローカルモードでも VACUUM・バックアップ・ヘルスチェックの
                // 別接続と競合し得る。`development-conventions.md`「原因を断定する前に、
                // その原因が成立する構成かを確認する」）。
                case SQLiteException _:
                    return ("データベースの読み書きができませんでした。",
                            "ほかのパソコンや別の操作で同じデータを使用している可能性があります。"
                            + "しばらく待ってから再度実行してください。");

                case UnauthorizedAccessException _:
                    return ("ファイルへのアクセス権限がありません。",
                            "保存先フォルダーの書き込み権限を確認するか、管理者に連絡してください。");

                case IOException _:
                    return ("ファイルの読み書き中に問題が発生しました。",
                            "対象のファイルが他のプログラムで開かれていないか確認し、しばらく待ってから再度実行してください。");

                case TimeoutException _:
                    return ("処理に時間がかかり、中断されました。",
                            "しばらく待ってから再度実行してください。");

                case InvalidOperationException _:
                    return ("現在の状態ではこの操作を実行できません。",
                            "画面を最新の状態に更新してから再度実行してください。");

                // ArgumentNullException も ArgumentException を継承するためここで捕捉される。
                case ArgumentException _:
                    return ("入力された値に問題があります。",
                            "入力内容を確認してから再度実行してください。");

                case NotSupportedException _:
                    return ("この操作は現在サポートされていません。",
                            "操作内容を確認し、必要であれば管理者に連絡してください。");

                default:
                    return ("予期しない問題が発生しました。",
                            "しばらく待ってから再度実行してください。解決しない場合は管理者に連絡してください。");
            }
        }
    }
}
