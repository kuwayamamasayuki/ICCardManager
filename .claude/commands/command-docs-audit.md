---
name: docs-audit
description: docs(CLAUDE.md/AGENTS.md/docs配下)と実装コードのドリフト監査。Workflow ツールで ultracode 前提に fan-out + 敵対検証を loop-until-dry で回す(.agents/workflows/docs-audit.md)
---

`.agents/workflows/docs-audit.md` を読み、その手順に厳密に従って docs ↔ 実装のドリフト監査を実行してください。

この監査は **Claude の dynamic Workflow(Workflow ツール)で、ultracode 前提で深く回す**ものです。メインが plain JavaScript のオーケストレータを 1 本書いて Workflow ツールで起動し、領域ごとに reader を `parallel()` で並列起動(fan-out)→ 別エージェントで敵対的に検証(反証・多数決)→ 統合、を **新規 finding が連続 2 round ゼロになるまで(loop-until-dry、最大 5 round)** 繰り返してください。1 コンテキストで読んで 1 人で「ドリフトだ」と断じるのは禁止です。ultracode が OFF のときは、ON にしてもらってから回すか、どうしても今すぐなら『さっと 1 round』(主要 doc とコードを 1 巡読んで明白なズレだけ拾い、敵対的検証なし)に落として、その旨を明記してください。

着手前に **ループ条件(回数 / 停止条件 / 上限)・モード(A レポートのみ / B 修正まで)・範囲** の 3 点を 1 度に確認します(深さは別個に聞かない = ultracode に統合)。既定は **レポートのみ・読み取り専用**。修正まで進むのはユーザーが「修正まで(モード B)」を選び、レポートを承認したあとだけで、「安全・明白」(修正方針が一意 / doc 側のみ / 制約緩和や正典序列の変更を含まない / 検証済み)なものに限り、CLAUDE.md / AGENTS.md の規律(docs は最新仕様だけ・現在形 / 検証の緑を自分で確認 / 不可逆操作はメインが単独実行)に従ってください。エージェントの「緑」は鵜呑みにせず、最終判定はメインが実ファイルで確定します。**証拠(正確な file:line と「期待 vs 実際」)なき断定・捏造は禁止**です。