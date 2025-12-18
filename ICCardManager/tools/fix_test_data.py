#!/usr/bin/env python3
"""テストデータの修正スクリプト - 日付を今月に更新"""

import sqlite3
import os
from pathlib import Path
from datetime import datetime
from calendar import monthrange

def main():
    local_appdata = os.environ.get('LOCALAPPDATA', '')
    db_path = Path(local_appdata) / 'ICCardManager' / 'iccard.db'

    print("=" * 60)
    print(" テストデータ修正（日付を今月に更新）")
    print("=" * 60)
    print(f"\nDB: {db_path}")
    print()

    if not db_path.exists():
        print("[ERROR] データベースが見つかりません")
        return 1

    conn = sqlite3.connect(str(db_path))
    cursor = conn.cursor()

    test_idm = '0123456789ABCDEF'

    # 現在のテストデータを確認
    cursor.execute("""
        SELECT id, date, summary, lent_at, returned_at
        FROM ledger WHERE card_idm = ? AND summary LIKE '%テスト%'
        ORDER BY id LIMIT 5
    """, (test_idm,))

    rows = cursor.fetchall()
    if not rows:
        print("[ERROR] テストデータが見つかりません")
        print("先に add_test_data.py を実行してください")
        conn.close()
        return 1

    print("=== 修正前のデータ (最初の5件) ===")
    for row in rows:
        print(f"  ID={row[0]}, date={row[1]}, lent_at={row[3]}, returned_at={row[4]}")

    # 今月の情報を取得
    today = datetime.today()
    current_year = today.year
    current_month = today.month
    days_in_month = monthrange(current_year, current_month)[1]

    print(f"\n[INFO] 日付を今月（{current_year}年{current_month}月）に更新します...")

    # テストデータのIDを取得
    cursor.execute("""
        SELECT id FROM ledger
        WHERE card_idm = ? AND summary LIKE '%テスト%'
        ORDER BY id
    """, (test_idm,))

    ledger_ids = [row[0] for row in cursor.fetchall()]

    # 各レコードの日付を今月の日付に更新
    for i, ledger_id in enumerate(ledger_ids):
        day = min(i + 1, days_in_month)
        date_str = f"{current_year:04d}-{current_month:02d}-{day:02d}"
        lent_at = f"{date_str} 09:00:00"
        returned_at = f"{date_str} 17:00:00"

        cursor.execute("""
            UPDATE ledger
            SET date = ?,
                lent_at = ?,
                returned_at = ?,
                returner_idm = lender_idm,
                is_lent_record = 0
            WHERE id = ?
        """, (date_str, lent_at, returned_at, ledger_id))

    updated = len(ledger_ids)
    conn.commit()

    print(f"[SUCCESS] {updated}件のレコードを修正しました")

    # 修正後を確認
    cursor.execute("""
        SELECT id, date, summary, lent_at, returned_at
        FROM ledger WHERE card_idm = ? AND summary LIKE '%テスト%'
        ORDER BY date, id LIMIT 5
    """, (test_idm,))

    print("\n=== 修正後のデータ (最初の5件) ===")
    for row in cursor.fetchall():
        print(f"  ID={row[0]}, date={row[1]}, lent_at={row[3]}, returned_at={row[4]}")

    conn.close()

    print("\n" + "=" * 60)
    print("アプリを再起動して、はやかけん TEST-001 の履歴を確認してください。")
    print(f"対象期間: {current_year}年{current_month}月")
    print("=" * 60)

    return 0

if __name__ == "__main__":
    exit(main())
