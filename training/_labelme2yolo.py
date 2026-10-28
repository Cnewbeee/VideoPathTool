"""把 labelme 里改过的 JSON 写回 YOLO 标签。

用法（在仓库根目录）：

    python training/_labelme2yolo.py --dry-run     # 只看会改什么，不写盘
    python training/_labelme2yolo.py               # 写回，并把旧标签备份到 labels_backup/

规则：
  * 只认 shape_type == "rectangle" 且 label 为 glider（大小写不敏感）的框；
  * 其它形状/标签会被忽略，避免手滑画一个多边形就把类别搞乱；
  * 一个框都没有的 JSON → 写成空 txt，也就是"负样本"（这张图没有风之翼）。
    这正是需要人工确认的部分：披风、传送道具不该有框。

写回前会把原 txt 复制到 labels_backup/<split>/ 下，后悔了能恢复。
"""
import argparse
import json
import os
import shutil
import sys

from _paths import data

TARGET_LABEL = "glider"
CLASS_ID = 0


def load_boxes(json_path):
    with open(json_path, "r", encoding="utf-8") as fh:
        payload = json.load(fh)
    w = payload.get("imageWidth") or 0
    h = payload.get("imageHeight") or 0
    boxes = []
    for shape in payload.get("shapes", []):
        if shape.get("shape_type") != "rectangle":
            continue
        label = str(shape.get("label", "")).strip().lower()
        if label != TARGET_LABEL:
            continue
        pts = shape.get("points") or []
        if len(pts) < 2:
            continue
        xs = [float(p[0]) for p in pts]
        ys = [float(p[1]) for p in pts]
        boxes.append((min(xs), min(ys), max(xs), max(ys)))
    return payload, w, h, boxes


def to_yolo_lines(boxes, img_w, img_h):
    lines = []
    for x1, y1, x2, y2 in boxes:
        if x2 - x1 < 1 or y2 - y1 < 1:
            continue
        cx = (x1 + x2) / 2 / img_w
        cy = (y1 + y2) / 2 / img_h
        bw = (x2 - x1) / img_w
        bh = (y2 - y1) / img_h
        cx, cy = min(max(cx, 0.0), 1.0), min(max(cy, 0.0), 1.0)
        bw, bh = min(max(bw, 0.0), 1.0), min(max(bh, 0.0), 1.0)
        lines.append(f"{CLASS_ID} {cx:.6f} {cy:.6f} {bw:.6f} {bh:.6f}")
    return lines


def count_old_boxes(txt_path):
    if not os.path.exists(txt_path):
        return 0
    with open(txt_path, "r", encoding="utf-8", errors="ignore") as fh:
        return sum(1 for line in fh if line.strip())


def read_boxes(txt_path):
    """读回 txt 里的归一化框，用于容差比较。"""
    boxes = []
    if not os.path.exists(txt_path):
        return boxes
    with open(txt_path, "r", encoding="utf-8", errors="ignore") as fh:
        for line in fh:
            parts = line.split()
            if len(parts) < 5:
                continue
            try:
                boxes.append(tuple(float(v) for v in parts[1:5]))
            except ValueError:
                continue
    return boxes


def same_boxes(old_boxes, new_lines, tol=2e-4):
    """像素<->归一化来回换算会有 ~1e-6 的抖动，用容差比较。

    2e-4 在 1920 宽上约等于 0.4 像素 —— 人手不可能改这么小，
    所以判定"没动过"，避免每次打开再保存都把所有标签重写一遍。
    """
    if len(old_boxes) != len(new_lines):
        return False
    for old, line in zip(old_boxes, new_lines):
        parts = line.split()
        new = tuple(float(v) for v in parts[1:5])
        if any(abs(a - b) > tol for a, b in zip(old, new)):
            return False
    return True


def convert_split(root, split, dry_run, backup):
    img_dir = os.path.join(root, "images", split)
    lbl_dir = os.path.join(root, "labels", split)
    if not os.path.isdir(img_dir):
        print("跳过（没有这个目录）:", img_dir)
        return

    jsons = [n for n in sorted(os.listdir(img_dir)) if n.lower().endswith(".json")]
    if not jsons:
        print(f"{split}: 没有 json，先跑 _yolo2labelme.py")
        return

    changed = unchanged = 0
    added = removed = 0
    for name in jsons:
        json_path = os.path.join(img_dir, name)
        stem = os.path.splitext(name)[0]
        txt_path = os.path.join(lbl_dir, stem + ".txt")

        try:
            payload, w, h, boxes = load_boxes(json_path)
        except Exception as exc:
            print("  跳过（json 读不了）:", name, exc)
            continue

        if not w or not h:
            print("  跳过（json 里没有 imageWidth/Height）:", name)
            continue

        new_lines = to_yolo_lines(boxes, w, h)
        old_boxes = read_boxes(txt_path)
        old_lines = len(old_boxes)
        new_text = "\n".join(new_lines) + ("\n" if new_lines else "")

        if same_boxes(old_boxes, new_lines):
            unchanged += 1
            continue

        changed += 1
        if len(new_lines) > old_lines:
            added += len(new_lines) - old_lines
        elif len(new_lines) < old_lines:
            removed += old_lines - len(new_lines)

        if dry_run:
            print(f"  {split}/{stem}: {old_lines} -> {len(new_lines)} 个框")
            continue

        if backup and os.path.exists(txt_path):
            bdir = os.path.join(root, "labels_backup", split)
            os.makedirs(bdir, exist_ok=True)
            shutil.copy2(txt_path, os.path.join(bdir, stem + ".txt"))

        os.makedirs(lbl_dir, exist_ok=True)
        with open(txt_path, "w", encoding="utf-8") as fh:
            fh.write(new_text)

    mode = "将修改" if dry_run else "已写入"
    print(f"{split}: {mode} {changed} 个文件（新增框 {added}，删除框 {removed}），{unchanged} 个未变")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=data("yolo_ds"))
    ap.add_argument("--split", default="both", choices=["train", "val", "both"])
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--no-backup", action="store_true")
    args = ap.parse_args()

    print("数据集:", args.root)
    for split in (["train", "val"] if args.split == "both" else [args.split]):
        convert_split(args.root, split, args.dry_run, not args.no_backup)
    if not args.dry_run:
        print("\n下一步：重新训练")
        print("  python training/_train.py --name glider_v9 --epochs 100 --batch 8")


if __name__ == "__main__":
    main()
