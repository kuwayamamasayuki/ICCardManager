# スクリーンショット

このディレクトリには、ユーザーマニュアル・管理者マニュアルで使用するスクリーンショットを配置してください。

## 必須スクリーンショット（7枚）

| ファイル名 | 内容 | 使用先 |
|------------|------|--------|
| `main.png` | メイン画面（待機状態） | ユーザーマニュアル |
| `staff_recognized.png` | 職員証認識後の画面 | ユーザーマニュアル |
| `lend.png` | 貸出完了画面 | ユーザーマニュアル |
| `return.png` | 返却完了画面 | ユーザーマニュアル |
| `history.png` | 履歴照会画面 | ユーザーマニュアル |
| `card.png` | 交通系ICカード管理画面 | 管理者マニュアル |
| `staff.png` | 職員管理画面 | 管理者マニュアル |

## オプションスクリーンショット（11枚）

| ファイル名 | 内容 | 使用先 |
|------------|------|--------|
| `report.png` | 帳票出力画面 | ユーザーマニュアル、管理者マニュアル |
| `settings.png` | 設定画面 | ユーザーマニュアル、管理者マニュアル |
| `system.png` | システム管理画面 | 管理者マニュアル |
| `export.png` | データ入出力画面 | 管理者マニュアル |
| `busstop.png` | バス停名入力ダイアログ | ユーザーマニュアル |
| `ledger_detail_merge.png` | 履歴の統合分割（履歴詳細） | ユーザーマニュアル |
| `history_merge.png` | 履歴の統合（履歴一覧） | ユーザーマニュアル |
| `incomplete_busstop.png` | バス停名未入力の履歴一覧 | ユーザーマニュアル |
| `print_preview.png` | 帳票プレビュー画面 | ユーザーマニュアル |
| `report_excel.png` | 物品出納簿（Excel出力例） | ユーザーマニュアル |
| `installer_options.png` | インストーラーオプション選択画面 | 管理者マニュアル |
| `card_registration_mode.png` | カード登録方法の選択画面 | 管理者マニュアル |

## スクリーンショットの撮影方法

### 完全自動撮影（UI テスト基盤、29 枚）

UI テスト基盤（FlaUI）でアプリを起動し、画面を開く操作まで含めて自動で撮影します。人の操作は不要です。

```powershell
# Windows ネイティブの PowerShell から実行（WSL2 不可）
cd D:\OneDrive\交通系\src\ICCardManager

# 1. docs\screenshots\auto\ へ撮影（既存画像は上書きしない）
.\tools\take-screenshots-uitest.ps1

# 2. auto\ の画像を見比べて問題なければ、撮影せずにそのまま docs\screenshots\ へ上書きコピー
.\tools\take-screenshots-uitest.ps1 -Publish
```

| パス | 対象 | ファイル |
|------|------|---------|
| Release | メイン画面（待機状態／システム警告あり） | `main.png` `main_with_warnings.png` |
| Release | 履歴照会画面（通常／統合対象の選択） | `history.png` `history_merge.png` |
| Release | ツールバーから開くダイアログ | `card.png` `staff.png` `report.png` `export.png` `settings.png` `admin_dashboard.png` |
| Release | システム管理から開く二段目のダイアログ | `operation_log.png` `transfer_station_groups.png` |
| Release | 帳票の事前チェック結果 | `report_preflight.png` |
| Release | バス停名未入力の履歴一覧 | `incomplete_busstop.png` |
| Release | 画面の一部だけを撮るもの（要素の矩形で撮影） | `card_list_status_mixed.png`（カード一覧）`error_no_reader.png`（ステータスバーの接続状態と再接続ボタン） |
| Debug | 職員証認識・貸出完了・返却完了（メイン画面と画面隅のトーストの両方を含む矩形で撮る） | `staff_recognized.png` `lend.png` `return.png` |
| Debug | トースト単体（概要版マニュアル用。職員証認識は写真の職員証の氏名で撮る） | `toast_staff_recognized.png` `toast_lend.png` `toast_return.png` |
| Debug | 返却後に開くダイアログ | `busstop.png` `companion_count.png` |
| Debug | 仮想タッチ（DEBUG 限定の機能そのもの） | `virtual_touch.png` |
| Debug | 職員証認証を経る画面 | `system_lend.png` `ledger_row_edit.png` |
| Debug | バックアップの保存先・DB のパスを表示する画面（撮影用の置き場所で撮る） | `system.png` `restore_list.png`（リストア用バックアップ一覧、1 件選択した状態）`connection_diagnostics.png` |

動作の要点：
- 本体を Release と Debug の両方でビルドし、2 パスで撮る。タッチを要する画面は仮想タッチ（Debug 限定）で再現し、撮影モード（環境変数 `ICCARDMANAGER_SCREENSHOT_MODE=1`）で仮想タッチパネルを透明にし、起動時のテストデータ自動登録を止める
- 既存の DB（`%ProgramData%\ICCardManager\iccard.db`）は撮影中だけ退避し、終了後に復元する。撮影が途中で落ちた場合は、退避ファイル（`iccard.db.uitest-backup`）が残り、次に UI テストを実行したときに書き戻される
- ピッすい（インストール版・デバッグ起動）が DB を開いていると、撮影は DB に何も触れずに中止する。ピッすいを終了してから実行する（開いたまま退避すると、古い退避ファイルが残って次回の実行が DB を巻き戻すため）。撮影・UI テストの最中にもピッすいを起動しない（起動したピッすいはテスト用の DB を開くので、そこで記録したものは元の DB に残らず、終了時の復元も失敗して次回に書き戻される）
- `database_config.txt` で DB の保存先を変えている PC では撮影を中止する（アプリが退避していない DB を開くため）。設定画面（F5）の「デフォルトに戻す」で既定に戻してから実行する
- 職員 2 名・交通系ICカード 3 枚（通常／貸出中／残額不足）と当月の利用履歴をサンプルとして投入する（`tests/ICCardManager.UITests/Infrastructure/ScreenshotSeedData.cs`）
- 出力先 `docs\screenshots\auto\` は Git 管理外。撮影結果は既存画像とサイズを見比べてから `-Publish` で差し替える。`-Publish` は撮影し直さず、`auto\` にある画像をそのままコピーする（確認した画像と差し替える画像が同じであることを保証するため）。コピーするのは**対応表に載っている画像だけ**で、失敗時の切り分け用に残る `*_FAILED.png` のような対象外の成果物は公開されない
- **「撮れた」の根拠は出力先のファイルの存在ではない**（Issue #2095）。`auto\` は Git 管理外で前回の実行の成果物が残り続けるため、撮影が漏れた画像は残骸がそのまま再公開され、内容が前回と同じなら `git diff` にも現れない。撮影スクリプトは撮影の前に対象画像（と `*_FAILED.png`）を `auto\` から消し、撮影後に「今回作られた画像」をマニフェスト `auto\.screenshot-manifest.json` へ記録する。`-Publish` はこれを読んで、今回の撮影で作られていない画像を公開しない（判定は `tools\screenshot-capture-manifest.ps1` の 1 か所）
- **撮影テストがスキップされた画像は完了報告で分けて表示される**。xUnit の `Skip.If` はテスト成功扱いなので `dotnet test` の終了コードでは「撮れなかった」と「撮る必要が無かった」が区別できない。完了報告は「今回の実行で撮影 N 枚 / 対象 K 枚」と、撮れなかった画像名を並べる（**終了コードは変えない** — 環境の都合で撮れないだけのスキップを失敗にすると、正常な開発機で毎回赤くなる。区別は報告と `-Publish` の公開判定で付ける）。`error_no_reader.png` のように**その環境では原理的に撮れない画像**が対象に入っている場合の `-Publish` の振る舞いは 2 通りに分かれる:
  - **全撮影の `-Publish`** は、撮れた分だけ公開して撮れなかった画像を名指しで警告する。PaSoRi を繋いだ開発機では `error_no_reader.png` が必ずスキップされるため、全か無かにすると全撮影の公開が恒久的にできなくなる
  - **`-Changed -Publish`** は 1 枚でも欠けていれば**何も公開せずに止まる**。`-Changed` は「この画像を撮り直す」と名指しして走るので、欠けたまま一部だけ公開すると「撮り直したつもりの画像」が古いまま残る
  - いずれの場合も**今回の撮影で作られていない画像は公開されない**。撮れる環境で撮り直してから公開すること
- ステータスバーの「リーダー:」は撮影した PC の接続状態がそのまま写る（未接続なら「切断」）。警告欄もその PC の状態（journal_mode の低下など）を含み得るので、差し替え前に確認すること
- 更新の案内（「新しいバージョン … が公開されています」）は写らない。撮影の間は、DB と同じフォルダーの `latest_version.txt` を DB と一緒に退避して取り除き、終わったら元へ戻す（撮影機に残ったこのファイルが案内を写していた）。撮影テスト（`main` / `main_with_warnings` / `history` / `history_merge` / `staff_recognized` / `lend` / `return`）は撮った後に案内が出ていないことを確かめ、出ていればその画像を `*_FAILED.png` へ改名して撮影を失敗にする（公開対象にならない）。このファイルが読み取り専用・使用中だと、撮影は何も触らずに中止して対処を案内する
- 返却の撮影（`return` / `busstop` / `companion_count`）は、仮想タッチの「現在残高」へ「台帳の最新残高 − 運賃」（20,000 − 200 ＝ 19,800）を入れて返却する。既定値（台帳の最新残高）のまま返却すると「払出 200 なのに残高が変わらない」行が記録される（Issue #2425）。撮った後に撮影用の DB の残高チェーン（前行の残額 ＋ 受入 − 払出 ＝ 残額）を確かめ、切れていればその画像を `*_FAILED.png` へ改名して撮影を失敗にする。シードの残高を変えるときは `ScreenshotSeedData` の定数（`VirtualTouchCardSeededBalance`・`VirtualTouchFare`）を変える
- 撮影で起動したアプリの自動バックアップは、本番の既定のバックアップ先には作られない（既定の場所へ作ると、同じ日の実 DB のバックアップが整理で押し出されていた）。撮影の終わりに既定の場所のファイル一覧が変わっていたら、その撮影は失敗になる
  - 多くの撮影では、UI テスト専用のフォルダー `C:\ProgramData\ICCardManager.UITests\backup` に作られる（画面に保存先が写らない撮影）
  - バックアップの保存先・DB のパスを表示する画面（`system.png`・`restore_list.png`・`connection_diagnostics.png`）は Debug パスで撮り、アプリの置き場所ごと撮影用のフォルダー `C:\ProgramData\ICCardManager.UITests\screenshot-data` へ移して動かす（撮影モードの DEBUG ビルドだけが読む環境変数 `ICCARDMANAGER_SCREENSHOT_DATA_ROOT`）。このときアプリは、画面に出すパスを本番の置き場所の配下として表示する（「保存先: C:\ProgramData\ICCardManager\backup」「現在のデータベース: C:\ProgramData\ICCardManager\iccard.db」）。読み替えるのは表示だけで、Release ビルドには入らない（実際の画面は常に実際に使っている保存先を表示する）
  - 撮影用のフォルダーは撮影のたびに空にするので、`restore_list.png` の一覧と「N件のバックアップが見つかりました」・「保持世代」は撮影機の撮影の履歴に左右されない（その撮影の起動が作った世代だけが並ぶ）。「保存先の空き容量」・接続診断の「空きディスク容量」は撮影機のドライブの値になる
  - 3 枚の撮影は、撮った後に画面のどこにも UI テスト専用のフォルダー（`ICCardManager.UITests`）のパスが出ていないこと（UI Automation で読んだ文字。スクロールの外の欄も含む）を確かめ、あわせて既定のパスが出ていることを確かめる — `system.png`・`restore_list.png` は「保存先」が `C:\ProgramData\ICCardManager\backup` であること、`connection_diagnostics.png` は「データベース到達性」の項目を選んでから撮り、詳細欄が `C:\ProgramData\ICCardManager\iccard.db` を示していること（初期選択は問題のある最初の項目で、撮影機のリーダーの状態に左右されるため選び直す）。違えばその画像を `*_FAILED.png` へ改名して撮影を失敗にする（公開対象にならない）
  - `settings.png`（設定）: バックアップ先の入力欄は撮影範囲（スクロールの外）に入っていないので写らない。入力欄は編集する値なので読み替えない（この画像は Release パスで撮り、入力欄には UI テスト専用のフォルダーが入っている）。ツールバーから開くダイアログの撮影は、撮った後に**画面内に見えている要素**に UI テスト専用のフォルダーのパスが出ていないことを確かめ、出ていればその画像を `*_FAILED.png` へ改名して撮影を失敗にする（配置が変わって入力欄が写る範囲に入ったら気付ける。「見えている」は祖先の矩形との重なりで判定する — WPF の UIA の IsOffscreen はスクロールの外の要素にも false を返す）。設定画面の「※ 空欄の場合、このPCの … にバックアップされます」の注記は読み取り専用なので読み替えを通す
- **投入データで作れない外部状態に依存する画像は自動撮影の対象外**。`error_no_reader.png` は撮影機に PaSoRi が繋がっていると「接続済み」の画像ができてしまうため、切断でなければ撮らずに Skip する。共有モードのネットワーク切断（`warning_network_disconnected.png`）と、OS 標準の保存ダイアログを経る手動バックアップ（`backup_completed_status.png`）は対話式スクリプトに残している
- **職員証認証（`StaffAuthDialog`）を経る画面は Debug パスでしか撮れない**。履歴行の追加・貸出記録の作成は監査対象の操作で職員証のタッチを要求し、実カードリーダーの無い環境では DEBUG 限定の仮想タッチでしか通せない
- 撮影中はアプリのウィンドウが画面左上へ移動して前面に出る。マウス・キーボードに触れないこと
- **トースト単体の画像（`toast_*.png`）は角が透けて、撮影時の背後のデスクトップが写る**。トーストは角の丸いウィンドウで、矩形で撮ると角の外側に背後の画面がそのまま入る。`-Publish` の前に `auto\` の画像の四隅を確かめ、別のウィンドウ（ブラウザー・アイコン等）が写っていれば、既存の画像を下地に文字の領域だけを差し替えて公開する（Issue #2243 の `toast_staff_recognized.png` はこの方法で公開した）。撮影前にほかのウィンドウを最小化しておけば壁紙だけが写る
- **管理者権限で動いているウィンドウを前面にしたまま実行しない**。Windows が前面化と入力注入を拒否するため、クリック・キー入力を要する撮影（履歴照会）が「アプリのウィンドウを前面にできません」で失敗する。そのウィンドウを閉じるか最小化してからやり直す
- テストプロセスを DPI 対応にして物理ピクセルで撮るため、表示スケール 150% では 100% の 1.5 倍の寸法になる。既存画像と寸法を揃えたいときは表示スケールを 100% にして実行する

下記の対話式スクリプトにしか定義の無い画面（帳票プレビュー・インストーラーなど）は、引き続き対話式で撮影します。

### 画面を変えたときの撮り直し（変更検知、Issue #2021）

どの画像を撮り直すかは、対応表 `screenshot-sources.json`（画像 → その画面を構成するソースファイル）と git の差分から自動で決まります。

```powershell
cd D:\OneDrive\交通系\src\ICCardManager

# 1. origin/main との差分から影響を受けた画像だけを docs\screenshots\auto\ へ撮影
.\tools\take-screenshots-uitest.ps1 -Changed

# 2. 見比べて問題なければ、その画像だけを docs\screenshots\ へ上書きコピー
.\tools\take-screenshots-uitest.ps1 -Changed -Publish

# 比較元を変えるとき（例: ローカルの main）
.\tools\take-screenshots-uitest.ps1 -Changed -Base main

# 撮らずに「どの画像が影響を受けるか」だけを見る
.\tools\screenshot-sync.ps1            # 1 行 1 件（画像名・パス・更新済みか・理由）
.\tools\screenshot-sync.ps1 -Verify    # 未更新の画像があれば終了コード 3
```

動作の要点：
- **判定の根拠はソースの変更であって、撮影後のピクセル比較ではない**。サンプルデータが「当月」を使うため、履歴画面などは毎月ピクセルが変わり、比較では常に「変更あり」になる
- 対応表には画面の構成（レイアウト・文言・スタイル・表示変換）を持つファイルだけを載せる。表示するデータを作る Service / Repository は載せない。`common` に載せたファイル（`App.xaml`、`Resources/Styles/**`、変換器、投入データ `ScreenshotSeedData.cs`、撮影ヘルパー `ScreenshotHelper.cs`）はすべての画像に影響する
- 対応表と実リポジトリの整合（ソースの実在・画像の実在・撮影テストが保存する画像名との一致・フィルタが指すテストの実在）は単体テスト `Tools/ScreenshotSyncMappingTests` が固定する。**撮影対象を増やしたら対応表にも 1 行足す**（足さないとテストが赤になる）
- 変更集合は「比較元との merge-base からの差分 ＋ 作業ツリー ＋ 未追跡ファイル」。比較元の既定は `origin/main`（無ければ `main`）
- Claude Code の Stop フック（`.claude/hooks/check-doc-sync.sh`）は、画面ソースが変わったのに画像が未更新ならその画像名と上記コマンドを案内する。CI（`.github/workflows/screenshot-sync-check.yml`）も PR で同じ判定を行い、未更新なら warning アノテーションとサマリーで案内する（見た目が変わらない変更もソース単位では「影響あり」になるため、ジョブは失敗させない）
- 撮影そのものは Windows デスクトップを要するため、フックや CI では行わない
- **制約: 判定は順序を見ない**。「変更集合に画像が含まれるか」だけで更新済みと判断するため、画像を差し替えた**あと**にソースを再修正しても未更新とは判定されない。画面の修正が終わってから `-Changed` を実行すること（履歴の時刻比較による検出は未対応）

### 対話式スクリプト

PowerShellスクリプトを使用して、対話的にスクリーンショットを取得できます。

```powershell
# リポジトリルートから実行
cd D:\OneDrive\交通系\src\ICCardManager

# 必須画面のみ（7枚）
.\tools\TakeScreenshots.ps1

# すべての画面（18枚）
.\tools\TakeScreenshots.ps1 -All
```

スクリプトの動作：
1. 各画面の説明と操作手順が表示されます
2. 画面の準備ができたら Enter キーを押します
3. ICCardManagerのウィンドウを自動検索してスクリーンショットが保存されます

> **Note**: PowerShellウィンドウではなく、ICCardManagerのウィンドウ（メイン画面やダイアログ）が自動的に検索されます。一部の画面（`report_excel.png`、`installer_options.png`）はICCardManager外のウィンドウのため、手動で `PrtSc` で撮影してください。

操作オプション：
- `Enter` - スクリーンショットを撮影
- `S` - 現在の画面をスキップ
- `Q` - 終了

### Windows標準機能（手動）

1. `Win + Shift + S` でスクリーンショットツールを起動
2. 対象の画面を範囲選択
3. ペイントなどで保存

### 推奨設定

- フォーマット: PNG
- 解像度: 100% スケール
- 文字サイズ: 「中（標準）」設定で撮影

## 注意事項

- 個人情報（実際の職員名やIDm）が映らないようにしてください
- テストデータを使用して撮影することを推奨します
