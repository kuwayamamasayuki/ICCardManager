# Issue #1487 設計書: VirtualCardDialog の Release ビルド完全除外

## 1. 背景

`Views/Dialogs/VirtualCardDialog.xaml` は DEBUG 専用ダイアログ（Issue #640）として導入された。タイトル末尾の `[DEBUG]` 表記や `VirtualCardViewModel` の `#if DEBUG` 等で「DEBUG 専用」を示しているが、XAML 本体は WPF SDK の自動 `<Page>` 検出により Release ビルド時にも常時コンパイル対象となり、Release dll に型メタデータと XAML リソースが残る。

Issue #1487 は「起動経路の DEBUG ガードが完全に効いているか」「エンドユーザーに露出するリスクはないか」の検証を求めるもの。

## 2. 現状調査の結果

### 2.1 既存の多重ガード

| 経路 | 保護機構 | 場所 |
|---|---|---|
| UI ボタン | `app:App.IsDebugBuild` による StackPanel の Visibility 制御 | `Views/MainWindow.xaml` L881 周辺 |
| ViewModel コマンド | `[RelayCommand]` 付き `OpenVirtualCardAsync` 全体が `#if DEBUG / #endif` | `ViewModels/MainViewModel.cs` L2430–L2538 |
| DI 登録（ViewModel） | `services.AddTransient<VirtualCardViewModel>()` が `#if DEBUG / #endif` | `App.xaml.cs` L317–L320 |
| DI 登録（View） | `services.AddTransient<Views.Dialogs.VirtualCardDialog>()` が `#if DEBUG / #endif` | `App.xaml.cs` L338–L340 |

Release ビルド時の起動経路は閉じている。

### 2.2 残存リスク

1. **XAML 本体が Release dll に残る**
   `VirtualCardDialog.xaml` は WindowsDesktop SDK の自動含めにより常時 `<Page>` としてビルドされ、Release dll の中に `ICCardManager.Views.Dialogs.VirtualCardDialog` 型と XAML リソース `views/dialogs/virtualcarddialog.xaml` が残る。

2. **Release 用の引数なしコンストラクタが残置**
   `Views/Dialogs/VirtualCardDialog.xaml.cs` の `#else` ブロックに引数なし `public VirtualCardDialog()` が定義されている。これは DI レス（`new VirtualCardDialog()`）で開けてしまうため、将来 Release コードに誤って呼び出しが混入した場合に物理ガードがない。

3. **ガード位置の崩しを CI で検知できない**
   3 経路のガード（UI Visibility / Command / DI 登録）のうち 1 つでも `#if DEBUG` が外されると、Release ビルドが通って気付かれない可能性がある。

## 3. 設計方針

### 3.1 csproj レベルでの物理除外

`ICCardManager.csproj` の末尾近くに以下を追記し、Release ビルド時に `VirtualCardDialog` 関連ファイルをコンパイル対象から外す。

```xml
<!-- Issue #1487: VirtualCardDialog は DEBUG 専用機能のため、Release ビルドから物理的に除外 -->
<ItemGroup Condition="'$(Configuration)'=='Release'">
  <Compile Remove="Views\Dialogs\VirtualCardDialog.xaml.cs" />
  <Page Remove="Views\Dialogs\VirtualCardDialog.xaml" />
</ItemGroup>
```

- WindowsDesktop SDK は `**/*.xaml` を `<Page>` として自動含めるが、後続の `<Page Remove>` で打ち消せる
- `Compile Remove` により code-behind の `.cs` も除外され、Release dll に `VirtualCardDialog` 型が現れなくなる

### 3.2 code-behind の簡素化

`VirtualCardDialog.xaml.cs` から `#else` ブロックを削除する。csproj 除外により Release ビルド対象外になるため、引数なしコンストラクタは不要。

**変更前**:
```csharp
public partial class VirtualCardDialog : Window
{
#if DEBUG
    public VirtualCardDialog(VirtualCardViewModel viewModel) { ... }
#else
    public VirtualCardDialog() { InitializeComponent(); }
#endif
    private void CloseButton_Click(...) { ... }
}
```

**変更後**:
```csharp
#if DEBUG
public partial class VirtualCardDialog : Window
{
    public VirtualCardDialog(VirtualCardViewModel viewModel) { ... }
    private void CloseButton_Click(...) { ... }
}
#endif
```

クラス宣言ごと `#if DEBUG / #endif` で包む。`InitializeComponent` の partial 相方（`VirtualCardDialog.g.cs`）は `<Page>` から自動生成されるため、`<Page Remove>` により Release では生成されない。整合性が取れる。

### 3.3 静的解析テスト

`tests/ICCardManager.Tests/Infrastructure/VirtualCardDialogDebugIsolationTests.cs` を新規追加。すべてソースファイルを `File.ReadAllText` でテキスト解析するシンプルな xUnit テスト。

| テストケース | 検証内容 |
|---|---|
| `Csproj_RemovesVirtualCardDialog_OnReleaseConfiguration` | `ICCardManager.csproj` に `Condition="'$(Configuration)'=='Release'"` を持つ `ItemGroup` が存在し、その中で `Compile Remove="Views\Dialogs\VirtualCardDialog.xaml.cs"` と `Page Remove="Views\Dialogs\VirtualCardDialog.xaml"` の両方が記述されている |
| `MainViewModel_OpenVirtualCardAsync_IsGuardedByDebug` | `MainViewModel.cs` から `OpenVirtualCardAsync` の宣言行を見つけ、直前に閉じていない `#if DEBUG` があり、宣言行より後に対応する `#endif` がある |
| `App_VirtualCardDialogRegistration_IsGuardedByDebug` | `App.xaml.cs` の `AddTransient<Views.Dialogs.VirtualCardDialog>` 行と `AddTransient<VirtualCardViewModel>` 行がそれぞれ `#if DEBUG / #endif` ブロックの内側にある |
| `MainWindow_DebugButtons_AreGatedByIsDebugBuild` | `MainWindow.xaml` の「仮想タッチ」ボタンを含む StackPanel に `Visibility="{Binding Source={x:Static app:App.IsDebugBuild}, ...}"` が指定されている |
| `VirtualCardDialog_CodeBehind_HasNoElseBranch` | `VirtualCardDialog.xaml.cs` に `#else` トークンが存在せず、`#if DEBUG` の対応 `#endif` の間に Release 用引数なしコンストラクタ `public VirtualCardDialog()` が**ない**ことを確認 |

#### 静的解析の実装イメージ

```csharp
private static string ReadProjectFile(string relativePath)
{
    // テスト実行ディレクトリから src ルートを探す
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ICCardManager.sln")))
        dir = dir.Parent;
    if (dir == null) throw new InvalidOperationException("ICCardManager.sln が見つかりません");
    return File.ReadAllText(Path.Combine(dir.FullName, relativePath));
}

private static bool IsInsideIfDebug(string source, int targetIndex)
{
    // targetIndex より前の #if DEBUG と #endif をカウントし、開いたまま閉じていなければ true
    var ifDebugMatches = Regex.Matches(
        source.Substring(0, targetIndex), @"#if\s+DEBUG\b");
    var endifMatches = Regex.Matches(
        source.Substring(0, targetIndex), @"#endif\b");
    if (ifDebugMatches.Count == 0) return false;
    // 最後の #if DEBUG 以降に同数以上の #endif があるかで判定
    var lastIfDebug = ifDebugMatches[ifDebugMatches.Count - 1].Index;
    var endifsAfter = Regex.Matches(
        source.Substring(lastIfDebug, targetIndex - lastIfDebug), @"#endif\b").Count;
    return endifsAfter == 0;
}
```

> 注: `#if !DEBUG` や `#elif` を厳密に扱う必要は本テストではない。`#if DEBUG` 単独のみを許容する。

## 4. ファイル変更一覧

### 4.1 変更

- `src/ICCardManager/ICCardManager.csproj`: Release 用 `ItemGroup` を追加
- `src/ICCardManager/Views/Dialogs/VirtualCardDialog.xaml.cs`: クラス宣言全体を `#if DEBUG / #endif` で囲み、`#else` ブロックを削除
- `docs/design/03_画面設計書.md` §3.20: csproj 除外と多重ガードの追記
- `docs/design/05_クラス設計書.md` §9.3: VirtualCardDialog の Release 除外を明記
- `docs/design/07_テスト設計書.md`: 新規テスト 5 件の追加記載
- `CHANGELOG.md`: Unreleased セクションに項目追加

### 4.2 新規

- `tests/ICCardManager.Tests/Infrastructure/VirtualCardDialogDebugIsolationTests.cs`

### 4.3 影響なし

- DEBUG ビルド: 既存挙動完全維持
- Release ビルド: 型メタデータ消失（dll サイズ微減）、リフレクションでも到達不可

## 5. 想定リスク

| リスク | 対策 |
|---|---|
| `<Page Remove>` が WindowsDesktop SDK のバージョンによっては動作しない | `dotnet build -c Release` で必ず動作確認。失敗したら `<None Remove>` + 明示的 `<Page Include>` 等の代替を検討 |
| 静的解析テストが将来のリファクタリング（例: `OpenVirtualCardAsync` メソッド名変更）で誤検知 | テストケースのメッセージに「Issue #1487 のガード位置検証。リネーム時は本テストも更新」と明記 |
| Mono.Cecil による真の dll 検査を行わないため、SDK の挙動を間接的に信頼することになる | 設計書 5 章にトレードオフを記載。将来必要になれば `[Trait("Category", "Slow")]` で別途追加可能 |

## 6. テスト戦略まとめ

| レイヤ | 検証方法 |
|---|---|
| csproj の除外宣言 | 静的解析テスト 1 件 |
| 起動経路 3 ガードの整合性 | 静的解析テスト 3 件 |
| code-behind の引数なし constructor 撤去 | 静的解析テスト 1 件 |
| ビルド検証 | `dotnet build -c Release` 成功確認（手動 / CI） |

## 7. ブランチ・PR

- ブランチ: `chore/issue-1487-virtualcarddialog-release-exclusion`
- PR タイトル: `chore: VirtualCardDialog を Release ビルドから物理除外し回帰テストを追加 (Issue #1487)`
