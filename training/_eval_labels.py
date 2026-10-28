"""用人工修正过的标注当真值，评价模型在"工具实际推理设置"下的表现。

用法（仓库根目录）：

    python training/_eval_labels.py --weights ...\\glider_v10\\weights\\best.pt
    python training/_eval_labels.py --split val --imgsz 480 --conf 0.30

和 _eval.py 的区别
------------------
旧脚本拿 29 段"人工确认的滑翔窗口"当真值。但那份真值本身不完整 ——
人工标注时在训练集和验证集里都发现过漏标和错标，所以它算出来的 P/R/F1
不能用来选模型了。

这个脚本改用 **dataset 里修正过的标注**当真值：每一张图是"有风之翼"还是
"没有"，都是人工看过的。评价口径也更贴近实际使用 ——

  * imgsz 480、conf 0.30：C# 端 GlideYoloDetector 的默认值
  * 帧级"有没有检出"，因为下游只关心这个（决定 fly 路点）

输出
----
  终端：按视频分组的 P/R/F1
  文件：<out>/mistakes.csv        每一张判错的图（漏报/误报）及置信度
        <out>/fp_*.jpg            误报：画了预测框
        <out>/fn_*.jpg            漏报：画了真值框

这些图就是审核队列 —— 与其相信一个不完整的真值表，不如直接看模型错在哪。
"""
import argparse
import csv
import os
import sys

import cv2
import numpy as np

from _paths import data


def read_label(txt_path):
    """返回真值框（归一化 cx,cy,w,h）列表；空文件 = 负样本。"""
    boxes = []
    if not os.path.exists(txt_path):
        return boxes
    with open(txt_path, "r", encoding="utf-8", errors="ignore") as fh:
        for line in fh:
            p = line.split()
            if len(p) >= 5:
                boxes.append(tuple(float(v) for v in p[1:5]))
    return boxes


def draw_box(img, cx, cy, w, h, colour, thickness=3):
    ih, iw = img.shape[:2]
    x1, y1 = int((cx - w / 2) * iw), int((cy - h / 2) * ih)
    x2, y2 = int((cx + w / 2) * iw), int((cy + h / 2) * ih)
    cv2.rectangle(img, (x1, y1), (x2, y2), colour, thickness)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--weights", required=True)
    ap.add_argument("--root", default=data("yolo_ds"))
    ap.add_argument("--split", default="both", choices=["train", "val", "both"])
    ap.add_argument("--imgsz", type=int, default=480, help="工具的默认推理尺寸")
    ap.add_argument("--conf", type=float, default=0.30, help="工具的默认置信度")
    ap.add_argument("--out", default=None)
    args = ap.parse_args()

    from ultralytics import YOLO
    model = YOLO(args.weights)

    outdir = args.out or os.path.join(
        data("output", "yolo"), "label_eval_" + os.path.basename(os.path.dirname(
            os.path.dirname(args.weights))) + f"_{args.imgsz}_{args.conf}")
    os.makedirs(outdir, exist_ok=True)

    rows = []
    print(f"权重: {args.weights}")
    print(f"推理: imgsz={args.imgsz} conf={args.conf}\n")

    for split in (["train", "val"] if args.split == "both" else [args.split]):
        img_dir = os.path.join(args.root, "images", split)
        lbl_dir = os.path.join(args.root, "labels", split)
        if not os.path.isdir(img_dir):
            continue

        names = sorted(n for n in os.listdir(img_dir)
                       if n.lower().endswith((".jpg", ".jpeg", ".png")))
        stats = {}
        for i in range(0, len(names), 16):
            chunk = names[i:i + 16]
            paths = [os.path.join(img_dir, n) for n in chunk]
            results = model.predict(paths, conf=args.conf, imgsz=args.imgsz,
                                    verbose=False, device=0)
            for name, res in zip(chunk, results):
                stem = os.path.splitext(name)[0]
                # 文件名形如 p3_pos_0086.70 / p5_neg_0214.00，取视频代号
                video = stem.split("_")[0]
                gt = read_label(os.path.join(lbl_dir, stem + ".txt"))
                has_gt = len(gt) > 0
                conf_max = float(res.boxes.conf.max()) if res.boxes is not None and len(res.boxes) else 0.0
                has_pred = conf_max > 0

                bucket = stats.setdefault(video, {"tp": 0, "fp": 0, "fn": 0, "tn": 0})
                kind = None
                if has_gt and has_pred:
                    bucket["tp"] += 1
                elif not has_gt and has_pred:
                    bucket["fp"] += 1
                    kind = "fp"
                elif has_gt and not has_pred:
                    bucket["fn"] += 1
                    kind = "fn"
                else:
                    bucket["tn"] += 1

                if kind:
                    img = cv2.imread(paths[chunk.index(name)])
                    if img is not None:
                        if kind == "fn":
                            for b in gt:
                                draw_box(img, *b, (0, 200, 255), 3)
                        else:
                            p = res.boxes.xywhn.cpu().numpy()[0]
                            draw_box(img, float(p[0]), float(p[1]), float(p[2]), float(p[3]), (0, 0, 255), 3)
                        fn = f"{kind}_{split}_{stem}_c{conf_max:.2f}.jpg"
                        cv2.imwrite(os.path.join(outdir, fn), img)
                        rows.append({
                            "split": split, "video": video, "kind": kind,
                            "image": name, "conf": round(conf_max, 3), "file": fn,
                        })

        print(f"[{split}]")
        tp = sum(v["tp"] for v in stats.values())
        fp = sum(v["fp"] for v in stats.values())
        fn = sum(v["fn"] for v in stats.values())
        tn = sum(v["tn"] for v in stats.values())
        for video, v in sorted(stats.items()):
            prec = v["tp"] / (v["tp"] + v["fp"]) if v["tp"] + v["fp"] else float("nan")
            rec = v["tp"] / (v["tp"] + v["fn"]) if v["tp"] + v["fn"] else float("nan")
            print(f"  {video}: 有翼 {v['tp'] + v['fn']:3d} 张 | "
                  f"P={prec:.3f} R={rec:.3f}  (误报 {v['fp']}, 漏报 {v['fn']}, 正确排除 {v['tn']})")
        prec = tp / (tp + fp) if tp + fp else float("nan")
        rec = tp / (tp + fn) if tp + fn else float("nan")
        f1 = 2 * prec * rec / (prec + rec) if prec + rec else float("nan")
        print(f"  合计: P={prec:.3f} R={rec:.3f} F1={f1:.3f}  "
              f"(TP={tp} FP={fp} FN={fn} TN={tn})\n")

    with open(os.path.join(outdir, "mistakes.csv"), "w", newline="", encoding="utf-8-sig") as fh:
        w = csv.DictWriter(fh, fieldnames=["split", "video", "kind", "image", "conf", "file"])
        w.writeheader()
        w.writerows(rows)

    print(f"判错的图 {len(rows)} 张 -> {outdir}")
    print("  fp_*.jpg = 模型以为有翼（画了红框），fn_*.jpg = 模型没检出（画了黄框=真值）")


if __name__ == "__main__":
    main()
