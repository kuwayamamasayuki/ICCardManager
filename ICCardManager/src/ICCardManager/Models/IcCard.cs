using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace ICCardManager.Models
{
/// <summary>
    /// 交通系ICカードエンティティ（ic_cardテーブル）
    /// </summary>
    public class IcCard
    {
        /// <summary>
        /// ICカードIDm（主キー、16進数16文字）
        /// </summary>
        public string CardIdm { get; set; } = string.Empty;

        /// <summary>
        /// カード種別（はやかけん/nimoca/SUGOCA等）
        /// </summary>
        public string CardType { get; set; } = string.Empty;

        /// <summary>
        /// 通し番号（管理番号）
        /// </summary>
        public string CardNumber { get; set; } = string.Empty;

        /// <summary>
        /// 備考
        /// </summary>
        public string Note { get; set; }

        /// <summary>
        /// 削除フラグ（論理削除）
        /// </summary>
        public bool IsDeleted { get; set; }

        /// <summary>
        /// 削除日時
        /// </summary>
        public DateTime? DeletedAt { get; set; }

        /// <summary>
        /// 貸出状態（true: 貸出中）
        /// </summary>
        public bool IsLent { get; set; }

        /// <summary>
        /// 最終貸出日時
        /// </summary>
        public DateTime? LastLentAt { get; set; }

        /// <summary>
        /// 最終貸出者IDm（FK→staff）
        /// </summary>
        public string LastLentStaff { get; set; }
    }
}
