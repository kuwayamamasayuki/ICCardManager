#!/usr/bin/env python3
"""データベース状態確認スクリプト"""

import sqlite3
import os
from pathlib import Path

def main():
    local_appdata = os.environ.get('LOCALAPPDATA', '')
    db_path = Path(local_appdata) / 'ICCardManager' / 'iccard.db'

    print("=" * 60)
    print(" データベース状態確認")
    print("=" * 60)
    print(f"\nDB: {db_path}")
    print()

    if not db_path.exists():
        print("[ERROR] データベースが見つかりません")
        return

    conn = sqlite3.connect(str(db_path))
    cursor = conn.cursor()

    # テーブル一覧
    print("=== テーブル一覧 ===")
    cursor.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")
    for row in cursor.fetchall():
        print(f"  - {row[0]}")

    # カード一覧
    print("\n=== ICカード一覧 ===")
    cursor.execute("SELECT card_idm, card_type, card_number, is_deleted FROM ic_card")
    cards = cursor.fetchall()
    if cards:
        for card in cards:
            deleted = "(削除)" if card[3] else ""
            print(f"  IDm: {card[0]}, 種別: {card[1]}, 番号: {card[2]} {deleted}")
    else:
        print("  (カードなし)")

    # 職員一覧
    print("\n=== 職員一覧 ===")
    cursor.execute("SELECT staff_idm, name, is_deleted FROM staff")
    staff = cursor.fetchall()
    if staff:
        for s in staff:
            deleted = "(削除)" if s[2] else ""
            print(f"  IDm: {s[0]}, 名前: {s[1]} {deleted}")
    else:
        print("  (職員なし)")

    # Ledger件数
    print("\n=== Ledger (履歴) ===")
    cursor.execute("SELECT COUNT(*) FROM ledger")
    total = cursor.fetchone()[0]
    print(f"  総件数: {total}")

    # カード別件数
    cursor.execute("""
        SELECT l.card_idm, c.card_type, c.card_number, COUNT(*) as cnt
        FROM ledger l
        LEFT JOIN ic_card c ON l.card_idm = c.card_idm
        GROUP BY l.card_idm
        ORDER BY cnt DESC
    """)
    by_card = cursor.fetchall()
    if by_card:
        print("\n  カード別件数:")
        for row in by_card:
            card_name = f"{row[1]} {row[2]}" if row[1] else "(未登録カード)"
            print(f"    {row[0]}: {card_name} - {row[3]}件")

    # テストカードの確認
    print("\n=== テストカード詳細 ===")
    test_idm = '0123456789ABCDEF'
    cursor.execute("SELECT * FROM ic_card WHERE card_idm = ?", (test_idm,))
    test_card = cursor.fetchone()
    if test_card:
        print(f"  カード存在: Yes")
        print(f"  詳細: {test_card}")
    else:
        print(f"  カード存在: No (IDm: {test_idm})")

    cursor.execute("SELECT COUNT(*) FROM ledger WHERE card_idm = ?", (test_idm,))
    test_ledger_count = cursor.fetchone()[0]
    print(f"  Ledger件数: {test_ledger_count}")

    if test_ledger_count > 0:
        print("\n  最新5件:")
        cursor.execute("""
            SELECT id, date, summary, income, expense, balance
            FROM ledger WHERE card_idm = ?
            ORDER BY date DESC, id DESC LIMIT 5
        """, (test_idm,))
        for row in cursor.fetchall():
            print(f"    {row}")

    conn.close()

if __name__ == "__main__":
    main()
