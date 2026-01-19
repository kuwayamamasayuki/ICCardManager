#!/usr/bin/env python3
"""
交通系ICカード管理システム アイコン生成スクリプト
Issue #269: かっこいいアイコンを作成する

このスクリプトは標準ライブラリのみを使用して、
手に持ったICカードをリーダーにかざすアイコンを生成します。

使用方法:
    python generate_icon.py

出力:
    app.ico - 複数サイズを含むWindowsアイコンファイル
"""

import struct
import zlib
import os
import math


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
    手に持ったICカードをリーダーにかざすイメージ
    """
    pixels = [(0, 0, 0, 0)] * (size * size)  # 透明で初期化

    def set_pixel(x, y, color):
        if 0 <= x < size and 0 <= y < size:
            pixels[y * size + x] = color

    def blend_pixel(x, y, color):
        """アルファブレンドでピクセルを設定"""
        if 0 <= x < size and 0 <= y < size:
            idx = y * size + x
            existing = pixels[idx]
            r, g, b, a = color
            if a == 255:
                pixels[idx] = color
            elif a > 0:
                # 簡易アルファブレンド
                er, eg, eb, ea = existing
                alpha = a / 255.0
                nr = int(r * alpha + er * (1 - alpha))
                ng = int(g * alpha + eg * (1 - alpha))
                nb = int(b * alpha + eb * (1 - alpha))
                na = max(a, ea)
                pixels[idx] = (nr, ng, nb, na)

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

    def fill_ellipse(cx, cy, rx, ry, color):
        """楕円を塗りつぶす"""
        for y in range(int(cy - ry), int(cy + ry + 1)):
            for x in range(int(cx - rx), int(cx + rx + 1)):
                dx = (x - cx) / rx if rx > 0 else 0
                dy = (y - cy) / ry if ry > 0 else 0
                if dx * dx + dy * dy <= 1:
                    set_pixel(int(x), int(y), color)

    def draw_circle(cx, cy, r, color, thickness=1):
        """円の輪郭を描画"""
        for angle in range(360):
            rad = math.radians(angle)
            for dr in range(-thickness // 2, thickness // 2 + 1):
                x = int(cx + (r + dr) * math.cos(rad))
                y = int(cy + (r + dr) * math.sin(rad))
                set_pixel(x, y, color)

    def fill_rotated_rect(cx, cy, w, h, angle_deg, color):
        """回転した四角形を塗りつぶす"""
        angle = math.radians(angle_deg)
        cos_a = math.cos(angle)
        sin_a = math.sin(angle)

        # 四角形の頂点
        hw, hh = w / 2, h / 2
        corners = [
            (-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh)
        ]
        # 回転して座標変換
        rotated = []
        for px, py in corners:
            rx = px * cos_a - py * sin_a + cx
            ry = px * sin_a + py * cos_a + cy
            rotated.append((rx, ry))

        # バウンディングボックス
        min_x = int(min(p[0] for p in rotated))
        max_x = int(max(p[0] for p in rotated))
        min_y = int(min(p[1] for p in rotated))
        max_y = int(max(p[1] for p in rotated))

        # 点が四角形内かチェック
        def point_in_polygon(x, y):
            n = len(rotated)
            inside = False
            j = n - 1
            for i in range(n):
                xi, yi = rotated[i]
                xj, yj = rotated[j]
                if ((yi > y) != (yj > y)) and (x < (xj - xi) * (y - yi) / (yj - yi) + xi):
                    inside = not inside
                j = i
            return inside

        for y in range(min_y, max_y + 1):
            for x in range(min_x, max_x + 1):
                if point_in_polygon(x, y):
                    set_pixel(x, y, color)

    def draw_hand(s):
        """手（手首から先）を描画"""
        skin_color = (245, 208, 176, 255)
        skin_outline = (212, 165, 116, 255)

        # 簡略化した手のシルエット（楕円と四角形の組み合わせ）
        # 手首
        wrist_x = int(195 * s)
        wrist_y = int(85 * s)
        wrist_w = int(35 * s)
        wrist_h = int(55 * s)

        # 手のひら（楕円）
        palm_cx = int(188 * s)
        palm_cy = int(60 * s)
        palm_rx = int(32 * s)
        palm_ry = int(38 * s)
        fill_ellipse(palm_cx, palm_cy, palm_rx, palm_ry, skin_color)

        # 手首部分（四角形）
        fill_rounded_rect(int(170 * s), int(75 * s), int(220 * s), int(140 * s), int(8 * s), skin_color)

        # 親指
        thumb_cx = int(148 * s)
        thumb_cy = int(85 * s)
        fill_ellipse(thumb_cx, thumb_cy, int(12 * s), int(18 * s), skin_color)

    def draw_card(s, angle=-25):
        """斜めに傾いたICカードを描画"""
        card_color = (200, 200, 210, 255)
        card_border = (150, 150, 160, 255)
        chip_color = (212, 175, 55, 255)

        # カードの中心と寸法
        card_cx = int(108 * s)
        card_cy = int(70 * s)
        card_w = int(95 * s)
        card_h = int(60 * s)

        # 回転したカード本体
        fill_rotated_rect(card_cx, card_cy, card_w, card_h, angle, card_color)

        # ICチップ（簡易版 - 小さいサイズでは省略）
        if s >= 0.125:  # 32x32以上
            chip_cx = int(75 * s)
            chip_cy = int(62 * s)
            chip_w = int(22 * s)
            chip_h = int(16 * s)
            fill_rotated_rect(chip_cx, chip_cy, chip_w, chip_h, angle, chip_color)

    # スケーリング係数
    s = size / 256.0

    # 色定義
    reader_color = (40, 40, 40, 255)      # リーダー本体（ダークグレー）
    reader_top = (50, 50, 50, 255)        # リーダー上面
    led_color = (0, 200, 100, 255)        # LED（緑）
    wave_color = (0, 170, 85, 180)        # 通信波（緑、半透明）
    nfc_color = (80, 80, 80, 255)         # NFCマーク

    # ICカードリーダー本体
    fill_rounded_rect(40*s, 155*s, 216*s, 245*s, 12*s, reader_color)
    fill_rounded_rect(50*s, 165*s, 206*s, 225*s, 8*s, reader_top)

    # リーダー中央のNFCマーク（同心円）
    cx, cy = int(128*s), int(195*s)
    draw_circle(cx, cy, int(18*s), nfc_color, max(1, int(2*s)))
    draw_circle(cx, cy, int(10*s), nfc_color, max(1, int(1.5*s)))
    if s >= 0.125:  # 32x32以上
        fill_ellipse(cx, cy, int(4*s), int(4*s), nfc_color)

    # ステータスLED
    fill_rect(185*s, 235*s, 205*s, 239*s, led_color)

    # 手を描画
    draw_hand(s)

    # 交通系ICカード（斜め）
    draw_card(s, -25)

    # 通信波（弧線）
    wave_y_start = int(130*s)
    for wave_idx, wave_offset in enumerate([0, 10, 20]):
        wave_y = wave_y_start + int(wave_offset * s)
        wave_width = int((56 + wave_offset * 3) * s)
        wave_cx = int(128 * s)
        for x in range(wave_cx - wave_width // 2, wave_cx + wave_width // 2):
            # 簡易的な弧を描画
            dx = abs(x - wave_cx)
            dy = int(dx * dx / (wave_width * 0.5) * 0.3)
            blend_pixel(x, wave_y + dy, wave_color)
            if s >= 0.5:  # 大きいサイズでは線を太く
                blend_pixel(x, wave_y + dy + 1, wave_color)

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
