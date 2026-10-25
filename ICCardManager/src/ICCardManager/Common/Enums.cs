#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace ICCardManager.Common
{
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
}
