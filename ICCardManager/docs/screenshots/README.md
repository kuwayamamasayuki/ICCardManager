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
| `card.png` | カード管理画面 | 管理者マニュアル |
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
| `print_preview.png` | 帳票プレビュー画面 | ユーザーマニュアル |
| `report_excel.png` | 物品出納簿（Excel出力例） | ユーザーマニュアル |
| `installer_options.png` | インストーラーオプション選択画面 | 管理者マニュアル |
| `card_registration_mode.png` | カード登録方法の選択画面 | 管理者マニュアル |

## スクリーンショットの撮影方法

### 完全自動撮影（UI テスト基盤、15 枚）

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
| Release | メイン画面（待機状態） | `main.png` |
| Release | 履歴照会画面 | `history.png` |
| Release | ツールバーから開くダイアログ | `card.png` `staff.png` `report.png` `export.png` `settings.png` `system.png` |
| Debug | 職員証認識・貸出完了・返却完了（メイン画面を最大化して撮る） | `staff_recognized.png` `lend.png` `return.png` |
| Debug | トースト単体（概要版マニュアル用） | `toast_staff_recognized.png` `toast_lend.png` `toast_return.png` |
| Debug | バス停名入力ダイアログ | `busstop.png` |

動作の要点：
- 本体を Release と Debug の両方でビルドし、2 パスで撮る。タッチを要する画面は仮想タッチ（Debug 限定）で再現し、撮影モード（環境変数 `ICCARDMANAGER_SCREENSHOT_MODE=1`）で仮想タッチパネルを透明にし、起動時のテストデータ自動登録を止める
- 既存の DB（`%ProgramData%\ICCardManager\iccard.db`）は撮影中だけ退避し、終了後に復元する
- 職員 2 名・交通系ICカード 3 枚（通常／貸出中／残額不足）と当月の利用履歴をサンプルとして投入する（`tests/ICCardManager.UITests/Infrastructure/ScreenshotSeedData.cs`）
- 出力先 `docs\screenshots\auto\` は Git 管理外。撮影結果は既存画像とサイズを見比べてから `-Publish` で差し替える。`-Publish` は撮影し直さず、`auto\` にある画像をそのままコピーする（確認した画像と差し替える画像が同じであることを保証するため）
- ステータスバーの「リーダー:」は撮影した PC の接続状態がそのまま写る（未接続なら「切断」）。警告欄もその PC の状態（更新の案内など）を含み得るので、差し替え前に確認すること
- 撮影中はアプリのウィンドウが画面左上へ移動（貸出・返却は最大化）して前面に出る。マウス・キーボードに触れないこと
- **管理者権限で動いているウィンドウを前面にしたまま実行しない**。Windows が前面化と入力注入を拒否するため、クリック・キー入力を要する撮影（履歴照会）が「アプリのウィンドウを前面にできません」で失敗する。そのウィンドウを閉じるか最小化してからやり直す
- テストプロセスを DPI 対応にして物理ピクセルで撮るため、表示スケール 150% では 100% の 1.5 倍の寸法になる。既存画像と寸法を揃えたいときは表示スケールを 100% にして実行する

下記の対話式スクリプトにしか定義の無い画面（帳票プレビュー・インストーラーなど）は、引き続き対話式で撮影します。

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
