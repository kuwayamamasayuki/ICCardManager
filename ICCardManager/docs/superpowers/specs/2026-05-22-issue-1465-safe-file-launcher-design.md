# Issue #1465: Process.Start(UseShellExecute=true) のパス検証強化（SafeFileLauncher 導入）

- Issue: [#1465](https://github.com/masayuki-kuwayama/iccard-manager/issues/1465)
- 種別: セキュリティ修正 (priority: medium / type: bug / area: ui)
- 作成日: 2026-05-22
- 関連 Issue: ProgramData ACL 強化（別途）、Sec M2（2026-05-08 レビュー）

## 1. 背景

ViewModels の「出力フォルダを開く」「作成されたファイルを開く」系コマンドが、`OutputFolder`（`report_output_config.txt` から読み込み）や `LastExportedFile` を `Process.Start(UseShellExecute=true)` で起動している。設定ファイルが書換可能な場合、攻撃者が任意の `.exe` パスを仕込み、ユーザーが合法的なボタンをクリックすることでコード実行に誘導できる。

現状の検査は `File.Exists` / `Directory.Exists` のみで、拡張子・実行可能性のチェックがない。

### 該当箇所（7 箇所）

| ファイル | 行 | 種別 | 対象 |
|---|---|---|---|
| `ViewModels/ReportViewModel.cs` | 581 | フォルダ | `OutputFolder`（`report_output_config.txt` 由来 = 主攻撃面） |
| `ViewModels/ReportViewModel.cs` | 597 | ファイル | 帳票 `.xlsx` |
| `ViewModels/DataExportImportViewModel.cs` | 893 | ファイル | `LastExportedFile`（`.csv`） |
| `ViewModels/DataExportImportViewModel.cs` | 912 | フォルダ | `LastExportedFile` の親フォルダ |
| `ViewModels/SystemManageViewModel.cs` | 282 | フォルダ | バックアップフォルダ |
| `ViewModels/MainViewModel.cs` | 2388 | フォルダ | `Docs` フォルダ（インストール先固定） |
| `ViewModels/OperationLogSearchViewModel.cs` | 446 | ファイル | 操作ログ `.xlsx` |

## 2. 脅威モデル

- **想定攻撃者**: ローカル PC への書き込み権限を持つユーザー / マルウェア（共有モード時はネットワーク経由でも可能性）
- **攻撃ベクトル**: `report_output_config.txt` 等の設定ファイル書き換え → `OutputFolder` に `C:\Path\evil.exe` を仕込む → ユーザーが「出力フォルダを開く」ボタンを押す → `UseShellExecute=true` で `.exe` が実行される
- **本 Issue でのスコープ**: defense-in-depth の **2 層目**（拡張子ホワイトリスト + シェル関連付け回避）。設定ファイル自体の ACL 強化は別 Issue。

### Sticky boundary（本 Issue では扱わない）

- `report_output_config.txt` 等の **ACL 強化** は ProgramData 配下権限見直しの別 Issue に委ねる
- DLL hijacking / supply chain 攻撃は本 Issue の射程外
- Office マクロ実行リスクは `.xlsx` 開封という UX の本旨であり、Office 側の信頼境界に委ねる

## 3. 設計

### 3.1 構成図

```
┌─────────────────────────────┐
│   ViewModels (7 箇所)       │
│   - ReportVM                │
│   - DataExportImportVM      │
│   - SystemManageVM          │
│   - MainVM                  │
│   - OperationLogSearchVM    │
└──────────────┬──────────────┘
               │ DI
               ▼
┌─────────────────────────────┐
│   ISafeFileLauncher         │  ← Services/
│   - LaunchFolder(path)      │
│   - LaunchFile(path)        │
└──────────────┬──────────────┘
               │ uses
               ▼
┌─────────────────────────────┐
│   SafeFilePathValidator     │  ← Common/ (静的・純粋関数)
│   - ValidateFolder(path)    │
│   - ValidateFile(path)      │
└─────────────────────────────┘
```

### 3.2 `SafeFilePathValidator`（純粋関数・I/O なし）

`ICCardManager/src/ICCardManager/Common/SafeFilePathValidator.cs`

```csharp
public static class SafeFilePathValidator
{
    // 「ファイルを開く」コマンドで許可する拡張子（小文字・ドット込み）。
    // ホワイトリスト方式: 本アプリが生成する 2 種類のみ。
    private static readonly IReadOnlyCollection<string> AllowedFileExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".xlsx", ".csv" };

    public static PathValidationResult ValidateFolder(string folderPath);
    public static PathValidationResult ValidateFile(string filePath);
}

public sealed class PathValidationResult
{
    public bool IsValid { get; }
    public string ErrorMessage { get; }  // 失敗時のみ
    public static PathValidationResult Ok();
    public static PathValidationResult Fail(string message);
}
```

#### 検証ルール

**`ValidateFolder(path)`**
1. `string.IsNullOrWhiteSpace(path)` → Fail
2. `path.Any(c => char.IsControl(c))` → Fail（NUL / 制御文字を拒否）
3. `path.IndexOfAny(Path.GetInvalidPathChars())` >= 0 → Fail
4. それ以外 → Ok

**`ValidateFile(path)`**
1. `string.IsNullOrWhiteSpace(path)` → Fail
2. `path.Any(c => char.IsControl(c))` → Fail
3. `path.IndexOfAny(Path.GetInvalidPathChars())` >= 0 → Fail
4. `Path.GetExtension(path)` が `AllowedFileExtensions` に含まれない → Fail（**主要防御線**）
5. それ以外 → Ok

> `File.Exists` / `Directory.Exists` は `SafeFileLauncher` 側で確認する（Validator は I/O を持たない）。

#### エラーメッセージ品質（Issue #1275 準拠）

「何が／なぜ／どうすれば」の 3 要素を満たす。例:

- `「ファイルパスが空です。エクスポートを実行してからお試しください。」`
- `「ファイル拡張子「.exe」は開けません。本アプリは「.xlsx」「.csv」のみ開きます。」`
- `「パスに無効な文字が含まれています。設定ファイルを確認してください。」`

### 3.3 `ISafeFileLauncher` / `SafeFileLauncher`

`ICCardManager/src/ICCardManager/Services/ISafeFileLauncher.cs`

```csharp
public interface ISafeFileLauncher
{
    SafeFileLaunchResult LaunchFolder(string folderPath);
    SafeFileLaunchResult LaunchFile(string filePath);
}

public sealed class SafeFileLaunchResult
{
    public bool Success { get; }
    public string ErrorMessage { get; }
    public static SafeFileLaunchResult Ok();
    public static SafeFileLaunchResult Fail(string message);
}
```

`ICCardManager/src/ICCardManager/Services/SafeFileLauncher.cs`

```csharp
public sealed class SafeFileLauncher : ISafeFileLauncher
{
    public SafeFileLaunchResult LaunchFolder(string folderPath)
    {
        var validation = SafeFilePathValidator.ValidateFolder(folderPath);
        if (!validation.IsValid) return SafeFileLaunchResult.Fail(validation.ErrorMessage);

        if (!Directory.Exists(folderPath))
            return SafeFileLaunchResult.Fail("フォルダが見つかりません。パス: " + folderPath);

        // explorer.exe を直接起動し、シェル関連付けを経由しない（defense-in-depth）
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "\"" + folderPath + "\"",
            UseShellExecute = false
        });
        return SafeFileLaunchResult.Ok();
    }

    public SafeFileLaunchResult LaunchFile(string filePath)
    {
        var validation = SafeFilePathValidator.ValidateFile(filePath);
        if (!validation.IsValid) return SafeFileLaunchResult.Fail(validation.ErrorMessage);

        if (!File.Exists(filePath))
            return SafeFileLaunchResult.Fail("ファイルが見つかりません。パス: " + filePath);

        // .xlsx / .csv の関連付け（Excel 等）を起動するため UseShellExecute=true 必須。
        // Validator で拡張子を絞っているため、関連付け先は表計算アプリに限られる。
        Process.Start(new ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = true,
            Verb = "open"
        });
        return SafeFileLaunchResult.Ok();
    }
}
```

#### DI 登録

`App.xaml.cs` の DI コンテナに以下を追加:

```csharp
services.AddSingleton<ISafeFileLauncher, SafeFileLauncher>();
```

ステートレスなので Singleton で問題ない。

### 3.4 ViewModel 改修パターン

既存:

```csharp
[RelayCommand]
public void OpenOutputFolder()
{
    if (!string.IsNullOrEmpty(OutputFolder) && Directory.Exists(OutputFolder))
    {
        Process.Start(new ProcessStartInfo { FileName = OutputFolder, UseShellExecute = true });
    }
}
```

改修後:

```csharp
[RelayCommand]
public void OpenOutputFolder()
{
    var result = _safeFileLauncher.LaunchFolder(OutputFolder);
    if (!result.Success)
    {
        SetStatus(result.ErrorMessage, isError: true);
    }
}
```

各 ViewModel のコンストラクタに `ISafeFileLauncher safeFileLauncher` 引数を追加し、privateフィールドへ保持。

## 4. テスト計画

### 4.1 `SafeFilePathValidatorTests`（新規・最重要）

| ケース | 入力 | 期待 |
|---|---|---|
| 空文字 (folder) | `""` | Fail |
| 空白のみ (folder) | `"   "` | Fail |
| null (folder) | `null` | Fail |
| 制御文字を含む (folder) | `"C:\\foo "` | Fail |
| 通常フォルダ | `"C:\\Users\\foo"` | Ok |
| UNC パス | `"\\\\server\\share"` | Ok |
| .exe ファイル | `"C:\\evil.exe"` | Fail |
| .bat ファイル | `"C:\\evil.bat"` | Fail |
| .com / .vbs / .ps1 / .js / .scr / .lnk | 各 | Fail |
| .xlsx ファイル | `"C:\\report.xlsx"` | Ok |
| .csv ファイル | `"C:\\export.csv"` | Ok |
| .XLSX（大文字） | `"C:\\report.XLSX"` | Ok（拡張子は大文字小文字無視） |
| .pdf | `"C:\\foo.pdf"` | Fail（許可外） |
| 拡張子なし | `"C:\\foo"` | Fail |

### 4.2 `SafeFileLauncherTests`（新規・I/O 含む）

実プロセス起動はテストしない。Validator NG パスや存在しないパスに対して `SafeFileLaunchResult.Fail` が返ることだけを `Path.GetTempPath()` 配下で検証。

- Validator が NG → Fail
- 存在しないフォルダ/ファイル → Fail
- 存在する一時フォルダ → Ok（`Process.Start` 結果は確認しない＝ test smoke 回避）。Process.Start 自体は `[Fact(Skip="manual")]` で残す

> **注意**: `Process.Start("explorer.exe", "...")` は CI で実行すると Windows GUI を立ち上げてしまうため、xUnit テスト上では Validator 経由の `Fail` パスのみを CI 検証する。

### 4.3 ViewModel テスト（既存ファイルに追加）

各 ViewModel テストクラスに以下を追加:

- `Mock<ISafeFileLauncher>` を DI
- コマンド呼び出し時に `LaunchFolder` / `LaunchFile` が **1 回**呼ばれたことを検証
- 失敗時に `Status` がエラー表示になることを検証

該当 ViewModel テストクラス:

- `ReportViewModelTests`
- `DataExportImportViewModelTests`
- `SystemManageViewModelTests`
- `MainViewModelTests`
- `OperationLogSearchViewModelTests`

## 5. ドキュメント更新

| ファイル | 更新内容 |
|---|---|
| `ICCardManager/CHANGELOG.md` | `### Unreleased` セクションに「Process.Start のパス検証強化 (Issue #1465)」を追記 |
| `ICCardManager/docs/design/04_アーキテクチャ設計書.md` | サービス一覧に `ISafeFileLauncher` を追加（該当節があれば） |
| `ICCardManager/docs/design/07_テスト設計書.md` | テスト件数表 §1.1a に新規テスト件数を反映 |

## 6. 非ゴール

- `report_output_config.txt` の ACL 強化（別 Issue）
- 既存の `.exe` 実行履歴の検出・通知
- 「`.pdf` 帳票出力機能の追加」のような UX 拡張

## 7. ロールアウト・互換性

- 既存ユーザーへの影響: なし（正常系の `.xlsx` / `.csv` / フォルダオープンは従来通り動作）
- 攻撃面の縮退: 設定ファイル書換に対する 1 段の防御層が追加される
- マイグレーション不要（DB スキーマ変更なし）

## 8. 確認用テスト（手動）

1. `OutputFolder` を `C:\Windows\System32\notepad.exe` に書き換えて「出力フォルダを開く」 → エラー表示でメモ帳が起動しないこと
2. 通常のエクスポート → 「ファイルを開く」 → Excel が起動すること
3. エクスポート後にファイルを削除 → 「ファイルを開く」 → エラー表示で何も起動しないこと
