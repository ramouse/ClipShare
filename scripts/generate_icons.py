#!/usr/bin/env python3
"""ClipShare PWA 图标生成脚本（Pillow，幂等可重复执行）。

产物：app/static/icons/{icon-192,icon-512,maskable-512}.png
- 品牌蓝 #0d6efd 底 + 白色 C 形（粗弧线绘制，不依赖系统字体，产物可复现）；
- maskable 版图形收缩进中心安全区（厂商图标裁剪后内容不丢失）。

用法（容器内执行，qrcode[pil] 已带 Pillow）：
  docker compose run --rm app python scripts/generate_icons.py           # 生成
  docker compose run --rm app python scripts/generate_icons.py --check
      # 校验产物齐全且尺寸/底色正确（失败时退出码非零）
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

from PIL import Image, ImageDraw

# 品牌蓝（与 Bootstrap primary #0d6efd 一致）：页面导航栏同色，PWA theme_color 同色
BRAND_BLUE = (13, 110, 253)
WHITE = (255, 255, 255)

# 产物清单：(文件名, 边长, maskable 是否收缩图形)
ICONS_DIR = Path(__file__).resolve().parent.parent / "app" / "static" / "icons"
SPECS = [
    ("icon-192.png", 192, False),
    ("icon-512.png", 512, False),
    ("maskable-512.png", 512, True),
]


def draw_icon(size: int, maskable: bool) -> Image.Image:
    """绘制单枚图标：蓝底 + 白色 C 形。

    C 形用粗弧线绘制：开口朝右（3 点钟方向）。maskable 时图形整体收缩进
    中心 70% 区域内（安全区 = 中心 80% 圆，厂商按此裁剪不丢内容）。
    """
    img = Image.new("RGB", (size, size), BRAND_BLUE)
    draw = ImageDraw.Draw(img)
    # 收缩比例：普通图标图形约占满画布，maskable 预留安全区留白
    shrink = 0.14 if maskable else 0.06
    pad = int(size * shrink)
    # 弧线包围盒（方形 → 正圆）；start=40° 到 end=320°（PIL 逆时针），
    # 缺口落在 3 点钟方向（320°→360°→40°），即 C 形开口朝右
    bbox = (pad, pad, size - pad, size - pad)
    draw.arc(bbox, start=40, end=320, fill=WHITE, width=max(1, int(size * 0.18)))
    return img


def generate() -> None:
    """幂等生成全部图标：已存在的文件直接覆盖（内容确定，可反复执行）。"""
    ICONS_DIR.mkdir(parents=True, exist_ok=True)
    for name, size, maskable in SPECS:
        path = ICONS_DIR / name
        draw_icon(size, maskable).save(path, format="PNG")
        print(f"已生成：{path}（{size}x{size}，{'maskable' if maskable else 'any'}）")


def check() -> None:
    """校验产物：文件存在、尺寸与规格一致、四角底色为品牌蓝。"""
    errors: list[str] = []
    for name, size, _maskable in SPECS:
        path = ICONS_DIR / name
        if not path.is_file():
            errors.append(f"缺失：{path}")
            continue
        with Image.open(path) as img:
            if img.size != (size, size):
                errors.append(f"尺寸不符：{name} 应为 {size}x{size}，实际 {img.size}")
            # 四角像素应为品牌蓝（RGB 模式；PNG 可能为 RGBA，转 RGB 比较）
            rgba = img.convert("RGBA")
            for corner in ((0, 0), (size - 1, 0), (0, size - 1), (size - 1, size - 1)):
                if rgba.getpixel(corner)[:3] != BRAND_BLUE:
                    errors.append(f"底色不符：{name} 角落 {corner} 不是品牌蓝")
    if errors:
        print("校验失败：")
        for err in errors:
            print(f"  - {err}")
        sys.exit(1)
    print(f"校验通过：{len(SPECS)} 枚图标齐全，尺寸与底色正确")


def main() -> None:
    parser = argparse.ArgumentParser(description="生成/校验 ClipShare PWA 图标")
    parser.add_argument(
        "--check", action="store_true", help="仅校验产物（不重新生成），失败时退出码非零"
    )
    args = parser.parse_args()
    check() if args.check else generate()


if __name__ == "__main__":
    main()
