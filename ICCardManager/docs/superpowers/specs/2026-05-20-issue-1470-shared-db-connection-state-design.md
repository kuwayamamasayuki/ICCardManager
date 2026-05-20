# Issue #1470: 共有モード時のDB接続状態（Connected/Reconnecting/Disconnected）をステータスバーに表示

- 起票日: 2026-05-20
- 関連 Issue: #1470
- 関連 Issue（参考パターン）: #1428（カードリーダー接続状態の fail-safe 表示）、#1493（共有モードのヘルスチェック間隔）

## 背景

`MainWindow.xaml` のステータスバーには `IsSharedMode`（共有モードか否か）と `LastRefreshText`（最終同期時刻）の表示があるが、ネットワーク切断中の状態は `WarningMessages` 経由のヘッダー警告でしか伝わらない。`SharedModeMonitor` は 15 秒ごとに DB 接続のヘルスチェックを実行しているが、その結果に基づく「⚠ 切断 → 🔄 再接続中 → 🔗 接続中」の状態遷移を表すステータスバー表示が存在しない。

カードリーダー側は Issue #1428 で `CardReaderConnectionState` enum と DataTrigger による fail-safe 表示が実装済み。共有 DB 接続でも同等の表示を提供し、運用者が共有フォルダ接続の現況を一目で把握できるようにする。

## 目的

1. `Connected` / `Reconnecting` / `Disconnected` の 3 状態を `MainViewModel` に持たせ、ステータスバーで状態に応じたアイコン・色・テキスト・ToolTip を切り替える（色のみ依存しない 4 要素原則）。
2. `Disconnected` への遷移時と `Connected` への復帰時にトースト通知を発火する（連続発火は抑止）。
3. 状態遷移を単体テストで検証する。

## 非目的

- 共有 DB の能動的再接続リトライ（例えば 1 秒間隔で 10 回試行）は実装しない。再接続中の表示は「次のヘルスチェック実行中（最大 15 秒）」のものだけとする。
- ヘルスチェック間隔の変更は行わない（Issue #1493 で確定した 15 秒を維持）。
- カードリーダー側 (`CardReaderConnectionState`) との enum 共通化はしない。両者は意味的に近いが、ライフサイクルとリトライポリシーが異なるため別 enum とする。

## 設計

### 1. 新規 enum: `SharedDbConnectionState`

`ICCardManager/src/ICCardManager/Services/SharedDbConnectionState.cs` に新設。

```csharp
namespace ICCardManager.Services
{
    /// <summary>
    /// 共有モード時の DB 接続状態。
    /// </summary>
    public enum SharedDbConnectionState
    {
        /// <summary>直前のヘルスチェックが成功</summary>
        Connected,

        /// <summary>直前のヘルスチェックが失敗し、次のヘルスチェックを実行中</summary>
        Reconnecting,

        /// <summary>ヘルスチェック失敗が確定（次のチェックまで待機中）</summary>
        Disconnected
    }
}
```

`Connected` を 0 にして既定値とし、enum の `default` で fail-safe 寄りではなく楽観値になることを許容する（理由: 初期表示時にまだ一度もヘルスチェックが走っていない時点で `Disconnected` 警告を出すとローカルモード時の起動直後にも誤発火するため）。実際の表示はローカルモード時は StatusBarItem ごと非表示（`IsSharedMode` Visibility 連動）になるため安全。

### 2. `SharedModeMonitor` 拡張

#### 新規イベント

```csharp
public event EventHandler<SharedDbConnectionStateChangedEventArgs> ConnectionStateChanged;

public class SharedDbConnectionStateChangedEventArgs : EventArgs
{
    public SharedDbConnectionState OldState { get; }
    public SharedDbConnectionState NewState { get; }
    public SharedDbConnectionStateChangedEventArgs(SharedDbConnectionState oldState, SharedDbConnectionState newState)
    {
        OldState = oldState;
        NewState = newState;
    }
}
```

既存の `HealthCheckCompleted` イベントは後方互換のため残す。

#### 状態遷移ロジック

`ExecuteHealthCheckAsync` 内で以下のように状態を管理する:

```
private SharedDbConnectionState _currentState = SharedDbConnectionState.Connected;

internal async Task<bool> ExecuteHealthCheckAsync()
{
    if (_isHealthCheckRunning) return false;
    _isHealthCheckRunning = true;
    try
    {
        // 前回 Disconnected だった場合、チェック開始時に Reconnecting へ遷移
        if (_currentState == SharedDbConnectionState.Disconnected)
        {
            TransitionTo(SharedDbConnectionState.Reconnecting);
        }

        var isConnected = await CheckConnectionAsync().ConfigureAwait(false);

        TransitionTo(isConnected
            ? SharedDbConnectionState.Connected
            : SharedDbConnectionState.Disconnected);

        HealthCheckCompleted?.Invoke(this, new DatabaseHealthEventArgs(isConnected));
        return true;
    }
    finally
    {
        _isHealthCheckRunning = false;
    }
}

private void TransitionTo(SharedDbConnectionState newState)
{
    if (_currentState == newState) return;
    var oldState = _currentState;
    _currentState = newState;
    ConnectionStateChanged?.Invoke(this, new SharedDbConnectionStateChangedEventArgs(oldState, newState));
}
```

`Connected → Disconnected → Reconnecting → Connected` または `Connected → Disconnected → Reconnecting → Disconnected` のサイクルになる。

#### 状態取得 API

```csharp
public SharedDbConnectionState CurrentConnectionState => _currentState;
```

### 3. `MainViewModel` 更新

#### プロパティ追加

```csharp
[ObservableProperty]
private SharedDbConnectionState _sharedDbConnectionState = SharedDbConnectionState.Connected;
```

#### イベントハンドラ追加

```csharp
private void OnSharedDbConnectionStateChanged(object sender, SharedDbConnectionStateChangedEventArgs e)
{
    _dispatcherService.InvokeAsync(() =>
    {
        SharedDbConnectionState = e.NewState;

        // Toast: 遷移エッジでのみ発火（連続抑止）
        if (e.NewState == SharedDbConnectionState.Disconnected
            && e.OldState != SharedDbConnectionState.Disconnected
            && e.OldState != SharedDbConnectionState.Reconnecting)
        {
            // 初回切断検知時のみ
            _toastNotificationService.ShowWarning(
                "共有DB接続が切断されました",
                "ネットワーク接続を確認してください。15秒ごとに自動で再接続を試行します。");
        }
        else if (e.NewState == SharedDbConnectionState.Connected
                 && (e.OldState == SharedDbConnectionState.Disconnected
                     || e.OldState == SharedDbConnectionState.Reconnecting))
        {
            // 復帰時
            _toastNotificationService.ShowInfo(
                "共有DB接続が復旧しました",
                "データの同期を再開しました。");
        }
    });
}
```

`Reconnecting → Disconnected`（再試行失敗）では Toast を出さない。これは「点検したらまだ切断」状態であり、初回切断時の通知が既に出ているため。

#### コンストラクタ登録

`_sharedModeMonitor.ConnectionStateChanged += OnSharedDbConnectionStateChanged;` を追加。

#### 既存 `UpdateConnectionWarning` との関係

`WarningMessages` への追加/削除ロジックは `UpdateConnectionWarning(bool isConnected)` で既に行っているため変更不要。`OnSharedModeHealthCheckCompleted` 経由で従来どおり呼ばれる。

### 4. `MainWindow.xaml` の StatusBarItem 更新

`983` 行付近の「共有モード」StatusBarItem を以下のように書き換える:

```xml
<StatusBarItem AutomationProperties.Name="共有モード接続状態"
               Visibility="{Binding IsSharedMode, Converter={StaticResource BoolToVisibilityConverter}}">
    <TextBlock FontWeight="Bold">
        <TextBlock.Style>
            <Style TargetType="TextBlock">
                <!-- 既定: Connected -->
                <Setter Property="Text" Value="🔗 共有モード"/>
                <Setter Property="Foreground" Value="{DynamicResource PrimaryBrush}"/>
                <Setter Property="ToolTip" Value="共有フォルダ上のデータベースに接続中です。"/>
                <Style.Triggers>
                    <DataTrigger Binding="{Binding SharedDbConnectionState}"
                                 Value="{x:Static services:SharedDbConnectionState.Reconnecting}">
                        <Setter Property="Text" Value="🔄 共有モード（再接続中）"/>
                        <Setter Property="Foreground" Value="{DynamicResource WarningActionBrush}"/>
                        <Setter Property="ToolTip" Value="共有DBへの接続を再試行中です。"/>
                    </DataTrigger>
                    <DataTrigger Binding="{Binding SharedDbConnectionState}"
                                 Value="{x:Static services:SharedDbConnectionState.Disconnected}">
                        <Setter Property="Text" Value="⚠ 共有モード（切断中）"/>
                        <Setter Property="Foreground" Value="{DynamicResource ErrorBorderBrush}"/>
                        <Setter Property="ToolTip" Value="共有DBへの接続が切断されています。ネットワーク接続を確認してください。"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </TextBlock.Style>
    </TextBlock>
</StatusBarItem>
```

XAML ヘッダーに `xmlns:services="clr-namespace:ICCardManager.Services"` を追加。

### 5. 既存テストへの影響

`SharedModeMonitorTests` の `ExecuteHealthCheckAsync` 系テストは既存のシグネチャを変えないため互換。新規テストを追加する形で対応。

### 6. テスト計画

#### `SharedModeMonitorTests` に追加

- `ExecuteHealthCheckAsync_FirstFailure_TransitionsToDisconnected`: Connected 状態で失敗 → Disconnected へ遷移し ConnectionStateChanged 発火
- `ExecuteHealthCheckAsync_AfterFailure_TransitionsThroughReconnecting`: Disconnected 状態で実行 → Reconnecting 経由で結果状態へ遷移（2回イベント発火）
- `ExecuteHealthCheckAsync_RepeatedFailure_NoExtraEvents`: 連続失敗時、Reconnecting → Disconnected の遷移は発火するが、Disconnected → Disconnected は発火しない
- `ExecuteHealthCheckAsync_Recovery_FiresConnectedOnce`: Disconnected → Reconnecting → Connected の遷移シーケンス検証

#### `MainViewModel*Tests`（新規 `MainViewModelSharedDbStateTests.cs`）

- `SharedDbConnectionStateChanged_ToDisconnected_ShowsWarningToastOnce`: Disconnected 遷移時に ShowWarning が1回呼ばれる
- `SharedDbConnectionStateChanged_ReconnectingToDisconnected_NoToast`: 再接続失敗（Reconnecting → Disconnected）では Toast を発火しない
- `SharedDbConnectionStateChanged_ToConnectedAfterFailure_ShowsInfoToast`: 復帰時に ShowInfo が呼ばれる
- `SharedDbConnectionStateChanged_StaysConnected_NoToast`: Connected 維持では Toast 不要

### 7. ドキュメント更新

- `docs/design/03_画面設計書.md`: ステータスバー仕様セクションに 3 状態表示を追記
- `CHANGELOG.md`: `Unreleased` セクションに項目追加

## 後方互換性

- 既存の `HealthCheckCompleted` イベントとハンドラは維持。
- `WarningMessages` のヘッダー警告挙動は変更なし。
- ローカルモード時はステータスバー上の共有モード関連 StatusBarItem が `IsSharedMode` バインディングで非表示のため、変更の影響なし。

## リスクと緩和

- **リスク**: enum の `default` 値 `Connected` がローカルモードでも参照される。
  - **緩和**: ローカルモード時は UI が StatusBarItem を非表示にするため可視化されない。ViewModel テストでもローカルモードを想定したケースを含める。
- **リスク**: Toast の連続抑止条件 (`e.OldState != Disconnected && e.OldState != Reconnecting`) を間違えると初回切断時の Toast が出ない/連続発火する。
  - **緩和**: 4 種類の遷移パターン全てを単体テストで検証。

## 参考

- Issue #1428: カードリーダー接続状態 fail-safe 表示
- Issue #1493: 共有モードヘルスチェック間隔の整合
- `ICardReader.cs`: CardReaderConnectionState enum の先行実装
- `MainWindow.xaml:912-981`: カードリーダーステータスバー DataTrigger パターン
