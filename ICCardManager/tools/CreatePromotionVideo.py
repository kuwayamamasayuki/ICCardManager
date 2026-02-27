#!/usr/bin/env python3
"""
プロモーション動画生成スクリプト

交通系ICカード管理システムの庁内プロモーション用動画（約27秒）を
Pillow + ffmpeg で生成する。

使い方:
    pip install Pillow
    python tools/CreatePromotionVideo.py

出力:
    docs/promotion/promotion_video.mp4

依存:
    - Python 3.x
    - Pillow (pip install Pillow)
    - ffmpeg (パスが通っていること)
    - Yu Gothic Bold フォント (Windows標準)
"""

import math
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

try:
    from PIL import Image, ImageDraw, ImageFont
except ImportError:
    print("エラー: Pillow がインストールされていません。")
    print("  pip install Pillow")
    sys.exit(1)

# ============================================================
# 定数
# ============================================================
WIDTH, HEIGHT = 1920, 1080
FPS = 30

# カラーパレット
BG_COLOR = (245, 245, 245)           # #F5F5F5 ライトグレー背景
MAIN_BLUE = (33, 150, 243)           # #2196F3 アプリのテーマカラー
TEXT_DARK = (33, 33, 33)             # #212121
TEXT_WHITE = (255, 255, 255)         # #FFFFFF
CARD_SILVER_BASE = 192               # 交通系ICカードの銀色ベース値
READER_BLACK = (26, 26, 26)          # #1A1A1A カードリーダー本体
READER_TOP = (50, 50, 50)            # リーダー上面
READER_LAMP_OFF = (60, 60, 60)       # インジケーターOFF
READER_LAMP_ON = (76, 175, 80)       # #4CAF50 インジケーターON
SHADOW_COLOR = (180, 180, 180)       # スクリーンショットの影
BORDER_COLOR = (200, 200, 200)       # カード・スクリーンショットの枠線

# フォント
FONT_PATH = "/mnt/c/Windows/Fonts/YuGothB.ttc"

# パス
SCRIPT_DIR = Path(__file__).resolve().parent
PROJECT_DIR = SCRIPT_DIR.parent
SCREENSHOTS_DIR = PROJECT_DIR / "docs" / "screenshots"
SOUNDS_DIR = PROJECT_DIR / "src" / "ICCardManager" / "Resources" / "Sounds"
OUTPUT_DIR = PROJECT_DIR / "docs" / "promotion"

# カードサイズ (実際のICカード比率 85.6mm×54mm ≒ 1.585:1 に近似)
CARD_WIDTH = 300
CARD_HEIGHT = 190

# リーダーサイズ
READER_WIDTH = 200
READER_HEIGHT = 120
READER_TOP_HEIGHT = 15


# ============================================================
# フォントキャッシュ
# ============================================================
_font_cache = {}


def get_font(size: int) -> ImageFont.FreeTypeFont:
    """Yu Gothic Bold フォントを取得（キャッシュ付き）"""
    if size not in _font_cache:
        try:
            _font_cache[size] = ImageFont.truetype(FONT_PATH, size)
        except OSError:
            print(f"警告: フォント {FONT_PATH} が見つかりません。デフォルトフォントを使用します。")
            _font_cache[size] = ImageFont.load_default()
    return _font_cache[size]


# ============================================================
# 描画ヘルパー
# ============================================================
def new_frame() -> Image.Image:
    """背景色で塗りつぶした新規フレームを作成"""
    return Image.new("RGB", (WIDTH, HEIGHT), BG_COLOR)


def draw_staff_card(img: Image.Image, cx: int, cy: int, angle: float = 0):
    """
    職員証を描画
    白地の角丸長方形、上1/5が青(#2196F3)、下部中央に「職員証」テキスト
    """
    card = Image.new("RGBA", (CARD_WIDTH, CARD_HEIGHT), (0, 0, 0, 0))
    cd = ImageDraw.Draw(card)

    # カード本体 (白い角丸長方形)
    cd.rounded_rectangle(
        (0, 0, CARD_WIDTH - 1, CARD_HEIGHT - 1),
        radius=12, fill=(255, 255, 255), outline=BORDER_COLOR
    )

    # 上部1/5を青く塗る
    blue_h = CARD_HEIGHT // 5
    # 上部の角丸部分を青で描画
    cd.rounded_rectangle(
        (0, 0, CARD_WIDTH - 1, blue_h + 12),
        radius=12, fill=MAIN_BLUE
    )
    # 角丸の下にはみ出た部分を白で修正（下半分を白に戻す）
    cd.rectangle((0, blue_h, CARD_WIDTH - 1, blue_h + 12), fill=(255, 255, 255))

    # 「職員証」テキスト (下部エリアの中央)
    font = get_font(36)
    text = "職員証"
    bbox = cd.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    tx = (CARD_WIDTH - tw) // 2
    ty = blue_h + (CARD_HEIGHT - blue_h - th) // 2
    cd.text((tx, ty), text, fill=TEXT_DARK, font=font)

    # 回転（傾き演出用）
    if angle != 0:
        card = card.rotate(angle, expand=True, resample=Image.BICUBIC)

    # 中心座標基準で貼り付け
    paste_x = cx - card.width // 2
    paste_y = cy - card.height // 2
    img.paste(card, (paste_x, paste_y), card)


def draw_ic_card(img: Image.Image, cx: int, cy: int, angle: float = 0):
    """
    交通系ICカードを描画
    銀色ベースの角丸長方形にグラデーション風の質感、中央に「交通系」テキスト
    """
    card = Image.new("RGBA", (CARD_WIDTH, CARD_HEIGHT), (0, 0, 0, 0))
    cd = ImageDraw.Draw(card)

    # 角丸でマスクを作成してからグラデーション塗り
    mask = Image.new("L", (CARD_WIDTH, CARD_HEIGHT), 0)
    md = ImageDraw.Draw(mask)
    md.rounded_rectangle((0, 0, CARD_WIDTH - 1, CARD_HEIGHT - 1), radius=12, fill=255)

    # 銀色グラデーション（縦方向にsin波で明暗をつける）
    for y in range(CARD_HEIGHT):
        brightness = CARD_SILVER_BASE + int(20 * math.sin(y / CARD_HEIGHT * math.pi))
        color = (brightness, brightness, brightness + 5, 255)
        cd.line([(0, y), (CARD_WIDTH - 1, y)], fill=color)

    # マスク適用（角丸の外側を透過に）
    card.putalpha(mask)

    # 枠線
    cd = ImageDraw.Draw(card)
    cd.rounded_rectangle(
        (0, 0, CARD_WIDTH - 1, CARD_HEIGHT - 1),
        radius=12, fill=None, outline=(170, 170, 170)
    )

    # 「交通系」テキスト
    font = get_font(32)
    text = "交通系"
    bbox = cd.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    tx = (CARD_WIDTH - tw) // 2
    ty = (CARD_HEIGHT - th) // 2
    cd.text((tx, ty), text, fill=(80, 80, 80), font=font)

    if angle != 0:
        card = card.rotate(angle, expand=True, resample=Image.BICUBIC)

    paste_x = cx - card.width // 2
    paste_y = cy - card.height // 2
    img.paste(card, (paste_x, paste_y), card)


def draw_card_reader(img: Image.Image, cx: int, cy: int, lit: bool = False):
    """
    カードリーダーを描画
    黒い台形型の本体、上面に奥行き感、インジケーターランプ付き
    """
    margin = 20
    total_w = READER_WIDTH + margin * 2
    total_h = READER_HEIGHT + READER_TOP_HEIGHT + margin * 2
    reader = Image.new("RGBA", (total_w, total_h), (0, 0, 0, 0))
    rd = ImageDraw.Draw(reader)

    ox, oy = margin, margin

    # 本体（台形：上辺が少し狭い）
    body_pts = [
        (ox + 10, oy + READER_TOP_HEIGHT),
        (ox + READER_WIDTH - 10, oy + READER_TOP_HEIGHT),
        (ox + READER_WIDTH, oy + READER_TOP_HEIGHT + READER_HEIGHT),
        (ox, oy + READER_TOP_HEIGHT + READER_HEIGHT),
    ]
    rd.polygon(body_pts, fill=READER_BLACK)

    # 上面（パースを出す台形）
    top_pts = [
        (ox + 15, oy),
        (ox + READER_WIDTH - 15, oy),
        (ox + READER_WIDTH - 10, oy + READER_TOP_HEIGHT),
        (ox + 10, oy + READER_TOP_HEIGHT),
    ]
    rd.polygon(top_pts, fill=READER_TOP)

    # インジケーターランプ
    lamp_color = READER_LAMP_ON if lit else READER_LAMP_OFF
    lamp_cx = ox + READER_WIDTH // 2
    lamp_cy = oy + READER_TOP_HEIGHT + 15
    rd.ellipse(
        (lamp_cx - 6, lamp_cy - 6, lamp_cx + 6, lamp_cy + 6),
        fill=lamp_color
    )

    # ランプ点灯時の発光エフェクト
    if lit:
        glow = Image.new("RGBA", reader.size, (0, 0, 0, 0))
        gd = ImageDraw.Draw(glow)
        for r in range(15, 3, -1):
            alpha = int(40 * (1 - r / 15))
            gd.ellipse(
                (lamp_cx - r, lamp_cy - r, lamp_cx + r, lamp_cy + r),
                fill=(76, 175, 80, alpha)
            )
        reader = Image.alpha_composite(reader, glow)

    paste_x = cx - reader.width // 2
    paste_y = cy - reader.height // 2
    img.paste(reader, (paste_x, paste_y), reader)


def draw_ripple(img: Image.Image, cx: int, cy: int, radius: int, alpha: int = 80):
    """タッチ時の波紋エフェクトを描画（同心円が広がる）"""
    overlay = Image.new("RGBA", img.size, (0, 0, 0, 0))
    od = ImageDraw.Draw(overlay)

    for i in range(3):
        r = radius - i * (radius // 4)
        if r <= 0:
            continue
        a = max(0, alpha - i * 30)
        od.ellipse(
            (cx - r, cy - r, cx + r, cy + r),
            outline=(*MAIN_BLUE, a), width=3
        )

    img_rgba = img.convert("RGBA")
    composited = Image.alpha_composite(img_rgba, overlay)
    img.paste(composited.convert("RGB"))


def draw_text_multiline(img: Image.Image, text: str, font_size: int = 56,
                        color=TEXT_DARK, y_offset: int = 0, line_spacing: int = 20):
    """中央揃えで複数行テキストを描画"""
    draw = ImageDraw.Draw(img)
    font = get_font(font_size)
    lines = text.split("\n")

    # 各行の高さを計算して全体の高さを求める
    line_heights = []
    line_widths = []
    for line in lines:
        bbox = draw.textbbox((0, 0), line, font=font)
        line_widths.append(bbox[2] - bbox[0])
        line_heights.append(bbox[3] - bbox[1])

    total_h = sum(line_heights) + line_spacing * (len(lines) - 1)
    current_y = (HEIGHT - total_h) // 2 + y_offset

    for i, line in enumerate(lines):
        x = (WIDTH - line_widths[i]) // 2
        draw.text((x, current_y), line, fill=color, font=font)
        current_y += line_heights[i] + line_spacing


def apply_fade(img: Image.Image, alpha: float) -> Image.Image:
    """フレームにフェード効果を適用 (0.0=背景色のみ, 1.0=そのまま)"""
    if alpha >= 1.0:
        return img
    bg = Image.new("RGB", (WIDTH, HEIGHT), BG_COLOR)
    return Image.blend(bg, img, max(0.0, min(1.0, alpha)))


def load_and_fit_screenshot(path: Path, max_w: int, max_h: int) -> Image.Image:
    """スクリーンショットを読み込み、指定サイズ内にアスペクト比を維持してリサイズ"""
    screenshot = Image.open(path)
    ratio = min(max_w / screenshot.width, max_h / screenshot.height)
    new_w = int(screenshot.width * ratio)
    new_h = int(screenshot.height * ratio)
    return screenshot.resize((new_w, new_h), Image.LANCZOS)


def draw_caption_bar(img: Image.Image, text: str, y: int):
    """画面下部にキャプションバー（半透明の青帯+白文字）を描画"""
    bar_height = 70
    bar_img = Image.new("RGBA", (WIDTH, bar_height), (*MAIN_BLUE, 220))
    img_rgba = img.convert("RGBA")
    img_rgba.paste(bar_img, (0, y), bar_img)
    img.paste(img_rgba.convert("RGB"))

    draw = ImageDraw.Draw(img)
    font = get_font(32)
    bbox = draw.textbbox((0, 0), text, font=font)
    tw = bbox[2] - bbox[0]
    draw.text(((WIDTH - tw) // 2, y + 18), text, fill=TEXT_WHITE, font=font)


# ============================================================
# シーン生成
# ============================================================
def scene_text(text: str, duration_sec: float,
               fade_in_sec: float = 0.5, fade_out_sec: float = 0.5,
               font_size: int = 56) -> list:
    """テキスト表示シーン（フェードイン → ホールド → フェードアウト）"""
    frames = []
    total = int(duration_sec * FPS)
    fi = int(fade_in_sec * FPS)
    fo = int(fade_out_sec * FPS)

    for i in range(total):
        img = new_frame()
        draw_text_multiline(img, text, font_size=font_size)

        if i < fi:
            img = apply_fade(img, i / fi)
        elif i >= total - fo:
            img = apply_fade(img, (total - i) / fo)

        frames.append(img)
    return frames


def scene_card_touch(card_type: str, duration_sec: float = 4.0) -> list:
    """
    カードがリーダーに近づいてタッチするアニメーション
    card_type: "staff" (職員証) or "ic" (交通系ICカード)
    """
    frames = []
    total = int(duration_sec * FPS)

    # タイミング
    slide_end = int(total * 0.5)      # カードスライド完了
    touch_frame = slide_end           # タッチ瞬間
    ripple_dur = int(0.8 * FPS)       # 波紋持続フレーム数
    text_dur = int(1.2 * FPS)         # テキスト表示フレーム数

    # 位置
    reader_cx = WIDTH // 2
    reader_cy = HEIGHT // 2 + 40
    card_start_x = WIDTH + CARD_WIDTH     # 画面外右
    card_end_x = reader_cx + 20           # リーダーの少し右上
    card_base_y = reader_cy - 100

    for i in range(total):
        img = new_frame()

        # リーダー描画（タッチ後にランプ点灯）
        draw_card_reader(img, reader_cx, reader_cy, lit=(i >= touch_frame))

        # カード位置計算（ease-out cubic + 弧を描く軌道）
        if i < slide_end:
            t = i / slide_end
            t_ease = 1 - (1 - t) ** 3
            card_x = int(card_start_x + (card_end_x - card_start_x) * t_ease)
            card_y = card_base_y - int(30 * math.sin(t * math.pi))
            angle = -5 * (1 - t)
        else:
            card_x = card_end_x
            card_y = card_base_y
            angle = 0

        # カード描画
        if card_type == "staff":
            draw_staff_card(img, card_x, card_y, angle)
        else:
            draw_ic_card(img, card_x, card_y, angle)

        # 波紋エフェクト
        if touch_frame <= i < touch_frame + ripple_dur:
            rt = (i - touch_frame) / ripple_dur
            r_radius = int(20 + 80 * rt)
            r_alpha = int(100 * (1 - rt))
            draw_ripple(img, reader_cx, reader_cy - 30, r_radius, r_alpha)

        # 「ピッ♪」/ 「ピピッ♪」テキスト（タッチ後に上へ浮かぶ）
        if touch_frame <= i < touch_frame + text_dur:
            tt = (i - touch_frame) / text_dur
            text_alpha = 1.0 if tt < 0.7 else max(0, (1 - tt) / 0.3)
            text_y = int(reader_cy - 180 - 30 * tt)

            sound_text = "ピッ♪" if card_type == "staff" else "ピピッ♪"
            overlay = Image.new("RGBA", (WIDTH, HEIGHT), (0, 0, 0, 0))
            td = ImageDraw.Draw(overlay)
            font = get_font(40)
            bbox = td.textbbox((0, 0), sound_text, font=font)
            tw = bbox[2] - bbox[0]
            a = int(255 * max(0.0, min(1.0, text_alpha)))
            td.text(((WIDTH - tw) // 2, text_y), sound_text,
                    fill=(*MAIN_BLUE, a), font=font)

            img_rgba = img.convert("RGBA")
            img = Image.alpha_composite(img_rgba, overlay).convert("RGB")

        frames.append(img)
    return frames


def scene_screenshot(screenshot_path: Path, caption: str,
                     duration_sec: float = 3.0) -> list:
    """スクリーンショット表示シーン（スライドイン + キャプション）"""
    frames = []
    total = int(duration_sec * FPS)
    slide_frames = int(0.4 * FPS)

    # スクリーンショット読み込み（キャプションバー分の余白を残す）
    max_h = HEIGHT - 120
    screenshot = load_and_fit_screenshot(screenshot_path, WIDTH - 120, max_h)

    ss_x = (WIDTH - screenshot.width) // 2
    ss_y = (HEIGHT - 70 - screenshot.height) // 2

    for i in range(total):
        img = new_frame()
        draw = ImageDraw.Draw(img)

        # スライドインアニメーション
        if i < slide_frames:
            t = i / slide_frames
            t_ease = 1 - (1 - t) ** 3
            offset_x = int((WIDTH // 4) * (1 - t_ease))
            alpha = t_ease
        else:
            offset_x = 0
            alpha = 1.0

        # 影
        draw.rectangle(
            (ss_x + offset_x + 4, ss_y + 4,
             ss_x + offset_x + screenshot.width + 4, ss_y + screenshot.height + 4),
            fill=SHADOW_COLOR
        )

        # スクリーンショット
        img.paste(screenshot, (ss_x + offset_x, ss_y))

        # 枠線
        draw.rectangle(
            (ss_x + offset_x - 1, ss_y - 1,
             ss_x + offset_x + screenshot.width, ss_y + screenshot.height),
            outline=BORDER_COLOR, width=1
        )

        # キャプション
        draw_caption_bar(img, caption, HEIGHT - 70)

        # フェード
        if alpha < 1.0:
            img = apply_fade(img, alpha)

        frames.append(img)
    return frames


def scene_end_card(duration_sec: float = 4.0) -> list:
    """エンドカード（キャッチコピー + アプリ名）"""
    frames = []
    total = int(duration_sec * FPS)
    fi = int(0.8 * FPS)

    for i in range(total):
        img = new_frame()
        draw = ImageDraw.Draw(img)

        # メインコピー
        main_text = "タッチ2回。帳簿は自動。"
        font_main = get_font(64)
        bbox = draw.textbbox((0, 0), main_text, font=font_main)
        tw = bbox[2] - bbox[0]
        draw.text(((WIDTH - tw) // 2, HEIGHT // 2 - 80), main_text,
                  fill=TEXT_DARK, font=font_main)

        # 区切り線
        line_w = 200
        line_y = HEIGHT // 2 + 10
        draw.line(
            ((WIDTH - line_w) // 2, line_y, (WIDTH + line_w) // 2, line_y),
            fill=MAIN_BLUE, width=2
        )

        # アプリ名
        app_text = "交通系ICカード管理システム"
        font_app = get_font(36)
        bbox = draw.textbbox((0, 0), app_text, font=font_app)
        tw = bbox[2] - bbox[0]
        draw.text(((WIDTH - tw) // 2, HEIGHT // 2 + 30), app_text,
                  fill=MAIN_BLUE, font=font_app)

        # フェードイン
        if i < fi:
            img = apply_fade(img, i / fi)

        frames.append(img)
    return frames


# ============================================================
# メイン処理
# ============================================================
def save_frames(frames: list, tmpdir: Path, start_idx: int) -> int:
    """フレーム一覧をPNGとして保存し、次のインデックスを返す"""
    for img in frames:
        img.save(tmpdir / f"frame_{start_idx:05d}.png")
        start_idx += 1
    return start_idx


def build_ffmpeg_command(tmpdir: Path, output_path: Path,
                         total_duration: float,
                         touch1_time: float, touch2_time: float) -> list:
    """ffmpegコマンドを構築（映像 + 効果音）"""
    lend_wav = SOUNDS_DIR / "lend.wav"
    return_wav = SOUNDS_DIR / "return.wav"

    cmd = [
        "ffmpeg", "-y",
        "-framerate", str(FPS),
        "-i", str(tmpdir / "frame_%05d.png"),
    ]

    # 音声入力と遅延フィルター
    audio_inputs = []
    filter_parts = []
    audio_labels = []
    input_idx = 1

    if lend_wav.exists():
        cmd.extend(["-i", str(lend_wav)])
        delay_ms = int(touch1_time * 1000)
        filter_parts.append(
            f"[{input_idx}:a]adelay={delay_ms}|{delay_ms}[a{input_idx}]"
        )
        audio_labels.append(f"[a{input_idx}]")
        input_idx += 1

    if return_wav.exists():
        cmd.extend(["-i", str(return_wav)])
        delay_ms = int(touch2_time * 1000)
        filter_parts.append(
            f"[{input_idx}:a]adelay={delay_ms}|{delay_ms}[a{input_idx}]"
        )
        audio_labels.append(f"[a{input_idx}]")
        input_idx += 1

    if audio_labels:
        n = len(audio_labels)
        mix = "".join(audio_labels) + f"amix=inputs={n}:duration=longest[aout]"
        filter_complex = ";".join(filter_parts) + ";" + mix
        cmd.extend(["-filter_complex", filter_complex])
        cmd.extend(["-map", "0:v", "-map", "[aout]"])
        cmd.extend(["-c:a", "aac", "-b:a", "128k"])

    cmd.extend([
        "-c:v", "libx264",
        "-preset", "medium",
        "-crf", "23",
        "-pix_fmt", "yuv420p",
        "-t", str(total_duration),
        str(output_path),
    ])
    return cmd


def main():
    print("=" * 60)
    print("  交通系ICカード管理システム プロモーション動画生成")
    print("=" * 60)

    # 依存チェック
    if shutil.which("ffmpeg") is None:
        print("\nエラー: ffmpeg が見つかりません。")
        print("  sudo apt install ffmpeg  または")
        print("  https://ffmpeg.org/ からインストールしてください。")
        sys.exit(1)

    # 出力ディレクトリ作成
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    # スクリーンショット存在確認
    screenshots = {
        "staff_recognized": SCREENSHOTS_DIR / "staff_recognized.png",
        "lend": SCREENSHOTS_DIR / "lend.png",
        "report_excel": SCREENSHOTS_DIR / "report_excel.png",
    }
    for name, path in screenshots.items():
        if not path.exists():
            print(f"エラー: スクリーンショットが見つかりません: {path}")
            sys.exit(1)

    # シーンタイミング（各シーンの秒数）
    # Scene 1:  3s  キャッチコピー
    # Scene 2:  4s  職員証タッチ
    # Scene 3:  3s  職員証認識画面
    # Scene 4:  4s  交通系ICカードタッチ
    # Scene 5:  3s  貸出完了画面
    # Scene 6:  3s  帳票テキスト
    # Scene 7:  3s  帳票画面
    # Scene 8:  4s  エンドカード
    # 合計:    27s

    with tempfile.TemporaryDirectory(prefix="promo_") as tmpdir:
        tmpdir = Path(tmpdir)
        idx = 0

        print("\n[1/8] キャッチコピー (0-3s)...")
        idx = save_frames(
            scene_text("交通系ICカードの管理、\nまだ手書きですか？", 3.0),
            tmpdir, idx
        )

        print("[2/8] 職員証タッチ (3-7s)...")
        idx = save_frames(
            scene_card_touch("staff", 4.0),
            tmpdir, idx
        )

        print("[3/8] 職員証認識画面 (7-10s)...")
        idx = save_frames(
            scene_screenshot(screenshots["staff_recognized"], "職員証をタッチ", 3.0),
            tmpdir, idx
        )

        print("[4/8] 交通系ICカードタッチ (10-14s)...")
        idx = save_frames(
            scene_card_touch("ic", 4.0),
            tmpdir, idx
        )

        print("[5/8] 貸出完了画面 (14-17s)...")
        idx = save_frames(
            scene_screenshot(screenshots["lend"],
                             "交通系ICカードをタッチ → 貸出完了", 3.0),
            tmpdir, idx
        )

        print("[6/8] 帳票メッセージ (17-20s)...")
        idx = save_frames(
            scene_text("帳票もボタン1つで自動作成", 3.0),
            tmpdir, idx
        )

        print("[7/8] 帳票出力画面 (20-23s)...")
        idx = save_frames(
            scene_screenshot(screenshots["report_excel"],
                             "物品出納簿をExcelで自動出力", 3.0),
            tmpdir, idx
        )

        print("[8/8] エンドカード (23-27s)...")
        idx = save_frames(
            scene_end_card(4.0),
            tmpdir, idx
        )

        total_duration = idx / FPS
        print(f"\n全 {idx} フレーム生成完了（約 {total_duration:.1f} 秒）")

        # 効果音のタイミング
        # Scene 2: 3.0s開始、タッチはduration_sec*0.5=2.0s後 → 5.0s
        # Scene 4: 10.0s開始、タッチは2.0s後 → 12.0s
        touch1_time = 3.0 + 2.0   # 職員証タッチ
        touch2_time = 10.0 + 2.0  # 交通系ICカードタッチ

        # MP4エンコード
        print("\nffmpegでMP4エンコード中...")
        output_path = OUTPUT_DIR / "promotion_video.mp4"

        cmd = build_ffmpeg_command(
            tmpdir, output_path, total_duration,
            touch1_time, touch2_time
        )

        result = subprocess.run(cmd, capture_output=True, text=True)

        if result.returncode != 0:
            print(f"ffmpegエラー:\n{result.stderr[:500]}")
            print("\n音声なしでリトライします...")
            cmd_simple = [
                "ffmpeg", "-y",
                "-framerate", str(FPS),
                "-i", str(tmpdir / "frame_%05d.png"),
                "-c:v", "libx264",
                "-preset", "medium",
                "-crf", "23",
                "-pix_fmt", "yuv420p",
                "-t", str(total_duration),
                str(output_path),
            ]
            result = subprocess.run(cmd_simple, capture_output=True, text=True)
            if result.returncode != 0:
                print(f"ffmpegエラー:\n{result.stderr[:500]}")
                sys.exit(1)
            print("（音声なしで生成しました）")

        print(f"\n動画を生成しました: {output_path}")
        print(f"  解像度: {WIDTH}x{HEIGHT}")
        print(f"  長さ: 約 {total_duration:.1f} 秒")
        print(f"  フレームレート: {FPS} fps")


if __name__ == "__main__":
    main()
