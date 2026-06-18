# Issue #1428: PaSoRi 未接続時のステータスバー誤表示修正 設計書

- 起票: 2026-04-27
- 関連 Issue: [#1428](../../../) bug: PaSoRi 未接続のままアプリ起動時にステータスバーが「リーダー: 接続中」のままになる
- 発見経緯: PR #1427 マニュアル用スクリーンショット収集スクリプト実機検証中

## 1. 問題

PaSoRi カードリーダーが物理的に未接続の状態でアプリを起動すると、ステータスバー右下に **「リーダー: 接続中」** が表示される。期待動作は **「リーダー: 切断」** の表示と再接続ボタンの提示。

## 2. 真の原因

Issue 本文の「`InitializeAsync` 失敗 → イベント発火しない」という推定は不正確。実際には次の経路で `Connected` 状態に遷移する：

1. `FelicaCardReader.StartReadingAsync()` が `CheckFelicaLibAvailable()` を呼ぶ
2. `CheckFelicaLibAvailable()` の現実装（`Infrastructure/CardReader/FelicaCardReader.cs:428-453`）は `DllNotFoundException` のみを切断扱いとし、それ以外の例外（PaSoRi 未接続由来を含む）は **すべて `true`** を返す
3. 結果、PaSoRi 未接続でも `StartReadingAsync` が成功扱いとなり `SetConnectionState(Connected)` が発火
4. 10 秒間隔のヘルスチェック (`OnHealthCheckTimerElapsed`) も同じ判定ロジックを使うため、未接続のままでも `Connected` 表示が継続

つまり「DLL の存在チェック」と「PaSoRi の物理接続チェック」が混同されていることが本質的問題。

## 3. 修正方針（案 C: ロジック層 + 表示層の二重防御）

### 3.1 ロジック層（Infrastructure/CardReader/FelicaCardReader.cs）

`CheckFelicaLibAvailable()` を **2 つの責務に分割**：

| 新メソッド | 役割 | 戻り値 |
|---|---|---|
| `IsFelicaLibLoaded()` | felicalib.dll をロードできるか | `DllNotFoundException` 時のみ false |
| `IsReaderConnected()` | PaSoRi が物理接続されているか | `DllNotFoundException` または「リーダー未接続」相当の例外パターン時 false |

#### 3.1.1 「リーダー未接続」例外パターンの判定

`FelicaUtility.GetIDm` が PaSoRi 未接続時に投げる例外を、メッセージのキーワードで判定する。判定キーワード（既知パターン、保守的に大文字小文字無視）：

- `"pasori"`（リーダーデバイス名を含むメッセージ）
- `"open"`（`pasori_open` 失敗）
- `"device"`（デバイス検出失敗）
- `"reader"`（リーダー未接続）

これらのいずれも含まれない例外は「カードなし」と判定し、リーダー接続中扱いとする。

判定ロジックは `IsReaderUnavailableException(Exception)` という静的ヘルパーに切り出し、単体テスト可能にする。

#### 3.1.2 例外メッセージ依存の脆さへの保険

felicalib の例外メッセージは将来変わる可能性がある。そのため：

- 判定誤りで「カードなし」が「リーダー未接続」と誤分類された場合 → ヘルスチェックで自然回復する経路があるため致命的でない
- 「リーダー未接続」が「カードなし」と誤分類された場合 → 表示層（3.2）のフォールバックが効くため、最低限「リーダー: 接続中」誤表示は避けられる

ロジック判定の脆さを表示層が補完する二重防御構成。

#### 3.1.3 `StartReadingAsync` の修正

```
if (!IsFelicaLibLoaded()) { /* DLL なし: 既存と同じ Disconnected */ throw ... }
if (!IsReaderConnected()) {
    SetConnectionStateForceNotify(CardReaderConnectionState.Disconnected,
        "PaSoRi が見つかりません。USB 接続を確認してください。");
    return; // タイマー開始しない
}
StartPollingTimer();
StartHealthCheckTimer();
SetConnectionStateForceNotify(CardReaderConnectionState.Connected);
```

#### 3.1.4 強制通知メソッドの追加

`SetConnectionState(state, msg)` は現状「同じ状態なら早期 return」する。初期値 `Disconnected` のまま `Disconnected` を設定しても通知されない問題があるため、初期化時の最初の確定状態は確実に通知する：

```csharp
private void SetConnectionStateForceNotify(CardReaderConnectionState state, string message = null)
{
    _connectionState = state;
    _logger.LogDebug(...);
    ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state, message));
}
```

`StartReadingAsync` の初期確定通知でのみ使用。`OnHealthCheckTimerElapsed` の差分通知は既存 `SetConnectionState` を使い続ける。

### 3.2 表示層（Views/MainWindow.xaml）

ステータスバーの **デフォルト Setter を「切断」状態に反転** する。`Connected` を明示 DataTrigger 化：

#### 3.2.1 アイコン TextBlock (`x:Name="ConnectionIcon"`)

| 現状 (デフォルト) | 修正後 (デフォルト) |
|---|---|
| Text="🔗" Foreground=Success ToolTip="接続中" | Text="🔌" Foreground=Error ToolTip="切断" |

DataTrigger:
- `Disconnected` → 削除（デフォルトと一致するため不要）
- `Connected` → 新設（Text="🔗" Foreground=Success ToolTip="接続中"）
- `Reconnecting` → 既存維持

#### 3.2.2 接続状態テキスト TextBlock

| 現状 (デフォルト) | 修正後 (デフォルト) |
|---|---|
| Text="リーダー: 接続中" Foreground=Success | Text="リーダー: 切断" Foreground=Error |

DataTrigger:
- `Disconnected` → 削除（デフォルトと一致するため不要）
- `Connected` → 新設（Text="リーダー: 接続中" Foreground=Success）
- `Reconnecting` → 既存維持

#### 3.2.3 再接続ボタン

現状デフォルト `Visibility="Collapsed"` + `Disconnected` トリガで `Visible`。**これは反転しない**（Connected が通常状態である意味は維持。Disconnected トリガを残す）。

理由: ボタンの可視性はデフォルト「非表示」が安全（誤操作防止）。表示反転と異なる方向性で OK。

### 3.3 互換性・副作用

- 公開 API 変更なし（インターフェース `ICardReader` も維持）
- 既存テストへの影響: `FelicaCardReader` の単体テストは存在するか要確認、無ければ `IsReaderUnavailableException` のテストのみ追加
- `MainViewModel` の動作は変わらない（ViewModel から見れば「`Connected` イベントが来なくなる代わりに `Disconnected` イベントが来る」だけ）

## 4. テスト戦略

### 4.1 追加する単体テスト

#### `FelicaCardReaderHelpersTests.cs`（新規）

`IsReaderUnavailableException(Exception)` の判定ロジックを純粋関数として抽出してテスト：

| ケース | 入力例外 | 期待 |
|---|---|---|
| DllNotFoundException | `new DllNotFoundException("felicalib.dll")` | true |
| PaSoRi 未接続 (pasori) | `new Exception("Failed to open PaSoRi")` | true |
| open 失敗 | `new Exception("pasori_open failed")` | true |
| device エラー | `new Exception("Device not found")` | true |
| reader エラー | `new Exception("Reader not connected")` | true |
| カードなし (タイムアウト) | `new Exception("Polling timeout")` | false |
| 大文字小文字混在 | `new Exception("PASORI not available")` | true |
| null Message | `new Exception()` | false（DllNotFoundException 以外は接続中扱い） |

### 4.2 修正可能なら追加する統合テスト

XAML の DataTrigger 評価は単体テストで再現困難。ユーザー側で実機確認が必要：

- PaSoRi 未接続で起動 → ステータスバーが「🔌 リーダー: 切断」赤文字、再接続ボタン表示
- PaSoRi 接続済みで起動 → ステータスバーが「🔗 リーダー: 接続中」緑文字
- 起動後に PaSoRi を抜く → ヘルスチェック (10 秒以内) で「リーダー: 切断」に遷移

### 4.3 既存テスト修正の必要性

`FelicaCardReader` を直接 mock している既存テストがあるか調査。あれば `CheckFelicaLibAvailable` のシグネチャ変更による影響を確認し、必要なら更新。

## 5. ドキュメント更新

- `docs/manual/管理者マニュアル.md` §9.3「カードリーダーが見つからない」相当のセクションがあれば、ステータスバー表示と再接続ボタンの対応を追記（要確認）
- `CHANGELOG.md` の Unreleased セクションに修正項目を追加

## 6. リスクと緩和

| リスク | 緩和策 |
|---|---|
| felicalib の例外メッセージが将来変わる | 表示層フォールバックで最悪「Connected」誤表示は防止 |
| 「カードなし」例外を「リーダー未接続」と誤判定 | ヘルスチェックで自然回復、ログに記録 |
| 既存テストへの影響 | テストランで影響範囲を確認 |

## 7. 受け入れ基準

- [ ] PaSoRi 未接続で起動時、ステータスバーが「リーダー: 切断」を表示する
- [ ] PaSoRi 未接続で起動時、再接続ボタンが表示される
- [ ] PaSoRi 接続済みで起動時、ステータスバーが「リーダー: 接続中」を表示する（既存挙動維持）
- [ ] 起動後に PaSoRi を抜くと 10 秒以内に「リーダー: 切断」に遷移する（既存挙動維持）
- [ ] `IsReaderUnavailableException` の単体テストが全て pass
- [ ] ビルド警告ゼロを維持
