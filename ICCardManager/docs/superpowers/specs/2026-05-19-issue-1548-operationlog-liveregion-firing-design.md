# Issue #1548: OperationLogDialog の動的 TextBlock 読み上げ未発火（LiveRegionChanged 発火パターン未適用）

- 起票日: 2026-05-19
- 関連 Issue: #1548
- 関連 PR: #1530 (Issue #1501、本問題の手動確認過程で発覚)
- 先行事例: Issue #1509 (`StaffAuthDialog` の同根問題)
- 種別: バグ修正（アクセシビリティ）
- 優先度: Medium

## 1. 背景

`OperationLogDialog` の 4 つの動的 TextBlock（`PageInfoText` / `CurrentPageNumberText` / `StatusMessageText` / `ProcessingOverlayText`）に `AutomationProperties.LiveSetting="Polite"` が付与されているが、実機（NVDA / Narrator）でテキスト変化時の読み上げが**発火しない**。

WPF では `TextBlock.Text` バインド更新だけでは `LiveRegionChanged` イベントが確実に発火せず、`AutomationProperties.LiveSetting` 単独では「変化通知の意図」を宣言するのみで、実際の発火は `UIElementAutomationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)` の明示呼び出しが必要である（Issue #1509 で確立された知見）。

`StaffAuthDialog` ではコードビハインドが Text を直接代入する設計（`StatusText.Text = message;`）のため、代入直後に `RaiseAutomationEvent` を呼べたが、`OperationLogDialog` は ViewModel バインディング駆動のため、同パターンが直接転写できない。

## 2. ゴール

- `OperationLogDialog` の 4 つの動的 TextBlock がテキスト変化時に `LiveRegionChanged` を発火し、スクリーンリーダーがテキストを読み上げるようにする
- `ProcessingOverlayText` は `IsBusy` による Visibility トグル発生時にも通知する
- 回帰防止テストを追加する

## 3. ノンゴール

- 他のダイアログ（`StaffAuthDialog` 以外）への横展開
- 汎用的な LiveRegion 発火基盤（添付プロパティ / Behavior）の構築。今回は `OperationLogDialog` 単一ダイアログのバグ修正にスコープを絞る（YAGNI）
- スクリーンリーダー実機での自動読み上げ検証（単体テストで不可能なため、別途手動確認）

## 4. 設計方針

### 4.1 アーキテクチャ

**`OperationLogDialog.xaml.cs`（コードビハインド）で `DataContext`（= `OperationLogSearchViewModel`）の `INotifyPropertyChanged.PropertyChanged` を購読し、対象プロパティが変化したタイミングで対応する TextBlock に `RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)` を呼び出す。**

依存追加なし（WPF 標準 API のみ）、`OperationLogDialog.xaml.cs` 1 ファイルの変更で完結する最小手術的アプローチ。

### 4.2 購読プロパティと通知先 TextBlock の対応表

| ViewModel プロパティ | 通知先 TextBlock | 補足 |
|---|---|---|
| `PageInfo` | `PageInfoText` | 計算プロパティ。値変化時に `PropertyChanged` が発火する想定（実装中に確認） |
| `CurrentPage` | `CurrentPageNumberText` | Run 要素にバインド。`CurrentPage` または `TotalPages` のどちらか変化で通知 |
| `TotalPages` | `CurrentPageNumberText` | 同上 |
| `StatusMessage` | `StatusMessageText` | |
| `BusyMessage` | `ProcessingOverlayText` | テキスト変化時 |
| `IsBusy` | `ProcessingOverlayText` | `true` への遷移時に通知（Visibility 表示時のオンセット通知） |

### 4.3 実装コア

```csharp
public partial class OperationLogDialog : Window
{
    private INotifyPropertyChanged? _subscribedViewModel;

    public OperationLogDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 既存処理（DataGrid スクロール等）はそのまま

        if (DataContext is INotifyPropertyChanged vm)
        {
            _subscribedViewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(OperationLogSearchViewModel.PageInfo):
                RaiseLiveRegionChanged(PageInfoText);
                break;
            case nameof(OperationLogSearchViewModel.CurrentPage):
            case nameof(OperationLogSearchViewModel.TotalPages):
                RaiseLiveRegionChanged(CurrentPageNumberText);
                break;
            case nameof(OperationLogSearchViewModel.StatusMessage):
                RaiseLiveRegionChanged(StatusMessageText);
                break;
            case nameof(OperationLogSearchViewModel.BusyMessage):
                RaiseLiveRegionChanged(ProcessingOverlayText);
                break;
            case nameof(OperationLogSearchViewModel.IsBusy):
                if (sender is OperationLogSearchViewModel vm && vm.IsBusy)
                {
                    RaiseLiveRegionChanged(ProcessingOverlayText);
                }
                break;
        }
    }

    private static void RaiseLiveRegionChanged(UIElement element)
    {
        var peer = UIElementAutomationPeer.FromElement(element)
                   ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }
    }
}
```

### 4.4 メモリリーク対策

- `Window.Closed` で必ず `PropertyChanged -= OnViewModelPropertyChanged` を呼ぶ
- `OperationLogDialog` はモーダルダイアログとして開閉される想定で、`Closed` が確実に呼ばれる
- フィールド `_subscribedViewModel` を `null` クリアして二重解除を防止

### 4.5 PageInfo の発火確認

`PageInfo` は計算プロパティ（`OperationLogSearchViewModel.cs` 行 167-169 周辺）であり、依存プロパティ（`TotalCount` / `CurrentPage` / `PageSize`）の変化時に `OnPropertyChanged(nameof(PageInfo))` が明示的に呼ばれているか実装中に確認する。呼ばれていない場合は ViewModel 側にも修正を入れる（依存元の `[ObservableProperty]` のパーシャル `On<Name>Changed` から `OnPropertyChanged(nameof(PageInfo))` を発火）。

`CurrentPage` / `TotalPages` は `[ObservableProperty]` で自動発火されるため、本対応では新規発火は不要。

## 5. テスト戦略

### 5.1 静的解析テスト（必須、CI で実行）

`DialogAutomationPropertiesCoverageTests.cs` に新規アサーション 2 件追加:

1. **`OperationLogDialog.xaml.cs` に `RaiseAutomationEvent` が存在すること**
   ```csharp
   [Fact]
   public void OperationLogDialog_CodeBehindに_RaiseAutomationEventが存在すること()
   {
       var code = File.ReadAllText("...OperationLogDialog.xaml.cs");
       code.Should().Contain("RaiseAutomationEvent");
       code.Should().Contain("AutomationEvents.LiveRegionChanged");
   }
   ```

2. **`OperationLogDialog.xaml.cs` で `ViewModel.PropertyChanged` を購読していること**
   ```csharp
   [Fact]
   public void OperationLogDialog_ViewModelのPropertyChangedを購読していること()
   {
       var code = File.ReadAllText("...OperationLogDialog.xaml.cs");
       code.Should().MatchRegex(@"PropertyChanged\s*\+=");
       code.Should().MatchRegex(@"PropertyChanged\s*-=");  // 解除も必須
   }
   ```

### 5.2 ロジック単体テスト（必須、CI で実行）

**新規ファイル**: `ICCardManager/tests/ICCardManager.Tests/Views/Dialogs/OperationLogDialogLiveRegionTests.cs`

WPF Window を STA スレッドで実体化し、AutomationPeer から `LiveRegionChanged` イベントを `AutomationEvent` ハンドラで検証する。検証パターン:

| テストケース | Arrange | Act | Assert |
|---|---|---|---|
| `StatusMessage_変化時_StatusMessageTextに_LiveRegionChangedが発火すること` | VM をセット、ダイアログを `Show` し Loaded を待つ。AutomationPeer のイベント購読 | `vm.StatusMessage = "完了"` | StatusMessageText の AutomationPeer で LiveRegionChanged 発火 |
| `PageInfo_変化時_PageInfoTextに_LiveRegionChangedが発火すること` | 同上 | `vm.TotalCount = 100`（PageInfo に伝播） | PageInfoText で発火 |
| `CurrentPage_変化時_CurrentPageNumberTextに_LiveRegionChangedが発火すること` | 同上 | `vm.CurrentPage = 2` | CurrentPageNumberText で発火 |
| `IsBusy_trueへ変化時_ProcessingOverlayTextに_LiveRegionChangedが発火すること` | 同上 | `vm.IsBusy = true` | ProcessingOverlayText で発火 |
| `IsBusy_falseへ変化時_ProcessingOverlayTextへの通知は発火しないこと` | `IsBusy=true` から開始 | `vm.IsBusy = false` | ProcessingOverlayText で発火しない（オフセットは通知不要） |
| `Closed後_PropertyChanged通知でも_LiveRegionChangedが発火しないこと` | Window を Close 後 | `vm.StatusMessage = "..."` | 発火しない（メモリリーク・purgatory イベント防止） |

**実装ノート**:
- WPF UI スレッドが必要なため `[StaFact]` を使用（既存テストインフラに `Xunit.StaFact` が含まれているか確認、未導入なら追加）
- `Show()` ではなく `Loaded` イベントを `RaiseEvent` で手動発火させる方が CI フレンドリー（ウィンドウ表示が不要）
- AutomationPeer のイベント検証は `Automation.AddAutomationEventHandler` よりも、`UIElementAutomationPeer.FromElement(element)` が `null` でない時に手動で `RaiseAutomationEvent` 呼び出しの **副作用検証**（例: `AutomationEventTracker` という小さなテスト用クラスでイベント発火回数をカウント）でも可
- 上記が複雑になりすぎる場合のフォールバック: **「コードビハインドの `OnViewModelPropertyChanged` メソッドを `internal` にして直接呼び出し、TextBlock の AutomationPeer.RaiseAutomationEvent が呼ばれることをモック / インストルメンテーションで確認」**（メソッドを直接単体テストする方式）

### 5.3 UI 統合テスト（任意、別 PR 候補）

`StaffAuthDialogLiveRegionTests` と同パターンで `ICCardManager.UITests/Tests/OperationLogDialogLiveRegionTests.cs` を新設して FlaUI 5.0 で検証することも可能だが、本 PR では **5.1 静的解析 + 5.2 ロジック単体テスト** で必要なカバレッジを確保する。UI 統合テストは別 Issue で扱う。

### 5.4 単体テスト不可能な範囲（ユーザー側手動確認）

スクリーンリーダーでの実機読み上げ確認は単体テストで不可能なため、以下の手順をユーザー側でお願いする:

| 確認項目 | 操作 | 期待される読み上げ（NVDA/Narrator） |
|---|---|---|
| ページ送り通知 | 操作履歴を 21 件以上検索 → 次ページボタン押下 | 「2 / 3」「2 ページ目」（PageInfo / CurrentPage 双方の Polite 通知） |
| ステータスメッセージ | 検索ボタン押下 → 検索完了 | 「20 件取得しました」等の StatusMessage |
| 処理中オーバーレイ | 重い検索条件で検索実行 | 「データ取得中...」等の BusyMessage |
| 起動時に何も読まれないこと | ダイアログを開いた直後 | 不要な誤発火がない（初期 PropertyChanged のスパムが無いこと） |

## 6. リスクと対策

| リスク | 対策 |
|---|---|
| 初回 `Loaded` 時に PropertyChanged 連発で読み上げスパム | `Loaded` で購読を始める時点で初期値は既に View に表示済み。`Loaded` 後の変化のみを対象にすればスパムは起きない。実機確認で再確認 |
| `IsBusy` と `BusyMessage` の同時変化で重複通知 | `IsBusy=true` への遷移 + `BusyMessage` 変化が同フレームで発生した場合、2 連続通知になりうる。許容（NVDA は読み上げ中の同一テキスト連続発火を抑止する） |
| `Closed` 前に WindowsForm 相当の強制終了が発生 | `_subscribedViewModel` の存在チェックで二重解除を防ぐ。GC で ViewModel が回収されれば購読も結果的に切れる |
| `PageInfo` の `PropertyChanged` が発火していない場合 | 実装中に確認し、必要なら ViewModel に `OnPropertyChanged(nameof(PageInfo))` を追加（破壊的変更ではない） |

## 7. 受け入れ条件

- [ ] `OperationLogDialog.xaml.cs` に `ViewModel.PropertyChanged` 購読と `RaiseAutomationEvent(LiveRegionChanged)` 呼び出しが追加されている
- [ ] `Window.Closed` で `PropertyChanged` 購読が解除されている
- [ ] 5.1 静的解析テスト 2 件が追加され Pass
- [ ] 5.2 ロジック単体テスト 6 件（最低限）が追加され Pass
- [ ] `dotnet test` 全件 Pass、ビルド警告ゼロ
- [ ] ユーザー手動確認（5.4）の実施手順が PR 説明に明記
- [ ] CHANGELOG の Unreleased セクションに本修正を追記

## 8. 関連

- Issue #1509: `StaffAuthDialog` の同根問題を解決した先行 PR
- Issue #1468: 元のアクセシビリティ拡充（`LiveSetting` 付与）
- PR #1530（Issue #1501）: 動的 TextBlock の `x:Name` 付与とテスト追加。本 Issue で発覚した「読み上げ未発火」は PR #1530 起因ではなく、その手動確認過程で判明した既存バグ
