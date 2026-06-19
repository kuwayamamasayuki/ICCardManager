# エラーメッセージ品質ガイドライン（Issue #1275）

## 基本原則: 「何が」「なぜ」「どうすれば」の3要素

すべてのエラーメッセージは **3要素** を含めて構成すること。ユーザーが自力で問題を特定し解決できることが目的。

| 要素 | 意味 | 例 |
|------|------|----|
| **何が** | どのフィールド・どの値が問題か | 「職員名」「管理番号」「残高」|
| **なぜ** | なぜそれが問題か（ルール・制約の説明） | 「20文字を超えています」「マイナスになります」「16進数以外の文字が含まれています」|
| **どうすれば** | 具体的な解決アクション | 「0以上の金額を入力してください」「ドロップダウンから選択してください」 |

## 禁止パターン

### NG: 曖昧な「エラーが発生しました」
```csharp
// ❌ 悪い例
return ValidationResult.Failure("エラーが発生しました");
return ValidationResult.Failure("不正な値です");
return ValidationResult.Failure("入力が正しくありません");
```

### OK: 3要素を含む具体的な表現
```csharp
// ✅ 良い例
return ValidationResult.Failure(
    $"管理番号が{cardNumber.Length}文字で上限を超えています。" +  // 何が・なぜ
    $"{CardNumberMaxLength}文字以内の略称で入力してください。");    // どうすれば
```

## 推奨パターン

### 1. 実際の入力値を含める（デバッグ容易化）

```csharp
// ❌ "残高がマイナスになります"
// ✅ "計算後の残高が -1,500円（マイナス）になります。受入金額を増やすか、払出金額を減らしてください。"
ValidationMessage =
    $"計算後の残高が {Balance:N0}円（マイナス）になります。" +
    "受入金額を増やすか、払出金額を減らしてください。";
```

### 2. UI 操作の場所を示す

```csharp
// ❌ "カード種別を選択してください"
// ✅ "カード種別が未選択です。ドロップダウンから「はやかけん」「nimoca」等を選択してください。"
```

### 3. 行動指示型で終わる

メッセージは「～してください」「～で入力してください」「～を選択してください」で終わる。

```regex
してください。?$|入力してください。?$|選択してください。?$|設定してください。?$
```

### 4. 最小文字数基準: 20文字以上

短すぎるメッセージは情報不足になる傾向がある。単体テストでは最低 20 文字を品質閾値として検証する（`ValidationServiceErrorMessageQualityTests` 参照）。

## 復旧手順を UI で提示する場合

エラー Border 内の TextBlock で復旧手順を併記することで、ダイアログを開いたまま次のアクションに進める。

```xaml
<Border Background="{DynamicResource ErrorBackgroundBrush}" Padding="10">
    <StackPanel>
        <TextBlock Text="{Binding ValidationMessage}"
                   FontWeight="Bold"
                   Foreground="{DynamicResource DangerTextBrush}"/>
        <TextBlock Text="{Binding RecoverySuggestion}"
                   Margin="0,5,0,0"
                   TextWrapping="Wrap"
                   Foreground="{DynamicResource SecondaryTextBrush}"/>
    </StackPanel>
</Border>
```

## アクセシビリティ

- エラーメッセージは `AutomationProperties.Name` でスクリーンリーダーにも読み上げさせる
- 色（赤）だけでなくアイコン（⚠️）とテキストで情報を伝達（Issue #1274 の色覚多様性対応原則と一貫）

## 例外からのユーザー向けメッセージ生成（Issue #1614）

`catch (Exception ex)` で捕捉した例外を UI に表示する際、**生の `ex.Message` を直接ユーザーへ出さない**こと。`ex.Message` は英語・技術用語（SQLite エラー、スタックトレース由来文言等）を含みうるため、職員には解読不能で、内部実装の露出にもなる。

```csharp
// ❌ 悪い例: 生の例外メッセージが UI に漏れる
StatusMessage = $"エラー: {ex.Message}";

// ✅ 良い例: 3 要素準拠の文言を表示し、技術的詳細はログへ逃がす
_logger.LogError(ex, "Failed to save ledger");                 // ILogger 保持時
StatusMessage = ExceptionMessageFormatter.ToUserMessage(ex, "台帳の保存");
```

- 変換は `Common/ExceptionMessageFormatter.ToUserMessage(Exception, operation)` を使う。`operation` はユーザー視点の操作名（「台帳の保存」「エクスポート」「リストア」等）で、文言の「何が」部分になる。例外種別に応じた「なぜ／どうすれば」が付与される。`AppException` は整備済みの `UserFriendlyMessage` がそのまま使われる。
- 技術的詳細（`ex.Message`）は必ずログへ残す。`ILogger` を注入済みなら `_logger.LogError(ex, ...)`、注入していない ViewModel / View コードビハインドでは `ErrorDialogHelper.LogException(ex, "操作名")`（既存のファイルログ機構を再利用、ダイアログ非表示）を使う。
- トースト通知は文字数制約があるため、`ToUserMessage` のフル文言ではなく簡潔な行動指示（「もう一度タッチしてください」等）を優先してよい。

### `ToUserMessage` と `ErrorDialogHelper.GetErrorInfo` の役割分担（ドリフト監査 EM-R5-01）

例外種別から文言を引くマッピングは 2 か所にあり、**用途が異なる**ため意図的に併存させる:

| 変換 | 用途 | 文言の粒度 |
|------|------|-----------|
| `ExceptionMessageFormatter.ToUserMessage(ex, operation)` | **通常のエラー経路**（操作の失敗をステータス/ダイアログで案内し、ユーザーが自力で回復する） | 「何が／なぜ／どうすれば」の **3 要素**（操作名を含み行動指示で終わる） |
| `ErrorDialogHelper.GetErrorInfo(ex)` → `ShowFatalError` | **致命的エラーダイアログ**（継続不能・クラッシュ診断目的） | `SYS00x` エラーコード＋簡潔な「なぜ」。**併せて `StackTrace` を表示**して障害解析に供する |

`GetErrorInfo` の文言が 3 要素を満たさない（「どうすれば」を欠く）のは、致命エラー時はユーザーの自己回復より**エラーコード＋スタックトレースによる原因究明**を優先する設計判断であり、品質ガイドライン違反ではない。新規の**通常エラー**経路では `ToUserMessage` を使うこと（`GetErrorInfo` を通常経路に転用しない）。

## 既存コードへの適用

新規コード追加時は上記ガイドラインを適用。既存コードの改善は **該当 Issue にスコープを絞って** 段階的に実施（一括変更は diff の肥大化・レビュー困難化を招く）。

## テスト

エラーメッセージ品質を固定するため、`ValidationServiceErrorMessageQualityTests` の `AssertQualityCriteria` を参考に、新しい Validator を追加する際は同様の品質テストを書く。

品質テストは対象の増加に伴い複数クラスへ分化している。新規追加時は最も近い既存クラスを参考にすること。

| クラス | 対象 |
|--------|------|
| `ValidationServiceErrorMessageQualityTests` | `ValidationService` の各 Validator |
| `PathValidatorErrorMessageQualityTests` | パス検証（`SafeFilePathValidator` 等）のエラー文言 |
| `ExceptionMessageFormatterTests` | `ExceptionMessageFormatter.ToUserMessage`（例外→3要素文言、Issue #1614） |
