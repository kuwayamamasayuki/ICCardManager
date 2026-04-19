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

## 既存コードへの適用

新規コード追加時は上記ガイドラインを適用。既存コードの改善は **該当 Issue にスコープを絞って** 段階的に実施（一括変更は diff の肥大化・レビュー困難化を招く）。

## テスト

エラーメッセージ品質を固定するため、`ValidationServiceErrorMessageQualityTests` の `AssertQualityCriteria` を参考に、新しい Validator を追加する際は同様の品質テストを書く。
