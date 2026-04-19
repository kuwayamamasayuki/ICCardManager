using System;
using System.Collections.Generic;
using ICCardManager.Models;

namespace ICCardManager.Services.Import.Parsers
{
    /// <summary>
    /// 利用履歴CSVの1行をパースする共通ロジック。
    /// Import / Preview の両方で再利用するため、副作用を最小化し、
    /// errors リストへの追加のみで失敗を表現する。
    /// </summary>
    internal static class LedgerCsvRowParser
    {
        internal class ParsedLedgerRow
        {
            public int LineNumber { get; set; }
            public int? LedgerId { get; set; }
            public string CardIdm { get; set; }
            public DateTime Date { get; set; }
            public string Summary { get; set; }
            public int Income { get; set; }
            public int Expense { get; set; }
            public int Balance { get; set; }
            public string StaffName { get; set; }
            public string Note { get; set; }
        }

        public static ParsedLedgerRow TryParseRow(
            List<string> fields,
            int lineNumber,
            string line,
            bool hasIdColumn,
            int minColumns,
            HashSet<string> existingCardIdms,
            string targetCardIdm,
            List<CsvImportError> errors)
        {
            if (fields.Count < minColumns)
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = $"列数が不足しています（{minColumns}列必要）",
                    Data = line
                });
                return null;
            }

            var offset = hasIdColumn ? 1 : 0;
            var idStr = hasIdColumn ? fields[0].Trim() : string.Empty;
            var dateStr = fields[0 + offset].Trim();
            var cardIdm = fields[1 + offset].Trim().ToUpperInvariant();
            var summary = Infrastructure.Security.FormulaInjectionSanitizer.Sanitize(fields[3 + offset].Trim());
            var incomeStr = fields[4 + offset].Trim();
            var expenseStr = fields[5 + offset].Trim();
            var balanceStr = fields[6 + offset].Trim();
            var staffName = fields[7 + offset].Trim();
            var note = Infrastructure.Security.FormulaInjectionSanitizer.Sanitize(fields[8 + offset].Trim());

            int? ledgerId = null;
            if (hasIdColumn && !string.IsNullOrWhiteSpace(idStr))
            {
                if (!int.TryParse(idStr, out var parsedId))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "IDの形式が不正です",
                        Data = idStr
                    });
                    return null;
                }
                ledgerId = parsedId;
            }

            if (!DateTime.TryParse(dateStr, out var date))
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = "日時の形式が不正です",
                    Data = dateStr
                });
                return null;
            }

            if (string.IsNullOrWhiteSpace(cardIdm))
            {
                if (!string.IsNullOrWhiteSpace(targetCardIdm))
                {
                    cardIdm = targetCardIdm.ToUpperInvariant();
                }
                else
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "カードIDmは必須です（CSVで空欄の場合はインポート先カードを指定してください）",
                        Data = line
                    });
                    return null;
                }
            }

            if (!existingCardIdms.Contains(cardIdm))
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = "該当するカードが登録されていません",
                    Data = cardIdm
                });
                return null;
            }

            if (string.IsNullOrWhiteSpace(summary))
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = "摘要は必須です",
                    Data = line
                });
                return null;
            }

            if (!int.TryParse(balanceStr, out var balance))
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = "残額の形式が不正です",
                    Data = balanceStr
                });
                return null;
            }

            var income = 0;
            if (!string.IsNullOrWhiteSpace(incomeStr) && !int.TryParse(incomeStr, out income))
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = "受入金額の形式が不正です",
                    Data = incomeStr
                });
                return null;
            }

            var expense = 0;
            if (!string.IsNullOrWhiteSpace(expenseStr) && !int.TryParse(expenseStr, out expense))
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = "払出金額の形式が不正です",
                    Data = expenseStr
                });
                return null;
            }

            return new ParsedLedgerRow
            {
                LineNumber = lineNumber,
                LedgerId = ledgerId,
                CardIdm = cardIdm,
                Date = date,
                Summary = summary,
                Income = income,
                Expense = expense,
                Balance = balance,
                StaffName = staffName,
                Note = note
            };
        }
    }
}
