# 開発規約

## 環境制約
- インターネット非接続環境で動作する（クラウドサービス利用不可）
- Microsoft 365 E3のみ例外的に利用可能
- Windows 10/11対応（クロスプラットフォーム対応不要）
- 自己完結型ビルド（single-file publish）で配布
- **共有フォルダモード**: SMB共有フォルダ上にDBを配置し、複数PC（最大約20台）で共有可能。UNCパスまたはマップドネットワークドライブ指定時に自動判定（Issue #1559）。ローカルフルパス指定では共有モードにならない

## DB設計原則
- 日付はTEXT型（ISO 8601形式: YYYY-MM-DD HH:MM:SS）で保存
- 表示時に和暦変換（WarekiConverter使用）
- IDmは16進数文字列（16文字）として保存
- 外部キー制約を有効化
- 削除方式はテーブルごとに異なる（→「論理削除の方針」参照）

### 論理削除の方針
| テーブル | 削除方式 | 理由 |
|----------|----------|------|
| staff | 論理削除 | 履歴参照時に氏名を表示するため |
| ic_card | 論理削除 | 履歴参照時にカード情報を表示するため |
| ledger | 物理削除（6年後自動 ＋ 履歴画面からの個別削除） | 監査対応の保存期間経過後は不要。加えて、誤登録の訂正用に履歴画面から個別行を**職員認証＋確認のうえ物理削除**でき（`MainViewModel.DeleteLedgerRow` → `LedgerRepository.DeleteAsync`、Issue #635）、その操作は `operation_log` に記録される |
| operation_log | 物理削除（6年後自動） | ledgerと同じ保存期間経過後に削除 |

## UI/UX原則
- 色・アイコン・テキスト・音の4要素で状態を伝達（色や音のみに依存しない）
- 色覚多様性対応: 暖色（貸出）vs 寒色（返却）で色相差を明確に
- コントラスト: 背景色は彩度を確保しつつ、テキストとの可読性を維持
- 文字サイズは設定で変更可能（小/中/大/特大）
- **長文の可能性があるテキストは「幅」ではなく「折り返し」で担保する**（Issue #1687、#1688）。文字サイズが4段階で変わるため、`Width` / `MinWidth` の調整やボタン幅を詰める対処は特大でまた破綻する。`TextBlock` には `TextWrapping="Wrap"` を明示し、加えて以下の2つの罠に注意する:
  - **横方向 `StackPanel` は子を無限幅で測定する**ため、その中では `TextWrapping="Wrap"` が機能しない。`DockPanel` / `Grid` 等の幅制約のあるパネルを使う（Issue #1687）
  - **`Grid` は既定で子をクリップしない**（`ClipToBounds=false`）。`*` 列は隣の `Auto` 列（ボタン等）が広がると子の希望幅より狭くなるが、`TextWrapping` がないとテキストは列幅を無視して隣の要素の下へはみ出す（Issue #1688）
  - ボタン行にステータス表示を同居させる場合、ボタン側の `StackPanel` に `VerticalAlignment="Center"` を付け、テキストが2行に折り返しても縦に引き伸ばされないようにする
  - 回帰は XAML テキスト上の静的検証で固定する（`MainWindowWarningAreaLayoutTests` / `ReportDialogStatusAreaLayoutTests` が参考実装）。実描画の確認には UI オートメーションが必要なため、文字サイズ変更時の表示は手動検証する
- 貸出時: 薄いオレンジ背景(#FFF3E0、`LendingBackgroundBrush`) + アイコン + 「ピッ」
- 返却時: 薄い水色背景(#E3F2FD、`ReturnBackgroundBrush`) + アイコン + 「ピピッ」
- エラー時: 薄い赤背景(#FFEBEE、`ErrorBackgroundBrush`) + 「ピー」
- **色値の Single Source of Truth**: `Resources/Styles/AccessibilityStyles.xaml` のブラシキー（`LendingBackgroundBrush` / `ReturnBackgroundBrush` / `ErrorBackgroundBrush` / `HintForegroundBrush` 等）を `DynamicResource` で参照すること。色値リテラル（`#FFF3E0` 等）を直接指定しない（Issue #1392、#1461）。コードビハインドでは `Application.Current.TryFindResource("KeyName") as Brush` で取得する（`new SolidColorBrush(Color.FromRgb(...))` 禁止）。ViewModel/DTO で行ごとに色を切り替える場合は色値文字列ではなく**リソースキー名**を返し、XAML 側で `ResourceKeyToBrushConverter` 経由でブラシ解決する
- **`ReturnBackgroundBrush` の用途**: 「返却完了」シグナルに加え、ダイアログの情報ヘッダー・操作ヒント・装飾用途でも兼用する。専用の `HintBackgroundBrush` 等は新設しない設計判断。意味の区別は色ではなくアイコン・テキスト・配置で行う（Issue #1399、`docs/design/03_画面設計書.md` §4.1 参照）
- **`HintForegroundBrush` の用途**: 「💡 ヒント」「⚠ 注意」等の補足説明文の文字色（マテリアル Brown 700 / `#795548`）。背景ブラシに関する Issue #1399 の方針とは独立した「前景色」キー（Issue #1461）
- **設定で変更できる項目をコメントで断定しない**（Issue #1697）。設定項目が後から追加されると、それ以前に書かれた「常に◯◯」という前提のコメントが実装と乖離したまま残る。**コメントは PR 本文・設計書・マニュアルへの記述の元ネタとして参照される**ため、放置すると誤記述が下流へ伝播する（トースト表示位置設定 `AppSettings.ToastPosition` 追加後も「画面右上に表示」というコメントが残り、PR #1696 の本文へ「画面右上に表示される」と伝播した実例がある）。
  - 書き方: 「設定された画面隅（`ToastPosition`。既定は右上）に表示」のように**「設定に従う」＋「既定値」**の形にする。既定値の説明として具体値に言及するのは正当（`AppSettings` の enum コメント、設定画面の ToolTip 等）
  - 回帰は規約テストで固定する（`ToastPositionCommentConventionTests` が参考実装）。「断定表現の不在」だけでなく**「実装が全選択肢を分岐していること」も併せて検証**する。前者だけだと実装が固定値へ退化した際に逆方向の乖離が生まれるため
  - 検査対象が汎用ファイル（`MainViewModel` 等）に及ぶ場合は、対象機能のキーワードを含む行に絞る。「メイン画面右下の警告エリア」のような**別機能の正当な記述を誤検出する**（実際に発生）

## ICカード関連
- **用語の使い分け（重要）**: 本システムでは「職員証」と「交通系ICカード」の2種類のICカードを扱う。UI文言・マニュアル・コード内のユーザー向けメッセージでは、交通系ICカードを指す場合は必ず**「交通系ICカード」**と記載し、単に「ICカード」とは書かないこと。「ICカード」だけでは職員証と区別がつかずユーザーが混乱する。ただし「ICカードリーダー」等のハードウェア名称、および「ICカード管理」等の画面・機能名（固有名詞）はそのままでよい（用語ガード `UserFacingTextConventionTests` の `AllowedCompounds` がこれらの複合語を許容する）
- 履歴は最大20件まで取得可能
- **カード種別の判別について**: IDmからカード種別（Suica/PASMO等）を自動判別することは技術的に不可能
  - IDmの先頭2バイトは「製造者コード」（カードを製造した会社）であり「カード種別」ではない
  - 同じSuicaでも製造会社が異なれば先頭2バイトは異なる
  - カード種別はユーザーが登録時に手動で選択する
- **未登録カードの処理**: 職員証か交通系ICカードかをユーザーに選択させる
