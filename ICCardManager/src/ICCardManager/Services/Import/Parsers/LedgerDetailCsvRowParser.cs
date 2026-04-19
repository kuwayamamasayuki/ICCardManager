using System;
using System.Collections.Generic;
using ICCardManager.Models;

namespace ICCardManager.Services.Import.Parsers
{
    /// <summary>
    /// 利用履歴詳細 CSV の1行をパースする（Issue #1284 で CsvImportService.Detail.cs から移設）。
    /// ValidateColumnCount は Card/Staff/Detail 共通利用のため、呼び出し側で通した後に本パーサを呼ぶ契約。
    /// </summary>
    internal static class LedgerDetailCsvRowParser
    {
        /// <summary>
        /// CSVフィールドからLedgerDetailをパース
        /// </summary>
        /// <param name="fields">パース済みフィールド（13列）</param>
        /// <param name="lineNumber">行番号（エラー報告用）</param>
        /// <param name="line">元の行データ（エラー報告用）</param>
        /// <param name="errors">エラーリスト</param>
        /// <returns>パース成功時はLedgerDetail、失敗時はnull</returns>
        public static LedgerDetail ParseFields(
            List<string> fields,
            int lineNumber,
            string line,
            List<CsvImportError> errors)
        {
            // [0]利用履歴ID [1]利用日時 [2]カードIDm [3]管理番号 [4]乗車駅 [5]降車駅
            // [6]バス停 [7]金額 [8]残額 [9]チャージ [10]ポイント還元 [11]バス利用 [12]グループID

            var ledgerIdStr = fields[0].Trim();
            var useDateStr = fields[1].Trim();
            // fields[2] カードIDm（利用履歴ID空欄時の自動作成で使用）
            // fields[3] 管理番号（参照用）
            // Issue #1267: entry_station / exit_station / bus_stops は
            // ユーザー編集可能テキストのため式インジェクション対策を適用
            var entryStation = Infrastructure.Security.FormulaInjectionSanitizer.Sanitize(fields[4].Trim());
            var exitStation = Infrastructure.Security.FormulaInjectionSanitizer.Sanitize(fields[5].Trim());
            var busStops = Infrastructure.Security.FormulaInjectionSanitizer.Sanitize(fields[6].Trim());
            var amountStr = fields[7].Trim();
            var balanceStr = fields[8].Trim();
            var isChargeStr = fields[9].Trim();
            var isPointRedemptionStr = fields[10].Trim();
            var isBusStr = fields[11].Trim();
            var groupIdStr = fields[12].Trim();

            // 利用履歴ID: 空欄の場合は0（自動付与）、それ以外は整数
            int ledgerId = 0;
            if (!string.IsNullOrWhiteSpace(ledgerIdStr))
            {
                if (!int.TryParse(ledgerIdStr, out ledgerId))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "利用履歴IDの形式が不正です",
                        Data = ledgerIdStr
                    });
                    return null;
                }
            }

            // 利用日時: 任意（空欄=null）
            DateTime? useDate = null;
            if (!string.IsNullOrWhiteSpace(useDateStr))
            {
                if (!DateTime.TryParse(useDateStr, out var parsedDate))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "利用日時の形式が不正です",
                        Data = useDateStr
                    });
                    return null;
                }
                useDate = parsedDate;
            }

            // 金額: 任意（空欄=null）
            int? amount = null;
            if (!string.IsNullOrWhiteSpace(amountStr))
            {
                if (!int.TryParse(amountStr, out var parsedAmount))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "金額の形式が不正です",
                        Data = amountStr
                    });
                    return null;
                }
                amount = parsedAmount;
            }

            // 残額: 任意（空欄=null）
            int? balance = null;
            if (!string.IsNullOrWhiteSpace(balanceStr))
            {
                if (!int.TryParse(balanceStr, out var parsedBalance))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "残額の形式が不正です",
                        Data = balanceStr
                    });
                    return null;
                }
                balance = parsedBalance;
            }

            // チャージ: 0 or 1
            if (!ValidateBooleanField(isChargeStr, lineNumber, "チャージ", errors, out var isCharge))
            {
                return null;
            }

            // ポイント還元: 0 or 1
            if (!ValidateBooleanField(isPointRedemptionStr, lineNumber, "ポイント還元", errors, out var isPointRedemption))
            {
                return null;
            }

            // バス利用: 0 or 1
            if (!ValidateBooleanField(isBusStr, lineNumber, "バス利用", errors, out var isBus))
            {
                return null;
            }

            // グループID: 任意（空欄=null）
            int? groupId = null;
            if (!string.IsNullOrWhiteSpace(groupIdStr))
            {
                if (!int.TryParse(groupIdStr, out var parsedGroupId))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "グループIDの形式が不正です",
                        Data = groupIdStr
                    });
                    return null;
                }
                groupId = parsedGroupId;
            }

            return new LedgerDetail
            {
                LedgerId = ledgerId,
                UseDate = useDate,
                EntryStation = string.IsNullOrWhiteSpace(entryStation) ? null : entryStation,
                ExitStation = string.IsNullOrWhiteSpace(exitStation) ? null : exitStation,
                BusStops = string.IsNullOrWhiteSpace(busStops) ? null : busStops,
                Amount = amount,
                Balance = balance,
                IsCharge = isCharge,
                IsPointRedemption = isPointRedemption,
                IsBus = isBus,
                GroupId = groupId
            };
        }

        /// <summary>
        /// ブール値フィールド（0/1）のバリデーション
        /// </summary>
        /// <param name="value">検証する値</param>
        /// <param name="lineNumber">行番号</param>
        /// <param name="fieldName">フィールド名</param>
        /// <param name="errors">エラーリスト</param>
        /// <param name="result">パース結果</param>
        /// <returns>バリデーション成功の場合true</returns>
        internal static bool ValidateBooleanField(
            string value,
            int lineNumber,
            string fieldName,
            List<CsvImportError> errors,
            out bool result)
        {
            result = false;
            if (value == "0")
            {
                result = false;
                return true;
            }
            if (value == "1")
            {
                result = true;
                return true;
            }

            errors.Add(new CsvImportError
            {
                LineNumber = lineNumber,
                Message = $"{fieldName}は0または1で指定してください",
                Data = value
            });
            return false;
        }
    }
}
