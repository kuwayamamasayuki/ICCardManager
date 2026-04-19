# Issue #1286: SharedModeMonitor IDisposable 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `SharedModeMonitor` に `IDisposable` を実装し、`App.OnExit` で明示的に `Dispose()` を呼ぶ。アプリ終了時の確実なタイマー停止とリソース解放を担保する。

**Architecture:** 既存 `Stop()` メソッドが既にタイマー停止とイベント解除を実装しているため、`Dispose()` は `Stop()` を呼び冪等フラグを立てる薄いラッパー。`Start()` は破棄後呼び出しに対し `ObjectDisposedException` を投げる。`App.xaml.cs OnExit` では ServiceProvider.Dispose 前に明示的に Dispose を呼ぶ（二重 Dispose は冪等で吸収）。

**Tech Stack:** C# 10 / .NET Framework 4.8 / WPF / xUnit / FluentAssertions / Moq

---

## 事前確認

- ブランチ: `fix/issue-1286-shared-mode-monitor-dispose`（main から分岐、spec commit 済み）
- 対象ファイル:
  - `ICCardManager/src/ICCardManager/Services/SharedModeMonitor.cs` (206 行)
  - `ICCardManager/src/ICCardManager/App.xaml.cs` (L720-729 の `OnExit`)
  - `ICCardManager/tests/ICCardManager.Tests/Services/SharedModeMonitorTests.cs`（既存）
- 既存テスト: `SharedModeMonitorTests` + `SharedModeMonitorRecoveryTests`
- テストフィクスチャ: `CapturingTimerFactory` + `TestTimer`（`ICCardManager.Tests.Infrastructure.Timing` 名前空間）が既にあり、`timer.Tick?.Invoke(...)` で Tick を手動トリガーできる
- Test コマンド: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~SharedModeMonitor" --nologo --verbosity minimal`

## File Structure

### 変更

- `ICCardManager/src/ICCardManager/Services/SharedModeMonitor.cs`: IDisposable 実装追加
- `ICCardManager/src/ICCardManager/App.xaml.cs`: OnExit に明示 Dispose 呼び出し
- `ICCardManager/tests/ICCardManager.Tests/Services/SharedModeMonitorTests.cs`: Dispose 関連テスト 5 件追加
- `ICCardManager/CHANGELOG.md`: [Unreleased] バグ修正に追記
- `ICCardManager/docs/design/05_クラス設計書.md`: IDisposable 化を反映（任意・微小な追記）

### 新規作成

なし

---

## Task 1: Baseline 確認

- [ ] **Step 1: ブランチ + 既存テスト**

```bash
git branch --show-current
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~SharedModeMonitor" --nologo --verbosity minimal 2>&1 | tail -3
```

Expected:
- Branch: `fix/issue-1286-shared-mode-monitor-dispose`
- 失敗 0、既存 SharedModeMonitor テスト pass

---

## Task 2: SharedModeMonitor を IDisposable 化

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/SharedModeMonitor.cs`

- [ ] **Step 1: クラス宣言に IDisposable 追加 + `_disposed` フィールド追加**

`SharedModeMonitor.cs` L14:

Before:
```csharp
    public class SharedModeMonitor
    {
        private readonly IDatabaseInfo _databaseInfo;
```

After:
```csharp
    public class SharedModeMonitor : IDisposable
    {
        private readonly IDatabaseInfo _databaseInfo;
```

L23 付近（`_isHealthCheckRunning` の直後）にフィールド追加:

Before:
```csharp
        private DateTime? _lastRefreshTime;
        private bool _isHealthCheckRunning;
```

After:
```csharp
        private DateTime? _lastRefreshTime;
        private bool _isHealthCheckRunning;
        private bool _disposed;
```

- [ ] **Step 2: Start() に ObjectDisposedException ガードを追加**

L50-64 の `Start()` メソッド冒頭に追加:

Before:
```csharp
        public void Start()
        {
            Stop();

            _healthCheckTimer = _timerFactory.Create();
```

After:
```csharp
        public void Start()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SharedModeMonitor));
            }

            Stop();

            _healthCheckTimer = _timerFactory.Create();
```

- [ ] **Step 3: Dispose() を実装**

`Stop()` メソッド（L69-84）の**直後**に `Dispose()` を追加:

```csharp
        /// <summary>
        /// タイマーを停止してインスタンスを破棄する。
        /// 複数回呼び出しても安全（冪等）。Dispose 後の <see cref="Start"/> は
        /// <see cref="ObjectDisposedException"/> を投げる。
        /// </summary>
        /// <remarks>
        /// 通常のライフサイクル内の停止は <see cref="Stop"/> を使い、
        /// アプリ終了などの破棄時のみ Dispose を呼ぶこと（Issue #1286）。
        /// </remarks>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
        }
```

- [ ] **Step 4: ビルド確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```

Expected: エラー 0

- [ ] **Step 5: 既存テスト pass 確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~SharedModeMonitor" --nologo --verbosity minimal 2>&1 | tail -3
```

Expected: 失敗 0、既存テスト全 pass

- [ ] **Step 6: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/SharedModeMonitor.cs
git commit -m "$(cat <<'EOF'
feat: SharedModeMonitor に IDisposable を実装 (Issue #1286)

- Dispose() は Stop() を呼び _disposed フラグを立てる冪等実装
- Dispose 後の Start() は ObjectDisposedException を投げる
- Stop / RecordRefresh 等はシャットダウン中の例外リスクを避けるため破棄後も動作する

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Dispose 関連の単体テスト追加

**Files:**
- Modify: `ICCardManager/tests/ICCardManager.Tests/Services/SharedModeMonitorTests.cs`

既存テストファイルの末尾（最後の `}` の直前）、または適切な `#region` 内に以下テストブロックを追加。

- [ ] **Step 1: Dispose テストを追加**

テストファイル末尾の `}` の直前に以下を挿入:

```csharp
    #region Dispose（Issue #1286）

    [Fact]
    public void Dispose_TimersStopped()
    {
        _monitor.Start();
        var healthTimer = _timerFactory.CreatedTimers[0];
        var displayTimer = _timerFactory.CreatedTimers[1];

        _monitor.Dispose();

        healthTimer.IsRunning.Should().BeFalse();
        displayTimer.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        _monitor.Start();

        Action act = () =>
        {
            _monitor.Dispose();
            _monitor.Dispose();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void Start_AfterDispose_ThrowsObjectDisposedException()
    {
        _monitor.Dispose();

        Action act = () => _monitor.Start();

        act.Should().Throw<ObjectDisposedException>()
            .Which.ObjectName.Should().Be(nameof(SharedModeMonitor));
    }

    [Fact]
    public void Stop_AfterDispose_DoesNotThrow()
    {
        _monitor.Start();
        _monitor.Dispose();

        Action act = () => _monitor.Stop();

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_WithoutStart_DoesNotThrow()
    {
        Action act = () => _monitor.Dispose();

        act.Should().NotThrow();
    }

    #endregion
```

- [ ] **Step 2: 新規テスト pass 確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~SharedModeMonitor" --nologo --verbosity minimal 2>&1 | tail -3
```

Expected: 失敗 0、既存テスト + 新規 5 件が全 pass

- [ ] **Step 3: コミット**

```bash
git add ICCardManager/tests/ICCardManager.Tests/Services/SharedModeMonitorTests.cs
git commit -m "$(cat <<'EOF'
test: SharedModeMonitor の Dispose テストを追加 (Issue #1286)

5 件追加: Dispose 後のタイマー停止 / 冪等 / Start の例外 /
Stop の継続動作 / Start 未呼び出しでの Dispose。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: App.xaml.cs OnExit に明示的 Dispose 呼び出しを追加

**Files:**
- Modify: `ICCardManager/src/ICCardManager/App.xaml.cs` (L720-729)

- [ ] **Step 1: OnExit を更新**

`App.xaml.cs` L720-729 を以下に置換:

Before:
```csharp
        protected override void OnExit(ExitEventArgs e)
        {
            // リソースの解放
            if (ServiceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }

            base.OnExit(e);
        }
```

After:
```csharp
        protected override void OnExit(ExitEventArgs e)
        {
            // Issue #1286: 明示的に SharedModeMonitor を Dispose してタイマーを確実に停止する。
            // ServiceProvider.Dispose() でも破棄されるが、二重 Dispose は冪等で吸収される。
            try
            {
                var monitor = ServiceProvider?.GetService(typeof(Services.SharedModeMonitor)) as IDisposable;
                monitor?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "SharedModeMonitor の Dispose でエラー");
            }

            // ServiceProvider 経由のリソース解放（他の IDisposable シングルトンも含む）
            if (ServiceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }

            base.OnExit(e);
        }
```

`using Microsoft.Extensions.DependencyInjection;` が既にある想定で `GetService(typeof(...))` を使用。`Services.SharedModeMonitor` のフルパス参照は using 節に `ICCardManager.Services` があれば単に `SharedModeMonitor` でもよい（App.xaml.cs 既存の using を grep で確認）。

- [ ] **Step 2: ビルド + テスト**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~SharedModeMonitor" --nologo --verbosity minimal 2>&1 | tail -3
```

Expected: エラー 0、テスト失敗 0

- [ ] **Step 3: コミット**

```bash
git add ICCardManager/src/ICCardManager/App.xaml.cs
git commit -m "$(cat <<'EOF'
feat: OnExit で SharedModeMonitor を明示的に Dispose (Issue #1286)

ServiceProvider.Dispose() でも破棄されるが、明示呼び出しにより
終了シーケンスにおけるタイマー停止の意図を可読化する。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: 全体ビルド・テスト + CHANGELOG + テスト設計書 + Plan + PR

- [ ] **Step 1: 全体ビルドとテスト**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --nologo --verbosity minimal 2>&1 | tail -3
```

Expected: 失敗 0、合格は 3012 + 新規 5 = 3017 件程度

- [ ] **Step 2: CHANGELOG 更新**

`ICCardManager/CHANGELOG.md` の `[Unreleased]` **バグ修正** セクションの適切な位置（既存 `例外処理の無言握りつぶし...` エントリの後あたり）に追加:

```markdown
- `SharedModeMonitor` に `IDisposable` を実装し、`App.OnExit` で明示的に `Dispose()` を呼ぶようにした。従来は Singleton 登録された本サービスが `IDisposable` を実装していなかったため、アプリ終了時にタイマー（30秒ヘルスチェック + 1秒同期表示）が確実に停止される保証がなかった。`Dispose()` は `Stop()` を呼び冪等フラグを立てる実装で、二重 Dispose に対し冪等。Dispose 後の `Start()` は `ObjectDisposedException` をスロー。共有モードでネットワーク切断状態のまま終了するケースでのリソースリーク予防。単体テスト 5 件を追加（#1286）
```

- [ ] **Step 3: 07_テスト設計書.md 更新**

`ICCardManager/docs/design/07_テスト設計書.md` の `SharedModeMonitor` 関連セクション（`grep -n "SharedModeMonitor" ICCardManager/docs/design/07_テスト設計書.md` で位置を特定）末尾に以下を追加:

```markdown
#### UT-SHM-DISPOSE: SharedModeMonitor Dispose テスト（Issue #1286）

| No | テストケース | 期待結果 |
|----|-------------|---------|
| 1 | Dispose_TimersStopped | Dispose 後、ヘルス/表示タイマー両方が停止 |
| 2 | Dispose_CalledTwice_DoesNotThrow | 2 回目の Dispose で例外なし（冪等） |
| 3 | Start_AfterDispose_ThrowsObjectDisposedException | Dispose 後の Start は ObjectDisposedException |
| 4 | Stop_AfterDispose_DoesNotThrow | Dispose 後の Stop も安全（シャットダウン中の例外を避けるため） |
| 5 | Dispose_WithoutStart_DoesNotThrow | Start 未呼び出しでも Dispose が例外を投げない |

**テストクラス:** `SharedModeMonitorTests`
```

- [ ] **Step 4: コミット + push + PR 作成**

```bash
git add ICCardManager/CHANGELOG.md ICCardManager/docs/design/07_テスト設計書.md docs/superpowers/plans/2026-04-19-issue-1286-shared-mode-monitor-dispose.md
git commit -m "$(cat <<'EOF'
docs: CHANGELOG / テスト設計書 / 実装計画を Issue #1286 で更新

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"

git push -u origin fix/issue-1286-shared-mode-monitor-dispose

gh pr create --title "fix: SharedModeMonitor に IDisposable を実装 (Issue #1286)" --body "$(cat <<'EOF'
## Summary
- `SharedModeMonitor` に `IDisposable` を実装し、アプリ終了時のタイマー確実停止を担保
- `Dispose()` は既存 `Stop()` ロジックを再利用する薄いラッパー。冪等
- Dispose 後の `Start()` は `ObjectDisposedException` をスロー
- `App.OnExit` で明示的に `Dispose()` を呼び、リソース解放順序を可読化
- 単体テスト 5 件を追加（既存 `SharedModeMonitorTests` に追加）

## Related
- Closes #1286

## 変更ファイル
- `ICCardManager/src/ICCardManager/Services/SharedModeMonitor.cs` (+25 行)
- `ICCardManager/src/ICCardManager/App.xaml.cs` (OnExit に 10 行追加)
- `ICCardManager/tests/ICCardManager.Tests/Services/SharedModeMonitorTests.cs` (+50 行、5 テスト)

## Test plan
- [x] 新規 Dispose テスト 5 件 pass
- [x] 既存 `SharedModeMonitorTests` / `SharedModeMonitorRecoveryTests` 全 pass
- [x] ソリューション全体テスト pass、ビルド 0 error
- [ ] 手動テスト: アプリを起動 → 終了 → プロセスが即座に終了することを確認
- [ ] 手動テスト: 共有モードで UNC パス接続中にネットワークケーブルを抜いた状態でアプリ終了 → 即座にプロセス終了

## Issue 記述との差分
Issue 原文は `CancellationTokenSource` にも言及しているが、実装には存在しない（Timer のみ）。Timer 側の対応で Issue の目的（確実な停止とリソース解放）は達成される。

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Expected: PR URL

---

## 手動テスト依頼（マージ後）

1. **通常終了**: アプリを起動し「×」で終了 → プロセスが即座に消える（タスクマネージャーで確認）
2. **共有モード起動 + 終了**: UNC パス (`\\server\share\iccard.db`) を指定してアプリ起動 → ヘルスチェックが走る状態で終了 → プロセス即終了
3. **ネットワーク切断シナリオ**: 共有モードで起動後、ネットワークケーブルを抜く → ヘルスチェックがタイムアウト中にアプリ終了 → プロセスがハングしない

## リスクと対策

| リスク | 対策 |
|-------|-----|
| Dispose 中の Tick が subscriber に届く | 既存 Stop が Tick -= を先に呼ぶため、Dispose 完了後の Tick は subscribers 0 で no-op |
| ServiceProvider.Dispose との二重 Dispose | 冪等実装（`_disposed` ガード）で吸収 |
| `_logger?.LogWarning` が null の段階で呼ばれる | 既存 App.xaml.cs で `_logger` は OnStartup で初期化され、OnExit 時には既に設定済み |
| `Services.SharedModeMonitor` の namespace 指定 | App.xaml.cs の using 節に `ICCardManager.Services` が既にあることを確認し、なければ `Services.SharedModeMonitor` フルパス参照で対応 |
