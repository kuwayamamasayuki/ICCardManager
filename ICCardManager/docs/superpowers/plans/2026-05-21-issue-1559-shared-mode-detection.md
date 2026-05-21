# Issue #1559: 共有モード判定ロジック修正 + デフォルト復元UI — 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ローカルフルパス指定で誤って共有モード化される不具合を修正し、UNC または マップドネットワークドライブのときのみ共有モード扱いとする。加えて設定画面に「デフォルトに戻す」ボタンを追加。

**Architecture:**
- `DbContext` に `IsNetworkDrive(path)` を追加し、`IsSharedMode = IsUncPath(...) || IsNetworkDrive(...)` に変更
- `SettingsViewModel` に `IDialogService` を DI 経由で注入し、`ResetDatabasePathToDefaultCommand` を追加
- `SettingsDialog.xaml` に「デフォルトに戻す」ボタンを追加

**Tech Stack:** C# 10 / .NET Framework 4.8 / WPF / MVVM Toolkit / xUnit / FluentAssertions / Moq

---

## ファイル構成

**Modify:**
- `ICCardManager/src/ICCardManager/Data/DbContext.cs` — IsSharedMode 判定ロジック、IsNetworkDrive 追加
- `ICCardManager/src/ICCardManager/ViewModels/SettingsViewModel.cs` — IDialogService 注入、ResetDatabasePathToDefaultCommand 追加
- `ICCardManager/src/ICCardManager/Views/Dialogs/SettingsDialog.xaml` — 「デフォルトに戻す」ボタン追加
- `.claude/rules/business-logic.md` — 共有フォルダモード記述
- `.claude/rules/development-conventions.md` — 共有フォルダモード記述
- `ICCardManager/CHANGELOG.md` — Unreleased セクションに Fixed 項目

**Create:**
- `ICCardManager/tests/ICCardManager.Tests/Data/DbContextSharedModeDetectionTests.cs` — 判定ロジックの単体テスト
- 既存 `ICCardManager/tests/ICCardManager.Tests/ViewModels/SettingsViewModelTests.cs`（あれば追記、なければ新規作成） — ResetDatabasePathToDefault のテスト

---

### Task 1: DbContext.IsSharedMode を UNC + マップドドライブ判定に変更

**Files:**
- Test: `ICCardManager/tests/ICCardManager.Tests/Data/DbContextSharedModeDetectionTests.cs`
- Modify: `ICCardManager/src/ICCardManager/Data/DbContext.cs`

- [ ] **Step 1: 失敗テストを書く**

`ICCardManager/tests/ICCardManager.Tests/Data/DbContextSharedModeDetectionTests.cs` を新規作成:

```csharp
using System;
using System.IO;
using FluentAssertions;
using ICCardManager.Data;
using Xunit;

namespace ICCardManager.Tests.Data
{
    public class DbContextSharedModeDetectionTests : IDisposable
    {
        private readonly string _tempDir;

        public DbContextSharedModeDetectionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { /* best-effort */ }
        }

        [Fact]
        public void IsSharedMode_DefaultPath_IsFalse()
        {
            using var ctx = new DbContext(databasePath: null);
            ctx.IsSharedMode.Should().BeFalse("デフォルト（null）パスはローカルモード扱い");
        }

        [Fact]
        public void IsSharedMode_LocalAbsolutePath_IsFalse()
        {
            var localPath = Path.Combine(_tempDir, "iccard.db");
            using var ctx = new DbContext(databasePath: localPath);
            ctx.IsSharedMode.Should().BeFalse("ローカルフォルダのフルパス指定は共有モード扱いにしない");
        }

        [Fact]
        public void IsSharedMode_UncPath_IsTrue()
        {
            using var ctx = new DbContext(databasePath: @"\\server\share\iccard.db");
            ctx.IsSharedMode.Should().BeTrue("UNCパス指定時は共有モード");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not a valid path::::")]
        public void IsNetworkDrive_InvalidPath_ReturnsFalse(string path)
        {
            DbContext.IsNetworkDrive(path).Should().BeFalse();
        }

        [Fact]
        public void IsNetworkDrive_LocalPath_ReturnsFalse()
        {
            var localPath = Path.Combine(_tempDir, "iccard.db");
            DbContext.IsNetworkDrive(localPath).Should().BeFalse();
        }
    }
}
```

- [ ] **Step 2: テストを実行して失敗を確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~DbContextSharedModeDetectionTests" --no-restore
```

期待: `IsSharedMode_LocalAbsolutePath_IsFalse` および `IsNetworkDrive_*` が `does not contain a definition for` または `assertion failure` で失敗

- [ ] **Step 3: 実装**

`ICCardManager/src/ICCardManager/Data/DbContext.cs` を編集:

(a) コメントを更新（148行目付近）:

```csharp
/// <summary>
/// 共有モード（UNCパス または マップドネットワークドライブ指定時）かどうか
/// </summary>
/// <remarks>
/// UNCパス（\\server\share）と、ドライブレター形式（Z:\share）でも
/// DriveInfo.DriveType == DriveType.Network のマップドネットワークドライブを
/// 共有モード扱いとする。ローカルフルパスは共有モードにしない（Issue #1559）。
/// </remarks>
public bool IsSharedMode { get; }
```

(b) コンストラクタの初期化を変更（222行目付近）:

```csharp
// Issue #1559: UNCパス または マップドネットワークドライブ指定時のみ共有モード
IsSharedMode = databasePath != null &&
               (IsUncPath(databasePath) || IsNetworkDrive(databasePath));
```

(c) `IsNetworkDrive` を新設（`IsUncPath` の直後）:

```csharp
/// <summary>
/// マップドネットワークドライブ（例: Z:\share）かどうかを判定
/// </summary>
/// <remarks>
/// Path.GetPathRoot でルートを取得し、DriveInfo.DriveType が Network かを判定する。
/// パスが不正・null・未マウントドライブ等で例外発生時は false（ローカル扱い）にフォールバック。
/// Issue #1559。
/// </remarks>
internal static bool IsNetworkDrive(string path)
{
    if (string.IsNullOrWhiteSpace(path))
        return false;

    try
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return false;

        // UNCはここで除外（IsUncPathで判定済み想定だが、念のため）
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
            return false;

        var drive = new DriveInfo(root);
        return drive.DriveType == DriveType.Network;
    }
    catch
    {
        return false;
    }
}
```

`using System.IO;` が既存していることを確認（`DriveInfo` のため）。

- [ ] **Step 4: テストを再実行**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~DbContextSharedModeDetectionTests" --no-restore
```

期待: 全件 PASS

- [ ] **Step 5: フル回帰テスト（DbContext関連）**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~DbContext" --no-restore
```

期待: 既存テストも全件 PASS（IsSharedMode の挙動変更による影響の確認）

- [ ] **Step 6: コミット**

```bash
git add ICCardManager/src/ICCardManager/Data/DbContext.cs \
        ICCardManager/tests/ICCardManager.Tests/Data/DbContextSharedModeDetectionTests.cs
git commit -m "fix: 共有モード判定をUNC/マップドドライブ限定に修正 (Issue #1559)"
```

---

### Task 2: SettingsViewModel に IDialogService を注入し ResetDatabasePathToDefault コマンドを追加

**Files:**
- Modify: `ICCardManager/src/ICCardManager/ViewModels/SettingsViewModel.cs`
- Modify: `ICCardManager/src/ICCardManager/App.xaml.cs`（DI登録の確認のみ）
- Test: `ICCardManager/tests/ICCardManager.Tests/ViewModels/SettingsViewModelDatabasePathTests.cs`

- [ ] **Step 1: 失敗テストを書く**

`ICCardManager/tests/ICCardManager.Tests/ViewModels/SettingsViewModelDatabasePathTests.cs` を新規作成:

```csharp
using System.IO;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels
{
    public class SettingsViewModelDatabasePathTests
    {
        private static SettingsViewModel CreateVm(
            Mock<IDialogService> dialogMock,
            Mock<ISettingsRepository> repoMock = null,
            Mock<IValidationService> validatorMock = null,
            Mock<ISoundPlayer> soundMock = null)
        {
            repoMock ??= new Mock<ISettingsRepository>();
            validatorMock ??= new Mock<IValidationService>();
            soundMock ??= new Mock<ISoundPlayer>();
            var options = Options.Create(new DatabaseOptions());
            return new SettingsViewModel(
                repoMock.Object,
                validatorMock.Object,
                soundMock.Object,
                options,
                dialogMock.Object);
        }

        [Fact]
        public void ResetDatabasePathToDefault_UserConfirms_DeletesConfigAndClearsPath()
        {
            // 事前準備: 一時的に config ファイルを作る
            var configPath = SettingsViewModel.GetDatabaseConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, @"C:\some\local\db\iccard.db");

            try
            {
                var dialogMock = new Mock<IDialogService>();
                dialogMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

                var vm = CreateVm(dialogMock);

                vm.ResetDatabasePathToDefaultCommand.Execute(null);

                File.Exists(configPath).Should().BeFalse("確認後に config ファイルが削除される");
                vm.DatabasePath.Should().BeEmpty("UI上のパスは空欄に");
                vm.IsDatabasePathChanged.Should().BeTrue("再起動案内を表示するため変更フラグを立てる");
            }
            finally
            {
                if (File.Exists(configPath)) File.Delete(configPath);
            }
        }

        [Fact]
        public void ResetDatabasePathToDefault_UserCancels_KeepsConfig()
        {
            var configPath = SettingsViewModel.GetDatabaseConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, @"C:\some\local\db\iccard.db");

            try
            {
                var dialogMock = new Mock<IDialogService>();
                dialogMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

                var vm = CreateVm(dialogMock);
                vm.ResetDatabasePathToDefaultCommand.Execute(null);

                File.Exists(configPath).Should().BeTrue("ユーザーがキャンセルした場合は削除しない");
            }
            finally
            {
                if (File.Exists(configPath)) File.Delete(configPath);
            }
        }
    }
}
```

- [ ] **Step 2: テストを実行して失敗を確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelDatabasePathTests" --no-restore
```

期待: コンストラクタ引数不一致または ResetDatabasePathToDefaultCommand 未定義で失敗

- [ ] **Step 3: SettingsViewModel を編集**

(a) コンストラクタに `IDialogService` を追加:

```csharp
private readonly IDialogService _dialogService;

public SettingsViewModel(
    ISettingsRepository settingsRepository,
    IValidationService validationService,
    ISoundPlayer soundPlayer,
    IOptions<DatabaseOptions> databaseOptions,
    IDialogService dialogService)
{
    _settingsRepository = settingsRepository;
    _validationService = validationService;
    _soundPlayer = soundPlayer;
    _dialogService = dialogService;
    var fullPath = LoadDatabasePathFromConfigFile();
    _originalDatabasePath = ExtractDirectoryPath(fullPath);
    _databasePath = _originalDatabasePath;
}
```

`using ICCardManager.Services;` が無ければ追加。

(b) `ResetDatabasePathToDefaultCommand` を追加（`SaveAsync` の後あたり）:

```csharp
/// <summary>
/// データベース保存先をデフォルト（ローカル）に戻す（Issue #1559）。
/// database_config.txt を削除し、UI上のパスを空欄にする。
/// 反映には再起動が必要。
/// </summary>
[RelayCommand]
public void ResetDatabasePathToDefault()
{
    const string Title = "データベース保存先をデフォルトに戻す";
    var message =
        "データベース保存先をローカルのデフォルト（C:\\ProgramData\\ICCardManager\\iccard.db）に戻します。\n" +
        "現在の設定ファイル（database_config.txt）は削除されます。\n\n" +
        "変更を反映するにはアプリケーションの再起動が必要です。続行しますか？";

    if (!_dialogService.ShowConfirmation(message, Title))
    {
        return;
    }

    try
    {
        var configPath = GetDatabaseConfigPath();
        if (File.Exists(configPath))
        {
            File.Delete(configPath);
        }

        DatabasePath = string.Empty;
        _originalDatabasePath = string.Empty;
        IsDatabasePathChanged = true;
        SetStatus(
            "データベース保存先をデフォルトに戻しました。変更を反映するにはアプリケーションを再起動してください。",
            false);
    }
    catch (Exception ex)
    {
        SetStatus($"設定ファイルの削除に失敗しました: {ex.Message}", true);
    }
}
```

`using System.IO;` および `using System;` が既存していることを確認。

- [ ] **Step 4: テストを再実行**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelDatabasePathTests" --no-restore
```

期待: 全件 PASS

- [ ] **Step 5: フル回帰テスト（SettingsViewModel関連）**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~SettingsViewModel" --no-restore
```

期待: 既存テスト全件 PASS。コンストラクタ変更による失敗があれば既存テスト側を IDialogService モックを足す形で修正。

- [ ] **Step 6: App.xaml.cs のDI登録確認**

`SettingsViewModel` が DI 解決される箇所で `IDialogService` も解決可能なことを確認:

```bash
grep -n "AddSingleton<SettingsViewModel\|AddTransient<SettingsViewModel\|IDialogService" ICCardManager/src/ICCardManager/App.xaml.cs
```

すでに `IDialogService` / `DialogService` が登録されていれば追加作業不要。未登録なら以下を追加:

```csharp
services.AddSingleton<IDialogService, DialogService>();
```

- [ ] **Step 7: コミット**

```bash
git add ICCardManager/src/ICCardManager/ViewModels/SettingsViewModel.cs \
        ICCardManager/tests/ICCardManager.Tests/ViewModels/SettingsViewModelDatabasePathTests.cs
# App.xaml.cs を変更した場合のみ
git add ICCardManager/src/ICCardManager/App.xaml.cs 2>/dev/null || true
git commit -m "feat: 設定画面に「デフォルトに戻す」コマンドを追加 (Issue #1559)"
```

---

### Task 3: SettingsDialog.xaml に「デフォルトに戻す」ボタン追加

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Views/Dialogs/SettingsDialog.xaml`

- [ ] **Step 1: XAML を編集**

「データベース保存先」 GroupBox 内の `<Grid>...</Grid>`（参照ボタンを含む）の直後に「デフォルトに戻す」ボタンを追加。

参照ボタンの ColumnDefinitions を 3 列に拡張する案もあるが、視認性のため別 Grid 行にせず、参照ボタンの後ろの新規 StackPanel に並べる:

`<Grid>...</Grid>` の直後（273行目の TextBlock の直前）に挿入:

```xml
<StackPanel Orientation="Horizontal"
            HorizontalAlignment="Right"
            Margin="0,5,0,0">
    <Button Content="デフォルトに戻す(_R)"
            Command="{Binding ResetDatabasePathToDefaultCommand}"
            Padding="15,6"
            AutomationProperties.Name="データベース保存先をデフォルトに戻す"
            ToolTip="database_config.txt を削除して、ローカルデフォルトパス（C:\ProgramData\ICCardManager\iccard.db）に戻します（アクセスキー: Alt+R）"/>
</StackPanel>
```

- [ ] **Step 2: ビルドエラーがないことを確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/src/ICCardManager/ICCardManager.csproj --no-restore
```

期待: ビルド成功（XAML パースエラーがないこと、Binding 名が正しいこと）

- [ ] **Step 3: コミット**

```bash
git add ICCardManager/src/ICCardManager/Views/Dialogs/SettingsDialog.xaml
git commit -m "feat: 設定画面に「デフォルトに戻す」ボタンを追加 (Issue #1559)"
```

---

### Task 4: ドキュメント更新

**Files:**
- Modify: `.claude/rules/business-logic.md`
- Modify: `.claude/rules/development-conventions.md`
- Modify: `ICCardManager/CHANGELOG.md`

- [ ] **Step 1: business-logic.md を更新**

「共有フォルダモード（複数PC共有DB）」セクションの第2項目「UNCパス（`\\server\share\iccard.db`）指定時に自動的に共有モードとして動作」を以下に置換:

```
- 共有モードの判定は UNCパス（`\\server\share\iccard.db`）または **マップドネットワークドライブ**（`Z:\share\iccard.db` 等で `DriveInfo.DriveType == DriveType.Network`）指定時に自動的に有効化される（Issue #1559）。ローカルフルパス指定（例: `C:\Users\foo\db\iccard.db`）では共有モードにならない
```

- [ ] **Step 2: development-conventions.md を更新**

「環境制約」セクションの最後の項目「共有フォルダモード」の説明を以下に修正:

```
- **共有フォルダモード**: SMB共有フォルダ上にDBを配置し、複数PC（最大約20台）で共有可能。UNCパスまたはマップドネットワークドライブ指定時に自動判定（Issue #1559）
```

- [ ] **Step 3: CHANGELOG.md を更新**

`ICCardManager/CHANGELOG.md` の `### Unreleased` セクション（無ければ作成）に以下を追加:

```markdown
### Fixed
- 共有モード判定がローカルフルパス指定時にも誤って有効化されていた不具合を修正。UNCパスまたはマップドネットワークドライブ指定時のみ共有モード扱いとなるよう変更 (Issue #1559)

### Added
- 設定画面の「データベース保存先」に「デフォルトに戻す」ボタンを追加。`database_config.txt` を削除してローカルデフォルトパスに戻せる (Issue #1559)
```

- [ ] **Step 4: コミット**

```bash
git add .claude/rules/business-logic.md \
        .claude/rules/development-conventions.md \
        ICCardManager/CHANGELOG.md
git commit -m "docs: 共有モード判定仕様の修正をルール・CHANGELOGに反映 (Issue #1559)"
```

---

### Task 5: フルテスト + PR作成

**Files:** なし

- [ ] **Step 1: 設計書をコミット**

```bash
git add ICCardManager/docs/superpowers/specs/2026-05-21-issue-1559-shared-mode-detection-design.md \
        ICCardManager/docs/superpowers/plans/2026-05-21-issue-1559-shared-mode-detection.md
git commit -m "docs: Issue #1559 設計書・実装計画を追加"
```

- [ ] **Step 2: フルビルド（警告ゼロ確認）**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --no-restore
```

期待: ビルド成功 + 警告 0 件（CLAUDE.md ルール「ビルド警告ゼロ維持」）。
警告が出た場合は原因対処してから先へ。

- [ ] **Step 3: フルテスト（Release 構成）**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --configuration Release --no-restore
```

期待: 全件 PASS（CI test-count-sync の整合性のため Release 構成）

- [ ] **Step 4: ブランチを push**

```bash
git push -u origin fix/issue-1559-shared-mode-detection
```

- [ ] **Step 5: PR を作成**

`gh pr create` で以下の内容で PR 作成（Projects classic 警告で exit 1 する場合は `gh api -X PATCH .../pulls/N -F body=@file.md` で本文を後追い反映する。`feedback_gh_pr_edit_graphql_workaround` 参照）。

```bash
gh pr create --base main --title "fix: 共有モード判定をUNC/マップドドライブ限定に修正 (Issue #1559)" --body "$(cat <<'EOF'
## Summary
- 共有モード判定を `IsUncPath || IsNetworkDrive` に変更（ローカルフルパスで誤って共有モード化される不具合を修正）
- 設定画面の「データベース保存先」に「デフォルトに戻す」ボタンを追加。`database_config.txt` を削除して復旧可能に
- ルール（`business-logic.md` / `development-conventions.md`）と `CHANGELOG.md` を仕様に合わせて更新

Closes #1559

## Test plan
- [x] `DbContextSharedModeDetectionTests`: デフォルト/ローカル/UNC/不正パスでの判定が想定通り
- [x] `SettingsViewModelDatabasePathTests`: 「デフォルトに戻す」コマンドの確認/キャンセル動作
- [x] フルテスト（Release 構成）グリーン
- [ ] **手動確認（要実機）**: マップドドライブ（`Z:\share\iccard.db` 等）指定時に共有モードが有効化されることをステータスバー「共有モード」表示で確認
- [ ] **手動確認**: 設定画面で「デフォルトに戻す」ボタン押下→確認ダイアログ→OKで `C:\ProgramData\ICCardManager\database_config.txt` が消えること、再起動でローカルモードに戻ること

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 6: PR URL を確認してユーザーに伝える**

---

## 自己レビューチェック

- spec の §1〜§5 と Task 1〜4 の対応: ✓
- プレースホルダ無し: ✓
- 型・メソッド名整合性: `IsNetworkDrive`、`ResetDatabasePathToDefaultCommand`、`GetDatabaseConfigPath` は spec / 実装で一貫
- マップドドライブの true ケースは実機検証が必要な旨を PR の Test plan に明示済み
