# Issue #1493 設計書: 共有モードのヘルスチェック間隔をキャッシュ TTL に揃える

## 背景

共有フォルダモードで複数 PC が同一 SQLite DB を共有する構成において、
キャッシュ TTL とヘルスチェック間隔が不整合になっている。

| 項目 | 現状値 | 場所 |
|------|--------|------|
| `CardListSeconds`（共有モード） | 15 秒 | `App.xaml.cs:203` |
| `LentCardsSeconds`（共有モード） | 10 秒 | `App.xaml.cs:204` |
| `StaffListSeconds`（共有モード） | 30 秒 | `App.xaml.cs:205` |
| `SettingsMinutes`（共有モード） | 3 分 | `App.xaml.cs:206` |
| ヘルスチェック間隔 | **30 秒** | `SharedModeMonitor.cs:61` |
| `StaleThresholdSeconds` | 15 秒 | `SharedModeMonitor.cs:29` |

## 問題

1. ヘルスチェック間隔 30 秒は最短 TTL（`LentCardsSeconds = 10 秒`）の **3 倍**。
   他 PC で「ピッ」した直後、最大 10 秒のキャッシュ滞留 + ヘルスチェック未発火で、
   ダッシュボード反映に最大 30 秒近くの遅延が生じる可能性がある。
2. `StaleThresholdSeconds = 15 秒` を上回る間ヘルスチェックが走らないため、
   「同期表示」が stale 判定された後もしばらく実際の確認が走らない区間がある。

## 採用方針

Issue #1493 の提案中「選択肢 1: ヘルスチェック間隔を 15 秒に揃える」を採用する。

理由:
- 最小実装（1 行変更）で副作用が少ない。
- 共有モードの最大 TTL（`CardListSeconds = 15 秒`）と一致し、TTL を超えて stale データが滞留しない。
- `StaleThresholdSeconds`（15 秒）と一致し、「同期表示」の意味が揃う。
- DB アクセス頻度は 2 倍になるが、`CheckConnection()` は `SELECT 1` 相当の軽量クエリで、
  20 台同時接続でも実害なし（Issue #1107 の busy_timeout=15000ms で吸収可能）。

不採用案:
- **選択肢 2（`LentCardsSeconds` を 5 秒に短縮）**: DB アクセス頻度が激増し、
  20 台同時接続時の SMB 負荷が増大する。
- **選択肢 3（イベント駆動キャッシュパージ）**: PaSoRi タッチイベントは PC ローカルのため、
  別 PC への通知機構が必要になり実装範囲が大幅に拡大する。

## 変更内容

### コード変更

**`ICCardManager/src/ICCardManager/Services/SharedModeMonitor.cs`**

```csharp
// 変更前
_healthCheckTimer.Interval = TimeSpan.FromSeconds(30);

// 変更後
_healthCheckTimer.Interval = TimeSpan.FromSeconds(HealthCheckIntervalSeconds);
```

ハードコード値を `internal const int HealthCheckIntervalSeconds = 15;` として定数化する。
理由:
- 単体テストから期待値を参照しやすくする（マジックナンバー回避）。
- 既存の `StaleThresholdSeconds` と同じ書式に揃える。

### テスト追加

**`ICCardManager/tests/ICCardManager.Tests/Services/SharedModeMonitorTests.cs`**

以下を追加:
1. `Start_HealthCheckTimerInterval_Is15Seconds` — `Start()` 後にヘルスチェックタイマーの
   `Interval` が 15 秒であることを検証。
2. `HealthCheckIntervalSeconds_MatchesMaxCacheTtl` — `HealthCheckIntervalSeconds` が
   `CacheOptions` の共有モード上限（`CardListSeconds = 15`）と一致することを検証
   （TTL/間隔の整合性を回帰防止）。

### ドキュメント更新

1. **`.claude/rules/business-logic.md`** — 「共有フォルダモード」セクション
   「30秒ごとの接続ヘルスチェック」→「15秒ごとの接続ヘルスチェック」に修正。

2. **`ICCardManager/docs/design/`** 配下の該当設計書（共有モード関連の章があれば）
   — 同様に 30 秒 → 15 秒 の記載修正。

3. **`ICCardManager/CHANGELOG.md`** — 未リリース欄に Issue #1493 のエントリ追加。

## 影響範囲

- 共有モード時のみ影響（ローカルモードでは `SharedModeMonitor.Start()` が呼ばれない）。
- DB アクセス頻度: 30 秒に 1 回 → 15 秒に 1 回（クエリは `SELECT 1` 相当）。
- ユーザー視認可能な動作変化: 他 PC の操作結果が最大 15 秒以内に反映されるようになる。

## テスト戦略

- 単体テスト: タイマー間隔の検証（`ITimerFactory` モックの `Create()` 戻り値の
  `Interval` プロパティを検証）。
- 手動テスト不要: 値の定数変更のみで、UI 操作や複数 PC 環境を要しない。

## ロールアウト

- パッチリリース扱い（v.X.Y.Z+1）。
- マイグレーション・設定ファイル変更なし。
