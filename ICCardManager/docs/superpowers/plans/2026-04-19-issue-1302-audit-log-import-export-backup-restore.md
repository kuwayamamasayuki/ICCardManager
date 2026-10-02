# Issue #1302: 監査ログへの一括操作記録 実装プラン

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** CSVインポート/エクスポート/バックアップ/リストア操作を `operation_log` テーブルに記録し、監査証跡として6年保管する。

**Architecture:** `OperationLogger` に新しい Action 定数（`IMPORT`/`EXPORT`/`BACKUP`）と DB 単位の `Tables.Database` を追加し、4 つの新 API (`LogImportAsync`/`LogExportAsync`/`LogBackupAsync`/`LogRestoreAsync`) を実装する。呼び出しは `DataExportImportViewModel` と `SystemManageViewModel` の成功パスから行う。操作者は既存の `ICurrentOperatorContext` (PR #1291 で導入) から自動解決し、セッション無効時は `GuiOperator` にフォールバックする。

**Tech Stack:** C# 10 / .NET Framework 4.8 / xUnit / FluentAssertions / Moq / SQLite (InMemory for tests)

**設計上の決定事項:**

1. **`Actions.Restore` の意味拡張**: 既存定数は「レコード単位の復元 (論理削除取消)」に使われているが、Issue #1302 の DB リストアと用途が近接するため、定数名は共用し、`TargetTable = "database"` で区別する。
2. **`Actions.Import/Export/Backup` 新設**: 新規追加。
3. **`Tables.Database` / `Tables.LedgerDetail` 新設**: DB 丸ごと操作および `ledger_detail` CSV 操作用。
4. **`TargetId` の扱い**: ファイル名（basename のみ）を格納。フルパスは `AfterData` の JSON に含める（ファイル名にはパス区切り文字を含めない監査用簡易キー）。
5. **`AfterData` の JSON**: 件数・ファイルパス等の詳細情報を格納。
6. **VM 層テストの方針**: `DataExportImportViewModel` / `SystemManageViewModel` は `SaveFileDialog`/`OpenFileDialog` を直接 `new` しているため単体テスト不可能。新 API (`OperationLogger`) の単体テストは網羅し、VM 統合部分は手動 UI テスト手順を明記する（ユーザー確認依頼）。

---

## File Structure

**新規作成:**
- なし（全て既存ファイルの修正）

**修正:**
- `ICCardManager/src/ICCardManager/Services/OperationLogger.cs` — 新しい定数・API 追加
- `ICCardManager/src/ICCardManager/ViewModels/DataExportImportViewModel.cs` — Import/Export 成功時に Log 呼び出し
- `ICCardManager/src/ICCardManager/ViewModels/SystemManageViewModel.cs` — Backup/Restore 成功時に Log 呼び出し（DI に `OperationLogger` 追加）
- `ICCardManager/src/ICCardManager/App.xaml.cs` — SystemManageViewModel の DI 依存追加対応（既存登録に依存関係が追加されるだけなので通常は変更不要だが確認）
- `ICCardManager/tests/ICCardManager.Tests/Services/OperationLoggerTests.cs` — 新 API の単体テスト追加
- `ICCardManager/CHANGELOG.md` — Unreleased エントリ追加
- `docs/design/05_クラス設計書.md` — OperationLogger クラスの API 表更新
- `docs/design/07_テスト設計書.md` — 新テストケース追記

---

## Task 1: ブランチ作成と事前確認

**Files:**
- なし (git コマンドのみ)

- [ ] **Step 1: main を最新化**

```bash
cd /mnt/d/OneDrive/交通系/src
git checkout main
git pull origin main
```

- [ ] **Step 2: ブランチ作成**

```bash
git checkout -b feat/issue-1302-audit-log-bulk-operations
```

- [ ] **Step 3: 現在の OperationLoggerTests が全て通ることを確認（ベースライン）**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName~OperationLoggerTests"`
Expected: 既存テスト全てが PASS

---

## Task 2: 新しい Actions / Tables 定数を追加

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/OperationLogger.cs:26-60`

- [ ] **Step 1: Actions 定数に新しい値を追加**

`Actions` 静的クラス (行 26-34) に以下を追加：

```csharp
public static class Actions
{
    public const string Insert = "INSERT";
    public const string Update = "UPDATE";
    public const string Delete = "DELETE";
    public const string Restore = "RESTORE";
    public const string Merge = "MERGE";
    public const string Split = "SPLIT";
    // Issue #1302: 一括操作 (監査証跡)
    public const string Import = "IMPORT";
    public const string Export = "EXPORT";
    public const string Backup = "BACKUP";
}
```

- [ ] **Step 2: Tables 定数に新しい値を追加**

`Tables` 静的クラス (行 55-60) に以下を追加：

```csharp
public static class Tables
{
    public const string Staff = "staff";
    public const string IcCard = "ic_card";
    public const string Ledger = "ledger";
    // Issue #1302: 一括操作対象
    public const string LedgerDetail = "ledger_detail";
    public const string Database = "database";
}
```

- [ ] **Step 3: ビルドが通ることを確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/src/ICCardManager/ICCardManager.csproj`
Expected: エラーなしでビルド成功

---

## Task 3: `LogImportAsync` を TDD で実装

**Files:**
- Test: `ICCardManager/tests/ICCardManager.Tests/Services/OperationLoggerTests.cs`
- Modify: `ICCardManager/src/ICCardManager/Services/OperationLogger.cs`

- [ ] **Step 1: 失敗するテストを追加**

`OperationLoggerTests.cs` に以下のテストを追加する（ファイル末尾の最終 `}` の前に挿入）：

```csharp
#region Issue #1302: 一括操作 (Import/Export/Backup/Restore) のテスト

[Fact]
public async Task LogImportAsync_WithCsvFile_RecordsImportAction()
{
    // Arrange
    var context = new CurrentOperatorContext(_clock);
    context.BeginSession("1234567890ABCDEF", "田中 太郎");
    var logger = new OperationLogger(_repository, context);

    // Act
    await logger.LogImportAsync(
        OperationLogger.Tables.Staff,
        @"C:\import\staff_20260419.csv",
        insertedCount: 10,
        skippedCount: 2,
        errorCount: 0);

    // Assert
    var logs = await _repository.GetAllAsync();
    logs.Should().HaveCount(1);
    var log = logs[0];
    log.Action.Should().Be("IMPORT");
    log.TargetTable.Should().Be("staff");
    log.TargetId.Should().Be("staff_20260419.csv"); // basename のみ
    log.OperatorIdm.Should().Be("1234567890ABCDEF");
    log.OperatorName.Should().Be("田中 太郎");
    log.BeforeData.Should().BeNull();
    log.AfterData.Should().Contain("\"InsertedCount\":10");
    log.AfterData.Should().Contain("\"SkippedCount\":2");
    log.AfterData.Should().Contain("\"ErrorCount\":0");
    log.AfterData.Should().Contain("staff_20260419.csv");
}

[Fact]
public async Task LogImportAsync_WithoutSession_FallsBackToGuiOperator()
{
    // Arrange
    var context = new CurrentOperatorContext(_clock);
    // セッション未開始
    var logger = new OperationLogger(_repository, context);

    // Act
    await logger.LogImportAsync(
        OperationLogger.Tables.Ledger,
        @"D:\data\ledger.csv",
        insertedCount: 100,
        skippedCount: 0,
        errorCount: 0);

    // Assert
    var logs = await _repository.GetAllAsync();
    logs.Should().HaveCount(1);
    logs[0].OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
    logs[0].OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
}

#endregion
```

注: `_repository` / `_clock` は既存 `OperationLoggerTests` のフィクスチャを使う。既存のテストが同名フィールドを定義していない場合は、既存テストクラスのセットアップに合わせて書き換えること。（既存テストクラスを先に確認すること。）

- [ ] **Step 2: テスト実行（失敗を確認）**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName~LogImportAsync"`
Expected: FAIL (メソッド未定義で コンパイルエラー)

- [ ] **Step 3: `LogImportAsync` を実装**

`OperationLogger.cs` の「新 API」リージョン内（`LogLedgerSplitAsync` の直後、`#endregion` の前）に以下を追加：

```csharp
/// <summary>
/// CSV インポート操作のログを記録。
/// </summary>
/// <param name="tableName">対象テーブル名 (<see cref="Tables"/> 定数)</param>
/// <param name="filePath">インポート元ファイルのフルパス</param>
/// <param name="insertedCount">新規挿入件数</param>
/// <param name="skippedCount">スキップ件数</param>
/// <param name="errorCount">エラー件数</param>
public async Task LogImportAsync(
    string tableName,
    string filePath,
    int insertedCount,
    int skippedCount,
    int errorCount)
{
    var (idm, name) = ResolveOperator();
    var fileName = System.IO.Path.GetFileName(filePath);
    var payload = new
    {
        FilePath = filePath,
        FileName = fileName,
        InsertedCount = insertedCount,
        SkippedCount = skippedCount,
        ErrorCount = errorCount
    };
    await _operationLogRepository.InsertAsync(new OperationLog
    {
        Timestamp = DateTime.Now,
        OperatorIdm = idm,
        OperatorName = name,
        TargetTable = tableName,
        TargetId = fileName,
        Action = Actions.Import,
        BeforeData = null,
        AfterData = SerializeToJson(payload)
    }).ConfigureAwait(false);
}
```

- [ ] **Step 4: テスト実行（成功を確認）**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName~LogImportAsync"`
Expected: PASS (2 件)

---

## Task 4: `LogExportAsync` を TDD で実装

**Files:**
- Test: `ICCardManager/tests/ICCardManager.Tests/Services/OperationLoggerTests.cs`
- Modify: `ICCardManager/src/ICCardManager/Services/OperationLogger.cs`

- [ ] **Step 1: 失敗するテストを追加**

Task 3 で追加した `#region Issue #1302` 内に追記：

```csharp
[Fact]
public async Task LogExportAsync_WithCsvFile_RecordsExportAction()
{
    // Arrange
    var context = new CurrentOperatorContext(_clock);
    context.BeginSession("AABBCCDDEEFF0011", "山田 花子");
    var logger = new OperationLogger(_repository, context);

    // Act
    await logger.LogExportAsync(
        OperationLogger.Tables.Ledger,
        @"C:\export\ledgers_20260419_20260419.csv",
        recordCount: 523);

    // Assert
    var logs = await _repository.GetAllAsync();
    logs.Should().HaveCount(1);
    var log = logs[0];
    log.Action.Should().Be("EXPORT");
    log.TargetTable.Should().Be("ledger");
    log.TargetId.Should().Be("ledgers_20260419_20260419.csv");
    log.OperatorIdm.Should().Be("AABBCCDDEEFF0011");
    log.OperatorName.Should().Be("山田 花子");
    log.BeforeData.Should().BeNull();
    log.AfterData.Should().Contain("\"RecordCount\":523");
}

[Fact]
public async Task LogExportAsync_LedgerDetailTable_RecordsCorrectly()
{
    // Arrange
    var context = new CurrentOperatorContext(_clock);
    var logger = new OperationLogger(_repository, context);

    // Act
    await logger.LogExportAsync(
        OperationLogger.Tables.LedgerDetail,
        @"C:\export\ledger_details.csv",
        recordCount: 1500);

    // Assert
    var logs = await _repository.GetAllAsync();
    logs[0].TargetTable.Should().Be("ledger_detail");
    logs[0].AfterData.Should().Contain("\"RecordCount\":1500");
}
```

- [ ] **Step 2: テスト実行（失敗を確認）**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName~LogExportAsync"`
Expected: FAIL (メソッド未定義)

- [ ] **Step 3: `LogExportAsync` を実装**

```csharp
/// <summary>
/// CSV/Excel エクスポート操作のログを記録。
/// </summary>
/// <param name="tableName">対象テーブル名 (<see cref="Tables"/> 定数)</param>
/// <param name="filePath">エクスポート先ファイルのフルパス</param>
/// <param name="recordCount">出力件数</param>
public async Task LogExportAsync(string tableName, string filePath, int recordCount)
{
    var (idm, name) = ResolveOperator();
    var fileName = System.IO.Path.GetFileName(filePath);
    var payload = new
    {
        FilePath = filePath,
        FileName = fileName,
        RecordCount = recordCount
    };
    await _operationLogRepository.InsertAsync(new OperationLog
    {
        Timestamp = DateTime.Now,
        OperatorIdm = idm,
        OperatorName = name,
        TargetTable = tableName,
        TargetId = fileName,
        Action = Actions.Export,
        BeforeData = null,
        AfterData = SerializeToJson(payload)
    }).ConfigureAwait(false);
}
```

- [ ] **Step 4: テスト実行（成功を確認）**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName~LogExportAsync"`
Expected: PASS

---

## Task 5: `LogBackupAsync` を TDD で実装

**Files:**
- Test: `ICCardManager/tests/ICCardManager.Tests/Services/OperationLoggerTests.cs`
- Modify: `ICCardManager/src/ICCardManager/Services/OperationLogger.cs`

- [ ] **Step 1: 失敗するテストを追加**

```csharp
[Fact]
public async Task LogBackupAsync_RecordsBackupActionWithDatabaseTarget()
{
    // Arrange
    var context = new CurrentOperatorContext(_clock);
    context.BeginSession("FFEEDDCCBBAA9988", "管理者");
    var logger = new OperationLogger(_repository, context);

    // Act
    await logger.LogBackupAsync(@"C:\backup\iccard_20260419_100000.db");

    // Assert
    var logs = await _repository.GetAllAsync();
    logs.Should().HaveCount(1);
    var log = logs[0];
    log.Action.Should().Be("BACKUP");
    log.TargetTable.Should().Be("database");
    log.TargetId.Should().Be("iccard_20260419_100000.db");
    log.OperatorName.Should().Be("管理者");
    log.BeforeData.Should().BeNull();
    log.AfterData.Should().Contain("iccard_20260419_100000.db");
}
```

- [ ] **Step 2: テスト実行（失敗を確認）**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName~LogBackupAsync"`
Expected: FAIL

- [ ] **Step 3: `LogBackupAsync` を実装**

```csharp
/// <summary>
/// バックアップ取得のログを記録。
/// </summary>
/// <param name="filePath">バックアップファイルのフルパス</param>
public async Task LogBackupAsync(string filePath)
{
    var (idm, name) = ResolveOperator();
    var fileName = System.IO.Path.GetFileName(filePath);
    var payload = new
    {
        FilePath = filePath,
        FileName = fileName
    };
    await _operationLogRepository.InsertAsync(new OperationLog
    {
        Timestamp = DateTime.Now,
        OperatorIdm = idm,
        OperatorName = name,
        TargetTable = Tables.Database,
        TargetId = fileName,
        Action = Actions.Backup,
        BeforeData = null,
        AfterData = SerializeToJson(payload)
    }).ConfigureAwait(false);
}
```

- [ ] **Step 4: テスト実行（成功を確認）**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName~LogBackupAsync"`
Expected: PASS

---

## Task 6: `LogRestoreAsync` を TDD で実装 (DB リストア用)

**Files:**
- Test: `ICCardManager/tests/ICCardManager.Tests/Services/OperationLoggerTests.cs`
- Modify: `ICCardManager/src/ICCardManager/Services/OperationLogger.cs`

**注:** 既存の `LogStaffRestoreAsync` / `LogCardRestoreAsync` は「レコード論理削除の復元」で、Action=RESTORE + TargetTable=staff/ic_card。新しい `LogRestoreAsync` は「DB 丸ごとリストア」で、Action=RESTORE + TargetTable=database。テストで区別を確認する。

- [ ] **Step 1: 失敗するテストを追加**

```csharp
[Fact]
public async Task LogRestoreAsync_RecordsRestoreActionWithDatabaseTarget()
{
    // Arrange
    var context = new CurrentOperatorContext(_clock);
    context.BeginSession("1122334455667788", "管理者2");
    var logger = new OperationLogger(_repository, context);

    // Act
    await logger.LogRestoreAsync(@"C:\backup\iccard_backup.db");

    // Assert
    var logs = await _repository.GetAllAsync();
    logs.Should().HaveCount(1);
    var log = logs[0];
    log.Action.Should().Be("RESTORE");
    log.TargetTable.Should().Be("database");  // ← 職員/カード論理削除復元と区別
    log.TargetId.Should().Be("iccard_backup.db");
    log.OperatorName.Should().Be("管理者2");
    log.AfterData.Should().Contain("iccard_backup.db");
}

[Fact]
public async Task LogRestoreAsync_DistinctFromStaffRestore_ByTargetTable()
{
    // Arrange
    var context = new CurrentOperatorContext(_clock);
    var logger = new OperationLogger(_repository, context);
    var staff = new Staff
    {
        StaffIdm = "AAAA11112222BBBB",
        Name = "復元対象",
        IsDeleted = false
    };

    // Act — 両方のRESTORE を実行
    await logger.LogStaffRestoreAsync(staff);
    await logger.LogRestoreAsync(@"C:\backup.db");

    // Assert — Action=RESTORE が2件あるが TargetTable で区別可能
    var logs = await _repository.GetAllAsync();
    logs.Should().HaveCount(2);
    logs.Should().Contain(l => l.Action == "RESTORE" && l.TargetTable == "staff");
    logs.Should().Contain(l => l.Action == "RESTORE" && l.TargetTable == "database");
}
```

- [ ] **Step 2: テスト実行（失敗を確認）**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName~LogRestoreAsync_RecordsRestore|LogRestoreAsync_DistinctFromStaffRestore"`
Expected: FAIL (メソッド未定義)

- [ ] **Step 3: `LogRestoreAsync(string filePath)` を実装**

注: 既存の `LogStaffRestoreAsync(Staff)` / `LogCardRestoreAsync(IcCard)` とはシグネチャが違うのでオーバーロード解決は自動的に区別される。

```csharp
/// <summary>
/// DB リストア操作のログを記録。
/// </summary>
/// <remarks>
/// Issue #1302: 既存の <see cref="LogStaffRestoreAsync(Staff)"/> はレコード単位復元だが、
/// こちらは DB 丸ごとリストア。<c>TargetTable="database"</c> で区別する。
/// </remarks>
/// <param name="filePath">リストア元ファイルのフルパス</param>
public async Task LogRestoreAsync(string filePath)
{
    var (idm, name) = ResolveOperator();
    var fileName = System.IO.Path.GetFileName(filePath);
    var payload = new
    {
        FilePath = filePath,
        FileName = fileName
    };
    await _operationLogRepository.InsertAsync(new OperationLog
    {
        Timestamp = DateTime.Now,
        OperatorIdm = idm,
        OperatorName = name,
        TargetTable = Tables.Database,
        TargetId = fileName,
        Action = Actions.Restore,
        BeforeData = null,
        AfterData = SerializeToJson(payload)
    }).ConfigureAwait(false);
}
```

- [ ] **Step 4: テスト実行（成功を確認）**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName~LogRestoreAsync"`
Expected: PASS

- [ ] **Step 5: OperationLoggerTests 全体を実行して既存テストが壊れていないことを確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName~OperationLoggerTests"`
Expected: 全て PASS（既存 + 新規）

- [ ] **Step 6: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/OperationLogger.cs ICCardManager/tests/ICCardManager.Tests/Services/OperationLoggerTests.cs
git commit -m "$(cat <<'EOF'
feat: OperationLogger に Import/Export/Backup/Restore API を追加 (Issue #1302)

- Actions 定数に IMPORT/EXPORT/BACKUP を追加 (RESTORE は既存を流用)
- Tables 定数に LedgerDetail / Database を追加
- LogImportAsync / LogExportAsync / LogBackupAsync / LogRestoreAsync の4つの新APIを実装
- すべて ICurrentOperatorContext 経由で操作者を解決 (なりすまし防止を踏襲)
- DB リストアと職員論理削除復元は TargetTable で区別 ("database" vs "staff"/"ic_card")

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: `DataExportImportViewModel` にエクスポートログ記録を追加

**Files:**
- Modify: `ICCardManager/src/ICCardManager/ViewModels/DataExportImportViewModel.cs`

**注:** このタスクは UI ダイアログ経由の処理なので単体テスト不可。手動 UI テストで確認する。

- [ ] **Step 1: `OperationLogger` を DI 依存に追加**

コンストラクタ (~行 280 周辺) に `OperationLogger` パラメータを追加。既存のフィールド宣言 (~行 95 周辺) にも `private readonly OperationLogger _operationLogger;` を追加し、コンストラクタで代入。

例:

```csharp
private readonly OperationLogger _operationLogger;

public DataExportImportViewModel(
    // ...既存引数,
    IDialogService dialogService,
    OperationLogger operationLogger)
{
    // ...既存代入
    _dialogService = dialogService;
    _operationLogger = operationLogger;
}
```

- [ ] **Step 2: `ExportAsync` の成功パスにログ呼び出しを追加**

`ExportAsync` メソッド (行 349-418) の `result.Success = true` 分岐、`LastExportedFile` 代入の直後に以下を追加：

```csharp
// Issue #1302: 監査ログ記録
var tableName = SelectedExportType switch
{
    ExportType.Cards => OperationLogger.Tables.IcCard,
    ExportType.Staff => OperationLogger.Tables.Staff,
    ExportType.Ledgers => OperationLogger.Tables.Ledger,
    ExportType.LedgerDetails => OperationLogger.Tables.LedgerDetail,
    _ => "unknown"
};
await _operationLogger.LogExportAsync(tableName, dialog.FileName, result.ExportedCount);
```

注: `SelectedExportType` / `ExportType` enum の実名は実装時にファイルを開いて確認すること。プロパティ名・enum 値名が異なる場合は合わせる。

- [ ] **Step 3: `ExecuteImportAsync` の成功パスにログ呼び出しを追加**

`ExecuteImportAsync` メソッド (行 532-648) の `result.Success = true` 分岐に以下を追加：

```csharp
// Issue #1302: 監査ログ記録
var tableName = SelectedImportType switch
{
    ImportType.Cards => OperationLogger.Tables.IcCard,
    ImportType.Staff => OperationLogger.Tables.Staff,
    ImportType.Ledgers => OperationLogger.Tables.Ledger,
    ImportType.LedgerDetails => OperationLogger.Tables.LedgerDetail,
    _ => "unknown"
};
await _operationLogger.LogImportAsync(
    tableName,
    ImportFilePath,  // 実際に使われている変数名に合わせる
    result.ImportedCount,
    result.SkippedCount,
    result.ErrorCount);
```

- [ ] **Step 4: ビルドを通す**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/src/ICCardManager/ICCardManager.csproj`
Expected: エラーなし

- [ ] **Step 5: 既存テスト全体を走らせてリグレッションがないことを確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests`
Expected: 全て PASS

---

## Task 8: `SystemManageViewModel` にバックアップ/リストアログ記録を追加

**Files:**
- Modify: `ICCardManager/src/ICCardManager/ViewModels/SystemManageViewModel.cs`

- [ ] **Step 1: `OperationLogger` を DI 依存に追加**

既存コンストラクタ (行 44) を以下のように変更：

```csharp
private readonly BackupService _backupService;
private readonly ISettingsRepository _settingsRepository;
private readonly INavigationService _navigationService;
private readonly OperationLogger _operationLogger;

public SystemManageViewModel(
    BackupService backupService,
    ISettingsRepository settingsRepository,
    INavigationService navigationService,
    OperationLogger operationLogger)
{
    _backupService = backupService;
    _settingsRepository = settingsRepository;
    _navigationService = navigationService;
    _operationLogger = operationLogger;
}
```

- [ ] **Step 2: `CreateBackupAsync` 成功時のログ記録を追加**

`CreateBackupAsync` (行 94-138) の `LastBackupFile = dialog.FileName;` 代入直後に追加：

```csharp
// Issue #1302: 監査ログ記録
await _operationLogger.LogBackupAsync(dialog.FileName);
```

- [ ] **Step 3: `RestoreAsync` 成功時のログ記録を追加**

`RestoreAsync` (行 144-235) の `_backupService.RestoreFromBackup(...)` が `true` を返した分岐で、リストア完了メッセージを出す直前に追加：

```csharp
// Issue #1302: 監査ログ記録 (注意: リストア後は OperationLogger の書き込み先が
// 新しい DB になっている可能性があるため、リストア処理直後は意味ある監査証跡にならない
// 可能性がある。ただしリストア操作を開始した事実を記録する意図として、リストア後の
// 新DB 上に記録する実装を採用する。)
await _operationLogger.LogRestoreAsync(SelectedBackup.FilePath);
```

- [ ] **Step 4: `RestoreFromFileAsync` 成功時のログ記録を追加**

`RestoreFromFileAsync` (行 267-366) の同じ成功分岐に追加：

```csharp
// Issue #1302: 監査ログ記録
await _operationLogger.LogRestoreAsync(dialog.FileName);
```

- [ ] **Step 5: ビルドを通す**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/src/ICCardManager/ICCardManager.csproj`
Expected: エラーなし

- [ ] **Step 6: 既存テスト全体を走らせてリグレッションがないことを確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests`
Expected: 全て PASS

- [ ] **Step 7: コミット**

```bash
git add ICCardManager/src/ICCardManager/ViewModels/DataExportImportViewModel.cs ICCardManager/src/ICCardManager/ViewModels/SystemManageViewModel.cs
git commit -m "$(cat <<'EOF'
feat: VM から Import/Export/Backup/Restore の監査ログを記録 (Issue #1302)

- DataExportImportViewModel: ExportAsync/ExecuteImportAsync 成功時に OperationLogger を呼び出し
- SystemManageViewModel: CreateBackupAsync/RestoreAsync/RestoreFromFileAsync 成功時に呼び出し
- DI 構成は追加パラメータで解決 (AddTransient/AddSingleton は変更不要)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: CHANGELOG 更新

**Files:**
- Modify: `ICCardManager/CHANGELOG.md`

- [ ] **Step 1: Unreleased セクションに追記**

CHANGELOG.md の `## [Unreleased]` 直下（または該当セクションがなければ作成）に以下を追加：

```markdown
### Added
- **監査ログ機能の拡充 (Issue #1302)**: CSV インポート/エクスポート、バックアップ取得、DB リストアの操作を `operation_log` テーブルに記録。Action 定数 `IMPORT`/`EXPORT`/`BACKUP` を追加。操作者は `ICurrentOperatorContext` から解決 (PR #1291 と同じなりすまし防止ポリシー)。データ持ち出し・履歴改変の事後追跡が可能に。
```

- [ ] **Step 2: Git add（コミットは Task 11 でまとめて）**

```bash
git add ICCardManager/CHANGELOG.md
```

---

## Task 10: 設計書の更新

**Files:**
- Modify: `docs/design/05_クラス設計書.md`
- Modify: `docs/design/07_テスト設計書.md`

- [ ] **Step 1: 05 クラス設計書を確認し、OperationLogger セクションに新 API を追記**

既存の OperationLogger のメソッド一覧表がある箇所を探し (grep で `LogStaffInsertAsync` を検索)、同じ表に追記：

```markdown
| LogImportAsync | CSV インポート実行ログ | tableName, filePath, insertedCount, skippedCount, errorCount |
| LogExportAsync | CSV/Excel エクスポート実行ログ | tableName, filePath, recordCount |
| LogBackupAsync | バックアップ取得ログ | filePath |
| LogRestoreAsync | DB リストア実行ログ | filePath |
```

また、Action 定数・Tables 定数の一覧にも IMPORT/EXPORT/BACKUP と Database/LedgerDetail を追記。

- [ ] **Step 2: 07 テスト設計書に新規テストケースを追記**

OperationLoggerTests に相当するセクションに新規テストケース表を追加：

```markdown
### Issue #1302 監査ログ追加機能テスト

| テストID | テスト名 | 概要 |
|---|---|---|
| OL-1302-01 | LogImportAsync_WithCsvFile_RecordsImportAction | Import 操作を正しい Action/TargetTable/TargetId で記録すること |
| OL-1302-02 | LogImportAsync_WithoutSession_FallsBackToGuiOperator | セッション無効時に GuiOperator にフォールバックすること |
| OL-1302-03 | LogExportAsync_WithCsvFile_RecordsExportAction | Export 操作を正しく記録すること |
| OL-1302-04 | LogExportAsync_LedgerDetailTable_RecordsCorrectly | LedgerDetail テーブル名が正しく設定されること |
| OL-1302-05 | LogBackupAsync_RecordsBackupActionWithDatabaseTarget | Backup を TargetTable=database で記録すること |
| OL-1302-06 | LogRestoreAsync_RecordsRestoreActionWithDatabaseTarget | DB リストアを TargetTable=database で記録すること |
| OL-1302-07 | LogRestoreAsync_DistinctFromStaffRestore_ByTargetTable | 既存の職員復元とは TargetTable で区別可能であること |
```

- [ ] **Step 3: Git add**

```bash
git add docs/design/05_クラス設計書.md docs/design/07_テスト設計書.md
```

---

## Task 11: ドキュメント類をまとめてコミット

- [ ] **Step 1: コミット**

```bash
git commit -m "$(cat <<'EOF'
docs: Issue #1302 の CHANGELOG と設計書同期更新

- CHANGELOG.md: Unreleased に監査ログ拡充の追加項目
- 05_クラス設計書.md: OperationLogger の新 API を追記
- 07_テスト設計書.md: OL-1302-01〜07 のテストケース追記

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 12: 最終確認とプルリクエスト作成

- [ ] **Step 1: 最終ビルド & 全テスト**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build
"/mnt/c/Program Files/dotnet/dotnet.exe" test
```
Expected: 両方ともエラー/失敗なし

- [ ] **Step 2: ブランチを push**

```bash
git push -u origin feat/issue-1302-audit-log-bulk-operations
```

- [ ] **Step 3: PR 作成**

```bash
gh pr create --title "feat: CSVインポート/エクスポート/バックアップ/リストアの監査ログ記録 (Issue #1302)" --body "$(cat <<'EOF'
## Summary
- Issue #1302: データ持ち出し/履歴改変を監査証跡に残すための OperationLogger API 拡張
- `Actions.Import/Export/Backup` 定数と `Tables.Database/LedgerDetail` を追加、4 つの新 API (`LogImportAsync`/`LogExportAsync`/`LogBackupAsync`/`LogRestoreAsync`) を実装
- 操作者は `ICurrentOperatorContext` (PR #1291) から解決し、セッション失効時は `GuiOperator` にフォールバック

## 仕様
- 既存の `LogStaffRestoreAsync` は `TargetTable="staff"/"ic_card"` でレコード復元用途、新規 `LogRestoreAsync(string filePath)` は `TargetTable="database"` で DB リストア用途として区別
- `TargetId` にはファイル名 (basename) のみを格納、フルパスと件数は `AfterData` の JSON に含める

## Test plan
- [x] OperationLogger 新 API の単体テスト (7 ケース、OL-1302-01〜07)
- [x] 既存 OperationLoggerTests リグレッションなし
- [ ] **手動 UI テスト (VM 層は SaveFileDialog を直接 new しており単体テスト不可)**:
  - [ ] データ管理画面でカードをエクスポート → `operation_log` に EXPORT 行が記録される
  - [ ] データ管理画面で職員をインポート → `operation_log` に IMPORT 行が記録される (InsertedCount/SkippedCount/ErrorCount が入る)
  - [ ] システム管理画面で手動バックアップ → `operation_log` に BACKUP 行が記録される
  - [ ] システム管理画面でリストア実行 → リストア後の新 DB の `operation_log` に RESTORE 行が記録される (TargetTable=database)
  - [ ] 職員証タッチ直後に各操作 → `OperatorIdm`/`OperatorName` が該当職員で記録される
  - [ ] 職員証タッチなし (5分経過後) に各操作 → `OperatorIdm=0000000000000000`, `OperatorName="GUI操作"` で記録される

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 4: PR URL をユーザーに報告**

PR URL が表示されたらユーザーに伝える。

---

## Self-Review チェックリスト

1. **Spec coverage**: Issue #1302 のチェックリスト項目を全てタスク化したか確認
   - [x] Actions 定数追加 (Task 2)
   - [x] OperationLogger の4 API 実装 (Task 3-6)
   - [x] VM からの呼び出し追加 (Task 7-8)
   - [x] 単体テスト (Task 3-6 内で TDD)
   - [x] 設計書同期更新 (Task 10)
   - [x] CHANGELOG 記載 (Task 9)

2. **Placeholder scan**: "TBD"/"TODO"/"similar to" なし ✓

3. **Type consistency**: 
   - `Actions.Import`/`Export`/`Backup` は Task 2 で定義、Task 3-5 で使用、一致 ✓
   - `Tables.Database`/`LedgerDetail` は Task 2 で定義、Task 5/6/7 で使用、一致 ✓
   - `LogImportAsync(tableName, filePath, inserted, skipped, error)` は Task 3 で定義、Task 7 で呼び出し、シグネチャ一致 ✓
   - `LogExportAsync(tableName, filePath, recordCount)` は Task 4 で定義、Task 7 で呼び出し、シグネチャ一致 ✓
