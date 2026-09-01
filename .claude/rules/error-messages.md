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
- **ボタン行と幅を分け合うステータス欄も同様**に簡潔でよい（例: 帳票作成ダイアログ左下の `StatusMessage`。「事前チェック: 警告3件」「帳票作成を中止しました」）。直前のダイアログで「なぜ／どうすれば」を提示済みなら、ステータス欄で繰り返さない。§「最小文字数基準: 20文字以上」は `ValidationService` の Validator 文言に対する基準であり、これら表示領域が制約された箇所には適用しない（Issue #1688）。ただし文字数を詰めること自体を対策にせず、`TextWrapping="Wrap"` による折り返しを併せて担保すること（`.claude/rules/development-conventions.md` の UI/UX原則を参照）。

### `ToUserMessage` と `ErrorDialogHelper.GetErrorInfo` の役割分担（ドリフト監査 EM-R5-01）

例外種別から文言を引くマッピングは 2 か所にあり、**用途が異なる**ため意図的に併存させる:

| 変換 | 用途 | 文言の粒度 |
|------|------|-----------|
| `ExceptionMessageFormatter.ToUserMessage(ex, operation)` | **通常のエラー経路**（操作の失敗をステータス/ダイアログで案内し、ユーザーが自力で回復する） | 「何が／なぜ／どうすれば」の **3 要素**（操作名を含み行動指示で終わる） |
| `ErrorDialogHelper.GetErrorInfo(ex)` → `ShowFatalError` | **致命的エラーダイアログ**（継続不能・クラッシュ診断目的） | `SYS00x` エラーコード＋簡潔な「なぜ」。**併せて `StackTrace` を表示**して障害解析に供する |

`GetErrorInfo` の文言が 3 要素を満たさない（「どうすれば」を欠く）のは、致命エラー時はユーザーの自己回復より**エラーコード＋スタックトレースによる原因究明**を優先する設計判断であり、品質ガイドライン違反ではない。新規の**通常エラー**経路では `ToUserMessage` を使うこと（`GetErrorInfo` を通常経路に転用しない）。

### 生 `ex.Message` を是正するときは「UI 文言」と「ログ」を対で数える（Issue #1817）

`ex.Message` の露出を潰すと、**それまで唯一の技術的詳細の出口だったものが消える**。UI 文言だけ差し替えて終わると、失敗の原因がどこにも残らない状態になる。#1817 の 4 経路のうち 2 経路（`IncompleteBusStopDialog` の `Loaded`、`DataExportImportViewModel` のカードリーダー開始）は `ILogger` も `ErrorDialogHelper.LogException` も通っておらず、**「ログには出ている」すら成立していなかった** — 修正前は生の英語文言が画面に出るだけで、閉じれば何も残らない。

- **`ILogger` を持たない層（View コードビハインド・一部 ViewModel・`Common` の静的クラス）では `ErrorDialogHelper.LogException(ex, "操作名")` を使う**。`ILogger` 注入済みの層なら `_logger.LogError(ex, ...)`。**呼び出し元が既にログを出している経路（`LendingService.GetUserFriendlyErrorMessage` は `LendAsync` / `ReturnAsync` の `catch` が `LogError` 済み）では二重に出さない** — まず「この経路のログはどこで出ているか」を確認してから足す
- **集約ヘルパー（`ExceptionMessageFormatter.ToUserMessage`）へ委譲するか専用文言を持つかは、「表示先の制約」と「その場で取れる行動」の2つで決める**。**表示先**: 戻り値がトーストへ渡るなら、`ToUserMessage` のフル文言（例: `InvalidOperationException` で 58 文字）は文字サイズ「大」以上で末尾が切れる。**「どの UI 要素に出るか」を、戻り値を辿って確認してから決める**（`LendingService.GetUserFriendlyErrorMessage` → `LendingResult.ErrorMessage` → `MainViewModel._toastNotificationService.ShowError`）。**取れる行動**: `ToUserMessage` の `IOException` 分岐は「対象のファイルが他のプログラムで開かれていないか確認し」と案内するため、`PathValidator` の**フォルダーへの書き込みプローブ**にはそのまま使えない（原因は共有の切断・ディスク満杯で、その行動指示は実行できない）。#1757 の「取れる行動が違う経路には専用のファクトリ」を、集約ヘルパーへの委譲判断にも適用する。同じ罠は `default` 分岐でも起きる — カードリーダーのネイティブ失敗（`DllNotFound` / SEH / Win32）は必ず`default` の「しばらく待ってから再度実行してください」へ落ちるが、**未接続の PaSoRi は待っても繋がらない**。**近くにある既存文言の語彙に揃える**（「カードリーダーが接続されていません」／`MainViewModel` のnull フォールバック「もう一度タッチしてください」）と、経路間で案内が食い違わない
- **経路が単体テストから踏めないことは、回帰を持たない理由にならない**。`Window` のコードビハインド（STA 依存で xUnit から実行できない）はソーステキストの静的検査で、実機でしか再現しない I/O 失敗は**文言生成を純関数へ切り出して**固定する（#1794 と同じ形）。静的検査では**「禁止された形の不在」と「正しい形の存在」を対で表明する** — 不在だけを見ると、`catch` ごと消して無言で握りつぶす実装でも緑になる
- **「Issue に列挙された箇所」を着手前に main で検算する**。#1817 の 5 件のうち 1 件は起票後に別 Issue（#1806）で解消済みだった。Issue の一覧は起票時点のスナップショットである

### 同じ制約違反は「すべての経路」で同じ例外へ変換する（Issue #1757）

DB 制約（UNIQUE 等）の違反を捕捉してドメイン例外へ変換するとき、**その制約に触れる経路を全部列挙してから書く**。1 経路だけ変換すると、同じユーザー操作が経路によって「親切な案内」と「クラッシュ相当」に分かれる。

`CardRepository` は `INSERT` の UNIQUE 制約違反（`idx_card_type_number_active`）だけを `DuplicateCardNumberException` へ変換しており、`UPDATE` と復元には同等の catch が無かった。結果、**カード登録で管理番号が重複すれば「別の番号を指定してください」と案内されるのに、カード編集で同じことをすると生の `SQLiteException` が未処理例外ハンドラーまで抜けて「予期しないエラーが発生しました。／エラーコード: SYS999」**という、原因も回復手段も示さないダイアログになった。

- **「触れる経路」はテーブルへの書き込み文の形で列挙する**。#1757 の初版は INSERT と UPDATE の 2 経路だけを直したが、部分ユニークインデックス（`WHERE is_deleted = 0`）は**復元（`is_deleted` を 1→0 に戻す UPDATE）でも評価される**ため 3 経路目が残った。「登録／編集」という**画面の言葉**で数えると、この経路は視野に入らない
- **変換用のドメイン例外は `AppException` を継承させる**。`App.OnDispatcherUnhandledException` / `ErrorDialogHelper.GetErrorInfo` / `CsvImportService.ToUserFacingErrorMessage` はいずれも `AppException` を特別扱いして `UserFriendlyMessage` を使うため、**捕捉漏れがあっても「予期しないエラー（SYS999）」ではなく整備済みの案内へ倒れる**。文言も例外クラス 1 か所に集約でき、経路ごとの食い違いを構造的に防げる
- **ただし `AppException` 継承が守るのは文言だけ**。#1757 では `UpdateAsync` が `SQLiteException` を投げなくなったことで、CSV インポートの `catch (SQLiteException)` を通らなくなり、**行番号付きのエラーとして報告される形が失われた**（文言は `AppException` 分岐で保たれる）。**例外型の変更は、その型を前提にしていた上位の分岐を静かに外す**。「フォールバックがあるから大丈夫」と判断する前に、フォールバックが何を保存し何を捨てるかを見る
- **一括処理（CSVインポート等）では、行番号付きのエラーとして報告する**。復旧可能な入力ミスを結果全体のエラーにすると、利用者は「どの行を直せばよいか」が分からない。他のバリデーションエラーと同じ形に揃える
- **文言を 1 か所へ集約しても、「どうすれば」は経路によって変わり得る**。復元経路には管理番号の入力欄が無いため「別の番号を指定してください」は**実行できない指示**になる。集約した文言をそのまま流用せず、取れる行動が違う経路には専用のファクトリ（`DuplicateCardNumberException.ForRestore`）で「どうすれば」だけを差し替える。「何が」「なぜ」は共通のままでよい
- **エラー表示時に入力内容を消さない**。編集ダイアログで `CancelEdit()` 相当を呼ぶと、ユーザーは指摘された 1 項目だけを直して再保存できず、最初からやり直しになる
- 回帰テストは「重複を検出すること」だけでなく「**正当な操作を塞いでいないこと**」を対で固定する。前者だけだと、対象の操作を無条件に失敗させる実装でも緑になる（#1757 では「削除済みカードの番号は再利用できる」「番号を変えない更新は成功する」を併置）

### 「影響行数 0」は失敗ではなく競合 — 原因を名指しできる（Issue #1759）

`UpdateAsync` / `RestoreAsync` / `DeleteAsync` が `bool` で `false` を返すのは、WHERE 句（`is_deleted = 0` または `= 1`）に **1 行も一致しなかった場合だけ**である（#1753 で導入した影響行数による競合検出）。つまり原因は「対象行が別の状態へ変わった」に特定でき、**「更新に失敗しました」と書く理由がない**。

- **戻り値が `bool` の分岐を見たら、まずリポジトリの WHERE 句を読む**。`false` の意味が 1 つに定まるなら、その 1 つを文言に書く。#1759 では Card / Staff の両 ViewModel に同型の分岐が **7 か所**あり、いずれも 8〜9 文字の定型文だった
- **原因を断定する前に、その原因が成立する構成かを確認する**（`.claude/rules/development-conventions.md` と同じ判断）。「他のパソコンで削除されました」はローカルモードでは誤りになる。「他のパソコンや**別の操作**で〜した**可能性があります**」とモード中立に書く
- **操作ごとに「なぜ」を変える**。復元できなかった原因は「先に**復元**された」であって「削除された」ではない。文言を集約するときも `ForUpdate` / `ForRestore` / `ForDelete` を分け、互いの文言を含まないことをテストで表明する
- **「一覧を確認してやり直す」と案内するなら、案内する側が先に一覧を再読込する**（#1753）。再読込しないと同じエラーを繰り返す。文言が「再読み込みしました」と述べる以上、**再読込を先に実行してから**文言を設定する
- **エラー表示で入力内容を消さない**（#1757）。`CancelEdit()` 相当を呼ぶと、指摘された項目だけを直して再操作できない
- **「なぜ」が同じでも、「何が」が違えばファクトリを分ける**（Issue #1760）。払い戻しの競合は原因（対象行が削除された）が更新と同一だが、**「何が」は利用者が実際に行った操作で述べる**。`ForUpdate` を流用すると、払い戻しを試みた職員に「更新できませんでした」と出ることになり、自分の操作と結果が結び付かない。`ForRefund` を新設し、`Build` の共通テンプレートで「なぜ」「どうすれば」だけを共有する（#1757 の「取れる行動が違う経路には専用のファクトリ」の裏返しで、**行動が同じでも操作名が違えば分ける**）
  - 集約先のテストが**リフレクションで全ファクトリを列挙**していれば、ファクトリの追加は 3 要素の品質検証に自動で載る（`ConcurrencyConflictMessageTests`）。分けるコストが下がるので、迷ったら分ける
- **文言を長くしたら、その表示領域が「その状態で生きているか」を必ず確認する**（#1727 の「所在」）。#1759 では職員管理ダイアログのステータス欄が `Visibility="{Binding IsEditing}"` のパネル内にあり、**削除の結果表示は成功・失敗とも一度も表示されていなかった**（削除ボタンは非編集時にしか押せないため）。ViewModel のテストはこれを検出できないので、XAML テキスト上の静的検証を対で置く
- **「所在」を直したら「順序」も同時に確認する — 片方だけでは届かない**（#1759）。`CancelEdit()` は `StatusMessage` をクリアするため、完了メッセージをその前に設定すると表示領域を直しても消える。#1727 はこの順序をカード登録の 1 経路でのみ是正しており、**残る 10 経路（カード: 更新・削除・払戻・復元×2／職員: 登録・更新・削除・復元×2）の完了メッセージは一度も表示されていなかった**。**同じ画面の失敗パスを直すときは、成功パスが表示されていることを回帰テストで併せて固定する**（そうしないと、#1759 の初版のように「メッセージを見せるための修正」の中で見えないメッセージを見落とす）
  - **経路ごとの個別テストで守り切れないと分かったら、ソーステキストの静的検査へ移す**（Issue #1764）。上の順序ミスは #1727（登録 1 経路）→ #1759（残る 10 経路）→ #1764（起票時点の指摘）と **3 度再発**した。個別の `*_ShouldKeepCompletionMessage` は経路ごとの検査であり、**経路が増えたときの追随漏れを検出できない**。実際、両 ViewModel の `OnCardRead` 内にある復元経路は `Application.Current.Dispatcher.InvokeAsync` の内側にあるため **ViewModel 単体テストからは到達不能**で、個別テストが 1 件も無かった。`CompletionMessageOrderConventionTests` は「`CancelEdit();` を呼ぶ ViewModel すべて」を走査対象に導出し、同一ブロック内で `StatusMessage` 代入が `CancelEdit()` より前にある形を検出する
    - **走査対象をファイル名で列挙しない**。同じ形を持つ画面が追加されたときに検査から静かに漏れる（`.claude/rules/development-conventions.md` #1786 の「ガードを書くときは経路を列挙する」）
    - **判定は同一ブロックに限定する**。祖先ブロックまで遡ると、エラー分岐でその場に表示して `return` する正当な形（#1757）が同じメソッドの後続の成功分岐と衝突して誤検出になる
    - **コメントと文字列リテラルを剥がしてから検査する**。「`CancelEdit()` は `StatusMessage` をクリアするため〜」という**由来コメント自体**が違反として検出される（#1692 の極性の反転）。補間文字列の `{ }` も剥がさないとブロック対応がずれ、検査が別の場所を見る
    - **検出力は修正前のコードに当てて実測する**。#1764 では PR #1827 の親コミットに当てて 10 件を検出し、それが #1759 で**手作業により列挙した 10 経路**と一致することを確認した

### 「ログを併設する」と「ログを二重に出さない」は同じ数え上げで決まる（Issue #1991）

`ex.Message` の露出を潰した経路は 3 種類に分かれる。**経路ごとに、その catch が既にログを出しているかを見てから決める**。

| 経路 | 是正前のログ | 是正 |
|------|--------------|------|
| `CsvExportService`（5 箇所） | **無し**（`ILogger` 未注入） | ロガーを**既定値なしの必須引数**で注入し、`LogError` を併設（#1820） |
| `CsvImportService` の共通 catch（4 箇所） | 無し | 変換関数にログを併設 |
| `CsvImportService.Detail.cs` の明細置換失敗 | 直前で `_logger?.LogError` 済み | **ログを伴わない純粋な変換**を別に用意して使う |

- **変換の対応表とログの併設を同じ関数に閉じ込めると、既にログを出している経路が二重に記録する**。対応表は 1 つに寄せたまま（#1744）、ログの有無だけを 2 つの入口で分ける（`ToUserFacingErrorMessage` / `ToUserFacingErrorMessageCore`）
- **`ExceptionMessageFormatter.ToUserMessage` へ寄せる前に、その分岐の「どうすれば」が実行できるかを確かめる**（#1817）。CSV 取込の `IOException` は「対象のファイルが他のプログラムで開かれていないか確認し」がそのまま実行できる（Excel で開いたままの CSV が実原因の大半）ので寄せてよい。フォルダーへの書き込みプローブでは実行できないので寄せられない
- **静的検査の走査対象は、否定後読みで静かに縮む**。`(?<![A-Za-z0-9_.])Message\s*=` は `ErrorMessage =` に**原理的に一致しない**ため、#1986 の検査は同じ欠陥のもう半分を 1 件も見ていなかった。広げるときは `UserFriendlyMessage` のような別の接尾辞まで巻き込まないよう接頭辞を限り、**検出する形と検出しない形をサンプル入力で対に固定する**（#1786）
- **文言とログの表明は独立して効くように書く**。#1991 では、ログの併設だけを外す変異でログの表明 2 件だけが赤になり（文言の表明は緑）、文言だけを戻す変異では文言の表明だけが赤になることを実測した。片方が他方を巻き込んで赤くなる書き方だと、どちらが壊れたのかテスト名から読み取れない

### サービス内の「例外 → 文言」の対応表は 1 か所に集約する（Issue #1744）

同じ `catch` の ladder（`FileNotFoundException` → … → `catch (Exception)`）を複数のメソッドへ書き写さない。**次に対応表を変える人が、一部の経路を取りこぼす**。

- `CsvImportService` は共通ハンドラー 2 つと利用履歴の Import / Preview に同じ ladder を計 4 回持っており、#1744 で `FileOperationException` を足す際に 4 か所すべてへ同じ catch を書く必要があった（1 つ漏らすと `catch (Exception)` に落ちて生の `ex.Message` が UI に出る＝#1614 違反）。`catch (Exception)` 1 つから共通の変換関数を呼ぶ形へ統一した
- 集約すると `AppException` を一括で `UserFriendlyMessage` へ寄せられる。#1744 では副次的に、利用履歴経路だけ `DatabaseException` が生の `ex.Message` で表示されていた欠陥も同時に消えた
- **同じ規約が「文言の対応表」以外にも効く**: 何かを列挙するコードが 2 か所以上にできたら、それは片方だけ更新される日が来るという合図（`.claude/rules/development-conventions.md` の「全消し＋再生成」「ガードを書くときは経路を列挙する」と同じ判断）

## エラーコードは 1 つの原因だけを指す（Issue #1985）

エラーコード（`DB001` / `CR001` / `VAL001` …）は**職員が問い合わせで伝える識別子**であり、ログと
致命エラーダイアログ（`ErrorDialogHelper.ShowFatalError`）の障害調査の起点でもある。同じコードが
2 つの異なる原因に割り当たると、**受け取った側が原因を取り違える**。

- **採番は複数ファイルに分かれている**（`Common/Exceptions/` 配下の各例外クラスが自分で持つ）。
  新設時に「次の空き番号」を人手で探す限り衝突は再発する。実際 #1985 で
  `DatabaseException.InvalidStoredDate` に振った `DB008` が `DatabaseVersionMismatchException` と
  衝突していた（コードレビューでは検出されず、**ドキュメント同期の自問**で発見）
- 回帰は `ErrorCodeUniquenessConventionTests` が静的検査で固定する。走査対象は
  `Common/Exceptions/` 配下の全 `.cs` から**導出**する（ファイル名で列挙すると例外クラスが
  増えたときに静かに漏れる。#1786）。「重複が無いこと」だけでなく**既知のコードが実際に拾えること**を
  対で表明する — 抽出が 0 件に縮んだ状態でも緑になるため
- 検査は**コメントを除去してから**行う。「`DB001` は接続エラーに使用済み」という
  規約の理由を書いたコメント自体が重複として検出される極性の反転を避ける（#1692）

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
| `LedgerMergeServiceTests` / `LedgerSplitServiceTests` の各1件 | 履歴統合・分割の競合エラー文言（内部 ID を露出しないこと、原因と回復手段を含み行動指示で終わること、Issue #1753） |
| `ReportPreflightCheckerTests.AllWarnings_SatisfyErrorMessageQualityCriteria` | 帳票出力前プリフライトチェックの警告文言（5種別すべてを発生させ、`DisplayText` の情報量とカード名の明示、`DetailText` が行動指示で終わることを検証、Issue #1688） |
| `WarningServiceBackupHealthTests.BackupStaleWarning_SatisfiesErrorMessageQualityCriteria` | バックアップ健全性警告の文言（経過日数・最終成功日時の明示、原因候補、システム管理画面（F6）への誘導と行動指示、Issue #1689） |
| `ConnectionDiagnosticsServiceTests.AllProblemItems_SatisfyErrorMessageQualityCriteria` | 接続診断の警告・異常文言（8項目すべてを問題状態へ落とし、`DetailText` が20文字以上・行動指示で終わる・曖昧文言を含まないことを検証、Issue #1690） |
| `CsvImportServiceLedgerTransactionTests.ImportLedgersAsync_SQLiteエラー_生の例外メッセージをUIへ出さないこと` | 履歴CSVインポートの書き込み失敗（Issue #1745）。`SQLiteException` を `DatabaseException.QueryFailed` へラップしてから再スローすること（生のままだと `ToUserFacingErrorMessage` の `default` 分岐に落ちて `ex.Message` が漏れる）を、`NotContain("database is locked")` / `NotContain("予期しないエラー")` で表明 |
| `ConcurrencyConflictMessageTests` | 競合（影響行数 0）検出時の案内文言（`Common/ConcurrencyConflictMessage`、Issue #1759）。**全ファクトリをリフレクションで列挙**して 3 要素を検証し、操作ごとに「なぜ」が異なること（更新＝削除された／復元＝先に復元された／削除＝先に削除された）も表明する |
| `CsvImportServiceTests` の 2 件（`文字コード判別不能の…` / `宣言された文字コードで読めない…`） | CSVインポートの文字コードエラー（`FileOperationException.UndecidableEncoding` / `UnreadableDeclaredEncoding`）。判別に用いた候補（UTF-8 / Shift_JIS）と Excel の保存形式名を示し、行動指示で終わること、**ファイルパスをユーザー向け文言へ露出しない**ことを検証（Issue #1744） |

> **「判別できない」と「判別できたが読めない」で文言を分ける**（Issue #1744）: 原因が違えば「どうすれば」も違う。BOM が文字コードを宣言しているファイルに「文字コードを判別できませんでした。CSV UTF-8 形式で保存し直してください」と案内すると、**既にその形式であるファイルに対する無意味な指示**になり、真の原因（転送の失敗・破損）から利用者を遠ざける。品質テストは互いの文言を含まないこと（`NotContain("判別できませんでした")`）も表明し、取り違えを検出する。

> **品質テストは「対象の網羅」も併せて表明する**: 診断・チェック系のように項目が増えていくサービスでは、文言の品質だけを検証すると項目追加時に品質テストの追随漏れが静かに起きる。`ConnectionDiagnosticsServiceTests` は全項目種別が問題状態として集まっていること（`Enum.GetValues(typeof(DiagnosticItemKind)).Length` と一致）を同じテスト内で表明し、項目を足したのに文言を検証していない状態を検出する。

> **バリデーション以外の「警告文言」も本ガイドラインの対象**: プリフライトチェックのように、入力値の妥当性ではなく**データの状態**を警告する文言も 3 要素を満たすこと。「何が」＝どのカードのどの行か（カード名・利用日・摘要・金額）、「なぜ」＝帳票にどう影響するか、「どうすれば」＝どの画面で何を直すか。専用の品質テストクラスを新設せず、対象サービスのテストクラス内に品質テストを1件置く形でよい。
