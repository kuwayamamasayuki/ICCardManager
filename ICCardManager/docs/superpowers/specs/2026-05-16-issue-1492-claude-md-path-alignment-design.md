# Issue #1492 — CLAUDE.md のディレクトリ構成記載を実体パスに整合させる

- 起票: 2026-05-08
- 修正対象: `/CLAUDE.md`
- 種別: documentation
- 関連事故: 2026-05-08 リポジトリ全体レビュー時、Claude エージェントが `docs/design/` を探索して空振り

## 1. 背景

`/CLAUDE.md` の §「ディレクトリ構成」と §「参照ドキュメント」では、設計書・マニュアル・テンプレート・駅コード CSV をリポジトリルート直下にあるかのように記述している（例: `docs/design/`、`Resources/Templates/...`）。

実体は `ICCardManager/` サブディレクトリ配下にあり、ルート直下の `./docs/` は superpowers プラグインキャッシュのみが存在する。エージェントが CLAUDE.md の記述通りに探索すると空振りする。

## 2. 実体パスの調査結果

| CLAUDE.md の記述 | 実体パス |
|---|---|
| `docs/design/` | `ICCardManager/docs/design/` |
| `docs/manual/` | `ICCardManager/docs/manual/` |
| `Resources/Templates/物品出納簿テンプレート.xlsx` | `ICCardManager/src/ICCardManager/Resources/Templates/`（実体は `物品出納簿テンプレート（企業会計部局）.xlsx` と `物品出納簿テンプレート（市長事務部局）.xlsx` の 2 ファイル。元の単一ファイル名は実在しない） |
| `docs/線区駅順コード/StationCode.csv` | `ICCardManager/docs/線区駅順コード/StationCode.csv` |
| ASCII tree のルート `ICCardManager/` | リポジトリルートは `.`。`./docs/`（superpowers のみ）と `./tools/` がルートに別途存在 |

CHANGELOG.md (`ICCardManager/CHANGELOG.md`) は既に正しく記述されているため対象外。

## 3. 修正方針（Approach A: ルート起点で完全整合）

### 3.1 ASCII tree

起点を `.`（リポジトリルート）に変更し、`ICCardManager/` を level-2 ノードとして描画する。
ルート直下に存在する `./docs/`（superpowers キャッシュ）と `./tools/`（補助スクリプト群）の存在も明示し、混同事故を防ぐ。

### 3.2 「参照ドキュメント」セクション

設計書・マニュアル・テンプレート・駅コード CSV の 4 件すべてに `ICCardManager/` プレフィックスを付与する。

## 4. 検証方法

ドキュメントのみの修正で、ビルド・テスト対象のコードは変更しない。代わりに以下の検証を行う:

1. **ファイル存在チェック**: 修正後の各パスについて、現リポジトリ上で `ls` または `test -e` が成功することを目視確認する。
2. **diff レビュー**: `git diff main -- CLAUDE.md` で意図通りの差分のみであることを確認する。

## 5. テストの取り扱い

`CLAUDE.md` はビルド対象ではないため単体テストは作成しない。代わりに本 PR の Description に「修正後の各パスが実体と一致していること」を確認した結果を箇条書きで記録する。

## 6. ロールバック

`git revert <commit>` で復元可能。リスクは極小。

## 7. スコープ外

- ルート直下 `./docs/` の整理（superpowers キャッシュは別途）
- `./tools/` と `ICCardManager/tools/` の役割分担明確化（別 Issue で扱う）
- 設計書本体（`02_DB設計書.md` 等）の内容更新

## 8. 参考

- Issue #1492
- 関連既存ファイル: `/CLAUDE.md` §15–25, §64–70
