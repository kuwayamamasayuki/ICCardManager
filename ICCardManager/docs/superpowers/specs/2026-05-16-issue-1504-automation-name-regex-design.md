# Issue #1504 — OperationLogDialog の AutomationProperties.Name 検査を Regex 化して空白許容にする

- 起票: 2026-05-10
- 修正対象: `ICCardManager/tests/ICCardManager.Tests/Views/Dialogs/DialogAutomationPropertiesCoverageTests.cs`
- 種別: test / enhancement（priority: low）
- 関連 PR: #1500（Issue #1468 のフォローアップ）

## 1. 背景

`DialogAutomationPropertiesCoverageTests.OperationLogDialog_should_label_key_controls_for_screen_readers`（L97-104）は次のような**厳密な文字列マッチ**を使用している:

```csharp
xaml.Should().Contain($"AutomationProperties.Name=\"{requiredName}\"", ...);
```

XAML エディタ（Visual Studio の整形機能や手動編集）で属性が複数行展開され、`AutomationProperties.Name = "検索を実行"`（`=` 前後にスペース）や改行を挟む書式に変わると、テストは **偽陰性で失敗** する（実体は付与されているのに「ない」と判定される）。

## 2. 修正方針（Approach A）

### 2.1 メイン検査の Regex 化

L101-103 を `xaml.Should().MatchRegex(...)` に変更し、空白を許容する:

```csharp
xaml.Should().MatchRegex(
    $@"AutomationProperties\.Name\s*=\s*""{Regex.Escape(requiredName)}""",
    $"OperationLogDialog: 主要コントロールに AutomationProperties.Name=\"{requiredName}\" が必要。" +
    "Issue #1468 で業務監査画面（操作ログ）のスクリーンリーダー対応を改善した際の付与項目。");
```

同ファイル L142-144 が既に `xaml.Should().MatchRegex(...)` を使用しており、表現スタイルを統一する。

### 2.2 regression サブテスト追加

「regex を緩めすぎて値違いも通ってしまう」事故を防ぐため、合成 XAML 文字列に対する regex 評価を `[Theory]` で固定する。これは新規追加メソッド `AutomationNamePattern_should_be_whitespace_tolerant_but_value_strict`（新規） とする。

検証ケース:

| 入力 XAML | requiredName | 期待 |
|---|---|---|
| `AutomationProperties.Name="検索を実行"` | `検索を実行` | true |
| `AutomationProperties.Name = "検索を実行"` | `検索を実行` | true |
| `AutomationProperties.Name  =  "検索を実行"` | `検索を実行` | true |
| `AutomationProperties.Name="別の語"` | `検索を実行` | false |
| `AutomationProperties.Name="検索"` | `検索を実行` | false（部分一致でも通らない） |
| `(空)` | `検索を実行` | false |

regex パターンの構築ロジックはメイン検査と共有するため、共通ヘルパ `BuildAutomationNamePattern(string requiredName)` を private static で抽出する。

## 3. スコープ外（明示）

- ファイル内の他の `Should().Contain(...)` 呼び出し（L196, L236）は C# code-behind 文字列を対象としており、整形による空白挿入リスクが XAML より低い。Issue 本文の「影響範囲」も L97-104 のみを指定。本 PR では触らない。
- 同種の問題が StaffAuthDialog 検査側にも将来発生したら別 Issue で対応。

## 4. テスト戦略

### 4.1 実コード XAML に対する既存テスト

メイン検査 `OperationLogDialog_should_label_key_controls_for_screen_readers` の `[Theory]` ケース 10 件は引き続き対象。実コード `OperationLogDialog.xaml` で全件 pass することを確認する。

### 4.2 regex 自体の挙動テスト（新規）

`AutomationNamePattern_should_be_whitespace_tolerant_but_value_strict` で合成 XAML 6 ケース（上表）を検証。実コード XAML に依存しないため、将来 XAML が変わってもこのテストは壊れず、regex の挙動だけを純粋に固定する。

## 5. ビルド・テスト確認

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj \
    --filter "FullyQualifiedName~DialogAutomationPropertiesCoverageTests"
```

## 6. ロールバック

`git revert <commit>` で復元可能。テストコードのみの変更でアプリ動作に影響なし。

## 7. 設計書更新の要否

`docs/design/07_テスト設計書.md` への記載追加は不要と判断する。理由:

- 本 PR はテストの内部実装（マッチ手法）の変更のみで、**検証対象の意味は不変**（OperationLogDialog の主要コントロール 10 件に AutomationProperties.Name が付与されていること）。
- テスト設計書はテストケースの目的と検証項目を記録する文書であり、マッチング手法の実装詳細までは記載していない。

仮にレビュー時にテスト設計書の同期が必要と判断された場合は、§ DialogAutomationPropertiesCoverageTests の備考に「XAML 整形による空白挿入に耐える Regex マッチを採用」の 1 行追記とする。

## 8. 参考

- Issue #1504
- 関連既存テスト: `DialogAutomationPropertiesCoverageTests.cs:97-104`
- 関連既存パターン（既に MatchRegex 使用）: `DialogAutomationPropertiesCoverageTests.cs:142-144`
