using System;

namespace ICCardManager.Dtos
{
    /// <summary>
    /// 繰越情報が失われたカード1枚分の検出結果（Issue #1758）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 各 <c>Lost*</c> プロパティは「その項目が失われたか」と「失われた元の値」を兼ねる。
    /// <c>null</c> は**その項目は失われていない**ことを意味する（値 0 やページ番号 1 は
    /// 既定値であって「失われた値」にはなり得ないため、null との取り違えは起きない）。
    /// </para>
    /// <para>
    /// 消失した項目だけを非 null にするのは、復旧を依頼するときに渡す値を誤らせないため。
    /// 消失していない項目まで並べると、現在の正しい値を「失われた値」で上書きさせてしまう。
    /// </para>
    /// </remarks>
    public class CarryoverDataLossItem
    {
        /// <summary>対象カードのIDm</summary>
        public string CardIdm { get; set; }

        /// <summary>表示用のカード名（例: "はやかけん 001"。現在の登録内容に基づく）</summary>
        public string CardDisplayName { get; set; }

        /// <summary>失われた開始ページ番号（Issue #510）。失われていない場合は null</summary>
        public int? LostStartingPageNumber { get; set; }

        /// <summary>失われた繰越累計受入金額（Issue #1215）。失われていない場合は null</summary>
        public int? LostCarryoverIncomeTotal { get; set; }

        /// <summary>失われた繰越累計払出金額（Issue #1215）。失われていない場合は null</summary>
        public int? LostCarryoverExpenseTotal { get; set; }

        /// <summary>失われた繰越累計の対象年度（Issue #1215）。失われていない場合は null</summary>
        public int? LostCarryoverFiscalYear { get; set; }

        /// <summary>値が失われた操作の日時（operation_log の記録）</summary>
        public DateTime LostAt { get; set; }

        /// <summary>値を失わせた操作の操作者名（operation_log のスナップショット）</summary>
        public string OperatorName { get; set; }
    }
}
