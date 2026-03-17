namespace ICCardManager.Common
{
    /// <summary>
    /// 利用区間の表示文字列を生成する共通フォーマッター
    /// </summary>
    /// <remarks>
    /// Issue #1023: LedgerDetailDto.RouteDisplay と LedgerDetailItemViewModel.RouteDisplay の
    /// 重複ロジックを共通化するために導入。
    /// 判定優先順位: チャージ → ポイント還元 → バス → 鉄道（乗車駅・降車駅）→ フォールバック
    /// </remarks>
    public static class RouteDisplayFormatter
    {
        /// <summary>
        /// 利用区間の表示文字列を生成
        /// </summary>
        /// <param name="isCharge">チャージフラグ</param>
        /// <param name="isPointRedemption">ポイント還元フラグ</param>
        /// <param name="isBus">バス利用フラグ</param>
        /// <param name="busStops">バス停名</param>
        /// <param name="entryStation">乗車駅</param>
        /// <param name="exitStation">降車駅</param>
        /// <param name="stationSeparator">駅名間の区切り文字（デフォルト: "～"）</param>
        /// <param name="showPartialStations">乗車駅のみ・降車駅のみの場合も表示するか</param>
        /// <param name="fallback">どの条件にも該当しない場合の文字列</param>
        /// <returns>表示用の区間文字列</returns>
        public static string Format(
            bool isCharge,
            bool isPointRedemption,
            bool isBus,
            string busStops,
            string entryStation,
            string exitStation,
            string stationSeparator = "～",
            bool showPartialStations = true,
            string fallback = "不明")
        {
            if (isCharge)
            {
                return "チャージ";
            }

            if (isPointRedemption)
            {
                return "ポイント還元";
            }

            if (isBus)
            {
                return string.IsNullOrEmpty(busStops) ? "バス（★）" : $"バス（{busStops}）";
            }

            if (!string.IsNullOrEmpty(entryStation) && !string.IsNullOrEmpty(exitStation))
            {
                return $"{entryStation}{stationSeparator}{exitStation}";
            }

            if (showPartialStations)
            {
                if (!string.IsNullOrEmpty(entryStation))
                {
                    return $"{entryStation}～";
                }

                if (!string.IsNullOrEmpty(exitStation))
                {
                    return $"～{exitStation}";
                }
            }

            return fallback;
        }
    }
}
