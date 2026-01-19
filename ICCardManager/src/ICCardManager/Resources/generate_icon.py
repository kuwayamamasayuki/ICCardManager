#!/usr/bin/env python3
"""
交通系ICカード管理システム アイコン生成スクリプト
Issue #269: かっこいいアイコンを作成する

このスクリプトは標準ライブラリのみを使用して、
シンプルなICカードアイコンを生成します。

使用方法:
    python generate_icon.py

出力:
    app.ico - 複数サイズを含むWindowsアイコンファイル
"""

import struct
import zlib
import os

def create_png(width, height, pixels):
    """
    RGBAピクセルデータからPNGを生成
    pixels: [(r, g, b, a), ...] の配列 (左上から右下へ)
    """
    def png_chunk(chunk_type, data):
        chunk = chunk_type + data
        crc = zlib.crc32(chunk) & 0xffffffff
        return struct.pack('>I', len(data)) + chunk + struct.pack('>I', crc)

    # PNG シグネチャ
    png_data = b'\x89PNG\r\n\x1a\n'

    # IHDR チャンク
    ihdr = struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0)  # 8bit RGBA
    png_data += png_chunk(b'IHDR', ihdr)

    # IDAT チャンク (画像データ)
    raw_data = b''
    for y in range(height):
        raw_data += b'\x00'  # フィルタータイプ: None
        for x in range(width):
            idx = y * width + x
            r, g, b, a = pixels[idx]
            raw_data += bytes([r, g, b, a])

    compressed = zlib.compress(raw_data, 9)
    png_data += png_chunk(b'IDAT', compressed)

    # IEND チャンク
    png_data += png_chunk(b'IEND', b'')

    return png_data


def draw_icon(size):
    """
    指定サイズのアイコン画像を生成
    ICカードリーダーにカードをかざすイメージ
    """
    pixels = [(0, 0, 0, 0)] * (size * size)  # 透明で初期化

    def set_pixel(x, y, color):
        if 0 <= x < size and 0 <= y < size:
            pixels[y * size + x] = color

    def fill_rect(x1, y1, x2, y2, color):
        for y in range(int(y1), int(y2)):
            for x in range(int(x1), int(x2)):
                set_pixel(x, y, color)

    def fill_rounded_rect(x1, y1, x2, y2, radius, color):
        """角丸四角形を描画"""
        for y in range(int(y1), int(y2)):
            for x in range(int(x1), int(x2)):
                # 角の処理
                in_corner = False
                corners = [
                    (x1 + radius, y1 + radius),  # 左上
                    (x2 - radius, y1 + radius),  # 右上
                    (x1 + radius, y2 - radius),  # 左下
                    (x2 - radius, y2 - radius),  # 右下
                ]
                for cx, cy in corners:
                    dx = abs(x - cx)
                    dy = abs(y - cy)
                    if dx <= radius and dy <= radius:
                        if dx * dx + dy * dy > radius * radius:
                            in_corner = True
                            break
                if not in_corner:
                    set_pixel(x, y, color)

    # スケーリング係数
    s = size / 256.0

    # 色定義
    reader_color = (40, 40, 40, 255)      # リーダー本体（ダークグレー）
    reader_top = (50, 50, 50, 255)        # リーダー上面
    card_color = (200, 200, 210, 255)     # カード（シルバー）
    card_border = (150, 150, 160, 255)    # カード枠
    chip_color = (212, 175, 55, 255)      # ICチップ（ゴールド）
    led_color = (0, 200, 100, 255)        # LED（緑）
    wave_color = (0, 170, 85, 180)        # 通信波（緑、半透明）
    text_color = (60, 60, 60, 255)        # テキスト

    # ICカードリーダー本体
    fill_rounded_rect(40*s, 140*s, 216*s, 240*s, 12*s, reader_color)
    fill_rounded_rect(50*s, 150*s, 206*s, 220*s, 8*s, reader_top)

    # リーダー中央のNFCマーク（同心円）
    cx, cy = int(128*s), int(185*s)
    for r in [int(20*s), int(14*s), int(8*s)]:
        for angle in range(360):
            import math
            rad = math.radians(angle)
            for dr in range(-1, 2):
                x = int(cx + (r + dr) * math.cos(rad))
                y = int(cy + (r + dr) * math.sin(rad))
                if r == int(8*s):
                    set_pixel(x, y, (80, 80, 80, 255))
                else:
                    set_pixel(x, y, (70, 70, 70, 255))

    # ステータスLED
    fill_rect(185*s, 228*s, 205*s, 232*s, led_color)

    # 交通系ICカード（斜め）
    # カード本体
    card_x, card_y = int(70*s), int(30*s)
    card_w, card_h = int(116*s), int(73*s)
    fill_rounded_rect(card_x, card_y, card_x + card_w, card_y + card_h, 6*s, card_color)

    # カード枠
    for i in range(max(1, int(2*s))):
        for x in range(card_x, card_x + card_w):
            set_pixel(x, card_y + i, card_border)
            set_pixel(x, card_y + card_h - 1 - i, card_border)
        for y in range(card_y, card_y + card_h):
            set_pixel(card_x + i, y, card_border)
            set_pixel(card_x + card_w - 1 - i, y, card_border)

    # ICチップ
    chip_x, chip_y = int(82*s), int(45*s)
    chip_w, chip_h = int(28*s), int(22*s)
    fill_rect(chip_x, chip_y, chip_x + chip_w, chip_y + chip_h, chip_color)

    # 通信波（弧線）
    wave_y_start = int(115*s)
    for wave_idx, wave_offset in enumerate([0, 10, 20]):
        wave_y = wave_y_start + int(wave_offset * s)
        wave_width = int((60 + wave_offset * 2) * s)
        wave_cx = int(128 * s)
        for x in range(wave_cx - wave_width // 2, wave_cx + wave_width // 2):
            # 簡易的な弧を描画
            dx = abs(x - wave_cx)
            dy = int(dx * dx / (wave_width * 0.5) * 0.3)
            set_pixel(x, wave_y + dy, wave_color)
            if s >= 0.5:  # 大きいサイズでは線を太く
                set_pixel(x, wave_y + dy + 1, wave_color)

    return pixels


def create_ico(output_path):
    """
    複数サイズを含むICOファイルを生成
    """
    sizes = [16, 32, 48, 256]
    images = []

    for size in sizes:
        print(f"  生成中: {size}x{size}...")
        pixels = draw_icon(size)
        png_data = create_png(size, size, pixels)
        images.append((size, png_data))

    # ICO ファイル構造
    # ICONDIR (6 bytes)
    ico_data = struct.pack('<HHH', 0, 1, len(images))  # Reserved, Type=1 (ICO), Count

    # ICONDIRENTRY の位置を計算
    header_size = 6 + 16 * len(images)
    current_offset = header_size

    entries = []
    for size, png_data in images:
        # ICONDIRENTRY (16 bytes)
        width = size if size < 256 else 0  # 256は0で表現
        height = size if size < 256 else 0
        entry = struct.pack('<BBBBHHII',
            width,          # Width
            height,         # Height
            0,              # Color count (0 for > 256 colors)
            0,              # Reserved
            1,              # Color planes
            32,             # Bits per pixel
            len(png_data),  # Size of image data
            current_offset  # Offset to image data
        )
        entries.append(entry)
        current_offset += len(png_data)

    # 全てのエントリを追加
    for entry in entries:
        ico_data += entry

    # 画像データを追加
    for size, png_data in images:
        ico_data += png_data

    # ファイルに書き込み
    with open(output_path, 'wb') as f:
        f.write(ico_data)

    print(f"\n完了: {output_path}")
    print(f"サイズ: {os.path.getsize(output_path):,} bytes")


if __name__ == '__main__':
    import sys
    output = os.path.join(os.path.dirname(__file__), 'app.ico')
    if len(sys.argv) > 1:
        output = sys.argv[1]

    print("交通系ICカード管理システム アイコン生成")
    print("=" * 40)
    create_ico(output)
