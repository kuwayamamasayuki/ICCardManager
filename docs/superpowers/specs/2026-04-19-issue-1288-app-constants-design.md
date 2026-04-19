# Issue #1288: タイムアウト等マジック定数の AppConstants 集約 設計書

作成日: 2026-04-19
対象 Issue: [#1288](https://github.com/kuwayamamasayuki/ICCardManager/issues/1288)

## 背景と問題

`AppOptions.cs` の 3 つのタイムアウトプロパティのデフォルト値（60, 30, 5）が裸のマジック数値で書かれており、業務ルール由来であることが分かりにくい。`AppConstants.cs` は既に存在するが `SystemName` 定数のみを持つ状態。

## スコープ

### 含む

1. `AppConstants.cs` に 3 つの `Default*` const を追加（業務ルール文書への参照コメント付き）
2. `AppOptions.cs` のデフォルト値を `AppConstants.Default*` を参照する形に変更
3. 振る舞い不変（値は同じ、`appsettings.json` override 機能は維持）

### 含まない

- 他のマジック数値（残額警告 10000 円、残高不足 50 円閾値等）の集約
- `AppOptions` 自体の API 変更
- 利用側（LendingService / MainViewModel / StaffAuthDialog）のコード変更
- 新規テスト追加（値の不変性は既存テストと `const` 参照で担保）

## 設計

### AppConstants への追加

```csharp
namespace ICCardManager.Common
{
    internal static class AppConstants
    {
        /// <summary>
        /// システム表示名。ウィンドウタイトル、ヘッダー、スプラッシュ画面等で使用。
        /// </summary>
        public const string SystemName = "交通系ICカード管理システム：ピッすい";

        // --- タイムアウト系デフォルト値（Issue #1288 で集約） ---
        // 業務ルール由来のため、.claude/rules/business-logic.md を参照のこと。
        // 実行時は AppOptions 経由で appsettings.json によるオーバーライドが可能。

        /// <summary>
        /// 30 秒再タッチルール: 同一カードが再タッチされた場合に逆処理を実行する猶予時間（秒）。
        /// .claude/rules/business-logic.md「状態遷移」参照。
        /// </summary>
        public const int DefaultCardRetouchTimeoutSeconds = 30;

        /// <summary>
        /// 職員証タッチ後のタイムアウト（秒）。この時間を経過すると職員証タッチ待ちに戻る。
        /// .claude/rules/business-logic.md「状態遷移」参照。
        /// </summary>
        public const int DefaultStaffCardTimeoutSeconds = 60;

        /// <summary>
        /// 同一カードへの同時アクセスを防ぐ排他ロック取得のタイムアウト（秒）。
        /// .claude/rules/business-logic.md「排他制御」参照。
        /// </summary>
        public const int DefaultCardLockTimeoutSeconds = 5;
    }
}
```

### AppOptions の変更

```csharp
namespace ICCardManager.Services
{
    public class AppOptions
    {
        public int StaffCardTimeoutSeconds { get; set; } = Common.AppConstants.DefaultStaffCardTimeoutSeconds;
        public int RetouchWindowSeconds { get; set; } = Common.AppConstants.DefaultCardRetouchTimeoutSeconds;
        public int CardLockTimeoutSeconds { get; set; } = Common.AppConstants.DefaultCardLockTimeoutSeconds;
    }
}
```

### 設計判断

- **PascalCase + `Default` プレフィックス**: C# 慣例に合わせる。Issue 推奨の SCREAMING_SNAKE_CASE は既存 `AppConstants.SystemName` と不整合のため却下
- **`internal static class` のまま**: 同 assembly 内の `AppOptions` から参照可能なので public 昇格不要
- **値は既存と同じ（60/30/5）**: behaviour 不変
- **`appsettings.json` による override は引き続き動作**: プロパティ初期化子はインスタンス作成時の値を決めるだけ、JSON binding はその後に適用される

## リスクと対策

| リスク | 対策 |
|-------|-----|
| 定数名のタイポ | ビルド時に検出 |
| 別の場所で同じ値がマジック数値で使われている | 今回の PR では扱わず別 Issue で。まず 3 定数の集約パターンを確立 |
| `const int` は assembly 境界を越えると値が固定化される | 同 assembly 内の利用のみのため影響なし |

## 非対象（別 Issue 候補）

- 業務ロジック系マジック数値（残高警告、残高不足閾値、リトライ回数、共有モード TTL など）の集約
- `LendingService.InsufficientBalanceExcessThreshold` などサービス内 const の集約
