# Issue #1286: SharedModeMonitor の IDisposable 実装 設計書

作成日: 2026-04-19
対象 Issue: [#1286](https://github.com/kuwayamamasayuki/ICCardManager/issues/1286)
対象ファイル:
- `ICCardManager/src/ICCardManager/Services/SharedModeMonitor.cs` (206 行)
- `ICCardManager/src/ICCardManager/App.xaml.cs` (819 行、該当は L720-729 `OnExit`)

## 背景と問題

`SharedModeMonitor` は `Services/SharedModeMonitor.cs:14` に定義された Singleton サービスで、内部に 2 つの `ITimer`（30 秒間隔のヘルスチェック + 1 秒間隔の同期表示更新）を持つ。

- `App.xaml.cs:275` で `services.AddSingleton<SharedModeMonitor>()` として登録
- `OnExit` (L720-729) で `ServiceProvider.Dispose()` を呼んでいるが、**SharedModeMonitor が `IDisposable` を実装していない**ため DI コンテナから破棄通知が届かない
- 結果、アプリ終了時もタイマーとイベントハンドラが保持される可能性（プロセス終了で解放されるものの、潜在的リソースリークと終了フェーズでの不安定動作の原因）

現状 `Stop()` メソッドは既に適切な cleanup ロジック（タイマー停止 + イベント解除 + null 代入）を実装しているため、`IDisposable` 実装は既存ロジックへの薄いラッパーで済む。

Issue 原文は `CancellationTokenSource` にも言及しているが、実装には存在しない（Timer のみ）。

## スコープ

### 含む

1. `SharedModeMonitor` に `IDisposable` を実装
2. `Dispose()` が冪等（複数回呼び出し安全）
3. Dispose 後の `Start()` で `ObjectDisposedException` を投げる
4. `App.xaml.cs` `OnExit` で明示的に `SharedModeMonitor.Dispose()` を呼ぶ（ServiceProvider.Dispose との二重呼び出しは冪等性で吸収）
5. 既存 `SharedModeMonitorTests` / `SharedModeMonitorRecoveryTests` への追加テスト（~5 件）

### 含まない

- `ITimer` インターフェース自体の `IDisposable` 化
- 他の Singleton サービス（`CardLockManager`, `DashboardService` 等）の Dispose 監査
- `SharedModeMonitor` の責務分割や refactor
- ログメッセージ・動作仕様の変更

## 設計

### Dispose パターン

標準的な .NET の Dispose パターンを採用。`SharedModeMonitor` は sealed ではないが、継承される想定がないので最小限の実装とする。

```csharp
public class SharedModeMonitor : IDisposable
{
    private bool _disposed;

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SharedModeMonitor));
        // 既存の Start 実装（Stop 呼び出し + 新規タイマー作成）
    }

    public void Stop() { /* 既存のまま */ }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
```

### Dispose 後の動作契約

- `Dispose()` 自身: 冪等（複数回呼べる）
- `Start()`: `ObjectDisposedException` をスロー
- `Stop()`: no-op（既存ロジックで null チェックしているのでそのまま呼べる）
- `RecordRefresh()` / `UpdateSyncDisplayText()` / `ExecuteHealthCheckAsync()`: 破棄後も壊れない（event 発火は subscribers がいなくなれば自然に no-op）。ObjectDisposedException は投げない（タイマー Tick 経由で呼ばれる可能性があり、シャットダウン中に例外を投げるとトラブル源）

### App.xaml.cs の変更

`OnExit` 内で明示的に `SharedModeMonitor` を Dispose する。ServiceProvider.Dispose も引き続き呼ぶので二重 Dispose になるが、冪等性で吸収される。

```csharp
protected override void OnExit(ExitEventArgs e)
{
    // Issue #1286: SharedModeMonitor を明示的に Dispose
    (ServiceProvider as IServiceProvider)?.GetService<SharedModeMonitor>()?.Dispose();

    if (ServiceProvider is IDisposable disposable)
    {
        disposable.Dispose();
    }
    base.OnExit(e);
}
```

明示呼び出しの利点: リソース解放順序を意図的に制御できる（タイマーを先に止めてから他の依存を解放）。

### テスト追加

既存 `ICCardManager/tests/ICCardManager.Tests/Services/SharedModeMonitorTests.cs` に以下 5 件を追加:

1. `Dispose_StopsTimers`: Dispose 後に fake timer が tick しても event が発火しない
2. `Dispose_CalledTwice_DoesNotThrow`: 2 回目の Dispose が例外を投げない
3. `Start_AfterDispose_ThrowsObjectDisposedException`: Start 呼び出しで例外
4. `Stop_AfterDispose_DoesNotThrow`: Stop は冪等のまま保つ
5. `Dispose_WithoutStart_DoesNotThrow`: Start 未呼び出しでも Dispose が例外を投げない

`FakeTimer` / `FakeTimerFactory` は既存テストで使用されているはず（要確認、必要なら既存テストの setup パターンに合わせる）。

## 期待される効果

- アプリ終了時のタイマー確実停止 → リソースリーク予防
- 共有モードでネットワーク切断状態のまま終了するケースでも確実に停止（従来はプロセス終了で強制解放されていたが、タイマー Tick 中のハンドラが中途状態になるリスクあり）
- `IDisposable` 契約が明示されたことで、将来 Timer を CancellationTokenSource に置換する場合も追加変更なしに対応可能

## リスクと対策

| リスク | 対策 |
|-------|-----|
| `Dispose` と `Stop` が機能重複し、どちらを呼ぶべきか混乱 | XML コメントで「通常は `Stop`、終了時のみ `Dispose`」と明示 |
| Dispose 中に別スレッドで Tick が発火して race | 既存の `Stop` が event unsubscribe しているので、Dispose 完了後に Tick が subscribers を持たない。仮に Tick 開始済みでも event invoke 時点で subscribers null になる |
| ServiceProvider.Dispose による二重 Dispose | 冪等実装で吸収 |
| `ObjectDisposedException` を投げるべきメソッドの選定ミス | 使用側から触れるのは `Start` のみ（タイマー Tick は内部呼び出しのみ）。`Start` のみ guard する |

## 非対象（別 Issue 候補）

- `CardLockManager` の IDisposable 実装監査
- `DashboardService` の定期タイマー Dispose
- `ITimer` の IDisposable 化（タイマー抽象の再設計）
