"""在负样本（空标签）里找"其实有滑翔翼但没标"的图。

用法（仓库根目录）：

    python training/_neg_candidates.py                 # 用已部署的 glider.onnx
    python training/_neg_candidates.py --conf 0.05
    python training/_neg_candidates.py --weights ...\\best.pt

输出按最高置信度倒序的清单：这些人眼确认后直接补框即可。
低阈值（默认 0.05）是为了"宁可多看几张也不要漏"，所以排在前面的未必都对。
"""
import argparse
import os

from _paths import data

import numpy as np


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=data("yolo_ds"))
    ap.add_argument("--split", default="train")
    ap.add_argument("--weights", default=data("..", "VideoPathTool", "glider.onnx"))
    ap.add_argument("--conf", type=float, default=0.05)
    ap.add_argument("--limit", type=int, default=40)
    args = ap.parse_args()

    from ultralytics import YOLO

    model = YOLO(args.weights)

    img_dir = os.path.join(args.root, "images", args.split)
    lbl_dir = os.path.join(args.root, "labels", args.split)
    if not os.path.isdir(img_dir):
        raise SystemExit("没有这个目录: " + img_dir)

    # 只看空标签的图
    negatives = []
    for name in sorted(os.listdir(img_dir)):
        if not name.lower().endswith((".jpg", ".jpeg", ".png")):
            continue
        stem = os.path.splitext(name)[0]
        txt = os.path.join(lbl_dir, stem + ".txt")
        if os.path.exists(txt) and os.path.getsize(txt) > 0:
            continue
        negatives.append(name)

    print(f"负样本 {len(negatives)} 张，用 conf>={args.conf} 扫描…")

    scored = []
    for i in range(0, len(negatives), 32):
        batch = [os.path.join(img_dir, n) for n in negatives[i:i + 32]]
        results = model.predict(batch, conf=args.conf, imgsz=480, verbose=False, device=0)
        for name, res in zip(negatives[i:i + 32], results):
            confs = res.boxes.conf.cpu().numpy() if res.boxes is not None and len(res.boxes) else np.array([])
            if confs.size:
                scored.append((float(confs.max()), name, int(confs.size)))

    scored.sort(reverse=True)
    print(f"\n有检出（可能是漏标）的图: {len(scored)} 张\n")
    print(f"{'置信度':>8}  {'框数':>4}  文件")
    for conf, name, count in scored[:args.limit]:
        print(f"{conf:8.3f}  {count:4d}  {name}")

    print("\n建议：从置信度最高的开始，在 labelme 里打开这些图确认；")
    print("确认有风之翼就画一个矩形（标签 glider），没有就不用动。")


if __name__ == "__main__":
    main()
