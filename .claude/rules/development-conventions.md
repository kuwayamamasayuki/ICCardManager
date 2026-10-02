# 開発規約

## 環境制約
- インターネット非接続環境で動作する（クラウドサービス利用不可）
- Microsoft 365 E3のみ例外的に利用可能
- Windows 10/11対応（クロスプラットフォーム対応不要）
- `dotnet publish` のフォルダー出力を Inno Setup インストーラー（`ICCardManager/installer/`）に同梱して配布する。ターゲットは .NET Framework 4.8（`net48`）のため single-file publish / self-contained は使えない
- **共有フォルダモード**: SMB共有フォルダ上にDBを配置し、複数PC（最大約20台）で共有可能。UNCパスまたはマップドネットワークドライブ指定時に自動判定（Issue #1559）。ローカルフルパス指定では共有モードにならない

## 分割した節の索引

本ファイルは常時ロードされるため、Issue 由来の詳細な規約は担当する層ごとのファイルへ分割した（条件付きロード。`paths` に一致するファイルを扱うときだけ読み込まれるので、**該当作業に入る前に自分で読むこと**）。他ファイル・コード内コメント・CHANGELOG からの「`development-conventions.md` の「○○」」という参照は、下表の移動先にある同名の節を指す。

| 節 | 移動先 |
|---|---|
| DB設計原則 | `db-write-conventions.md` |
| 日付の整形・解析は文化圏から独立させる（Issue #1985） | `db-write-conventions.md` |
| WHERE で列を関数に包まない（Issue #1834 / #1996） | `db-write-conventions.md` |
| 監査ログの JSON を手組みしない（Issue #1996） | `db-write-conventions.md` |
| 保存順が意味を持つなら、並べ替えを呼び出し元に配らない（Issue #1913） | `service-conventions.md` |
| 「規約どおりの並び」を、規約の例外がある場所で唯一の根拠にしない（Issue #1932） | `service-conventions.md` |
| SQLite の接続文字列は 1 か所で組み立てる（Issue #1924） | `service-conventions.md` |
| 「存在するか」を「到達できるか」の代わりに使わない（Issue #1924） | `service-conventions.md` |
| 書き込み途中のファイルに「最終名」を与えない（Issue #1748） | `service-conventions.md` |
| 巡回する値（月・曜日・角度）は、折り返した後の値どうしで大小比較しない（Issue #1812） | `service-conventions.md` |
| 設定値で生成したものは、設定値で判定する（Issue #1818） | `service-conventions.md` |
| fire-and-forget の本体は、それ自体が最後の受け皿（Issue #1816） | `viewmodel-conventions.md` |
| 「処理中」フラグの解除は `finally` で保証する（Issue #1725） | `viewmodel-conventions.md` |
| 値を丸め込んだら、その値に依存して取得したデータも取り直す（Issue #1814） | `viewmodel-conventions.md` |
| 「全消し＋再生成」を書くときは、消す側ではなく**残す側**を列挙しない（Issue #1739） | `viewmodel-conventions.md` |
| 「クリアして再生成」は、再生成が作り直すものを漏れなくクリアする（Issue #1810） | `viewmodel-conventions.md` |
| UI/UX原則 | `ui-conventions.md` |
| ロギング | `logging.md` |

## 論理削除の方針
| テーブル | 削除方式 | 理由 |
|----------|----------|------|
| staff | 論理削除 | 履歴参照時に氏名を表示するため |
| ic_card | 論理削除 | 履歴参照時にカード情報を表示するため |
| ledger | 物理削除（6年後自動 ＋ 履歴画面からの個別削除） | 監査対応の保存期間経過後は不要。加えて、誤登録の訂正用に履歴画面から個別行を**職員認証＋確認のうえ物理削除**でき（`HistoryPanelViewModel.DeleteLedgerRow` → `LedgerRepository.DeleteAsync`、Issue #635）、その操作は `operation_log` に記録される |
| operation_log | 物理削除（6年後自動） | ledgerと同じ保存期間経過後に削除 |

## ビルド警告は「抑制」ではなく「是正」で消す（Issue #1786）

警告ゼロは**二段構え**で担保する。警告の**発生**は CI の「ビルド警告ゼロ検証」ステップ（`.github/workflows/ci.yml` の code-quality ジョブ。Release と Debug の両方でビルドし、`: warning ` を含む行を数えて 1 件でもあれば fail。Issue #2099）が止める。警告を消した**手段**が是正だったか抑制だったかは `BuildWarningSuppressionConventionTests` が静的検査で見張る。

- **`NoWarn` へ追加したら、同じファイルのコメントに ID と理由を書く**。抑制自体は禁じない（テスト特有の表現に対する抑制は正当）が、理由の無い抑制が積み上がると「ビルド警告ゼロ」が実態を伴わなくなる。コメントでは `CS8600/8602/8603` のような圧縮表記を使わず `CS8600, CS8602, CS8603` と完全な形で列挙する（照合は語境界一致のため）
- **CS8618（未初期化の非 Null 許容フィールド）は抑制しない**。テストフィクスチャの初期化漏れという実バグを示し得る。宣言側を是正すること
- **`= null!` は「必ず非 null」という宣言**であり、実際に null チェックしているフィールドに付けると意図とコードが食い違う。null になり得るなら `?` を付ける（#1786 の `CleanupSimulator._worker` は `StartAndWaitUntilTransactionOpenAsync` 前は実際に null で、`RollbackAndCompleteAsync` も null チェックしていた → `Task?` が正解）
- **`<Nullable>enable</Nullable>` を外して黙らせない**。Null 許容系の警告が一括で消え、個別に理由を書く規約ごと無効化される。MSBuild は後勝ち評価のため、既存行を残したまま後ろへ `disable` を足す形も同じ結果になる
- ビルド警告を伴う変更をしたら、**Release / Debug 双方**でソリューションビルドし 0 警告を実測する（`"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln -c Release`）

### .NET アナライザー（CA ルール）は本体で有効（Issue #2162）

本体（`src/ICCardManager`）の csproj で `EnableNETAnalyzers=true`・`AnalysisLevel=latest`・`AnalysisMode=Recommended` とし、`Microsoft.CodeAnalysis.NetAnalyzers` をパッケージで版固定している（net48 では既定で無効）。CA ルールの警告も上の「警告ゼロ」の対象。テストプロジェクト・DebugDataViewer は対象外。

- **是正しないルールは `ICCardManager/.editorconfig` で ID ごとに重大度を下げ、設定行の直前のコメントに ID と理由を書く**。ファイルのどこかに ID があるだけでは理由と認めない（理由の一覧と抑制が離れると対応が読めない）。1 つのコメントで続く複数の設定行に理由を書くのはよい。現在の抑制は CA1848（LoggerMessage）・CA1716（他言語の予約語）・CA1000（ジェネリック型の静的ファクトリ）・CA1859（具象型の提案）・CA1707（マイグレーションの命名）と、CA1822 の対象を private に限る `dotnet_code_quality.CA1822.api_surface`
- **`dotnet_code_quality.<ID>.<オプション>`（`api_surface` 等）も抑制として扱う**。重大度を変えずに検出範囲を狭めるため。ID を書かない指定（全体・カテゴリ単位）は一括の格下げと同じく禁止
- **特定のメンバーだけを抑制するなら `[SuppressMessage]` に `Justification` を書く**（.editorconfig は行単位でしか抑制できない）
- **アナライザーを止める・弱める設定は置かない**（`RunAnalyzers` / `RunAnalyzersDuringBuild` / `EnableNETAnalyzers` = false、`AnalysisMode` / `AnalysisLevel` を Recommended より弱める・`AnalysisLevel` を 8.0 より古い版へ下げる）。これらは共有設定・csproj・コマンドライン・環境変数のどこに書いても `BuildWarningSuppressionConventionTests` が検出する。**`.ruleset`（`CodeAnalysisRuleSet`）・`GlobalAnalyzerConfigFiles` での任意名の設定ファイル・`ExcludeAssets="analyzers"` は検出対象外**なので使わない（抑制は `.editorconfig` の ID ごとの格下げか `[SuppressMessage]` に限る）
- **書式の文化圏（CA1305）は「誰が読むか」で決める**。DB・CSV・操作ログ・ファイル名など機械が読み直す値は `CultureInfo.InvariantCulture`、画面・印刷の表示は `CultureInfo.CurrentCulture` を明示する（`db-write-conventions.md`「日付の整形・解析は文化圏から独立させる」の数値版）。**保存側を直したら読み取り側（`TryParse`）も同じカルチャにそろえる** — CA1305 は `TryParse(string, out)` を検出しないため、保存側だけ直すと読めなくなる（Issue #2162 の設定のウィンドウ位置）
- `ConfigureAwait`（CA2007）の扱いは `async-configureawait.md` の「アナライザ」を参照

### Null 許容参照型はファイル単位で有効にする — 新規 .cs は `#nullable enable` 必須（Issue #2163）

本体の csproj は `<Nullable>` を宣言しておらず（C# 10 の既定で無効）、一括で有効にすると数千件の警告になって警告ゼロと両立しない。**新規ファイルと改修したファイルの先頭に `#nullable enable` を付けて**、移行を日常の改修に乗せる（テスト 2 プロジェクトは csproj で有効）。

- **新しく追加する .cs は、1 行目（コメント・空行の後でもよいが、コードより前）に `#nullable enable` を置く**。`enable warnings` / `enable annotations` の片方だけ、途中での `#nullable restore`（本体ではプロジェクト既定＝無効へ戻る）・`disable` は「付いていない」と数える。改修したファイルにも付け、出た警告はその PR で是正する
- **移行が済んだ層**（現在は `Common/` 配下すべて）へファイルを足すときは例外なく付ける
- **`#nullable enable` の無いファイル数**は `NullableContextConventionTests.MaxFilesWithoutNullableEnable` で上限を固定している。付けずに足すと上限を超えて赤、移行したのに上限を下げないと実数との差で赤になる — **上限は下げる方向にだけ動かす**（下げ忘れの余裕は、その分だけ付けずに足せる穴になる）。未移行ファイルの一覧を許可リストにする形は、移行のたびに赤くなる誤検出になるので採らない（#1786）
- **net48 には Null 許容のフロー解析用の属性（`NotNullWhen` / `MaybeNullWhen` / `NotNullIfNotNull` / `MemberNotNullWhen` 等）が無い**ので、`Common/Polyfills/NullableAttributes.cs` に internal で定義している。`TryGet…` 形の `out` 引数は `[NotNullWhen(true)] out T? value`、フラグで非 null を約束するプロパティは `[MemberNotNullWhen(true, nameof(X))]` を付ける（C# 10 のため属性引数に引数名の `nameof` は書けず、`NotNullIfNotNull("source")` のように文字列で書く）
- **net48 の BCL は Null 許容の注釈を持たない**。`string.IsNullOrEmpty(s)` / `IsNullOrWhiteSpace(s)` で調べた後も `s` は非 null と推論されないので、`s is not null && !string.IsNullOrWhiteSpace(s)` と前置するか、入口で `?? string.Empty` に寄せる（`!` で黙らせない）
- **`?` を付けるのは「実際に null になり得る」箇所だけ**。null を返す・null を既定値に持つ・null チェックしている — このどれかに当たるなら `?`。`= null!` は「必ず非 null」という宣言で、null チェックしているフィールドには付けない（#1786）
- **注釈の変更はテストプロジェクト（Nullable 有効）の警告として現れる**。戻り値を `string?` にすると、それを非 null の引数へ渡すテストで CS8604 が出る。本体だけでなくソリューション全体を Release・Debug でビルドして 0 警告を確かめる
- 全ファイルの移行が終わったら、csproj に `<Nullable>enable</Nullable>` を置き、各ファイルの `#nullable enable` と `NoWarn` の CS8632 を外す

### コード整形は CI で検査される（Issue #2161）

CI の code-quality ジョブは `dotnet format --verify-no-changes` の違反で fail する。.cs を書いたら、コミット前に `ICCardManager` ディレクトリで `dotnet format --verify-no-changes`（WSL2 では `"/mnt/c/Program Files/dotnet/dotnet.exe"`）を実行し、違反があれば `dotnet format` で直す。

- **.cs は CRLF・BOM なしの UTF-8**。WSL のツール（Edit / Write・python のテキストモード書き戻し）は LF の行を混ぜやすく、手元の検査で ENDOFLINE になる（CI は `.gitattributes` の `*.cs text eol=crlf` で CRLF に展開されるが、手元の検査は作業ツリーをそのまま読む）
- private の `const` / `static readonly` は PascalCase、その他の private フィールドと `[ObservableProperty]` のフィールドは `_camelCase`。1 文の `if` 等にも波括弧
- 整形検証ステップは許可形（1 行の `dotnet format`・許可したオプションのみ）で `CiWorkflowConventionTests` が固定している。ステップを書き換えるときは同テストを先に読む

### ガードを書くときは「守りたい性質」ではなく「その性質を破れる全経路」を列挙する

#1786 の初版は csproj の `<NoWarn>` だけを検査しており、**4 テストすべてが緑のまま規約を破れる経路が 5 通り**残っていた。ガード系のコードは経路の網羅で設計する。

| 迂回経路 | 見落とした理由 |
|---|---|
| 同一 csproj 内の 2 つ目（構成条件付き）の `<NoWarn>` | `Regex.Match`（単数形）が最初の要素しか返さない |
| `Directory.Build.props` | 各 csproj の `<NoWarn>` が `$(NoWarn)` で始まり、その継承値を抽出器が除外していた |
| 走査対象から漏れたプロジェクト（`ICCardManager.UITests`） | sln のプロジェクト数を確認せず 3 つと思い込んだ |
| 後勝ちで上書きされる `<Nullable>` | リテラル文字列の存在だけを検査し、MSBuild の実効値を見ていなかった |
| ソース中の `#pragma warning disable` | csproj だけを抑制手段とみなした（既に 7 箇所で使われている確立した手段だった） |

- **「禁止語がコメントに現れるか」を単純な部分文字列一致で書かない**。「この ID は抑制しない」という**戒めのコメント自体**が、その ID の抑制を正当化してしまう（極性の反転）。否定語を含む行を除外し、語境界付きで照合する（`CS862` が既存の `CS8620` の記述で「理由あり」になる前方一致の誤りも同時に防げる）
- **空振り検出を「各対象が非空であること」で書かない**。規約が推奨する方向の変更（抑制の解消）でテストが赤になり、修正者を「対象から外す」方向へ誘導する。外された対象は他の検査からも静かに落ちる。**検査ロジック自体を既知のサンプル入力で固定**すれば、実データが空でも空振り検出は働き続ける

## ICカード関連
- **用語の使い分け（重要）**: 本システムでは「職員証」と「交通系ICカード」の2種類のICカードを扱う。UI文言・マニュアル・コード内のユーザー向けメッセージでは、交通系ICカードを指す場合は必ず**「交通系ICカード」**と記載し、単に「ICカード」とは書かないこと。「ICカード」だけでは職員証と区別がつかずユーザーが混乱する。ただし「ICカードリーダー」等のハードウェア名称、および「ICカード管理」等の画面・機能名（固有名詞）はそのままでよい（用語ガード `UserFacingTextConventionTests` の `AllowedCompounds` がこれらの複合語を許容する）
- 履歴は最大20件まで取得可能
- **カード種別の判別について**: IDmからカード種別（Suica/PASMO等）を自動判別することは技術的に不可能
  - IDmの先頭2バイトは「製造者コード」（カードを製造した会社）であり「カード種別」ではない
  - 同じSuicaでも製造会社が異なれば先頭2バイトは異なる
  - カード種別はユーザーが登録時に手動で選択する
- **未登録カードの処理**: 職員証か交通系ICカードかをユーザーに選択させる
