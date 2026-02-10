using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace ICCardManager.Common
{
/// <summary>
    /// アプリケーションの状態
    /// </summary>
    public enum AppState
    {
        /// <summary>職員証タッチ待ち</summary>
        WaitingForStaffCard,

        /// <summary>交通系ICカードタッチ待ち</summary>
        WaitingForIcCard,

        /// <summary>処理中</summary>
        Processing
    }

    /// <summary>
    /// 交通系ICカードの種別
    /// </summary>
    public enum CardType
    {
        /// <summary>Suica</summary>
        Suica,

        /// <summary>PASMO</summary>
        PASMO,

        /// <summary>ICOCA</summary>
        ICOCA,

        /// <summary>PiTaPa</summary>
        PiTaPa,

        /// <summary>nimoca</summary>
        Nimoca,

        /// <summary>SUGOCA</summary>
        SUGOCA,

        /// <summary>はやかけん</summary>
        Hayakaken,

        /// <summary>Kitaca</summary>
        Kitaca,

        /// <summary>TOICA</summary>
        TOICA,

        /// <summary>manaca</summary>
        Manaca,

        /// <summary>その他・不明</summary>
        Unknown
    }

    /// <summary>
    /// 利用種別（履歴詳細の手動入力用）
    /// </summary>
    public enum UsageType
    {
        /// <summary>鉄道利用</summary>
        Rail,

        /// <summary>バス利用</summary>
        Bus,

        /// <summary>チャージ</summary>
        Charge,

        /// <summary>ポイント還元</summary>
        PointRedemption
    }
}
