using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Services;

/// <summary>
/// CSVインポート結果
/// </summary>
public class CsvImportResult
{
    /// <summary>成功したか</summary>
    public bool Success { get; init; }

    /// <summary>インポートした件数</summary>
    public int ImportedCount { get; init; }

    /// <summary>スキップした件数（既存データ）</summary>
    public int SkippedCount { get; init; }

    /// <summary>エラー件数</summary>
    public int ErrorCount { get; init; }

    /// <summary>エラー詳細リスト</summary>
    public List<CsvImportError> Errors { get; init; } = new();

    /// <summary>エラーメッセージ</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// CSVインポートエラー詳細
/// </summary>
public class CsvImportError
{
    /// <summary>行番号</summary>
    public int LineNumber { get; init; }

    /// <summary>エラー内容</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>対象データ</summary>
    public string? Data { get; init; }
}

/// <summary>
/// CSVインポートサービス
/// </summary>
public class CsvImportService
{
    private readonly ICardRepository _cardRepository;
    private readonly IStaffRepository _staffRepository;
    private readonly IValidationService _validationService;

    public CsvImportService(
        ICardRepository cardRepository,
        IStaffRepository staffRepository,
        IValidationService validationService)
    {
        _cardRepository = cardRepository;
        _staffRepository = staffRepository;
        _validationService = validationService;
    }

    /// <summary>
    /// カードCSVをインポート
    /// </summary>
    /// <param name="filePath">CSVファイルパス</param>
    /// <param name="skipExisting">既存データをスキップするか（falseの場合は更新）</param>
    public async Task<CsvImportResult> ImportCardsAsync(string filePath, bool skipExisting = true)
    {
        var errors = new List<CsvImportError>();
        var importedCount = 0;
        var skippedCount = 0;

        try
        {
            var lines = await ReadCsvFileAsync(filePath);
            if (lines.Count < 2)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ErrorMessage = "CSVファイルにデータがありません（ヘッダー行のみ）"
                };
            }

            // ヘッダー行をスキップして処理
            for (var i = 1; i < lines.Count; i++)
            {
                var lineNumber = i + 1;
                var line = lines[i];

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var fields = ParseCsvLine(line);

                // 最低4列（カードIDm, カード種別, 管理番号, 備考）が必要
                if (fields.Count < 4)
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "列数が不足しています",
                        Data = line
                    });
                    continue;
                }

                var cardIdm = fields[0].Trim();
                var cardType = fields[1].Trim();
                var cardNumber = fields[2].Trim();
                var note = fields.Count > 3 ? fields[3].Trim() : "";

                // バリデーション
                if (string.IsNullOrWhiteSpace(cardIdm))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "カードIDmは必須です",
                        Data = line
                    });
                    continue;
                }

                var idmValidation = _validationService.ValidateCardIdm(cardIdm);
                if (!idmValidation.IsValid)
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = idmValidation.ErrorMessage ?? "カードIDmの形式が不正です",
                        Data = cardIdm
                    });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(cardType))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "カード種別は必須です",
                        Data = line
                    });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(cardNumber))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "管理番号は必須です",
                        Data = line
                    });
                    continue;
                }

                // 既存チェック
                var existingCard = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true);
                if (existingCard != null)
                {
                    if (skipExisting)
                    {
                        skippedCount++;
                        continue;
                    }

                    // 更新処理
                    existingCard.CardType = cardType;
                    existingCard.CardNumber = cardNumber;
                    existingCard.Note = string.IsNullOrWhiteSpace(note) ? null : note;

                    if (await _cardRepository.UpdateAsync(existingCard))
                    {
                        importedCount++;
                    }
                    else
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "カードの更新に失敗しました",
                            Data = cardIdm
                        });
                    }
                }
                else
                {
                    // 新規登録
                    var card = new IcCard
                    {
                        CardIdm = cardIdm,
                        CardType = cardType,
                        CardNumber = cardNumber,
                        Note = string.IsNullOrWhiteSpace(note) ? null : note
                    };

                    if (await _cardRepository.InsertAsync(card))
                    {
                        importedCount++;
                    }
                    else
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "カードの登録に失敗しました",
                            Data = cardIdm
                        });
                    }
                }
            }

            return new CsvImportResult
            {
                Success = errors.Count == 0,
                ImportedCount = importedCount,
                SkippedCount = skippedCount,
                ErrorCount = errors.Count,
                Errors = errors
            };
        }
        catch (Exception ex)
        {
            return new CsvImportResult
            {
                Success = false,
                ErrorMessage = $"ファイルの読み込みエラー: {ex.Message}",
                Errors = errors
            };
        }
    }

    /// <summary>
    /// 職員CSVをインポート
    /// </summary>
    /// <param name="filePath">CSVファイルパス</param>
    /// <param name="skipExisting">既存データをスキップするか（falseの場合は更新）</param>
    public async Task<CsvImportResult> ImportStaffAsync(string filePath, bool skipExisting = true)
    {
        var errors = new List<CsvImportError>();
        var importedCount = 0;
        var skippedCount = 0;

        try
        {
            var lines = await ReadCsvFileAsync(filePath);
            if (lines.Count < 2)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ErrorMessage = "CSVファイルにデータがありません（ヘッダー行のみ）"
                };
            }

            // ヘッダー行をスキップして処理
            for (var i = 1; i < lines.Count; i++)
            {
                var lineNumber = i + 1;
                var line = lines[i];

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var fields = ParseCsvLine(line);

                // 最低4列（職員IDm, 氏名, 職員番号, 備考）が必要
                if (fields.Count < 4)
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "列数が不足しています",
                        Data = line
                    });
                    continue;
                }

                var staffIdm = fields[0].Trim();
                var name = fields[1].Trim();
                var number = fields.Count > 2 ? fields[2].Trim() : "";
                var note = fields.Count > 3 ? fields[3].Trim() : "";

                // バリデーション
                if (string.IsNullOrWhiteSpace(staffIdm))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "職員IDmは必須です",
                        Data = line
                    });
                    continue;
                }

                var staffIdmValidation = _validationService.ValidateStaffIdm(staffIdm);
                if (!staffIdmValidation.IsValid)
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = staffIdmValidation.ErrorMessage ?? "職員IDmの形式が不正です",
                        Data = staffIdm
                    });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "氏名は必須です",
                        Data = line
                    });
                    continue;
                }

                // 既存チェック
                var existingStaff = await _staffRepository.GetByIdmAsync(staffIdm, includeDeleted: true);
                if (existingStaff != null)
                {
                    if (skipExisting)
                    {
                        skippedCount++;
                        continue;
                    }

                    // 更新処理
                    existingStaff.Name = name;
                    existingStaff.Number = string.IsNullOrWhiteSpace(number) ? null : number;
                    existingStaff.Note = string.IsNullOrWhiteSpace(note) ? null : note;

                    if (await _staffRepository.UpdateAsync(existingStaff))
                    {
                        importedCount++;
                    }
                    else
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "職員の更新に失敗しました",
                            Data = staffIdm
                        });
                    }
                }
                else
                {
                    // 新規登録
                    var staff = new Staff
                    {
                        StaffIdm = staffIdm,
                        Name = name,
                        Number = string.IsNullOrWhiteSpace(number) ? null : number,
                        Note = string.IsNullOrWhiteSpace(note) ? null : note
                    };

                    if (await _staffRepository.InsertAsync(staff))
                    {
                        importedCount++;
                    }
                    else
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "職員の登録に失敗しました",
                            Data = staffIdm
                        });
                    }
                }
            }

            return new CsvImportResult
            {
                Success = errors.Count == 0,
                ImportedCount = importedCount,
                SkippedCount = skippedCount,
                ErrorCount = errors.Count,
                Errors = errors
            };
        }
        catch (Exception ex)
        {
            return new CsvImportResult
            {
                Success = false,
                ErrorMessage = $"ファイルの読み込みエラー: {ex.Message}",
                Errors = errors
            };
        }
    }

    /// <summary>
    /// CSVファイルを読み込み（UTF-8 BOM対応）
    /// </summary>
    private static async Task<List<string>> ReadCsvFileAsync(string filePath)
    {
        // UTF-8 with BOMに対応
        using var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line != null)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>
    /// CSV行をパース（ダブルクォート対応）
    /// </summary>
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var currentField = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    // エスケープされたダブルクォート
                    currentField.Append('"');
                    i++; // 次の文字をスキップ
                }
                else
                {
                    // クォートの開始/終了
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                // フィールドの区切り
                fields.Add(currentField.ToString());
                currentField.Clear();
            }
            else
            {
                currentField.Append(c);
            }
        }

        // 最後のフィールドを追加
        fields.Add(currentField.ToString());

        return fields;
    }
}
