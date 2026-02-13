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
        CardReaderConnection
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
        /// 対象カードIDm（LowBalance時のみ使用）
        /// </summary>
        public string CardIdm { get; set; }
    }
}
