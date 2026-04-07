using System;
using ICCardManager.Models;

namespace ICCardManager.Infrastructure.CardReader
{
    /// <summary>
    /// FeliCa履歴ブロック（16バイト）を <see cref="LedgerDetail"/> にデコードする純粋関数群。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FelicaCardReader.ParseHistoryData"/> から抽出された、副作用・DB依存・
    /// ハードウェア依存を持たない純粋関数です。
    /// 入力（16バイトの生データ）と駅名解決デリゲートのみで結果が決まります。
    /// </para>
    /// <para>
    /// 担保するロジック:
    /// </para>
    /// <list type="bullet">
    /// <item><description>日付フィールド（バイト4-5）のビットフィールド解析</description></item>
    /// <item><description>利用種別（0x02/0x14: チャージ、0x0D: ポイント還元）の判定</description></item>
    /// <item><description>バス利用判定（駅コード両方0、または駅名両方未解決）</description></item>
    /// <item><description>金額計算（前回残高との差分）</description></item>
    /// <item><description>Issue #942: 利用種別が0x0D以外でも残高増加時はポイント還元として扱う</description></item>
    /// </list>
    /// </remarks>
    public static class FelicaHistoryBlockDecoder
    {
        /// <summary>
        /// 16バイトのFeliCa履歴ブロックをデコードします。
        /// </summary>
        /// <param name="currentData">現在のレコードデータ（16バイト以上）</param>
        /// <param name="previousData">前回のレコードデータ（金額計算用、null可）</param>
        /// <param name="resolveStationName">
        /// 駅名解決デリゲート: (lineCode, stationNum) → 駅名 or null。
        /// 呼び出し側でカード種別をクロージャに閉じ込めて渡すこと。
        /// </param>
        /// <param name="pointRedemptionFallbackTriggered">
        /// Issue #942 のフォールバック判定（利用種別0x0D以外で残高増加を検出してポイント還元化）が
        /// 発生した場合 true。呼び出し側のロガーで診断ログを出すために使用。
        /// </param>
        /// <returns>
        /// デコード済みの <see cref="LedgerDetail"/>。
        /// 入力が null/不正の場合は null。
        /// </returns>
        public static LedgerDetail Decode(
            byte[] currentData,
            byte[] previousData,
            Func<int, int, string> resolveStationName,
            out bool pointRedemptionFallbackTriggered)
        {
            pointRedemptionFallbackTriggered = false;

            if (currentData == null || currentData.Length < 16)
            {
                return null;
            }

            try
            {
                // バイト1: 利用種別
                var usageType = currentData[1];

                // バイト4-5: 日付（2000年起点のビットフィールド）
                var dateValue = (currentData[4] << 8) | currentData[5];
                var year = 2000 + ((dateValue >> 9) & 0x7F);
                var month = (dateValue >> 5) & 0x0F;
                var day = dateValue & 0x1F;

                DateTime? useDate = null;
                if (year >= 2000 && month >= 1 && month <= 12 && day >= 1 && day <= 31)
                {
                    try
                    {
                        useDate = new DateTime(year, month, day);
                    }
                    catch
                    {
                        // 無効な日付は無視
                    }
                }

                // バイト6-7: 入場駅コード（ビッグエンディアン）
                var entryStationCode = (currentData[6] << 8) | currentData[7];

                // バイト8-9: 出場駅コード（ビッグエンディアン）
                var exitStationCode = (currentData[8] << 8) | currentData[9];

                // バイト10-11: 残額（リトルエンディアン）
                var balance = currentData[10] + (currentData[11] << 8);

                // 前回の残高を取得（金額計算用）
                int? previousBalance = null;
                if (previousData != null && previousData.Length >= 12)
                {
                    previousBalance = previousData[10] + (previousData[11] << 8);
                }

                // 利用種別の判定
                // 0x02: チャージ（現金入金）
                // 0x0D: ポイント還元
                // 0x14: オートチャージ（チャージとして扱う）
                var isCharge = usageType == 0x02 || usageType == 0x14;
                var isPointRedemption = usageType == 0x0D;

                // 駅名の解決を試みる（バス判定に使用）
                string entryStationName = null;
                string exitStationName = null;
                if (entryStationCode > 0 && resolveStationName != null)
                {
                    var lineCode = (entryStationCode >> 8) & 0xFF;
                    var stationNum = entryStationCode & 0xFF;
                    entryStationName = resolveStationName(lineCode, stationNum);
                }
                if (exitStationCode > 0 && resolveStationName != null)
                {
                    var lineCode = (exitStationCode >> 8) & 0xFF;
                    var stationNum = exitStationCode & 0xFF;
                    exitStationName = resolveStationName(lineCode, stationNum);
                }

                // バス利用の判定:
                // 1. 駅コードが両方0の場合（従来のバス判定）
                // 2. 駅コードはあるが駅名が両方とも解決できなかった場合（西鉄バス等）
                // かつ、チャージでもポイント還元でもない場合
                var isBus = !isCharge && !isPointRedemption &&
                           ((entryStationCode == 0 && exitStationCode == 0) ||
                            (entryStationName == null && exitStationName == null));

                // 金額の計算
                int? amount = null;
                if (previousBalance.HasValue)
                {
                    if (isCharge || isPointRedemption)
                    {
                        // チャージまたはポイント還元は残高が増加する
                        amount = balance - previousBalance.Value;
                    }
                    else
                    {
                        amount = previousBalance.Value - balance;
                    }
                }

                // Issue #942: 利用種別バイトが0x0D以外でも残高が増加している場合はポイント還元とみなす
                // FeliCaカードの利用種別はカード/リーダーによってバリエーションがあるため、
                // 金額の符号（残高増減の方向）を最終的な判断基準とする
                if (amount.HasValue && amount.Value < 0 && !isCharge && !isPointRedemption)
                {
                    pointRedemptionFallbackTriggered = true;
                    isPointRedemption = true;
                    amount = -amount.Value;  // 正の金額（入金額）に変換
                }

                // 生データを保持（デバッグ・診断用）
                var rawBytes = new byte[16];
                Array.Copy(currentData, 0, rawBytes, 0, Math.Min(currentData.Length, 16));

                return new LedgerDetail
                {
                    UseDate = useDate,
                    // バス利用の場合はnullを設定（バス停名入力ダイアログを表示するため）
                    EntryStation = isBus ? null : entryStationName,
                    ExitStation = isBus ? null : exitStationName,
                    Amount = amount,
                    Balance = balance,
                    IsCharge = isCharge,
                    IsPointRedemption = isPointRedemption,
                    IsBus = isBus,
                    RawBytes = rawBytes
                };
            }
            catch
            {
                // パースエラー時は null を返す（呼び出し側でログ出力）
                return null;
            }
        }
    }
}
