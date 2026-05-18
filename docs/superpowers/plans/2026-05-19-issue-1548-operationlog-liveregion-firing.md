# Issue #1548 OperationLogDialog LiveRegion 発火対応 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `OperationLogDialog` の 4 つの動的 TextBlock がテキスト変化時に `LiveRegionChanged` を発火し、スクリーンリーダーで読み上げられるようにする（Issue #1548）。

**Architecture:** `OperationLogDialog.xaml.cs` のコードビハインドで `DataContext`（= `OperationLogSearchViewModel`）の `PropertyChanged` を購読する。プロパティ名 → 対象 TextBlock 名のマッピングは純粋関数 `GetTargetElementName(propertyName, isBusy)` に抽出して単体テストする。WPF 標準 API のみで実装し、依存追加なし。

**Tech Stack:** WPF (.NET Framework 4.8) / xUnit + FluentAssertions / `System.Windows.Automation.Peers.UIElementAutomationPeer` / `AutomationEvents.LiveRegionChanged`

**Branch:** `fix/issue-1548-operationlog-liveregion-firing`（既に作成済み、設計書もコミット済み）

---

## File Structure

| ファイル | 操作 | 責務 |
|---|---|---|
| `ICCardManager/src/ICCardManager/Views/Dialogs/OperationLogDialog.xaml.cs` | Modify | コードビハインドに `OnViewModelPropertyChanged` ハンドラと購読/解除ロジックを追加。プロパティ名 → 対象 TextBlock の対応は `internal static GetTargetElementName` に分離 |
| `ICCardManager/src/ICCardManager/ViewModels/OperationLogSearchViewModel.cs` | Modify (条件付き) | `PageInfo` 計算プロパティの `PropertyChanged` が発火していなければ、依存元プロパティ変化時に `OnPropertyChanged(nameof(PageInfo))` を追加 |
| `ICCardManager/tests/ICCardManager.Tests/Views/Dialogs/DialogAutomationPropertiesCoverageTests.cs` | Modify | 静的解析テスト 2 件を追加（`RaiseAutomationEvent` の存在 / `PropertyChanged` 購読パターンの存在） |
| `ICCardManager/tests/ICCardManager.Tests/Views/Dialogs/OperationLogDialogLiveRegionTests.cs` | Create | `GetTargetElementName` の単体テスト 6 件 + `PageInfo` 発火連鎖のテスト（VM 側） |
| `ICCardManager/CHANGELOG.md` | Modify | Unreleased セクションに本修正を追記 |

---

## Task 0: 環境準備とコード現状確認

**Files:**
- Read-only: `ICCardManager/src/ICCardManager/Views/Dialogs/OperationLogDialog.xaml`
- Read-only: `ICCardManager/src/ICCardManager/Views/Dialogs/OperationLogDialog.xaml.cs`
- Read-only: `ICCardManager/src/ICCardManager/ViewModels/OperationLogSearchViewModel.cs`

- [ ] **Step 1: 現在のブランチが `fix/issue-1548-operationlog-liveregion-firing` であることを確認**

Run:
```bash
git branch --show-current
```
Expected: `fix/issue-1548-operationlog-liveregion-firing`

異なる場合: `git checkout fix/issue-1548-operationlog-liveregion-firing`

- [ ] **Step 2: ベースラインのビルドとテスト Pass 確認**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```
Expected: `0 Error`、警告ゼロ

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --nologo --verbosity quiet 2>&1 | tail -3
```
Expected: `Passed: N, Failed: 0, Skipped: 0`（実数を記録、Task 6 で比較）

- [ ] **Step 3: `OperationLogDialog.xaml.cs` を全行読む**

`OperationLogDialog.xaml.cs` を Read で読み、現在の構造を把握:
- using ディレクティブ
- コンストラクタ
- `Loaded` ハンドラ（PR #1530 で追加されたもの）
- 既存のフィールド

`OperationLogDialog.xaml` も該当箇所（4 つの動的 TextBlock の `x:Name`）を確認する。

- [ ] **Step 4: `OperationLogSearchViewModel.cs` の `PageInfo` / `IsBusy` / `BusyMessage` / `StatusMessage` / `CurrentPage` / `TotalPages` 周辺を読む**

特に重要:
- `PageInfo` が `=> $"..."` のような計算プロパティの場合、依存元（`TotalCount` / `CurrentPage` / `PageSize`）の `[ObservableProperty]` から自動生成される `partial void OnXxxChanged` で `OnPropertyChanged(nameof(PageInfo))` が呼ばれているか確認
- 呼ばれていない場合は Task 2 で修正対象になる

---

## Task 1: 純粋関数 `GetTargetElementName` の単体テストを書く（Red）

**Files:**
- Create: `ICCardManager/tests/ICCardManager.Tests/Views/Dialogs/OperationLogDialogLiveRegionTests.cs`

- [ ] **Step 1: テストファイルを新規作成**

Write `/mnt/d/OneDrive/交通系/src/ICCardManager/tests/ICCardManager.Tests/Views/Dialogs/OperationLogDialogLiveRegionTests.cs`:

```csharp
using FluentAssertions;
using ICCardManager.Views.Dialogs;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// Issue #1548: <see cref="OperationLogDialog"/> の LiveRegion 発火対応のテスト。
/// プロパティ名 → 対象 TextBlock 名 のマッピング純粋関数を検証する。
/// 実際の RaiseAutomationEvent 発火は WPF UI スレッドが必要なため、
/// スクリーンリーダー実機読み上げ確認はユーザー手動で実施する（設計書 §5.4 参照）。
/// </summary>
public class OperationLogDialogLiveRegionTests
{
    [Theory]
    [InlineData("PageInfo", false, "PageInfoText")]
    [InlineData("CurrentPage", false, "CurrentPageNumberText")]
    [InlineData("TotalPages", false, "CurrentPageNumberText")]
    [InlineData("StatusMessage", false, "StatusMessageText")]
    [InlineData("BusyMessage", false, "ProcessingOverlayText")]
    public void GetTargetElementName_対象プロパティ変化時に_対応するTextBlock名を返すこと(
        string propertyName, bool isBusy, string expectedTargetName)
    {
        var result = OperationLogDialog.GetTargetElementName(propertyName, isBusy);
        result.Should().Be(expectedTargetName);
    }

    [Fact]
    public void GetTargetElementName_IsBusyがtrueへ変化時_ProcessingOverlayTextを返すこと()
    {
        var result = OperationLogDialog.GetTargetElementName("IsBusy", isBusy: true);
        result.Should().Be("ProcessingOverlayText");
    }

    [Fact]
    public void GetTargetElementName_IsBusyがfalseへ変化時_nullを返すこと()
    {
        // IsBusy=false への遷移はオーバーレイ非表示なので通知不要
        var result = OperationLogDialog.GetTargetElementName("IsBusy", isBusy: false);
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("UnknownProperty")]
    [InlineData("")]
    [InlineData(null)]
    public void GetTargetElementName_対象外プロパティの場合_nullを返すこと(string? propertyName)
    {
        var result = OperationLogDialog.GetTargetElementName(propertyName, isBusy: false);
        result.Should().BeNull();
    }
}
```

- [ ] **Step 2: テスト実行で失敗を確認（Red）**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -10
```
Expected: ビルド失敗。エラーは「`OperationLogDialog.GetTargetElementName` が定義されていない」相当（CS0117 or CS0103）

---

## Task 2: `PageInfo` の PropertyChanged 発火を確認/修正

**Files:**
- Modify (条件付き): `ICCardManager/src/ICCardManager/ViewModels/OperationLogSearchViewModel.cs`

- [ ] **Step 1: `PageInfo` の発火経路を確認**

`OperationLogSearchViewModel.cs` を Read で開き、`PageInfo` プロパティと、依存元プロパティ（典型的には `TotalCount`、`CurrentPage`、`PageSize`）の宣言を確認する。

判定:
- **`[ObservableProperty]` の `partial void On<X>Changed` で `OnPropertyChanged(nameof(PageInfo))` が呼ばれている** → Step 2 不要、Step 4 へ
- **呼ばれていない（手動で発火していない）** → Step 2 で追加

- [ ] **Step 2: 依存元プロパティに `OnPropertyChanged(nameof(PageInfo))` を追加（必要な場合のみ）**

例（`TotalCount` / `CurrentPage` / `PageSize` の 3 つの partial メソッドに対して同じ修正を行う）:

```csharp
partial void OnTotalCountChanged(int value)
{
    OnPropertyChanged(nameof(PageInfo));
}

partial void OnCurrentPageChanged(int value)
{
    OnPropertyChanged(nameof(PageInfo));
    // 既存処理があれば併存
}

partial void OnPageSizeChanged(int value)
{
    OnPropertyChanged(nameof(PageInfo));
}
```

既存の partial メソッドがある場合はその中に `OnPropertyChanged(nameof(PageInfo));` の 1 行を追加。

- [ ] **Step 3: 修正分の単体テスト追加**

Step 2 で修正した場合のみ、`ICCardManager/tests/ICCardManager.Tests/ViewModels/OperationLogSearchViewModelTests.cs` に以下を追加（ファイル存在しない場合は新規作成）:

```csharp
[Fact]
public void PageInfo_TotalCount変化時に_PropertyChangedが発火すること()
{
    var vm = new OperationLogSearchViewModel(/* 必要な DI コンストラクタ引数 */);
    var fired = false;
    vm.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(OperationLogSearchViewModel.PageInfo)) fired = true;
    };

    vm.TotalCount = 100;

    fired.Should().BeTrue();
}
```

（DI 引数が複雑な場合は既存テストの Arrange パターンに合わせる）

`CurrentPage`・`PageSize` 変化時も同様にテストを追加（計 3 件）。

- [ ] **Step 4: Step 2 で修正した場合、ビルドとテストを実行**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```
Expected: `0 Error`（Task 1 で書いた `OperationLogDialogLiveRegionTests` のコンパイルエラーはまだ残る、それは Task 3 で解消）

`OperationLogDialog.xaml.cs` の修正がまだのため `OperationLogDialogLiveRegionTests` は build エラーになる。それは想定通り。

Task 2 の Step 2 修正分のみ単体テストを実行:

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --nologo --verbosity quiet --filter "FullyQualifiedName~OperationLogSearchViewModelTests" 2>&1 | tail -3
```
Expected: Task 1 のテストはコンパイルエラーで実行されないが、ViewModel テストは Pass

- [ ] **Step 5: Step 2 で修正した場合のみコミット**

Run:
```bash
git add ICCardManager/src/ICCardManager/ViewModels/OperationLogSearchViewModel.cs ICCardManager/tests/ICCardManager.Tests/ViewModels/OperationLogSearchViewModelTests.cs
git commit -m "$(cat <<'EOF'
fix: OperationLogSearchViewModel の PageInfo 計算プロパティで PropertyChanged を発火 (Issue #1548)

PageInfo は TotalCount / CurrentPage / PageSize に依存する計算プロパティだが、これら依存元の partial OnXxxChanged 内で OnPropertyChanged(nameof(PageInfo)) が呼ばれていなかったため、ViewModel バインドの PageInfoText が更新されてもスクリーンリーダーが変化を検知できなかった。本コミットは LiveRegion 発火対応（後続コミット）の前提として PageInfo の PropertyChanged 発火を保証する。

Refs #1548

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

Step 2 が不要だった場合、本タスクの Step 5 は実行しない（コミットなし）。

---

## Task 3: `OperationLogDialog.xaml.cs` を修正（Green）

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Views/Dialogs/OperationLogDialog.xaml.cs`

- [ ] **Step 1: 修正方針の確認**

Task 0 Step 3 で読んだ `OperationLogDialog.xaml.cs` の構造に合わせて、以下を追加する:
1. using ディレクティブに `System.ComponentModel`、`System.Windows`、`System.Windows.Automation`、`System.Windows.Automation.Peers`、`System.Windows.Controls` を追加（不足分のみ）
2. `_subscribedViewModel` フィールド追加
3. コンストラクタに `Closed += OnClosed;` を追加
4. 既存 `Loaded` ハンドラに購読開始処理を追加
5. `OnViewModelPropertyChanged` / `OnClosed` / `RaiseLiveRegionChanged` メソッド追加
6. `internal static GetTargetElementName` メソッド追加

- [ ] **Step 2: `OperationLogDialog.xaml.cs` を修正**

Edit でファイルを修正。既存の `using` セクション直後に必要な using を追加し、クラス内に以下のメンバーを追加する。**既存メソッド名や Loaded ハンドラ名は Task 0 Step 3 で確認した実際の名前に合わせる**こと。

クラスのメンバーとして追加するコード:

```csharp
private System.ComponentModel.INotifyPropertyChanged? _subscribedViewModel;

private void SubscribeToViewModel()
{
    if (DataContext is System.ComponentModel.INotifyPropertyChanged vm)
    {
        _subscribedViewModel = vm;
        vm.PropertyChanged += OnViewModelPropertyChanged;
    }
}

private void UnsubscribeFromViewModel()
{
    if (_subscribedViewModel is not null)
    {
        _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedViewModel = null;
    }
}

private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
{
    var isBusy = (sender as ICCardManager.ViewModels.OperationLogSearchViewModel)?.IsBusy ?? false;
    var targetName = GetTargetElementName(e.PropertyName, isBusy);
    System.Windows.UIElement? target = targetName switch
    {
        "PageInfoText" => PageInfoText,
        "CurrentPageNumberText" => CurrentPageNumberText,
        "StatusMessageText" => StatusMessageText,
        "ProcessingOverlayText" => ProcessingOverlayText,
        _ => null
    };
    if (target is not null)
    {
        RaiseLiveRegionChanged(target);
    }
}

/// <summary>
/// Issue #1548: <see cref="OperationLogSearchViewModel"/> のプロパティ変化に対して
/// LiveRegion 通知を発火すべき TextBlock の x:Name を返す純粋関数。
/// 単体テスト容易化のため <see cref="OperationLogDialog"/> 自体から分離した静的メソッド。
/// </summary>
/// <param name="propertyName">変化した ViewModel プロパティ名</param>
/// <param name="isBusy">変化通知時点での IsBusy 値（ProcessingOverlay の Visibility 判定に使用）</param>
/// <returns>通知対象 TextBlock の x:Name、対象外なら null</returns>
internal static string? GetTargetElementName(string? propertyName, bool isBusy)
{
    return propertyName switch
    {
        nameof(ICCardManager.ViewModels.OperationLogSearchViewModel.PageInfo) => "PageInfoText",
        nameof(ICCardManager.ViewModels.OperationLogSearchViewModel.CurrentPage) => "CurrentPageNumberText",
        nameof(ICCardManager.ViewModels.OperationLogSearchViewModel.TotalPages) => "CurrentPageNumberText",
        nameof(ICCardManager.ViewModels.OperationLogSearchViewModel.StatusMessage) => "StatusMessageText",
        nameof(ICCardManager.ViewModels.OperationLogSearchViewModel.BusyMessage) => "ProcessingOverlayText",
        nameof(ICCardManager.ViewModels.OperationLogSearchViewModel.IsBusy) => isBusy ? "ProcessingOverlayText" : null,
        _ => null
    };
}

private static void RaiseLiveRegionChanged(System.Windows.UIElement element)
{
    var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(element)
               ?? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(element);
    peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
}

private void OnClosed(object? sender, System.EventArgs e)
{
    UnsubscribeFromViewModel();
}
```

- [ ] **Step 3: コンストラクタと Loaded ハンドラに購読開始を追加**

既存のコンストラクタ末尾に追加:
```csharp
Closed += OnClosed;
```

既存の Loaded ハンドラ末尾に追加:
```csharp
SubscribeToViewModel();
```

注意: Loaded ハンドラ名は Task 0 Step 3 で確認した実名に合わせる（例: `OnLoaded`、`Dialog_Loaded` 等）。

- [ ] **Step 4: ビルド成功確認**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```
Expected: `0 Error`、警告ゼロ

警告が出た場合（例: `partial void` の引数未使用 `IDE0060` 等）は同コミット内で修正。

- [ ] **Step 5: Task 1 のテストを実行（Green 確認）**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --nologo --verbosity quiet --filter "FullyQualifiedName~OperationLogDialogLiveRegionTests" 2>&1 | tail -3
```
Expected: `Passed: 9, Failed: 0, Skipped: 0`（Theory 5 件 + IsBusy true/false 2 件 + 対象外プロパティ Theory 3 件 = 計 10 件、ただし Theory の InlineData 展開後の件数で変動）

- [ ] **Step 6: 単体テストとコードビハインドをコミット**

Run:
```bash
git add ICCardManager/src/ICCardManager/Views/Dialogs/OperationLogDialog.xaml.cs ICCardManager/tests/ICCardManager.Tests/Views/Dialogs/OperationLogDialogLiveRegionTests.cs
git commit -m "$(cat <<'EOF'
fix: OperationLogDialog で LiveRegionChanged を発火 (Issue #1548)

DataContext (OperationLogSearchViewModel) の PropertyChanged を購読し、PageInfo / CurrentPage / TotalPages / StatusMessage / BusyMessage / IsBusy=true への遷移時に対応する TextBlock の UIElementAutomationPeer に対して AutomationEvents.LiveRegionChanged を発火させる。これにより、NVDA / Narrator が動的 TextBlock のテキスト変化を読み上げるようになる（Issue #1509 で StaffAuthDialog に適用したパターンを ViewModel バインド向けに適用）。

プロパティ名 → 対象 TextBlock 名 のマッピングは internal static GetTargetElementName(string?, bool) に分離し、WPF UI スレッド不要で単体テスト可能にした。Window.Closed で PropertyChanged を解除してメモリリーク防止。

設計書: ICCardManager/docs/superpowers/specs/2026-05-19-issue-1548-operationlog-liveregion-firing-design.md

Refs #1548

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: 静的解析テストを追加

**Files:**
- Modify: `ICCardManager/tests/ICCardManager.Tests/Views/Dialogs/DialogAutomationPropertiesCoverageTests.cs`

- [ ] **Step 1: 既存 `DialogAutomationPropertiesCoverageTests.cs` を Read**

ファイルの構造を把握（既存テストメソッドの命名規則、ファイルパスの定数、`File.ReadAllText` のパターン等）。

- [ ] **Step 2: 既存のテストパターンに合わせて 2 件のテストを追加**

既存ファイルの末尾（最後の `}` の前）に Edit で以下を追加:

```csharp
    /// <summary>
    /// Issue #1548: OperationLogDialog のコードビハインドが LiveRegionChanged を明示発火していること。
    /// AutomationProperties.LiveSetting="Polite" 単独ではスクリーンリーダーが沈黙するため、
    /// UIElementAutomationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged) の呼び出しが必須。
    /// </summary>
    [Fact]
    public void OperationLogDialog_CodeBehindに_RaiseAutomationEventLiveRegionChangedが存在すること()
    {
        var path = Path.Combine(GetProjectRoot(), "src", "ICCardManager", "Views", "Dialogs", "OperationLogDialog.xaml.cs");
        var code = File.ReadAllText(path);

        code.Should().Contain("RaiseAutomationEvent",
            "AutomationProperties.LiveSetting='Polite' 単独では発火しないため、明示的な RaiseAutomationEvent 呼び出しが必須（Issue #1509 で StaffAuthDialog に確立されたパターン）");
        code.Should().Contain("AutomationEvents.LiveRegionChanged",
            "発火するイベント種別は LiveRegionChanged であること");
    }

    /// <summary>
    /// Issue #1548: OperationLogDialog のコードビハインドが ViewModel.PropertyChanged を購読・解除していること。
    /// ViewModel バインド駆動の TextBlock に対する LiveRegion 通知は、ViewModel 側のプロパティ変化を起点とするため、
    /// 購読と解除の両方が必要（解除漏れはメモリリーク）。
    /// </summary>
    [Fact]
    public void OperationLogDialog_CodeBehindで_ViewModelPropertyChangedを購読と解除していること()
    {
        var path = Path.Combine(GetProjectRoot(), "src", "ICCardManager", "Views", "Dialogs", "OperationLogDialog.xaml.cs");
        var code = File.ReadAllText(path);

        code.Should().MatchRegex(@"PropertyChanged\s*\+=",
            "ViewModel の PropertyChanged を購読していること");
        code.Should().MatchRegex(@"PropertyChanged\s*-=",
            "Window.Closed 等で PropertyChanged を解除していること（メモリリーク防止）");
    }
```

注意: `GetProjectRoot()` ヘルパーが既存ファイルになければ、既存テストが使っているパス組み立てパターンに合わせる（Task 4 Step 1 で確認）。

- [ ] **Step 3: ビルドとテスト Pass 確認**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```
Expected: `0 Error`、警告ゼロ

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --nologo --verbosity quiet --filter "FullyQualifiedName~DialogAutomationPropertiesCoverageTests" 2>&1 | tail -3
```
Expected: `Passed: N+2, Failed: 0, Skipped: 0`（N は元の件数）

- [ ] **Step 4: コミット**

Run:
```bash
git add ICCardManager/tests/ICCardManager.Tests/Views/Dialogs/DialogAutomationPropertiesCoverageTests.cs
git commit -m "$(cat <<'EOF'
test: OperationLogDialog の LiveRegion 発火パターンを静的解析で検証 (Issue #1548)

DialogAutomationPropertiesCoverageTests に 2 件を追加:
- OperationLogDialog.xaml.cs に RaiseAutomationEvent(AutomationEvents.LiveRegionChanged) 呼び出しが存在することを検証
- OperationLogDialog.xaml.cs で PropertyChanged の購読 (+=) と解除 (-=) の両方が存在することを検証

これにより、将来のリファクタリングで RaiseAutomationEvent 呼び出しや PropertyChanged 解除が消失した場合に CI で検知できる。

Refs #1548

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: 全テスト Pass とビルド警告ゼロを最終確認

**Files:**
- Read-only: 全テスト

- [ ] **Step 1: 全テスト実行**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln --nologo --verbosity quiet 2>&1 | tail -5
```
Expected: `Passed: N+X, Failed: 0, Skipped: 0`（Task 0 Step 2 の N より、追加テスト数（Task 1 の 9 件程度 + Task 2 の最大 3 件 + Task 4 の 2 件）増えていることを確認）

失敗テストがある場合は内容を確認し、本 PR の修正が原因なら修正。原因が他要因（環境）なら判断を仰ぐ。

- [ ] **Step 2: ビルド警告ゼロを最終確認**

Run:
```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | grep -E "warning|Warning" | wc -l
```
Expected: `0`

警告がある場合は同 PR 内で修正。

---

## Task 6: テスト件数表 §1.1a の同期判定

**Files:**
- Read-only / Modify (条件付き): `ICCardManager/docs/design/07_テスト設計書.md`

- [ ] **Step 1: CI スクリプト相当の件数計測**

Release ビルドを実行してから、CI と同じパターンで件数計測:

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal --configuration Release 2>&1 | tail -3
```
Expected: ビルド成功

```bash
echo -n "Unit: " && "/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --list-tests --nologo --verbosity quiet --no-build --configuration Release 2>&1 | grep -cE "^\s+ICCardManager\.Tests\." && echo -n "UI: " && "/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.UITests/ICCardManager.UITests.csproj --list-tests --nologo --verbosity quiet --no-build --configuration Release 2>&1 | grep -cE "^\s+ICCardManager\.UITests\."
```
Expected: `Unit: X, UI: 26`（X は新規追加テスト数を含めた値）

- [ ] **Step 2: §1.1a 件数表との差分を確認**

```bash
grep -n "件" ICCardManager/docs/design/07_テスト設計書.md | grep -E "単体テスト|UIテスト|合計" | head -5
```
Expected: `単体テスト ... 3,227件`、`UIテスト ... 26件`、`合計 ... 3,253件` 等（最新値）

`Step 1` の実測値と表記載値を比較。

- [ ] **Step 3: 件数表を更新（差分がある場合のみ）**

実測値と表記載値に差がある場合のみ、`07_テスト設計書.md` §1.1a の単体テスト件数行と合計件数行を Edit で更新。

更新後、Step 1 の計測を再実行して値が一致することを確認。

- [ ] **Step 4: 件数表を更新した場合のみコミット**

Run:
```bash
git add ICCardManager/docs/design/07_テスト設計書.md
git commit -m "$(cat <<'EOF'
docs: テスト件数表 §1.1a を Issue #1548 追加分に同期更新

Issue #1548 で追加した LiveRegion 発火関連テスト分を反映。

Refs #1548

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

差分なしの場合は本タスクのコミットなし。

---

## Task 7: テスト設計書 §X.X への追記

**Files:**
- Modify: `ICCardManager/docs/design/07_テスト設計書.md`

- [ ] **Step 1: テスト設計書のテストケース番号体系を把握**

`07_テスト設計書.md` の `§2` 配下を見て、既存テストケース番号の最大値（例: UT-XXX、最大何番）を確認:

```bash
grep -oE "UT-[0-9]+" ICCardManager/docs/design/07_テスト設計書.md | sort -u | tail -5
```

最新最大値の次を本タスクで使用する番号にする（例: 既存 UT-064 が最大なら UT-065 を使用）。

- [ ] **Step 2: §2 配下の適切な節に新規テストケース説明を追加**

`07_テスト設計書.md` の構造に合わせて、`§2.XX OperationLogDialog のアクセシビリティ (Issue #1548)` 節を追加。記載例:

```markdown
### 2.XX OperationLogDialog の LiveRegion 発火 (Issue #1548)

**目的**: 動的 TextBlock のテキスト変化時にスクリーンリーダーが読み上げられること。

#### テストケース

| ID | テスト名 | 検証内容 |
|---|---|---|
| UT-XXX-01 | GetTargetElementName_対象プロパティ変化時に_対応するTextBlock名を返すこと | PageInfo→PageInfoText、CurrentPage/TotalPages→CurrentPageNumberText、StatusMessage→StatusMessageText、BusyMessage→ProcessingOverlayText のマッピング |
| UT-XXX-02 | GetTargetElementName_IsBusyがtrueへ変化時_ProcessingOverlayTextを返すこと | Visibility 表示時の通知 |
| UT-XXX-03 | GetTargetElementName_IsBusyがfalseへ変化時_nullを返すこと | 非表示への遷移では通知しない |
| UT-XXX-04 | GetTargetElementName_対象外プロパティの場合_nullを返すこと | 無関係なプロパティ変化で誤発火しない |
| UT-XXX-05 | OperationLogDialog_CodeBehindに_RaiseAutomationEventLiveRegionChangedが存在すること | 静的解析: 発火コード自体の存在 |
| UT-XXX-06 | OperationLogDialog_CodeBehindで_ViewModelPropertyChangedを購読と解除していること | 静的解析: 購読/解除パターン |

**手動確認**（単体テスト不可能な範囲）: NVDA / Narrator でダイアログ操作中にステータス変化が読み上げられることをユーザーが確認する。

**先行事例**: Issue #1509（StaffAuthDialog の同根問題）
```

実際の `XX` 番号は Step 1 の結果に合わせる。

- [ ] **Step 3: コミット**

Run:
```bash
git add ICCardManager/docs/design/07_テスト設計書.md
git commit -m "$(cat <<'EOF'
docs: テスト設計書に Issue #1548 のテストケース説明を追加

OperationLogDialog の LiveRegion 発火対応で追加したテストケースを §2 に追記。
GetTargetElementName 純粋関数のマッピング検証 + コードビハインドの静的解析検証で構成。

Refs #1548

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: CHANGELOG 更新

**Files:**
- Modify: `ICCardManager/CHANGELOG.md`

- [ ] **Step 1: CHANGELOG の冒頭構造を確認**

```bash
head -40 ICCardManager/CHANGELOG.md
```
Expected: `### Unreleased` セクションが存在し、`**アクセシビリティ改善**` または `**バグ修正**` の下位カテゴリがあること

- [ ] **Step 2: Unreleased セクションに追記**

カテゴリの慣例:
- 「`**バグ修正**`」または「`**アクセシビリティ改善**`」セクションがあれば、その末尾に 1 行追加
- 両方ない場合は、`**バグ修正**` を新規追加

追記例（Edit で適切な位置に挿入）:

```markdown
- OperationLogDialog の動的 TextBlock 4 要素（`PageInfoText` / `CurrentPageNumberText` / `StatusMessageText` / `ProcessingOverlayText`）が `AutomationProperties.LiveSetting="Polite"` を付与済みにもかかわらず、テキスト変化時に NVDA / Narrator で読み上げが発火しなかったバグを修正。原因は WPF の `TextBlock.Text` バインド更新だけでは `LiveRegionChanged` イベントが確実に発火しないためで、コードビハインドで ViewModel (`OperationLogSearchViewModel`) の `PropertyChanged` を購読し、対応 TextBlock の `UIElementAutomationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)` を明示呼び出しするよう修正（Issue #1509 で `StaffAuthDialog` に確立されたパターンを ViewModel バインド向けに適用）。`ProcessingOverlayText` は `IsBusy` による `Visibility` トグルでも通知発火。プロパティ名 → 対象 TextBlock 名 のマッピングは `internal static GetTargetElementName(string?, bool)` に分離し WPF UI スレッド不要で単体テスト可能にした。`Window.Closed` で `PropertyChanged` を解除してメモリリーク防止。`DialogAutomationPropertiesCoverageTests` に静的解析テスト 2 件、`OperationLogDialogLiveRegionTests` に純粋関数のロジックテスト 9 件を追加。スクリーンリーダー実機読み上げ確認はユーザー手動（設計書 §5.4 参照）。設計書 `ICCardManager/docs/superpowers/specs/2026-05-19-issue-1548-operationlog-liveregion-firing-design.md` に方針（アプローチ A: ViewModel.PropertyChanged 購読方式採用）と試験戦略を記録（#1548）
```

- [ ] **Step 3: コミット**

Run:
```bash
git add ICCardManager/CHANGELOG.md
git commit -m "$(cat <<'EOF'
docs: CHANGELOG に Issue #1548 OperationLogDialog LiveRegion 発火対応を追記

Refs #1548

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: ブランチを push して PR を作成

**Files:**
- なし（ブランチ操作と PR 作成のみ）

- [ ] **Step 1: ブランチを push**

Run:
```bash
git push -u origin fix/issue-1548-operationlog-liveregion-firing
```
Expected: `branch 'fix/issue-1548-operationlog-liveregion-firing' set up to track 'origin/fix/issue-1548-operationlog-liveregion-firing'.`

DNS で失敗した場合は 10〜20 秒待ってリトライ。

- [ ] **Step 2: PR を作成**

Run:
```bash
gh pr create --title "fix: OperationLogDialog の動的 TextBlock で LiveRegionChanged を発火 (Issue #1548)" --body-file - <<'EOF'
## Summary

`OperationLogDialog` の 4 つの動的 TextBlock（`PageInfoText` / `CurrentPageNumberText` / `StatusMessageText` / `ProcessingOverlayText`）が `AutomationProperties.LiveSetting="Polite"` を付与済みにもかかわらず、テキスト変化時に NVDA / Narrator で読み上げが発火しなかったバグを修正。

## 原因

WPF の `TextBlock.Text` バインド更新だけでは `LiveRegionChanged` イベントが確実に発火しない（Issue #1509 で `StaffAuthDialog` に対して既に確立された知見）。`AutomationProperties.LiveSetting` 単独では「変化通知の意図」を宣言するのみで、実際の発火は `UIElementAutomationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)` の明示呼び出しが必要。

## 解決アプローチ

`OperationLogDialog.xaml.cs`（コードビハインド）で `DataContext`（= `OperationLogSearchViewModel`）の `PropertyChanged` を購読し、対象プロパティが変化したタイミングで対応する TextBlock に `RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)` を呼ぶ。`Window.Closed` で購読解除しメモリリーク防止。

| ViewModel プロパティ | 通知先 TextBlock |
|---|---|
| `PageInfo` | `PageInfoText` |
| `CurrentPage` / `TotalPages` | `CurrentPageNumberText` |
| `StatusMessage` | `StatusMessageText` |
| `BusyMessage` | `ProcessingOverlayText` |
| `IsBusy`（true への遷移時） | `ProcessingOverlayText` |

プロパティ名 → 対象 TextBlock 名 のマッピングは `internal static GetTargetElementName(string?, bool)` に分離して WPF UI スレッド不要で単体テスト可能にした。

## テスト

- **静的解析テスト**（`DialogAutomationPropertiesCoverageTests`）:
  - `OperationLogDialog.xaml.cs` に `RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)` が存在すること
  - `PropertyChanged` の購読 (`+=`) と解除 (`-=`) の両方が存在すること
- **ロジック単体テスト**（`OperationLogDialogLiveRegionTests`、新規）:
  - `GetTargetElementName` のマッピング検証（Theory 5 件）
  - `IsBusy` true/false 時の動作（2 件）
  - 対象外プロパティで `null` 返却（Theory 3 件）

## ユーザー手動確認のお願い

スクリーンリーダー実機での読み上げ確認は単体テストで不可能なため、以下を確認してください（設計書 §5.4 参照）:

| 確認項目 | 操作 | 期待される読み上げ |
|---|---|---|
| ページ送り通知 | 操作履歴を 21 件以上検索 → 次ページボタン押下 | 「2 / 3」「2 ページ目」 |
| ステータスメッセージ | 検索ボタン押下 → 検索完了 | 「20 件取得しました」等 |
| 処理中オーバーレイ | 重い検索条件で検索実行 | 「データ取得中...」等 |
| 起動時の誤発火がないこと | ダイアログを開いた直後 | 不要な読み上げが発生しない |

## 設計書

- `ICCardManager/docs/superpowers/specs/2026-05-19-issue-1548-operationlog-liveregion-firing-design.md`
- 実装プラン: `docs/superpowers/plans/2026-05-19-issue-1548-operationlog-liveregion-firing.md`

## 関連

- Issue #1509: `StaffAuthDialog` の同根問題を解決した先行 PR
- Issue #1468: 元のアクセシビリティ拡充（`LiveSetting` 付与）
- PR #1530（Issue #1501）: 動的 TextBlock の `x:Name` 付与とテスト追加

## Test plan
- [x] ビルド警告ゼロ（Debug / Release）
- [x] `dotnet test` 全件 Pass
- [x] 静的解析テスト 2 件追加 Pass
- [x] ロジック単体テスト 9 件追加 Pass
- [x] CI のテスト件数表自動検証通過予定

Closes #1548

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
```

Expected: PR URL が出力される。

DNS で失敗した場合は 10〜20 秒待ってリトライ。

- [ ] **Step 3: PR URL を確認してユーザーに報告**

Run:
```bash
gh pr view --json url --jq .url
```

最終報告でこの URL をユーザーに伝える。

---

## 失敗時の復旧

- **ビルドエラー**: 直前の Edit を `git diff` で確認、using ディレクティブ抜けや括弧の不整合を点検
- **テスト Fail（Task 1）**: `dotnet test --logger "console;verbosity=detailed" --filter "FullyQualifiedName~OperationLogDialogLiveRegionTests"` で詳細ログを取り、`InlineData` の値や `GetTargetElementName` の switch 漏れを疑う
- **テスト Fail（Task 2）**: VM 側の `OnPropertyChanged(nameof(PageInfo))` が partial メソッド名を正しく特定できているか確認
- **静的解析テスト Fail（Task 4）**: `OperationLogDialog.xaml.cs` を Read で確認し、`RaiseAutomationEvent` / `PropertyChanged +=` / `PropertyChanged -=` の文字列が含まれていることを目視確認
- **CI テスト件数検証 Fail（Task 9 後）**: §1.1a の更新（Task 6）が漏れている可能性。CI のログに記載された乖離値で §1.1a を Edit して push する

---

## 注意事項

- **STA 不要設計**: 純粋関数 `GetTargetElementName` を分離したため、テストに `[StaFact]` や `Xunit.StaFact` パッケージ追加は不要
- **`PageInfo` が `[ObservableProperty]` の場合**: もし `PageInfo` 自体が `[ObservableProperty]` で宣言されていれば、`PropertyChanged` は自動発火されるため Task 2 の修正は不要。Task 0 Step 4 で必ず確認する
- **Loaded ハンドラ名**: PR #1530（Issue #1501）で追加された Loaded ハンドラの実名は Task 0 Step 3 で確認し、Task 3 Step 3 で実名を使用する
- **メモリリーク**: `Window.Closed` 時に `PropertyChanged -= ` を必ず呼ぶこと。`Closed` イベント発火後に WeakEventManager 経由でない `+=` 購読は ViewModel を保持し続けるため
