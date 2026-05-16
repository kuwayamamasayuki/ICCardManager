# Issue #1503 — LiveSetting 検査の Regex を要素境界で絞る

- 起票: 2026-05-16
- 修正対象: `ICCardManager/tests/ICCardManager.Tests/Views/Dialogs/DialogAutomationPropertiesCoverageTests.cs`
- 種別: test (improvement)
- 関連 PR: #1500（元 PR）、Issue #1504（同ファイルの regex 改善）

## 1. 背景

`DialogAutomationPropertiesCoverageTests` の 3 つの正規表現が、要素境界を考慮せず `[\s\S]*?` で起点と終点を繋いでいる。XAML 中に同一値が複数の要素に分散して登場するケースで、ファイル内の任意の組み合わせを「同一要素にあるかのように」誤検出するリスクがある。

```csharp
// 現状（OperationLogDialog_status_message_should_announce_changes）
xaml.Should().MatchRegex(
    @"Text=""\{Binding StatusMessage\}""[\s\S]*?AutomationProperties\.LiveSetting=""Polite""",
    "...");
```

`OperationLogDialog.xaml` は `AutomationProperties.LiveSetting="Polite"` を 4 箇所で使用しているため、StatusMessage の TextBlock から `LiveSetting` を外しても、他の要素（ページ番号 TextBlock、ページネーション、エクスポートステータス）の `Polite` を拾って regex が緑のまま通り、テストが回帰を検知できない。

同様の脆弱性が以下の 2 テストにもある（`x:Name="..."` で起点を絞っているため誤マッチ確率は実用上低いが、原則として同じ問題）:

- `StaffAuthDialog_status_text_should_have_assertive_live_setting`
- `StaffAuthDialog_timeout_text_should_have_polite_live_setting`

## 2. 修正方針

XAML の属性値は `"..."` で囲まれており、開始タグ内に裸の `>` は出現しない。よって `[\s\S]*?` を `[^>]*?` に置換すれば、起点と終点を**同一開始タグ内**に確実に閉じ込められる。マルチライン属性レイアウト（属性ごとの改行）でも `[^>]` は改行・空白を含むためマッチ可能。

| ファイル位置（main 基準） | 起点 | 終点 |
|---|---|---|
| L116 `StaffAuthDialog_status_text_…` | `x:Name="StatusText"` | `AutomationProperties.LiveSetting="Assertive"` |
| L128 `StaffAuthDialog_timeout_text_…` | `x:Name="TimeoutText"` | `AutomationProperties.LiveSetting="Polite"` |
| L143 `OperationLogDialog_status_message_…` | `Text="{Binding StatusMessage}"` | `AutomationProperties.LiveSetting="Polite"` |

すべて `[\s\S]*?` → `[^>]*?` の機械的置換。

## 3. 検証方法 — TDD

「regex を厳しくした」修正の効果を回帰防止するため、合成 XAML サンプルで境界挙動を固定する Theory を追加する。Issue #1504 で導入された `AutomationNamePattern_should_be_whitespace_tolerant_but_value_strict` と同じパターン（main にはまだ未取込のため、本 PR ではそれを参考にしつつ独立して実装）。

### 3.1 新規テスト: `LiveSettingPattern_should_be_scoped_to_same_element`

合成 XAML を渡し、要素境界を跨ぐマッチが起きないことを検証する。

検証ケース（抜粋）:

- **同一要素内（マッチすべき）**: `<TextBlock Text="{Binding StatusMessage}" AutomationProperties.LiveSetting="Polite"/>`
- **マルチライン属性レイアウト（マッチすべき）**: 属性ごとに改行された `TextBlock`
- **別要素を跨ぐ（マッチしてはならない）**: `<TextBlock Text="{Binding StatusMessage}"/>` と `<TextBlock AutomationProperties.LiveSetting="Polite"/>` が並んだケース → 旧 regex なら誤検出、新 regex なら検出しない
- **同じ要素内に LiveSetting がない（マッチしてはならない）**: `<TextBlock Text="{Binding StatusMessage}"/>` のみ

旧 regex で実行すると「別要素を跨ぐ」ケースで合格してしまい、テストが赤になる前提で実装する（TDD: Red → Green）。

### 3.2 既存テストへの影響

実 XAML（`OperationLogDialog.xaml` L371-376、`StaffAuthDialog.xaml` L69-76, L87-94）は起点と終点が**同一開始タグ内**にあるため、`[^>]*?` でもマッチし続ける。後退なし。

## 4. テストの取り扱い

修正対象がテストコード自体のため、TDD は「合成 XAML を入力とする Theory」で完結する。実 XAML 側の変更は不要。

## 5. ロールバック

`git revert <commit>` で復元可能。リスクは極小（テストコードのみの変更）。

## 6. スコープ外

- `OperationLogDialog.xaml` の他の `Polite` 箇所（L280, L316, L417）に対する同様の `Text-Binding` ベースのカバレッジ追加（Issue では 1 箇所＋関連 2 箇所のみ言及）
- regex を共通ヘルパ関数化（3 つは起点・終点が微妙に異なるためベタ書きで十分。`BuildElementScopedPattern` 等の抽象化は YAGNI）
- Issue #1504 のマージ待ちおよびその範囲（別 PR で対応）

## 7. 参考

- Issue #1503
- Issue #1504（同ファイルの `AutomationProperties.Name` 検査 regex 改善）
- 元 PR: #1500
- 関連既存ファイル: `ICCardManager/tests/ICCardManager.Tests/Views/Dialogs/DialogAutomationPropertiesCoverageTests.cs:116, :128, :143`
- 実 XAML: `ICCardManager/src/ICCardManager/Views/Dialogs/OperationLogDialog.xaml:371-376`, `ICCardManager/src/ICCardManager/Views/Dialogs/StaffAuthDialog.xaml:69-76, :87-94`
