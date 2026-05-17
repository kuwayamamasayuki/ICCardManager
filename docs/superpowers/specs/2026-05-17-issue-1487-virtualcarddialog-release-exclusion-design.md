# Issue #1487 設計書: VirtualCardDialog の Release ビルド完全除外検証

- 起票日: 2026-05-17
- 対象 Issue: #1487
- 対象ブランチ: `fix/issue-1487-virtualcarddialog-release-exclusion`

## 背景

`Views/Dialogs/VirtualCardDialog.xaml` は DEBUG ビルド専用の「仮想タッチ」設定ダイアログ（Issue #640）である。Release ビルドでエンドユーザーに露出してはならない。Issue #1487 は「ガードが完全に効いているかを検証していない」という指摘である。

### 現状調査（2026-05-17 時点）

調査の結果、起動経路は **既にすべて `#if DEBUG` または `App.IsDebugBuild` でガード済み** であることが判明した：

| 箇所 | 現状 |
|------|------|
| `VirtualCardDialog.xaml.cs` のコンストラクタ | DEBUG/Release で分岐済み ✓ |
| `App.xaml.cs:317-320` の `VirtualCardViewModel` DI 登録 | `#if DEBUG` ガード済み ✓ |
| `App.xaml.cs:338-340` の `VirtualCardDialog` DI 登録 | `#if DEBUG` ガード済み ✓ |
| `MainViewModel.cs:2432-2540` の `OpenVirtualCardAsync` / `ProcessVirtualTouchAsync` | `#if DEBUG` ガード済み ✓ |
| `MainWindow.xaml:876-883` の「仮想タッチ」ボタン（StackPanel 全体） | `App.IsDebugBuild` バインディングでガード済み ✓ |

つまり実害となる起動経路は既に閉じられている。しかし、これを**継続的に保証する自動検証が存在しない**ため、将来「他のキーボードショートカット」「メニュー項目」「コマンド」を追加した際に DEBUG ガードを忘れるリグレッションが発生し得る。

## 目的

既存の DEBUG ガード状態を**リグレッション防止テスト**で固定化し、Issue #1487 が指摘する「検証されていない」という状態を解消する。コード変更は行わず、テストとドキュメントのみで完結させる。

## 設計方針

[Option A] 起動経路の `#if DEBUG` ガード状態を**静的解析テスト**で継続検証する。

XAML/BAML 自体は Release バイナリに残るが、参照経路ゼロのため実害なし。csproj レベルでの XAML 除外（Option B）は WPF SDK の暗黙的ファイル登録と競合してビルドが壊れやすいため採用しない。CI でのアセンブリ逆解析（Option C）はインフラ整備が必要で過剰。

## 変更内容

### コード変更: なし

調査の結果、本体コードはすでに目的の状態（起動経路の DEBUG ガード）を満たしている。設計書のレビュー時に「コード変更不要」と判明したことを記録として残す意味でも、この設計書を成果物として残す。

### テスト追加: `ConditionalCompilationGuardTests`

配置先: `ICCardManager/tests/ICCardManager.Tests/ConditionalCompilationGuardTests.cs`（同種の規約検証テスト `UserFacingTextConventionTests.cs` がプロジェクト直下にあるため慣習に揃える）

ソースファイルを行単位で走査し、`#if DEBUG` / `#if !DEBUG` / `#else` / `#endif` のプリプロセッサスタックをトラッキングする補助メソッドを実装。対象識別子の出現位置が「DEBUG ブロック内」であることを検証する。

#### 検証項目

| # | テスト名 | 検証対象 |
|---|---------|---------|
| 1 | `MainViewModel_OpenVirtualCardAsync_IsInsideDebugGuard` | `MainViewModel.cs` の `public async Task OpenVirtualCardAsync()` 宣言行 |
| 2 | `MainViewModel_ProcessVirtualTouchAsync_IsInsideDebugGuard` | 同 `private async Task ProcessVirtualTouchAsync(` 宣言行 |
| 3 | `App_VirtualCardViewModelRegistration_IsInsideDebugGuard` | `App.xaml.cs` の `AddTransient<VirtualCardViewModel>()` 行 |
| 4 | `App_VirtualCardDialogRegistration_IsInsideDebugGuard` | `App.xaml.cs` の `AddTransient<Views.Dialogs.VirtualCardDialog>()` 行 |
| 5 | `MainWindow_VirtualCardButton_IsGuardedByIsDebugBuild` | `MainWindow.xaml` の `OpenVirtualCardCommand` を持つ Button が `App.IsDebugBuild` バインディング配下にあること |

#### 補助ロジック設計

C# プリプロセッサのスタック追跡:

```csharp
internal static bool IsLineInsideDebugBlock(string source, int targetLineNumber)
{
    var stack = new Stack<bool>(); // true = DEBUG ブロック内
    var lines = source.Replace("\r\n", "\n").Split('\n');
    for (int i = 0; i < lines.Length; i++)
    {
        var trimmed = lines[i].TrimStart();
        if (trimmed.StartsWith("#if DEBUG")) stack.Push(true);
        else if (trimmed.StartsWith("#if !DEBUG")) stack.Push(false);
        else if (trimmed.StartsWith("#if")) stack.Push(false);
        else if (trimmed.StartsWith("#else") && stack.Count > 0)
        { var top = stack.Pop(); stack.Push(!top); }
        else if (trimmed.StartsWith("#endif") && stack.Count > 0) stack.Pop();

        if (i + 1 == targetLineNumber)
            return stack.Count > 0 && stack.All(x => x);
    }
    return false;
}
```

XAML の場合は名前空間プレフィックス（例: `app:App.IsDebugBuild`）に該当バインディングを含む `Visibility` 属性を持つ StackPanel の子孫であることを検証する別ロジック。

#### リポジトリルートの解決

テスト実行時の作業ディレクトリは `bin/Debug/net48/` 等となるため、`AppContext.BaseDirectory` から親ディレクトリを辿って `.git` または `ICCardManager.sln` を探索する。

```csharp
private static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("リポジトリルートが見つかりません");
}
```

## 影響範囲

- **本体コード**: 変更なし
- **テスト件数**: +5 件（`ConditionalCompilationGuardTests`）
- **ドキュメント**:
  - `docs/design/07_テスト設計書.md` にテストクラスを追記
  - `CHANGELOG.md` の v2.8.2 (Unreleased) セクションに Issue #1487 対応として記録

## 検証手順

1. `dotnet build` (Debug) でエラー警告なしを確認
2. `dotnet test` で新規テスト 5 件を含め全件 PASS

## 想定リスクと対策

| リスク | 対策 |
|--------|------|
| BAML メタデータが Release に残ることでセキュリティ監査で指摘 | 起動経路ゼロを設計書に明記。Option C（アセンブリ逆解析）は将来 CI 整備時に追加検討 |
| 静的解析テストがソース文字列の表記揺れ（タブ・スペース等）で fragile | `TrimStart()` で先頭空白を許容。プリプロセッサ行は行頭が `#` であることが C# 仕様で保証される |
| 将来 `OpenVirtualCardCommand` を他 UI からバインドする変更が入る | 静的解析テストではバインディング全箇所は検出できないため、`docs/design/07_テスト設計書.md` の「DEBUG 限定機能の追加手順」に新規バインディング追加時のチェックポイントを明記 |
