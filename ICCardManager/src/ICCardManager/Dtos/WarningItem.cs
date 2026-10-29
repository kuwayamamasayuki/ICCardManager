#nullable enable
namespace ICCardManager.Dtos
{
    /// <summary>
    /// 警告の種別
    /// </summary>
    public enum WarningType
    {
        /// <summary>残額不足警告（カード単位）</summary>
        LowBalance,

        /// <summary>バス停名未入力警告（集約）</summary>
        IncompleteBusStop,

        /// <summary>カードリーダーエラー</summary>
        CardReaderError,

        /// <summary>カードリーダー接続状態</summary>
        CardReaderConnection,

        /// <summary>残高不整合警告（カード単位）</summary>
        BalanceInconsistency,

        /// <summary>データベース接続断（共有モード時のネットワーク切断）</summary>
        DatabaseConnectionLost,

        /// <summary>データベースのジャーナルモードがDELETEに設定できず、クラッシュ耐性が低下している警告（Issue #1172）</summary>
        DatabaseJournalModeDegraded,

        /// <summary>新しいアプリバージョンが公開されている通知（共有フォルダの latest_version.txt 経由、Issue #1687）</summary>
        NewVersionAvailable,

        /// <summary>バックアップ（自動・手動）が長期間成功していない警告（Issue #1689 / #2304）</summary>
        BackupStale,

        /// <summary>
        /// 紙出納簿移行カードの繰越累計・開始ページ番号が失われている警告（Issue #1758）
        /// </summary>
        /// <remarks>
        /// Issue #1726 以前の <c>UPDATE ic_card</c> が繰越4項目を既定値で上書きしていたことによる被害。
        /// 警告のクリックで開く一覧の「復旧...」から、失われた値を職員証の認証のうえで書き戻せる
        /// （繰越情報の復旧ダイアログ、Issue #2255）。
        /// </remarks>
        CarryoverDataLoss,

        /// <summary>
        /// 交通系ICカードの実残額と、ピッすいが記録している残額が食い違っている警告（カード単位、Issue #1908）
        /// </summary>
        /// <remarks>
        /// ピッすいを通さずに利用・返却された交通系ICカードを庶務担当者が見つけられるようにするための検出。
        /// 登録済みカードを単独でタッチしたとき（履歴表示）にだけ判定する
        /// （カードがリーダーに載っている瞬間にしか実残額を読めないため）。
        /// </remarks>
        CardBalanceMismatch,

        /// <summary>
        /// 払戻済の交通系ICカードが貸出中のまま残っている警告（カード単位、Issue #2322）
        /// </summary>
        /// <remarks>
        /// Issue #2322 の修正前は、30秒ルールの逆処理と貸出記録の作成ダイアログから払戻済のカードを貸出中にできた。
        /// そのカードは払戻済のため交通系ICカードのタッチで返却できず、残高ダッシュボードにも出ないので、
        /// 警告のクリックで履歴を開き、貸出中の行を削除して直す（削除すると貸出中の状態も解消される）。
        /// 生成・除去は貸出中カード一覧の読み直し（<c>RefreshLentCardsAsync</c>）が行う。
        /// </remarks>
        RefundedCardLent
    }

    /// <summary>
    /// システム警告エリアの1行に対応する警告情報DTO（Issue #672）
    /// </summary>
    public class WarningItem
    {
        /// <summary>
        /// 表示テキスト
        /// </summary>
        public string DisplayText { get; set; } = string.Empty;

        /// <summary>
        /// 警告種別
        /// </summary>
        public WarningType Type { get; set; }

        /// <summary>
        /// 対象カードIDm（LowBalance・BalanceInconsistency・CardBalanceMismatch・RefundedCardLent時に使用）
        /// </summary>
        public string? CardIdm { get; set; }

        /// <summary>
        /// 同じ種別の警告が繰り返し発生した回数（既定 1）。
        /// </summary>
        /// <remarks>
        /// Issue #1811: <see cref="WarningType.CardReaderError"/> は発生のたびに行を追加すると
        /// 無限に積み上がるため 1 行に集約し、繰り返し回数をこのプロパティと表示文言に載せる。
        /// 回数の状態を警告行自身が持つことで、クリックで取り除いた時点で回数も振り出しに戻る。
        /// <para>
        /// 数えるのは<b>種別</b>単位であり、原因（履歴の読み取り失敗／残高の読み取り失敗など）が
        /// 異なっても合算する。文言に載る理由は<b>最新の原因</b>で、いずれも対処は同じ
        /// （抜き差し・再起動）ため利用者の行動は変わらない。なおカードリーダーの切断は
        /// <see cref="WarningType.CardReaderConnection"/> として<b>別の行</b>に表示されるため、
        /// 合算によって切断が隠れることはない。
        /// </para>
        /// </remarks>
        public int OccurrenceCount { get; set; } = 1;
    }
}
