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
        /// 表示用: 貸出状態テキスト
        /// </summary>
        public string LentStatusDisplay => IsLent ? "貸出中" : "在庫";

        /// <summary>
        /// 表示用: 貸出日時
        /// </summary>
        public string LentAtDisplay => LentAt?.ToString("yyyy/MM/dd HH:mm");
    }
}
