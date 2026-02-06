using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

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
        /// </remarks>
        public string LentStatusDisplay => IsRefunded ? "払戻済" : (IsLent ? "貸出中" : "在庫");

        /// <summary>
        /// 表示用: 貸出日時
        /// </summary>
        public string LentAtDisplay => LentAt?.ToString("yyyy/MM/dd HH:mm");

        /// <summary>
        /// 開始ページ番号（Issue #510: 年度途中導入対応）
        /// </summary>
        /// <remarks>
        /// 紙の出納簿からの繰越時に、帳票のページ番号をこの値から開始する。
        /// デフォルトは1。
        /// </remarks>
        public int StartingPageNumber { get; set; } = 1;
    }
}
