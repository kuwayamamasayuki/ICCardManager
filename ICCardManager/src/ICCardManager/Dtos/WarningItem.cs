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

        /// <summary>自動バックアップが長期間成功していない警告（Issue #1689）</summary>
        BackupStale,

        /// <summary>
        /// 紙出納簿移行カードの繰越累計・開始ページ番号が失われている警告（Issue #1758）
        /// </summary>
        /// <remarks>
        /// Issue #1726 以前の <c>UPDATE ic_card</c> が繰越4項目を既定値で上書きしていたことによる被害。
        /// 検出のみで復旧手段は持たない（復旧は DB の直接修正）。
        /// </remarks>
        CarryoverDataLoss
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
        /// 対象カードIDm（LowBalance・BalanceInconsistency時に使用）
        /// </summary>
        public string CardIdm { get; set; }

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
