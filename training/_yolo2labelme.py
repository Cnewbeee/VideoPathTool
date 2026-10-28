"""把 YOLO 标签转成 labelme 的 JSON，方便人工检查/修正。

用法（在仓库根目录）：

    python training/_yolo2labelme.py            # train + val 都转
    python training/_yolo2labelme.py --split train
    python training/_yolo2labelme.py --overwrite   # 覆盖已存在的 json

生成的文件就放在图片旁边（<图片名>.json），labelme 打开图片目录即可看到
所有框。YOLO 训练不会读 .json，所以不影响训练集。

空标签（负样本，表示"这张图里没有风之翼"）也会生成一个没有 shape 的 json，
打开时看到的就是干净的画面 —— 那正是要检查的重点：模型误报的那些披风、
传送道具，需要人工确认它们确实不该有框。
"""
import argparse
import json
import os
import shutil
import sys
import time

from _paths import data

# YOLO 的类别顺序来自 data.yaml，这里只有一类
CLASS_NAMES = ["glider"]

LABEL_COLOURS = [(0, 255, 0)]


def yolo_to_shapes(txt_path, img_w, img_h):
    shapes = []
    if not os.path.exists(txt_path):
        return shapes
    with open(txt_path, "r", encoding="utf-8", errors="ignore") as fh:
        for line in fh:
            parts = line.split()
            if len(parts) < 5:
                continue
            cls = int(float(parts[0]))
            cx, cy, bw, bh = (float(v) for v in parts[1:5])
            # YOLO 是"中心点 + 宽高"的归一化值，labelme 要左上/右下两个角点
            x1 = (cx - bw / 2) * img_w
            y1 = (cy - bh / 2) * img_h
            x2 = (cx + bw / 2) * img_w
            y2 = (cy + bh / 2) * img_h
            x1, x2 = max(0, min(x1, img_w)), max(0, min(x2, img_w))
            y1, y2 = max(0, min(y1, img_h)), max(0, min(y2, img_h))
            name = CLASS_NAMES[cls] if 0 <= cls < len(CLASS_NAMES) else str(cls)
            shapes.append({
                "label": name,
                "points": [[round(x1, 2), round(y1, 2)], [round(x2, 2), round(y2, 2)]],
                "group_id": None,
                "shape_type": "rectangle",
                "flags": {},
            })
    return shapes


def backup_json(img_dir, root, stamp):
    """把已有的 json 复制一份，覆盖/删除之前先留个底。

    这些 json 里可能有手工补的框 —— 丢一次就没了（labelme 不保留历史）。
    """
    existing = [n for n in os.listdir(img_dir) if n.lower().endswith(".json")]
    if not existing:
        return None
    dest = os.path.join(root, "json_backup", stamp, os.path.basename(img_dir))
    os.makedirs(dest, exist_ok=True)
    for name in existing:
        shutil.copy2(os.path.join(img_dir, name), os.path.join(dest, name))
    return dest


def convert_split(root, split, overwrite, stamp):
    img_dir = os.path.join(root, "images", split)
    lbl_dir = os.path.join(root, "labels", split)
    if not os.path.isdir(img_dir):
        print("跳过（没有这个目录）:", img_dir)
        return 0, 0

    if overwrite:
        saved = backup_json(img_dir, root, stamp)
        if saved:
            print(f"  已备份现有 json -> {saved}")

    from PIL import Image

    made = skipped = 0
    for name in sorted(os.listdir(img_dir)):
        if not name.lower().endswith((".jpg", ".jpeg", ".png")):
            continue
        img_path = os.path.join(img_dir, name)
        json_path = os.path.splitext(img_path)[0] + ".json"
        if os.path.exists(json_path) and not overwrite:
            skipped += 1
            continue

        with Image.open(img_path) as im:
            w, h = im.size
        stem = os.path.splitext(name)[0]
        shapes = yolo_to_shapes(os.path.join(lbl_dir, stem + ".txt"), w, h)

        payload = {
            "version": "5.0.1",
            "flags": {},
            "shapes": shapes,
            "imagePath": name,
            "imageData": None,          # 不内嵌图片，json 只有几百字节
            "imageHeight": h,
            "imageWidth": w,
        }
        with open(json_path, "w", encoding="utf-8") as fh:
            json.dump(payload, fh, ensure_ascii=False, indent=2)
        made += 1

    return made, skipped


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=data("yolo_ds"))
    ap.add_argument("--split", default="both", choices=["train", "val", "both"])
    ap.add_argument("--overwrite", action="store_true",
                    help="覆盖已有 json（会先自动备份到 json_backup/）")
    args = ap.parse_args()

    print("数据集:", args.root)
    stamp = time.strftime("%Y%m%d_%H%M%S")
    for split in (["train", "val"] if args.split == "both" else [args.split]):
        made, skipped = convert_split(args.root, split, args.overwrite, stamp)
        print(f"  {split}: 生成 {made} 个 json，跳过 {skipped} 个已存在")

    print("\n下一步：")
    print(f'  "{data("../.venv-labelme/Scripts/labelme.exe")}" '
          f'"{os.path.join(args.root, "images", "train")}"')
    print("改完标签后跑 training/_labelme2yolo.py 写回 YOLO txt。")


if __name__ == "__main__":
    main()
