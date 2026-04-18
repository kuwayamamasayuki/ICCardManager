using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ICCardManager.Common;

namespace ICCardManager.Dtos
{
/// <summary>
    /// カード情報DTO
    /// ViewModelで使用するカード情報の表示用オブジェクト
    /// </summary>
    public partial class CardDto : ObservableObject
    {
        /// <summary>
        /// 選択状態（帳票作成画面等で使用）
        /// </summary>
        [ObservableProperty]
        private bool _isSelected;
        /// <summary>
        /// カードIDm（16進数16文字）
        /// </summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>
        /// カード種別（はやかけん/nimoca/SUGOCA等）
        /// </summary>
        public string CardType { get; set; } = string.Empty;

        /// <summary>
        /// 管理番号
        /// </summary>
        public string CardNumber { get; set; } = string.Empty;

        /// <summary>
        /// 備考
        /// </summary>
        public string Note { get; set; }

        /// <summary>
        /// 貸出状態
        /// </summary>
        public bool IsLent { get; set; }

        /// <summary>
        /// 最終貸出者IDm（更新用）
        /// </summary>
        public string LastLentStaff { get; set; }

        /// <summary>
        /// 最終貸出者名（表示用）
        /// </summary>
        public string LentStaffName { get; set; }

        /// <summary>
        /// 最終貸出日時
        /// </summary>
        public DateTime? LentAt { get; set; }

        /// <summary>
        /// 表示用: カード名（種別 + 番号）
        /// </summary>
        public string DisplayName => $"{CardType} {CardNumber}";

        /// <summary>
        /// 払戻済フラグ（Issue #530）
        /// </summary>
        /// <remarks>
        /// 払い戻したカードは削除せず、この状態で残す。
        /// 払戻済カードは貸出対象外だが、帳票作成は可能。
        /// </remarks>
        public bool IsRefunded { get; set; }

        /// <summary>
        /// 払戻日時（Issue #530）
        /// </summary>
        public DateTime? RefundedAt { get; set; }

        /// <summary>
        /// 表示用: 貸出状態テキスト
        /// </summary>
        /// <remarks>
        /// Issue #530対応: 払戻済の場合は「払戻済」と表示
        /// Issue #1274: Presenter で一元管理
        /// </remarks>
        public string LentStatusDisplay =>
            ICCardManager.Common.LendingStatusPresenter.Resolve(IsLent, IsRefunded, LentStaffName).ShortText;

        /// <summary>
        /// Issue #1274: 表示用の貸出状態アイコン（アイコン＋テキスト併記用）。
        /// </summary>
        public string LentStatusIcon =>
            ICCardManager.Common.LendingStatusPresenter.Resolve(IsLent, IsRefunded, LentStaffName).Icon;

        /// <summary>
        /// Issue #1274: スクリーンリーダー向けの完全な状態説明文。
        /// <c>AutomationProperties.Name</c> にバインドして使用する。
        /// </summary>
        public string LentStatusAccessibilityText =>
            ICCardManager.Common.LendingStatusPresenter.Resolve(IsLent, IsRefunded, LentStaffName).AccessibilityText;

        /// <summary>
        /// 表示用: 貸出日時
        /// </summary>
        public string LentAtDisplay => DisplayFormatters.FormatDateTime(LentAt, null);

        /// <summary>
        /// 開始ページ番号（Issue #510: 年度途中導入対応）
        /// </summary>
        /// <remarks>
        /// 紙の出納簿からの繰越時に、帳票のページ番号をこの値から開始する。
        /// デフォルトは1。
        /// </remarks>
        public int StartingPageNumber { get; set; } = 1;

        /// <summary>
        /// 繰越累計受入金額（Issue #1215）
        /// </summary>
        public int CarryoverIncomeTotal { get; set; }

        /// <summary>
        /// 繰越累計払出金額（Issue #1215）
        /// </summary>
        public int CarryoverExpenseTotal { get; set; }

        /// <summary>
        /// 繰越累計が有効な会計年度（Issue #1215）
        /// </summary>
        public int? CarryoverFiscalYear { get; set; }
    }
}
