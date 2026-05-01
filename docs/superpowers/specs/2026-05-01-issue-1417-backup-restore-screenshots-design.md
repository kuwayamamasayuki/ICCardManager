# Issue #1417: 管理者マニュアル §6.1 / §6.2 バックアップ完了通知・リストア一覧のスクリーンショット追加

## 背景

管理者マニュアル §6.1「バックアップとリストア」では「システム管理画面 (`system.png`)」のスクリーンショットのみ配置されている。バックアップ完了時の通知や、§6.2「リストア（復元）」で参照するバックアップ一覧の見え方・選択方法、外部ファイル指定時のファイル選択ダイアログのスクリーンショットが無い。

リストアは**他のすべてのPCでアプリ終了が必要**な不可逆的影響のある操作のため、操作前にダイアログ表示を確認できると安全性が向上する。

該当箇所: `ICCardManager/docs/manual/管理者マニュアル.md` §6.1「手動バックアップ」/ §6.2「リストア（復元）」(行 879〜905 付近)

## 実装側の状態（事前調査）

### バックアップ完了の通知方式 (実装バグ発覚)

`ViewModels/SystemManageViewModel.cs:132` 付近で完了時に以下を呼ぶ:

```csharp
SetStatus($"バックアップを作成しました: {Path.GetFileName(dialog.FileName)}", false);   // L132
await _operationLogger.LogBackupAsync(dialog.FileName);                                      // L135
await LoadBackupsAsync();                                                                    // L138
```

つまり**完了通知は専用ダイアログではなく、システム管理画面下部のメッセージバナー (緑背景の Border) へのメッセージ表示**で実装されている。

ところが本 Issue の対応中に**実装バグ**が発覚した: L138 で呼ばれる `LoadBackupsAsync` が内部で再度 `SetStatus($"{BackupFiles.Count}件のバックアップが見つかりました", false)` を呼んでおり、L132 で設定した完了メッセージが**即座に上書きされる**ため、ユーザーは「バックアップを作成しました: <ファイル名>」を**画面で目にすることができない**。実際に表示されるのは「○件のバックアップが見つかりました」という件数表示で、完了通知としての意味が薄い。

このバグは Issue 概要の「バックアップ完了時の通知」という観点を実質的に空文化させているため、**本 Issue のスコープに実装バグ修正を統合する** (方針 α 採用)。マニュアル文言・撮影画像・実装の 3 点を整合させる対応となる。

### システム管理画面 (リストア一覧 / ファイル指定)

`Views/Dialogs/SystemManageDialog.xaml`:

| 行 | 構造 |
|---|---|
| 56-90 | 「手動バックアップ」GroupBox（`バックアップを作成` / `バックアップフォルダを開く` ボタン、最後に作成したバックアップ表示） |
| 91-160 | 「リストア（データ復元）」GroupBox（バックアップファイル一覧 ListBox + `選択したバックアップからリストア` / `ファイルを指定してリストア` / 一覧更新 ボタン） |
| 168-180 | ステータスメッセージ TextBlock (`StatusMessage` バインド、`StringToVisibilityConverter` で空のとき非表示) |

「ファイルを指定してリストア」は ViewModel の `RestoreFromFileAsync()` (行 284 付近) から OS 標準の `OpenFileDialog` を起動する。

### TakeScreenshots.ps1 の事前準備状況

PR #1427 により本 Issue 用エントリは **既に追加済み** (行 488-506):

| Name | Title | Instructions |
|---|---|---|
| `backup_completed_status.png` | 手動バックアップ完了ステータス表示 | F6キーでシステム管理画面を開き「バックアップを作成」をクリック、ステータスバーに「バックアップを作成しました: <ファイル名>」と表示されたら（完了通知ダイアログは現状未実装） |
| `restore_list.png` | リストア用バックアップ一覧 | システム管理画面で「リストア」をクリックし、バックアップファイル一覧（ファイル名・タイムスタンプ・選択状態）が表示されたら |
| `restore_file_dialog.png` | 「ファイルを指定してリストア」選択ダイアログ | リストア画面で「ファイルを指定してリストア」を選択し、ファイル選択ダイアログが表示されたら |

ps1コメント (行 488) に「完了通知ダイアログは現状未実装」と明記されており、撮影者にも誤解が生まれない設計になっている。

## スコープ判断: 実装バグを修正し文書・撮影画像と整合させる (方針 α)

当初は「文書を現状実装(=ステータスバー表示)に整合させる」(方針 A) で進めようとしたが、レビュー段階で**実装側に「完了メッセージが上書きされて表示されないバグ」**が発覚。マニュアル文言と ps1 Instructions が指す「バックアップを作成しました: <ファイル名>」というメッセージは現実には UI に映らず、撮影者が ps1 通りに撮影しようとしても永遠に再現できない状態だった。

3 つの方針を比較:

|  | α 案（採用） | β 案 (旧 A) | γ 案 (旧 B) |
|---|---|---|---|
| 方針 | 実装バグを修正して完了メッセージが実際に表示されるようにし、文書・画像と一致させる | 文書を「○件のバックアップが見つかりました」(現状の実際の表示)に揃える | 専用「完了通知ダイアログ」を新規実装 |
| 実装変更 | `LoadBackupsAsync` を `LoadBackupsInternalAsync(announceCount)` に分離。`CreateBackupAsync` は `announceCount: false` で呼ぶ。SaveFileDialog 後の処理を `internal` メソッド `CreateBackupCoreAsync(string)` に抽出してテスト可能化 | なし | C# / XAML 追加、UI 設計変更 |
| 単体テスト追加 | `CreateBackupAsync_Success_StatusMessageが完了通知のまま` ほか | なし | 必要 |
| Issue 意図との整合 | ◯ 完了通知が機能する形を保証 | △ 完了表示としての意味が薄い件数表示で妥協 | ◎ Issue 原文「完了通知ダイアログ」と完全一致 |
| スコープ膨張 | 小 (LoadBackupsAsync の局所改修 + テスト) | 最小 (Markdown のみ) | 大 (UX 設計判断 + 連続バックアップ運用への影響) |
| `type: documentation` ラベル | `bug` 追加が妥当 | 維持 | `enhancement` へ変更 |

α 案を採用する理由:

1. 撮影される画像が「実際に存在する UI 状態」になることを保証する。文書・画像・実装の 3 点が一致
2. 修正は `LoadBackupsAsync` の internal 分離だけで局所的。既存テスト(`LoadBackupsAsync_BackupExists_StatusMessageに件数表示`)の挙動も変えない
3. `SaveFileDialog` 直後の処理を internal 化することで、これまで存在しなかった `CreateBackupAsync` の単体テストが書けるようになる (PR #1383 で同様の MessageBox→IsBusy バグを直した実績があり、同じ予防効果)
4. 完了通知ダイアログ (γ) は別 Issue で議論すべき設計判断 (連続バックアップ運用での煩雑さ・トースト通知との整合等)。本 Issue では γ ではなく α を採用してスコープ膨張を防ぐ

### スコープから除外する関連バグ

`RestoreFromFileAsync` (L386) も同パターンで `await LoadBackupsAsync()` が「リストアが完了しました」を上書きする。ただしこちらは MessageBox による「リストア完了」モーダル通知 (L378-383) が補助しており、ユーザーへの完了伝達は維持されている。本 Issue 概要は「バックアップ完了通知」が主軸のため `RestoreFromFileAsync` の同パターンは別 Issue で扱う方針とし、本 PR では触れない (CHANGELOG / PR 説明には言及して将来の発見しやすさを確保)。

## 修正対象

### `ICCardManager/src/ICCardManager/ViewModels/SystemManageViewModel.cs`

| 場所 | 修正 |
|---|---|
| `LoadBackupsAsync` (行 67〜93) | 内部実装を `LoadBackupsInternalAsync(bool announceCount)` に切り出し、`LoadBackupsAsync` (`[RelayCommand]`) は `announceCount: true` で委譲。`announceCount=false` のときは件数を `SetStatus` しないように分岐を追加 (catch のエラー報告は維持) |
| `CreateBackupAsync` (行 100〜150) | `SaveFileDialog` 後の本体処理を `internal virtual Task CreateBackupCoreAsync(string backupFilePath)` メソッドに抽出 (テスト可能化目的、`InternalsVisibleTo` 既設)。本体内の `await LoadBackupsAsync();` を `await LoadBackupsInternalAsync(announceCount: false);` に置換し、完了メッセージが上書きされないようにする |

抽出後の `CreateBackupCoreAsync` シグネチャ:

```csharp
internal virtual async Task CreateBackupCoreAsync(string backupFilePath)
{
    using (BeginBusy("バックアップを作成中..."))
    {
        try
        {
            var success = await _backupService.CreateBackupAsync(backupFilePath);
            if (success)
            {
                LastBackupFile = backupFilePath;
                SetStatus($"バックアップを作成しました: {Path.GetFileName(backupFilePath)}", false);
                await _operationLogger.LogBackupAsync(backupFilePath);
                // Issue #1417: 完了メッセージを LoadBackupsAsync の件数報告で上書きさせない
                await LoadBackupsInternalAsync(announceCount: false);
            }
            else
            {
                SetStatus("バックアップの作成に失敗しました", true);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"バックアップの作成に失敗しました: {ex.Message}", true);
        }
    }
}
```

### `ICCardManager/tests/ICCardManager.Tests/ViewModels/SystemManageViewModelTests.cs`

以下のテストを追加:

| テスト名 | 目的 |
|---|---|
| `CreateBackupCoreAsync_Success_StatusMessageが完了通知のまま` | `IBackupService.CreateBackupAsync` を成功・`GetBackupFilesAsync` を 3 件返却に Setup し、`CreateBackupCoreAsync` 完了後の `StatusMessage` が「バックアップを作成しました: <ファイル名>」で始まることを検証 (件数表示で上書きされないことの回帰防止) |
| `CreateBackupCoreAsync_Success_LastBackupFileが設定される` | `LastBackupFile` プロパティが渡したパスに更新されることを検証 |
| `CreateBackupCoreAsync_Failure_StatusMessageに失敗が含まれる` | `IBackupService.CreateBackupAsync` を `false` 返却に Setup し、`StatusMessage` が「バックアップの作成に失敗しました」で `IsStatusError=true` になることを検証 |
| `CreateBackupCoreAsync_Exception_StatusMessageに例外メッセージが含まれる` | `IBackupService.CreateBackupAsync` から例外をスローさせ、`StatusMessage` に例外文言が含まれ `IsStatusError=true` になることを検証 |

`SaveFileDialog` を直接呼ぶ `CreateBackupAsync` (RelayCommand 公開メソッド) は xUnit 環境では実行不能 (UI スレッドを要求) のため、`internal virtual` で抽出した `CreateBackupCoreAsync` をテストする。

### `ICCardManager/docs/manual/管理者マニュアル.md`

#### §6.1「手動バックアップ」(行 897〜905 付近)

step 3 の文言と直後に画像参照を追加:

```diff
 1. 「システム管理」画面（F6）を開きます
 2. 「手動バックアップ」セクションの「バックアップを作成」ボタンをクリックします
-3. バックアップが設定済みの保存先に作成されます
+3. バックアップが設定済みの保存先に作成され、ステータスバーに「バックアップを作成しました: <ファイル名>」と表示されます
+
+   ![手動バックアップ完了時のステータス表示（ステータスバーに完了メッセージ）](../screenshots/backup_completed_status.png){width=50%}

 バックアップファイル: `backup_YYYYMMDD_HHMMSS.db`
```

これにより**「完了通知ダイアログ」という誤ったメンタルモデルをユーザーに与えない**。

#### §6.2「リストア（復元）」(行 915〜922 付近)

step 2 直後にバックアップ一覧画像、「ファイルを指定してリストア」段落直後にファイル選択ダイアログ画像を追加:

```diff
 1. 「システム管理」画面（F6）を開きます
 2. 「リストア（データ復元）」セクションのバックアップ一覧から復元したいファイルを選択します
+
+   ![リストア用バックアップ一覧（ファイル名・タイムスタンプ・選択状態）](../screenshots/restore_list.png){width=50%}
+
 3. 「選択したバックアップからリストア」ボタンをクリックします
 4. 確認ダイアログで「はい」をクリックします

 外部のバックアップファイルから復元する場合は、「ファイルを指定してリストア」ボタンをクリックし、ファイルを選択します。
+
+![「ファイルを指定してリストア」のファイル選択ダイアログ](../screenshots/restore_file_dialog.png){width=60%}

 > **警告**: リストアを実行すると、現在のデータは上書きされます。
```

### 画像幅の根拠

| 画像 | width | 根拠 |
|---|---|---|
| `backup_completed_status.png` | 50% | 既存 §6.1 の `system.png` と同幅。ステータスバー文字が読める最低限を確保しつつ章内一貫性を維持 |
| `restore_list.png` | 50% | バックアップ一覧の列(ファイル名 / タイムスタンプ)判読性。`system.png` と一貫 |
| `restore_file_dialog.png` | 60% | OS 標準ファイル選択ダイアログは横長で、左ペイン+ファイルリスト+パス入力欄+ボタンの可視性のため僅かに広く |

直近 Issue #1415 (40〜80%) / #1416 (85%) の前例の中で「同一画面に映る要素は同幅」「OS外観のダイアログは少し広め」のルールに従う。

### `ICCardManager/CHANGELOG.md`

`## [Unreleased]` セクションの「変更」配下に以下 1 行を追加:

```
- 管理者マニュアル §6.1「手動バックアップ」/ §6.2「リストア（復元）」にバックアップ完了ステータス・リストア用バックアップ一覧・ファイル選択ダイアログのスクリーンショット参照（`backup_completed_status.png` / `restore_list.png` / `restore_file_dialog.png`）を追加 (Issue #1417)
```

§6.1 step 3 の文言修正(「ステータスバーに『バックアップを作成しました: <ファイル名>』と表示されます」)も同行で実装と文書の整合確保として記述する。

### `ICCardManager/tools/TakeScreenshots.ps1`

**変更なし** (PR #1427 で既設のエントリ行 488-506 をそのまま流用)。

## 作業順序

1. `git checkout main && git pull`（**完了**）
2. ブランチ作成: `docs/issue-1417-backup-restore-screenshots`（**完了**）
3. 設計書（本ファイル）をコミット（**完了**: `d711a32`）
4. マニュアル §6.1 / §6.2 編集 + CHANGELOG 編集（**完了**: `43e22e6`）
5. push → PR 作成（**完了**: PR #1442）
6. **本セッションで追加**: 実装バグ発覚 → 設計書/計画書を α 案へ更新
7. `SystemManageViewModel.cs` の修正実装
8. `SystemManageViewModelTests.cs` にテスト追加
9. ビルド・テスト通過確認
10. CHANGELOG を「ドキュメント」セクションのみから「バグ修正」セクションへ追記分離 (整合性更新)
11. 追加コミット → push (PR #1442 に追加)

## テスト方針

α 案でコード変更を含むため、単体テストを追加。

ユーザーへの動作確認依頼:

1. 当ブランチを checkout
2. テスト用バックアップを準備:
   - `restore_list.png` 用: 「バックアップを作成」を 2〜3 回実行して一覧に複数行表示される状態にし、1 つを選択した状態で撮影
   - `backup_completed_status.png` 用: 「バックアップを作成」直後 (ステータスバーにメッセージが表示されたタイミング) に撮影
   - `restore_file_dialog.png` 用: 「ファイルを指定してリストア」をクリックしてファイル選択ダイアログが開いた状態で撮影
3. PowerShell で:
   ```powershell
   .\tools\TakeScreenshots.ps1 -Only backup_completed_status,restore_list,restore_file_dialog
   ```
4. `ICCardManager/docs/screenshots/` に 3 ファイルが保存されることを確認
5. `ICCardManager/docs/manual/管理者マニュアル.md` の Markdown プレビューで以下を確認:
   - §6.1 step 3 直後に `backup_completed_status.png` が表示される
   - §6.2 step 2 直後に `restore_list.png` が表示される
   - §6.2 「ファイルを指定してリストア」段落直後に `restore_file_dialog.png` が表示される
6. 撮影画像の追加コミットは別途実施 (Issue #1415 / #1416 と同パターン)

## スコープ外

- 画像本体ファイルのコミット (撮影後にユーザーが追加コミット)
- 完了通知ダイアログ (専用モーダル) の新規実装: γ 案で議論したが、連続バックアップ運用での煩雑さやトースト通知との UX 整合等を別 Issue で扱う
- `RestoreFromFileAsync` の同種上書きバグ (L386): MessageBox による補助通知があるため危険度低。別 Issue で対応 (CHANGELOG / PR 説明に言及して将来の発見しやすさを確保)
- `restore_completed_status.png` (リストア完了通知): Issue 概要に明記なし & 不可逆操作の影響説明は §6.2 の警告文で既に十分
- §6.3「データエクスポート」/§6.4「データインポート」等他節への波及修正
- ps1 既存エントリの Instructions 文言精緻化 (現行で必要十分。バグ修正後は「ステータスバーに表示されたら」が**実際に再現可能**になる)
