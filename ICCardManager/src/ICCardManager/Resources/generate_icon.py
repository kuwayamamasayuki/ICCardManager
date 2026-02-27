#!/usr/bin/env python3
"""
交通系ICカード管理システム：ピッすい アイコン生成スクリプト
Issue #269: かっこいいアイコンを作成する

このスクリプトは標準ライブラリのみを使用して、
親指と人差し指でICカードを持ってリーダーにかざすアイコンを生成します。

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
    親指と人差し指でICカードを持ってリーダーにかざすイメージ
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
                in_corner = False
                corners = [
                    (x1 + radius, y1 + radius),
                    (x2 - radius, y1 + radius),
                    (x1 + radius, y2 - radius),
                    (x2 - radius, y2 - radius),
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
        if rx <= 0 or ry <= 0:
            return
        for y in range(int(cy - ry), int(cy + ry + 1)):
            for x in range(int(cx - rx), int(cx + rx + 1)):
                dx = (x - cx) / rx
                dy = (y - cy) / ry
                if dx * dx + dy * dy <= 1:
                    set_pixel(int(x), int(y), color)

    def draw_circle(cx, cy, r, color, thickness=1):
        """円の輪郭を描画"""
        if r <= 0:
            return
        for angle in range(360):
            rad = math.radians(angle)
            for dr in range(-thickness // 2, thickness // 2 + 1):
                x = int(cx + (r + dr) * math.cos(rad))
                y = int(cy + (r + dr) * math.sin(rad))
                set_pixel(x, y, color)

    def fill_rotated_rect(cx, cy, w, h, angle_deg, color):
        """回転した四角形を塗りつぶす"""
        if w <= 0 or h <= 0:
            return
        angle = math.radians(angle_deg)
        cos_a = math.cos(angle)
        sin_a = math.sin(angle)

        hw, hh = w / 2, h / 2
        corners = [(-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh)]
        rotated = []
        for px, py in corners:
            rx = px * cos_a - py * sin_a + cx
            ry = px * sin_a + py * cos_a + cy
            rotated.append((rx, ry))

        min_x = int(min(p[0] for p in rotated))
        max_x = int(max(p[0] for p in rotated))
        min_y = int(min(p[1] for p in rotated))
        max_y = int(max(p[1] for p in rotated))

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

    # スケーリング係数
    s = size / 256.0

    # 色定義
    reader_color = (40, 40, 40, 255)
    reader_top = (50, 50, 50, 255)
    led_color = (0, 200, 100, 255)
    wave_color = (0, 170, 85, 180)
    nfc_color = (80, 80, 80, 255)
    card_color = (210, 210, 215, 255)
    chip_color = (212, 175, 55, 255)
    skin_color = (245, 208, 176, 255)

    # === ICカードリーダー（小さめ、下部に配置） ===
    fill_rounded_rect(58*s, 185*s, 198*s, 245*s, 8*s, reader_color)
    fill_rounded_rect(68*s, 193*s, 188*s, 233*s, 5*s, reader_top)

    # NFCマーク
    cx_r, cy_r = int(128*s), int(213*s)
    if s >= 0.125:
        draw_circle(cx_r, cy_r, int(12*s), nfc_color, max(1, int(1.5*s)))
        draw_circle(cx_r, cy_r, int(6*s), nfc_color, max(1, int(1*s)))
        fill_ellipse(cx_r, cy_r, int(2*s), int(2*s), nfc_color)

    # ステータスLED
    fill_rect(168*s, 237*s, 182*s, 240*s, led_color)

    # === 交通系ICカード（大きく、斜めに、中央〜上に配置） ===
    card_cx = int(108 * s)
    card_cy = int(75 * s)
    card_w = int(120 * s)
    card_h = int(75 * s)
    card_angle = -20

    fill_rotated_rect(card_cx, card_cy, card_w, card_h, card_angle, card_color)

    # ICチップ（大きいサイズのみ）
    if s >= 0.125:
        chip_cx = int(70 * s)
        chip_cy = int(65 * s)
        chip_w = int(28 * s)
        chip_h = int(20 * s)
        fill_rotated_rect(chip_cx, chip_cy, chip_w, chip_h, card_angle, chip_color)

    # === 手（親指と人差し指でカードの右端をつまむ） ===

    # 人差し指（カードの上側、曲げた形）
    finger_cx = int(205 * s)
    finger_cy = int(55 * s)
    finger_w = int(38 * s)
    finger_h = int(18 * s)
    fill_rotated_rect(finger_cx, finger_cy, finger_w, finger_h, -30, skin_color)
    # 指先の丸み
    fill_ellipse(int(188 * s), int(62 * s), int(10 * s), int(9 * s), skin_color)

    # 親指（カードの下側）
    thumb_cx = int(210 * s)
    thumb_cy = int(105 * s)
    thumb_w = int(35 * s)
    thumb_h = int(16 * s)
    fill_rotated_rect(thumb_cx, thumb_cy, thumb_w, thumb_h, 15, skin_color)
    # 親指の先の丸み
    fill_ellipse(int(195 * s), int(100 * s), int(9 * s), int(8 * s), skin_color)

    # 手のひら（右端に少し見える部分）
    palm_cx = int(235 * s)
    palm_cy = int(80 * s)
    palm_rx = int(22 * s)
    palm_ry = int(35 * s)
    fill_ellipse(palm_cx, palm_cy, palm_rx, palm_ry, skin_color)

    # === 通信波（弧線） ===
    wave_y_start = int(148 * s)
    for wave_idx, wave_offset in enumerate([0, 12, 24]):
        wave_y = wave_y_start + int(wave_offset * s)
        wave_width = int((50 + wave_offset * 2.5) * s)
        wave_cx = int(128 * s)
        for x in range(wave_cx - wave_width // 2, wave_cx + wave_width // 2):
            dx = abs(x - wave_cx)
            dy = int(dx * dx / (wave_width * 0.5) * 0.25)
            blend_pixel(x, wave_y + dy, wave_color)
            if s >= 0.5:
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
    ico_data = struct.pack('<HHH', 0, 1, len(images))

    header_size = 6 + 16 * len(images)
    current_offset = header_size

    entries = []
    for size, png_data in images:
        width = size if size < 256 else 0
        height = size if size < 256 else 0
        entry = struct.pack('<BBBBHHII',
            width, height, 0, 0, 1, 32,
            len(png_data), current_offset
        )
        entries.append(entry)
        current_offset += len(png_data)

    for entry in entries:
        ico_data += entry

    for size, png_data in images:
        ico_data += png_data

    with open(output_path, 'wb') as f:
        f.write(ico_data)

    print(f"\n完了: {output_path}")
    print(f"サイズ: {os.path.getsize(output_path):,} bytes")


if __name__ == '__main__':
    import sys
    output = os.path.join(os.path.dirname(__file__), 'app.ico')
    if len(sys.argv) > 1:
        output = sys.argv[1]

    print("交通系ICカード管理システム：ピッすい アイコン生成")
    print("=" * 40)
    create_ico(output)
