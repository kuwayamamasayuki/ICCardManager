# Issue #1429 新規職員登録ダイアログの氏名欄自動フォーカス 設計書

- 作成日: 2026-04-27
- 対象 Issue: #1429
- 対象ブランチ: `fix/issue-1429-staff-register-focus`

## 1. 背景

F2 → 職員管理 → 「新規登録」 → 職員証タッチ、で IDm が取り込まれた直後、氏名入力欄に自動フォーカスが当たらず、ユーザーが氏名欄をマウスでクリックしないと文字入力できない。マニュアル想定（タッチ → 氏名入力の連続動作）と実装が乖離しており、連続登録時の作業効率とアクセシビリティが低下している。

PR #1427（マニュアル用スクリーンショット撮影スクリプト）の実機検証中にユーザーが観測。

## 2. 原因

`StaffManageDialog.xaml` に氏名 TextBox への明示的なフォーカス指定（`x:Name` も `FocusManager.FocusedElement` も）がない。`StaffManageViewModel.StartNewStaffWithIdmAsync` が `IsWaitingForCard = false` を立てた後も、View 側にフォーカス指示が伝わらないため、フォーカスは元の位置に留まる。

## 3. 修正方針

Issue 本文の「案 1（推奨・MVVM 原則準拠）」を採用。ViewModel が `RequestNameFocus` イベントを発火し、Dialog コードビハインドが購読して氏名 TextBox にフォーカスを当てる。

### 採用しなかった案

- **案 2（XAML DataTrigger + FocusManager.FocusedElement）**: WPF の Setter での Focus 制御は信頼性に欠けるため不採用（Issue 内に同記述あり）。
- **Messenger ベース（`WeakReferenceMessenger.Send`）**: 既存 `Common/Messages/` には 1 件しか定義がなくパターン化が薄い。Dialog ↔ ViewModel の 1:1 通信に対し過剰。直接イベントの方が結合度・行数ともに小さい。

## 4. 変更ファイル

### 4.1 `ICCardManager/src/ICCardManager/ViewModels/StaffManageViewModel.cs`

- `public event EventHandler? RequestNameFocus;` を追加。
- `StartNewStaffWithIdmAsync` の未登録職員証分岐末尾（`IsWaitingForCard = false;` の直後、return 直前）で `RequestNameFocus?.Invoke(this, EventArgs.Empty);` を呼ぶ。
- 既登録／削除済み分岐は `return true;` でダイアログを閉じる前提のため発火させない。

### 4.2 `ICCardManager/src/ICCardManager/Views/Dialogs/StaffManageDialog.xaml`

- 氏名 TextBox に `x:Name="NameTextBox"` を追加。

### 4.3 `ICCardManager/src/ICCardManager/Views/Dialogs/StaffManageDialog.xaml.cs`

- コンストラクタで `_viewModel.RequestNameFocus += ViewModel_RequestNameFocus;` を購読。
- `StaffManageDialog_Closed` で `-=` してリーク防止。
- `ViewModel_RequestNameFocus` で以下を実行：
  ```csharp
  Dispatcher.BeginInvoke(
      new Action(() =>
      {
          NameTextBox.Focus();
          Keyboard.Focus(NameTextBox);
      }),
      DispatcherPriority.Input);
  ```
  - `BeginInvoke` + `Input` 優先度: レイアウト更新と入力処理が落ち着いてからフォーカスを投入し、描画と競合させない。
  - `Focus()` + `Keyboard.Focus()` 二段呼び: 論理フォーカスとキーボードフォーカスを同期させ、即タイプ可能な状態にする。

## 5. テスト

### 5.1 単体テスト

`StaffManageViewModelTests` に Issue #1429 region を追加：

| ケース | 入力 | 期待 |
|--------|------|------|
| 未登録職員証 | `GetByIdmAsync(idm, true)` が null | `RequestNameFocus` が 1 回発火、`shouldClose==false` |
| 既登録職員証 | `GetByIdmAsync` が Staff を返す（IsDeleted=false） | `RequestNameFocus` は発火しない、`shouldClose==true` |

実フォーカス確定は WPF Dispatcher 動作が必要なため、ViewModel 単体テストでは発火タイミングのみ検証する。

### 5.2 手動テスト

F2 → 職員管理 → 「新規登録」 → 職員証タッチ → 氏名欄に Caret が点滅し、マウス操作なしで氏名を入力できること。

## 6. 横展開チェック

Issue 本文に「同様のパターンが他のタッチ → 入力ダイアログ（カード登録など）にもある可能性あり」との指摘あり。本 PR ではスコープを職員登録に絞り、カード登録ダイアログは別 Issue で対応するか、フォローアップで確認する。

## 7. リスク

- フォーカス遷移が他のフォーカス指定（例：`FocusManager.FocusedElement`）と競合する可能性。`StaffManageDialog.xaml` 全体に既存の FocusedElement 指定がないことは確認済み。
- イベント購読の解除漏れ → `Closed` ハンドラで `-=` を実装しリークを防止。
