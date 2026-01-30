using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace ICCardManager.Models
{
/// <summary>
    /// 利用履歴詳細エンティティ（ledger_detailテーブル）
    /// ICカードの個別利用記録
    /// </summary>
    public class LedgerDetail
    {
        /// <summary>
        /// 親レコードID（FK→ledger）
        /// </summary>
        public int LedgerId { get; set; }

        /// <summary>
        /// 利用日時
        /// </summary>
        public DateTime? UseDate { get; set; }

        /// <summary>
        /// 乗車駅（空欄の場合はバス利用の可能性）
        /// </summary>
        public string EntryStation { get; set; }

        /// <summary>
        /// 降車駅（空欄の場合はバス利用の可能性）
        /// </summary>
        public string ExitStation { get; set; }

        /// <summary>
        /// バス停名（手入力）
        /// </summary>
        public string BusStops { get; set; }

        /// <summary>
        /// 利用額／チャージ額
        /// </summary>
        public int? Amount { get; set; }

        /// <summary>
        /// 残額
        /// </summary>
        public int? Balance { get; set; }

        /// <summary>
        /// チャージフラグ（true: チャージ）
        /// </summary>
        public bool IsCharge { get; set; }

        /// <summary>
        /// ポイント還元フラグ（true: ポイント還元）
        /// </summary>
        /// <remarks>
        /// ポイント還元はチャージと同様に残高が増加する取引ですが、
        /// 摘要の表示を区別するために別フラグで管理します。
        /// FeliCaの利用種別コード 0x0D（ポイント還元）で判定されます。
        /// </remarks>
        public bool IsPointRedemption { get; set; }

        /// <summary>
        /// バス利用フラグ（true: バス）
        /// </summary>
        public bool IsBus { get; set; }

        /// <summary>
        /// ICカードから読み取った生データ（16バイト）
        /// </summary>
        /// <remarks>
        /// デバッグ・診断目的でのみ使用。DBには保存されない。
        /// FeliCa履歴ブロックの生バイト列をそのまま保持する。
        /// </remarks>
        public byte[] RawBytes { get; set; }

        /// <summary>
        /// 親レコードへの参照（ナビゲーションプロパティ）
        /// </summary>
        public Ledger Ledger { get; set; }
    }
}
