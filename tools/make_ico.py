#!/usr/bin/env python3
"""Собирает src/UniversalMeetingRecorder.App/Assets/app.ico из PNG-иконки macOS-версии.

Иконка рисуется кодом в macOS-версии (generate_app_icon.swift, каждый размер нативно) и лежит там
в Resources/Assets.xcassets/AppIcon.appiconset. Windows-иконка — производная: ICO-контейнер с теми же
PNG-кадрами (ICO с Vista допускает PNG внутри). Готовый app.ico лежит в репозитории; пересобирать
его нужно, только если иконка поменялась:

    python3 tools/make_ico.py <путь к AppIcon.appiconset>

Без внешних зависимостей: только чтение размеров из заголовка PNG.
"""
import os
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DST = os.path.join(ROOT, "src", "UniversalMeetingRecorder.App", "Assets", "app.ico")
SRC = sys.argv[1] if len(sys.argv) > 1 else None

# Размеры, которые использует Windows (Проводник, панель задач, Alt-Tab, трей при 100–300% масштаба).
FILES = {
    16: "icon_16x16.png",
    32: "icon_32x32.png",
    48: None,  # нет нативного 48 — Windows возьмёт ближайший
    64: "icon_32x32@2x.png",
    128: "icon_128x128.png",
    256: "icon_256x256.png",
}


def png_size(data):
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("не PNG")
    return struct.unpack(">II", data[16:24])


def main():
    if SRC is None or not os.path.isdir(SRC):
        sys.exit("Укажите путь к AppIcon.appiconset macOS-версии: python3 tools/make_ico.py <путь>")
    frames = []
    for size, name in FILES.items():
        if name is None:
            continue
        with open(os.path.join(SRC, name), "rb") as f:
            data = f.read()
        w, h = png_size(data)
        if (w, h) != (size, size):
            sys.exit(f"{name}: ожидался {size}x{size}, а там {w}x{h}")
        frames.append((size, data))

    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries = b""
    for size, data in frames:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    os.makedirs(os.path.dirname(DST), exist_ok=True)
    with open(DST, "wb") as f:
        f.write(header + entries + b"".join(d for _, d in frames))
    print(f"{DST}: {len(frames)} кадров")


if __name__ == "__main__":
    main()
