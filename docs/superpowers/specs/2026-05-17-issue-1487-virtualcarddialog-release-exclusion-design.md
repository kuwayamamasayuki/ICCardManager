# Issue #1487 設計書: VirtualCardDialog の Release ビルド完全除外検証

- 起票日: 2026-05-17
- 対象 Issue: #1487
- 対象ブランチ: `fix/issue-1487-virtualcarddialog-release-exclusion`

## 背景

`Views/Dialogs/VirtualCardDialog.xaml` は DEBUG ビルド専用の「仮想タッチ」設定ダイアログ（Issue #640）である。Release ビルドでエンドユーザーに露出してはならないが、現状は以下の状態で **「完全除外されているかどうかが検証されていない」**:

| 箇所 | 現状 | Release ビルドでの状態 |
|------|------|---------------------|
| `VirtualCardDialog.xaml.cs` のコンストラクタ | DEBUG/Release で分岐済み | デフォルトコンストラクタが残る |
| `App.xaml.cs:339` の Dialog DI 登録 | `#if DEBUG` ガード済み | DI 未登録 |
| `App.xaml.cs:319` の `VirtualCardViewModel` DI 登録 | **ガードなし** | DI 登録される |
| `MainViewModel.OpenVirtualCardAsync` | **ガードなし** | メソッド本体が残る |
| `MainViewModel.OpenVirtualCardCommand` (RelayCommand) | **ガードなし** | コマンドが生成される |
| `MainWindow.xaml:881` の「仮想タッチ」ボタン | 親 StackPanel が `App.IsDebugBuild` でガード | UI 非表示 |

UI には露出しないが、コマンド・ViewModel・メソッドはバイナリに残り、配布ビルドからのアクセス経路が完全に塞がっているとは言い切れない。また、将来「他のキーボードショートカット」や「メニュー」を追加した際に DEBUG ガードを忘れるリグレッションが発生し得る。

## 目的

1. Release ビルドにおいて、`VirtualCardDialog` への**起動経路を完全にゼロ**にする
2. その状態を**継続的に保証する単体テスト**を追加し、将来のリグレッションを防ぐ

## 設計方針

[Option A] 起動経路の完全 `#if DEBUG` ガード化 + 静的解析テストによる検証。

XAML/BAML 自体は Release バイナリに残るが、参照経路ゼロのため実害なし。csproj レベルでの XAML 除外（Option B）は WPF SDK の暗黙的ファイル登録と競合してビルドが壊れやすいため採用しない。CI でのアセンブリ逆解析（Option C）はインフラ整備が必要で過剰。

## 変更内容

### 1. `App.xaml.cs:319` の VirtualCardViewModel 登録を DEBUG ガード化

```csharp
// Before
services.AddTransient<VirtualCardViewModel>();

// After
#if DEBUG
services.AddTransient<VirtualCardViewModel>();
#endif
```

### 2. `MainViewModel.OpenVirtualCardAsync` と関連メソッドを DEBUG ガード化

`OpenVirtualCardAsync` および専用ヘルパー `ProcessVirtualTouchAsync` 全体を `#if DEBUG` ... `#endif` で囲む。`RelayCommand` 属性が付いたメソッドを条件付きコンパイル対象にすることで、ソースジェネレーターが生成する `OpenVirtualCardCommand` プロパティも Release から除外される。

```csharp
#if DEBUG
[RelayCommand]
public async Task OpenVirtualCardAsync() { ... }

private async Task ProcessVirtualTouchAsync(VirtualTouchResult touchResult) { ... }
#endif
```

`SimulateStaffCard` / `SimulateIcCard` は Mock データ依存のため `HybridCardReader`（既に DEBUG 限定型）の型チェックでガードしているが、**型自体は public で残る**。Issue #1487 のスコープは VirtualCardDialog のみのため、これらは対象外。

### 3. `MainWindow.xaml` は変更不要

「仮想タッチ」ボタン（line 881）を含む StackPanel（line 876-883）は既に `Visibility="{Binding Source={x:Static app:App.IsDebugBuild}, ...}"` でガード済み。Release ビルドでは UI に表示されない。

### 4. `VirtualCardDialog.xaml.cs` の Release 用デフォルトコンストラクタは現状維持

XAML が常時ビルドされるため `InitializeComponent()` を呼ぶコンストラクタが Release でも必要。ガード強化テストの対象としても都合がよい。

## テスト設計

### 新規テストクラス: `ConditionalCompilationGuardTests`

配置先: `ICCardManager/tests/ICCardManager.Tests/Architecture/ConditionalCompilationGuardTests.cs`

ソースファイルを行単位で走査し、`#if DEBUG` / `#if !DEBUG` / `#else` / `#endif` のプリプロセッサスタックをトラッキングする補助メソッド `IsInsideDebugGuard(string sourcePath, string identifier)` を実装。対象識別子の出現位置で「DEBUG ブロック内にあること」を検証する。

#### 検証項目

| # | テスト名 | 検証内容 |
|---|---------|---------|
| 1 | `MainViewModel_OpenVirtualCardAsync_IsGuardedByDebugDirective` | `MainViewModel.cs` の `OpenVirtualCardAsync` メソッド宣言行が `#if DEBUG` 内 |
| 2 | `MainViewModel_ProcessVirtualTouchAsync_IsGuardedByDebugDirective` | 同 `ProcessVirtualTouchAsync` |
| 3 | `App_VirtualCardViewModelRegistration_IsGuardedByDebugDirective` | `App.xaml.cs` の `AddTransient<VirtualCardViewModel>()` 行が `#if DEBUG` 内 |
| 4 | `App_VirtualCardDialogRegistration_IsGuardedByDebugDirective` | `App.xaml.cs` の `AddTransient<VirtualCardDialog>()` 行が `#if DEBUG` 内（既存ガードの回帰防止） |
| 5 | `MainWindow_VirtualCardButton_IsGuardedByIsDebugBuild` | `MainWindow.xaml` の「仮想タッチ」ボタン要素が `App.IsDebugBuild` バインディングを持つ StackPanel の子孫であること（既存ガードの回帰防止） |

### 補助メソッドのロジック

```csharp
private static bool IsLineInsideDebugBlock(string source, int targetLineNumber)
{
    var depth = 0;       // #if DEBUG の入れ子レベル
    var inDebug = false; // 現在 DEBUG ブロック内か（else で反転）
    var stack = new Stack<bool>();
    var lines = source.Split('\n');
    for (int i = 0; i < lines.Length; i++)
    {
        var trimmed = lines[i].TrimStart();
        if (trimmed.StartsWith("#if DEBUG")) { stack.Push(true); }
        else if (trimmed.StartsWith("#if !DEBUG")) { stack.Push(false); }
        else if (trimmed.StartsWith("#if")) { stack.Push(false); } // 他の #if は無視扱い
        else if (trimmed.StartsWith("#else") && stack.Count > 0)
        {
            var top = stack.Pop();
            stack.Push(!top);
        }
        else if (trimmed.StartsWith("#endif") && stack.Count > 0)
        {
            stack.Pop();
        }
        if (i + 1 == targetLineNumber)
        {
            return stack.Count > 0 && stack.All(x => x);
        }
    }
    return false;
}
```

### テストプロジェクトからのソース参照

`ICCardManager.Tests` プロジェクトの実行ディレクトリから本体ソースへの相対パス参照は不安定なため、ヘルパー `SolutionPathHelper.GetSourceRoot()` 相当があれば再利用、なければテスト内に簡易実装：

```csharp
private static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ICCardManager.sln")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("リポジトリルートが見つかりません");
}
```

## 影響範囲

- **Debug ビルド**: 完全に従来通り。「仮想タッチ」機能維持。
- **Release ビルド**: 起動経路（コマンド・DI 登録・UI ボタン）が完全除外。BAML だけが残るが呼び出し経路ゼロ。
- **テスト件数**: +5 件（`ConditionalCompilationGuardTests`）。
- **ドキュメント**:
  - `docs/design/07_テスト設計書.md` にテストクラスを追記
  - `CHANGELOG.md` の v2.8.2 (Unreleased) セクションに Issue #1487 対応を記録

## 検証手順

1. `dotnet build` (Debug) でエラー警告なしを確認
2. `dotnet test` で `ConditionalCompilationGuardTests` を含め全件 PASS
3. 手動確認：Debug ビルドでアプリ起動 → 「仮想タッチ」ボタンが従来通り動作することを確認

## 想定リスクと対策

| リスク | 対策 |
|--------|------|
| BAML メタデータが Release に残ることでセキュリティ監査で指摘 | 起動経路ゼロを設計書に明記。Option C（アセンブリ逆解析）は将来 CI 整備時に追加検討 |
| 静的解析テストがソース文字列の表記揺れ（タブ・スペース等）で fragile | `TrimStart()` で先頭空白を許容。プリプロセッサ行が無視されることはない |
| 他のメンバーが将来 `OpenVirtualCardCommand` をバインドする他 UI を追加 | 静的解析テストでバインディング箇所自体は検出できないため、`MainWindow_VirtualCardButton_IsGuardedByIsDebugBuild` を機能拡張時の手本としつつ、開発者ガイドの「DEBUG 限定機能の追加手順」セクションに記載 |
