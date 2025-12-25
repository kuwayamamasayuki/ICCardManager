using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace ICCardManager.Models
{
/// <summary>
    /// 職員エンティティ（staffテーブル）
    /// </summary>
    public class Staff
    {
        /// <summary>
        /// 職員証IDm（主キー、16進数16文字）
        /// </summary>
        public string StaffIdm { get; set; } = string.Empty;

        /// <summary>
        /// 職員氏名
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 職員番号
        /// </summary>
        public string Number { get; set; }

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
    }
}
