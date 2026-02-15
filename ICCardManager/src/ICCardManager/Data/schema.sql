-- ICCardManager データベーススキーマ定義
-- SQLite 3

-- 外部キー制約を有効化
PRAGMA foreign_keys = ON;

-- ============================================
-- 職員マスタ
-- ============================================
CREATE TABLE IF NOT EXISTS staff (
    staff_idm  TEXT PRIMARY KEY,           -- 職員証IDm（16進数16文字）
    name       TEXT NOT NULL,              -- 職員氏名
    number     TEXT,                       -- 職員番号
    note       TEXT,                       -- 備考
    is_deleted INTEGER DEFAULT 0,          -- 削除フラグ（0:有効、1:削除済）
    deleted_at TEXT                        -- 削除日時
);

-- ============================================
-- 交通系ICカードマスタ
-- ============================================
CREATE TABLE IF NOT EXISTS ic_card (
    card_idm             TEXT PRIMARY KEY,      -- ICカードIDm（16進数16文字）
    card_type            TEXT NOT NULL,         -- カード種別
    card_number          TEXT NOT NULL,         -- 通し番号（管理番号）
    note                 TEXT,                  -- 備考
    is_deleted           INTEGER DEFAULT 0,     -- 削除フラグ
    deleted_at           TEXT,                  -- 削除日時
    is_refunded          INTEGER DEFAULT 0,     -- 払戻済フラグ（Issue #530）
    refunded_at          TEXT,                  -- 払戻日時（Issue #530）
    is_lent              INTEGER DEFAULT 0,     -- 貸出状態（0:未貸出、1:貸出中）
    last_lent_at         TEXT,                  -- 最終貸出日時
    last_lent_staff      TEXT REFERENCES staff(staff_idm),  -- 最終貸出者IDm
    starting_page_number INTEGER DEFAULT 1      -- 開始ページ番号（Issue #510）
);

-- ============================================
-- 利用履歴概要（物品出納簿の1行に対応）
-- ============================================
CREATE TABLE IF NOT EXISTS ledger (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    card_idm       TEXT    NOT NULL REFERENCES ic_card(card_idm),
    lender_idm     TEXT    REFERENCES staff(staff_idm),
    date           TEXT    NOT NULL,       -- 出納年月日時（YYYY-MM-DD HH:MM:SS）
    summary        TEXT    NOT NULL,       -- 摘要
    income         INTEGER DEFAULT 0,      -- 受入金額（チャージ額）
    expense        INTEGER DEFAULT 0,      -- 払出金額（利用額）
    balance        INTEGER NOT NULL,       -- 残額
    staff_name     TEXT,                   -- 利用者氏名（スナップショット）
    note           TEXT,                   -- 備考
    returner_idm   TEXT,                   -- 返却者IDm
    lent_at        TEXT,                   -- 貸出日時
    returned_at    TEXT,                   -- 返却日時
    is_lent_record INTEGER DEFAULT 0       -- 貸出中レコードフラグ
);

-- ============================================
-- 利用履歴詳細（ICカードの個別利用記録）
-- ============================================
CREATE TABLE IF NOT EXISTS ledger_detail (
    ledger_id           INTEGER REFERENCES ledger(id) ON DELETE CASCADE,
    use_date            TEXT,                    -- 利用日時
    entry_station       TEXT,                    -- 乗車駅
    exit_station        TEXT,                    -- 降車駅
    bus_stops           TEXT,                    -- バス停名（手入力）
    amount              INTEGER,                 -- 利用額／チャージ額
    balance             INTEGER,                 -- 残額
    is_charge           INTEGER DEFAULT 0,       -- チャージフラグ
    is_point_redemption INTEGER DEFAULT 0,       -- ポイント還元フラグ
    is_bus              INTEGER DEFAULT 0,       -- バス利用フラグ
    group_id            INTEGER                  -- グループID（乗り継ぎ統合用、NULLは自動判定）
);

-- ============================================
-- 操作ログ（監査証跡）
-- ============================================
CREATE TABLE IF NOT EXISTS operation_log (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp     TEXT DEFAULT CURRENT_TIMESTAMP,
    operator_idm  TEXT NOT NULL,           -- 操作者IDm（FK制約なし：削除された職員のログも保持）
    operator_name TEXT NOT NULL,           -- 操作者氏名（スナップショット）
    target_table  TEXT,                    -- 操作対象テーブル名
    target_id     TEXT,                    -- 操作対象レコードID/IDm
    action        TEXT,                    -- 操作種別（INSERT/UPDATE/DELETE）
    before_data   TEXT,                    -- 操作前データ（JSON）
    after_data    TEXT                     -- 操作後データ（JSON）
);

-- ============================================
-- 設定（KVS形式）
-- ============================================
CREATE TABLE IF NOT EXISTS settings (
    key   TEXT PRIMARY KEY,
    value TEXT
);

-- ============================================
-- 統合履歴（Issue #548）
-- ============================================
CREATE TABLE IF NOT EXISTS ledger_merge_history (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    merged_at        TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,  -- 統合日時
    target_ledger_id INTEGER NOT NULL,                         -- 統合先レコードID
    description      TEXT NOT NULL,                            -- 統合内容の説明
    undo_data        TEXT NOT NULL,                            -- undo用データ（JSON）
    is_undone        INTEGER DEFAULT 0                         -- 取消済フラグ
);

-- ============================================
-- インデックス定義
-- ============================================
CREATE INDEX IF NOT EXISTS idx_staff_deleted      ON staff(is_deleted);
CREATE INDEX IF NOT EXISTS idx_card_deleted       ON ic_card(is_deleted);
CREATE INDEX IF NOT EXISTS idx_ledger_date        ON ledger(date);
CREATE INDEX IF NOT EXISTS idx_ledger_summary     ON ledger(summary);
CREATE INDEX IF NOT EXISTS idx_ledger_card_date   ON ledger(card_idm, date);
CREATE INDEX IF NOT EXISTS idx_ledger_lender      ON ledger(lender_idm);
CREATE INDEX IF NOT EXISTS idx_detail_ledger      ON ledger_detail(ledger_id);
CREATE INDEX IF NOT EXISTS idx_detail_bus         ON ledger_detail(is_bus);
CREATE INDEX IF NOT EXISTS idx_log_timestamp      ON operation_log(timestamp);
-- Issue #504: 起動高速化のための追加インデックス
CREATE INDEX IF NOT EXISTS idx_ledger_card_id     ON ledger(card_idm, id DESC);
CREATE INDEX IF NOT EXISTS idx_card_lent_deleted  ON ic_card(is_lent, is_deleted);
-- Issue #548: 統合履歴テーブルの検索用インデックス
CREATE INDEX IF NOT EXISTS idx_merge_history_target ON ledger_merge_history(target_ledger_id);

-- ============================================
-- 初期データ
-- ============================================
INSERT OR IGNORE INTO settings (key, value) VALUES ('warning_balance', '10000');
INSERT OR IGNORE INTO settings (key, value) VALUES ('font_size', 'medium');
