# Issue #1482: VACUUM 共有モード競合の解消（先勝ち CAS ロック方式）

- 日付: 2026-05-14
- Issue: [#1482](https://github.com/sherbet-ka/ICCardManager/issues/1482)
- 関連: Issue #1107（共有モード journal/busy_timeout）、Issue #1287（async/ConfigureAwait）

## 背景

月跨ぎの最初の起動で各 PC が同時に VACUUM を試みて失敗し、警告ログを汚す。共有モードでは数百 MB 規模の DB で他 PC を 10 秒以上ブロックする可能性がある。現在は `App.xaml.cs:700–723` で `today.Day >= 10` かつ「当月未実行」なら VACUUM を試行し、`Vacuum()` が `SQLITE_BUSY` / `SQLITE_LOCKED` を握り潰して `false` を返す設計。失敗 PC は `settings.LastVacuumDate` を更新しないため、翌起動時にまた試行する「デッドロックスパイラル」になりうる。

## 方針

Issue 本文の選択肢 3「先勝ち方式で重複試行を許容しない」を採用する。

`settings.last_vacuum_date` 行への単一 SQL 文を **compare-and-swap (CAS)** として用い、最初に更新成功した PC のみが当月の VACUUM を実行する。ロック獲得した PC が VACUUM 本体に失敗しても、当月の `LastVacuumDate` は今日の日付で確定させる（来月までは誰も再試行しない）。

### この設計の前提・トレードオフ

| 観点 | 結論 | 理由 |
|------|------|------|
| ロック獲得後の失敗扱い | 当月スキップとして確定 | 「先勝ちで重複試行を許容しない」（Issue 本文）に従う。デッドロックスパイラルを完全に防ぐ |
| 巨大 DB の防止 | 本 Issue のスコープ外 | 選択肢 2（`auto_vacuum=INCREMENTAL`）は別途検討 |
| 既存 `Vacuum()` の API | 変更しない | 戻り値 `bool`（成否）はそのまま。`SQLITE_BUSY/LOCKED` の握り潰しもそのまま |
| ローカルモード | 影響なし | ローカルモードでも CAS は機能するが、そもそも単一 PC なので必ず先勝ちが成立 |

## 設計

### 1. `ISettingsRepository` への追加

```csharp
/// <summary>
/// 当月の VACUUM 実行権を先勝ちで獲得する。
/// </summary>
/// <param name="today">基準日（通常は <see cref="DateTime.Now"/>）。</param>
/// <returns>
/// 自 PC が VACUUM を実行すべきなら <c>true</c>、
/// 既に他 PC が当月分を確保済みなら <c>false</c>。
/// </returns>
/// <remarks>
/// 共有モードで複数 PC が同時に呼び出しても、原子的 UPSERT により正確に 1 つだけが
/// <c>true</c> を返す。<c>true</c> を受け取った PC は VACUUM 失敗時も再試行しない
/// （来月まで誰も試行しない設計）。
/// </remarks>
Task<bool> TryAcquireMonthlyVacuumLockAsync(DateTime today);
```

### 2. `SettingsRepository` の実装

```sql
INSERT INTO settings (key, value, updated_at)
VALUES ('last_vacuum_date', :today, :nowIso)
ON CONFLICT(key) DO UPDATE SET
    value      = excluded.value,
    updated_at = excluded.updated_at
WHERE settings.value IS NULL
   OR substr(settings.value, 1, 7) <> :currentMonth;
```

- `:today` = `today.ToString("yyyy-MM-dd")`
- `:currentMonth` = `today.ToString("yyyy-MM")`
- `:nowIso` = `DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")`
- `command.ExecuteNonQuery()` の戻り値 (`rowsAffected`) を判定
  - `1` → ロック獲得（INSERT 成功 or UPDATE 成功）
  - `0` → 既に当月分が確保済み

#### SQLite UPSERT のバージョン要件

System.Data.SQLite 1.0.x（プロジェクトの参照バージョン）は SQLite 3.24+ を同梱しており UPSERT 対応済み。実装時に念のためログで現行バージョンを確認するが、フォールバックは原則不要。

#### 設定テーブルのスキーマ

既存 `settings` テーブルは `(key TEXT PRIMARY KEY, value TEXT, updated_at TEXT)` 想定。実装前に schema を確認する。`updated_at` カラムが無い場合は `INSERT INTO settings (key, value)` のみで OK。

### 3. `App.xaml.cs` の置換

`PerformStartupTasksAsync` の現行 700–723 行：

```csharp
// VACUUM（月次実行）
var settingsRepository = ServiceProvider.GetRequiredService<ISettingsRepository>();
var settings = await settingsRepository.GetAppSettingsAsync();

var today = DateTime.Now;
if (today.Day >= 10)
{
    var lastVacuum = settings.LastVacuumDate;
    if (!lastVacuum.HasValue ||
        lastVacuum.Value.Year != today.Year ||
        lastVacuum.Value.Month != today.Month)
    {
        if (await dbContext.VacuumAsync())
        {
            settings.LastVacuumDate = today;
            _ = settingsRepository.SaveAppSettingsAsync(settings);
            _logger?.LogInformation("VACUUM実行完了");
        }
        else
        {
            _logger?.LogWarning("VACUUM失敗（他の接続がアクティブ）。次回起動時にリトライします。");
        }
    }
}
```

を以下に置換：

```csharp
// VACUUM（月次実行、先勝ち CAS ロック）
var today = DateTime.Now;
if (today.Day >= 10)
{
    var settingsRepository = ServiceProvider.GetRequiredService<ISettingsRepository>();
    if (await settingsRepository.TryAcquireMonthlyVacuumLockAsync(today))
    {
        if (await dbContext.VacuumAsync())
        {
            _logger?.LogInformation("VACUUM実行完了");
        }
        else
        {
            _logger?.LogWarning("VACUUM失敗。来月再試行します。");
        }
    }
}
```

- ロック未獲得時はログを残さない（運用ログを汚さない。共有モードでは N-1 台が必ずスキップになるため）
- 例外フローは `PerformStartupTasksAsync` 全体の `catch` でハンドリング済み

### 4. テスト

`SettingsRepositoryTests` に以下を追加：

1. `TryAcquireMonthlyVacuumLockAsync_前回実行なしの場合_trueを返し当日日付を保存する`
2. `TryAcquireMonthlyVacuumLockAsync_前月実行済みの場合_trueを返し当日日付に更新する`
3. `TryAcquireMonthlyVacuumLockAsync_当月実行済みの場合_falseを返し値を変更しない`
4. `TryAcquireMonthlyVacuumLockAsync_並列実行で1つだけがtrueを返す`
   - 単一 DB ファイルに対し 2 つ以上の `SettingsRepository` インスタンスを生成し、`Task.WhenAll` で同時呼出
   - `true` を返したインスタンスがちょうど 1 つであることを検証

`App.xaml.cs` の startup task はテスト対象外（DI 経由の起動コードで単体テスト困難）。動作検証はユーザー手動テストへ委ねる（後述）。

### 5. ドキュメント更新

- `ICCardManager/CHANGELOG.md` の Unreleased に「VACUUM 共有モード競合の解消（先勝ち CAS、Issue #1482）」を追記
- `docs/design/07_テスト設計書.md` に上記 4 ケースの追加（テスト ID の連番を確認のうえ採番）
- `.claude/rules/business-logic.md` の共有フォルダモード節 VACUUM 行を、先勝ち方式の説明に書き換え

## ユーザー手動テスト依頼項目

`App.xaml.cs` の起動時 VACUUM 経路は xUnit から駆動できないため、以下を手動確認してください：

1. **ローカルモードでの月初起動**: 通常通り VACUUM が実行され、ログに「VACUUM実行完了」が出る
2. **共有モードで 2 台同時起動**:
   - 両方が `today.Day >= 10` かつ当月未実行の状態にする（DB の `settings` テーブルで `last_vacuum_date` を前月値に書き換えるか削除）
   - 同時に起動 → ログを確認
   - 一方の PC でのみ「VACUUM実行完了」または「VACUUM失敗。来月再試行します。」が出る
   - もう一方の PC は何もログを出さず正常起動する
3. **共有モードで 1 台目起動済みの後に 2 台目起動**: 2 台目はログなしで素通り
4. **`today.Day < 10` の起動**: いずれのモードでも VACUUM 試行されない

## ロールバック手順

問題発覚時は `App.xaml.cs` のみ revert して旧コードに戻せば動作復旧する。`SettingsRepository` の新メソッドは未参照になっても害がない（dead code として残るのみ）。

## Out of Scope

- 自動 VACUUM の完全廃止（選択肢 1）
- `PRAGMA auto_vacuum=INCREMENTAL` への移行（選択肢 2）
- 設定画面からの手動 VACUUM 実行ボタン
