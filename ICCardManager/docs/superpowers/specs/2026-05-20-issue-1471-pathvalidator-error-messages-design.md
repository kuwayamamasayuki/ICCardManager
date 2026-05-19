# Issue #1471: PathValidator のエラーメッセージ3要素ガイドライン対応

## 背景

`ICCardManager/src/ICCardManager/Common/PathValidator.cs` のエラーメッセージは
`.claude/rules/error-messages.md` で定義された「何が・なぜ・どうすれば」の3要素
ガイドラインを満たしていない。20〜25 字前後の短文が多く、ユーザーが自力で問題を
解決できる情報を欠いている。`ValidationService.cs` の IDm 系メッセージは Issue
#1275 で改善済みだが、PathValidator は取り残された follow-up タスク。

## 目的

1. `PathValidator.cs` の全 `Failure(...)` メッセージを 3 要素化する。
2. `ValidationServiceErrorMessageQualityTests` と同等の品質テスト
   `PathValidatorErrorMessageQualityTests` を追加し、品質を恒久的に固定する。

## 修正対象一覧（PathValidator.cs）

| 行 | 旧メッセージ | 不足要素 |
|----|--------------|----------|
| 96 | バックアップパスが指定されていません | 行動指示、句点 |
| 102 | パスが長すぎます（最大{N}文字） | 実際長、行動指示 |
| 109 | パスに使用できない文字が含まれています | 該当文字、行動指示 |
| 125 | 絶対パスを指定してください | なぜ、例 |
| 163 | ドライブ {root} が利用できません | なぜ、行動指示 |
| 214 | ネットワークパスにはサーバー名と共有名が必要です（例: \\server\share） | 不足軽微（句点と行動指示語の追加） |
| 220 | ネットワークパスのサーバー名が不正です | なぜ、例、行動指示 |
| 226 | ネットワークパスの共有名が不正です | なぜ、例、行動指示 |
| 401 | 指定されたフォルダへの書き込み権限がありません | 行動指示 |
| 422 | 指定されたフォルダの親ディレクトリへの書き込み権限がありません | 行動指示 |

`"フォルダへのアクセスエラー: {ex.Message}"`（行 405）は `IOException.Message`
依存のため対象外。`"ネットワーク共有に到達できません..."`（行 145）は既に
3 要素を満たしているため対象外。

## 設計

### 1. メッセージ書き換え方針

**後方互換性**: 既存 `PathValidatorTests` は部分文字列マッチ
（`Should().Contain("指定されていません")` 等）で書かれている。これらキーワードを
新メッセージにも保持し、テスト修正なしで通るようにする。

**末尾形式**: `AssertQualityCriteria` の正規表現
`してください。?$|入力してください。?$|選択してください。?$|設定してください。?$`
を満たすよう、各メッセージは「〜してください。」で終わらせる。

**最小文字数**: 20 文字以上を確保（句点・例示・行動指示を含めれば自然に満たす）。

**例示の使用**: コードリテラルの例（`` `C:\Backup` ``、`` `\\server\share` ``）
は逆クォートで囲まず、ダブルクォート風に書ける形を選ぶ（`MessageBox` で表示する
ため Markdown 記法は使えない）。

### 2. 修正後メッセージ案

| 行 | 新メッセージ |
|----|--------------|
| 96 | バックアップパスが指定されていません。バックアップ先のフォルダパス（例: C:\Backup または \\\\server\\share\\backup）を入力してください。 |
| 102 | パスが {path.Length} 文字で Windows の上限（{MaxPathLength} 文字）を超えています。{MaxPathLength} 文字以内の短いパスを指定してください。 |
| 109 | パスに使用できない文字が含まれています。`< > : " \| ? *` 等の予約文字を取り除いて指定してください。 |
| 125 | 絶対パスではありません。「C:\Backup」のようにドライブ文字から始まる絶対パス、または「\\\\server\\share」形式のネットワークパスを指定してください。 |
| 163 | ドライブ {root} が利用できません。USB メモリの抜けや未マウントが原因の可能性があります。接続を確認するか、別のドライブを指定してください。 |
| 214 | ネットワークパスにはサーバー名と共有名が必要です。「\\\\server\\share」の形式で指定してください。 |
| 220 | ネットワークパスのサーバー名が空です。「\\\\サーバー名\\共有名」の形式で、サーバー名にホスト名または IP アドレスを指定してください。 |
| 226 | ネットワークパスの共有名が空です。「\\\\サーバー名\\共有名」の形式で、サーバー名の後に共有名を指定してください。 |
| 401 | 指定されたフォルダへの書き込み権限がありません。フォルダのアクセス権を確認するか、書き込み可能な別のフォルダを指定してください。 |
| 422 | 指定されたフォルダの親ディレクトリへの書き込み権限がありません。親フォルダのアクセス権を確認するか、書き込み可能な別の場所を指定してください。 |

### 3. 新規テストクラス `PathValidatorErrorMessageQualityTests`

**配置**: `ICCardManager/tests/ICCardManager.Tests/Common/PathValidatorErrorMessageQualityTests.cs`

**構造**: `ValidationServiceErrorMessageQualityTests` を参考に、共通の
`AssertQualityCriteria` を private helper として持ち、各エラーケースについて
ケース固有のキーワード検証を追加する。

**テストケース**:

1. `ValidateBackupPath_Empty_MeetsQuality` — 「指定されていません」「入力してください」を含む
2. `ValidateBackupPath_TooLong_MeetsQuality_AndIncludesActualLength` — 実際の文字数を含む
3. `ValidateBackupPath_InvalidChars_MeetsQuality` — 禁止文字の例を含む
4. `ValidateBackupPath_RelativePath_MeetsQuality_AndIncludesExample` — 例（C:\）を含む
5. `ValidateBackupPath_UncMissingShare_MeetsQuality_AndIncludesExample` — `\\server\share` 例を含む
6. `ValidateBackupPath_UncEmptyServer_MeetsQuality` — 「サーバー名」「ホスト名」「IP」を含む
7. `ValidateBackupPath_UncEmptyShare_MeetsQuality` — 「共有名」を含む
8. `ValidateBackupPath_PathTraversal_MeetsQuality` — 既存メッセージ（既に3要素）の回帰検出
9. `ValidateBackupPath_UnreachableUnc_MeetsQuality` — Issue #1269 メッセージの回帰検出（スタブ注入）

`ValidateBackupPath_DriveNotReady_MeetsQuality` は実環境依存（存在しないドライブ
を物理的に再現できない）のため、Issue #1269 と同じスタブ注入パターンが使えない。
既存テストの作り（`DriveInfo` 依存）から該当パスを通すのが難しいため、
**ドライブ未準備のテストは新規追加しない**。代わりに対応するエラーメッセージは
他のテストと同じレビュー基準で目視確認する。

### 4. テスト件数表の同期（CI 自動検証あり）

`docs/design/07_テスト設計書.md` §1.1a の Common 配下の件数行および総件数を
新規追加テスト数だけ加算する。新規テストは 9 件を追加予定。

### 5. スコープ外

- 既存 `PathValidatorTests` の修正（既存テスト書き換えはユーザー事前承認が必要、
  `feedback_test_modification_approval` 参照。今回は後方互換キーワード保持により
  既存テスト修正なしで完結する設計）。
- `IOException.Message` を連結する例外文言。
- 他クラス（`SettingsValidator` 等）への波及。

## テスト戦略

1. **品質テスト**: 9 ケースの新規追加（上記）。`AssertQualityCriteria` で
   20 文字以上 / 句点 / 行動指示終止を機械的に検証。
2. **既存テスト**: 後方互換性キーワード（"指定されていません"・"長すぎます"・
   "絶対パス"・"サーバー名と共有名が必要"）を維持。「サーバー名が不正」「共有名が
   不正」は新メッセージで「サーバー名が指定されていません」「共有名が指定されて
   いません」に変わるため、Issue #1483 の `BothUncPrefixVariants_ProduceSameFormatVerdict`
   テストの OR 句を新キーワードに合わせて最小修正する。
3. **既存テスト最小修正の根拠**: `feedback_test_modification_approval` に従い、
   仕様変更に伴う既存テスト修正にはユーザー事前確認が必要。今回は Issue #1483 の
   テスト意図（プレフィックス両形式の等価判定）を維持する形で OR 句の語彙のみ
   更新する最小限の変更であり、設計時点でユーザー承認済み。

## 受入基準

- `PathValidator.cs` の全 `Failure(...)` メッセージが
  `AssertQualityCriteria`（20文字以上 / 句点 / 行動指示終止）を満たす。
- 新規 `PathValidatorErrorMessageQualityTests` 9 ケースが全てパス。
- 既存 `PathValidatorTests` が全てパス（修正なし、または事前承認済みの最小修正）。
- ビルド警告ゼロ。
- テスト件数表 §1.1a と実件数が一致（CI 自動検証）。
