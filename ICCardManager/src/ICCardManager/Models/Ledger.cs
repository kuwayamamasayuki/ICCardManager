using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Services;
namespace ICCardManager.Models
{
/// <summary>
    /// 利用履歴概要エンティティ（ledgerテーブル）
    /// 物品出納簿の1行に対応
    /// </summary>
    public class Ledger
    {
        /// <summary>
        /// レコードID（主キー、自動採番）
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 交通系ICカードIDm（FK→ic_card）
        /// </summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>
        /// 貸出者IDm（FK→staff）
        /// </summary>
        public string LenderIdm { get; set; }

        /// <summary>
        /// 出納年月日
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// 摘要
        /// </summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// 受入金額（チャージ額）
        /// </summary>
        public int Income { get; set; }

        /// <summary>
        /// 払出金額（利用額）
        /// </summary>
        public int Expense { get; set; }

        /// <summary>
        /// 残額
        /// </summary>
        public int Balance { get; set; }

        /// <summary>
        /// 利用者氏名（スナップショット保存）
        /// </summary>
        public string StaffName { get; set; }

        /// <summary>
        /// 備考
        /// </summary>
        public string Note { get; set; }

        /// <summary>
        /// 同行者数（本人を含まない。既定 0）
        /// </summary>
        /// <remarks>
        /// Issue #1906: 複数名で同一交通系ICカードを利用した場合の人数。
        /// 氏名欄の「外N名」は <see cref="DisplayStaffName"/> が導出し、<see cref="StaffName"/> には書き込まない。
        /// </remarks>
        public int CompanionCount { get; set; }

        /// <summary>
        /// 表示用の氏名（同行者がいれば「氏名 外N名」、Issue #1906）
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string DisplayStaffName => Common.StaffNameFormatter.Format(StaffName, CompanionCount);

        /// <summary>
        /// 返却者IDm（FK→staff）
        /// </summary>
        public string ReturnerIdm { get; set; }

        /// <summary>
        /// 貸出日時
        /// </summary>
        public DateTime? LentAt { get; set; }

        /// <summary>
        /// 返却日時
        /// </summary>
        public DateTime? ReturnedAt { get; set; }

        /// <summary>
        /// 貸出中レコードフラグ（true: 貸出中）
        /// </summary>
        public bool IsLentRecord { get; set; }

        /// <summary>
        /// 利用履歴詳細のコレクション（ナビゲーションプロパティ）
        /// </summary>
        public List<LedgerDetail> Details { get; set; } = new();

        /// <summary>
        /// 詳細件数（クエリで取得）
        /// </summary>
        public int DetailCount { get; set; }

        // === ドメインロジック ===

        /// <summary>
        /// 繰越レコード（新規購入または年度途中繰越）かどうか
        /// </summary>
        /// <remarks>
        /// 帳票のソート順や日次集計で特別扱いされるレコードの判定。
        /// SQL 側の同等条件（<c>LedgerRepository</c> の「summary = '新規購入' OR summary LIKE
        /// <c>@midYearCarryoverPattern</c>」）と対応する。繰越側の LIKE パターンは
        /// <c>SummaryGenerator.GetMidYearCarryoverLikePattern</c> が組織設定から導出し、
        /// 本プロパティと SQL が揃って設定に追従する（Issue #1749）。
        /// </remarks>
        public bool IsCarryover =>
            Summary == "新規購入" || IsMidYearCarryover;

        /// <summary>
        /// 年度途中導入の繰越レコード（「○月から繰越」）かどうか
        /// </summary>
        /// <remarks>
        /// 「○月から繰越」判定は <see cref="SummaryGenerator.IsMidYearCarryoverSummary"/> に
        /// 一元化されており、組織設定 <c>MidYearCarryoverPattern</c> に追従する（Issue #1604）。
        /// 以前は本クラス側にハードコードした正規表現を持っていたため、組織設定でパターンを
        /// カスタムすると帳票集計フィルタ（<c>ReportDataBuilder</c> 経由）とソート・特別扱い判定
        /// （本プロパティ）が乖離し、月計・累計の二重計上（Issue #1494 で対策した問題の再発形）
        /// を招くリスクがあった。判定を単一の真実源に統合してこれを防ぐ。
        /// </remarks>
        public bool IsMidYearCarryover =>
            SummaryGenerator.IsMidYearCarryoverSummary(Summary);
    }
}
