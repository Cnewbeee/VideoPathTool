"""把审核过的待审核目录合并进正式数据集。

待审核目录是 `_harvest.py --out-root` 产出的东西（新地区、新场景），
它**不参与训练**，避免没看过的自动标注直接污染数据集。流程是：

    _harvest.py --out-root video/yolo_new/<名字>
    _yolo2labelme.py --root video/yolo_new/<名字>
    labelme 里逐张确认（label-new.bat）            ← 人工这一步不可省
    _labelme2yolo.py --root video/yolo_new/<名字>
    _merge_new.py --src video/yolo_new/<名字>       ← 本脚本

安全措施
--------
* **不覆盖**已有同名文件：撞名就换一个后缀，绝不会悄悄改掉训练集里的东西；
* 每张图都必须有配对的 txt，缺一个就整体中止（先修好再合并）；
* 合并前打印将要新增的正/负样本数，`--dry-run` 可以先看不动手；
* 合并完提示重新跑 `_prescale.py`，否则预缩放数据集会落后于原数据集。
"""
import argparse
import csv
import glob
import os
import shutil
import sys

from _paths import data


def count_labels(paths):
    pos = sum(1 for p in paths if os.path.getsize(p) > 0)
    return pos, len(paths) - pos


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True, help="待审核目录，例如 video/yolo_new/inazuma")
    ap.add_argument("--dst", default=data("yolo_ds"))
    ap.add_argument("--split", default="train", choices=["train", "val"])
    ap.add_argument("--prefix", default="",
                    help="给合并进来的文件加前缀，方便以后按来源筛（如 ina_）")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    src = os.path.abspath(args.src)
    dst = os.path.abspath(args.dst)
    src_img = os.path.join(src, "images", args.split)
    src_lbl = os.path.join(src, "labels", args.split)
    if not os.path.isdir(src_img):
        sys.exit("找不到待审核的图片目录: " + src_img)

    imgs = sorted(glob.glob(os.path.join(src_img, "*.jpg")))
    if not imgs:
        sys.exit("待审核目录里没有图片")

    # 先做完整性检查：图与标注必须成对，否则宁可不合并
    missing = []
    for p in imgs:
        stem = os.path.splitext(os.path.basename(p))[0]
        if not os.path.exists(os.path.join(src_lbl, stem + ".txt")):
            missing.append(stem)
    if missing:
        sys.exit("有 %d 张图缺标注（例如 %s），先跑 _yolo2labelme / _labelme2yolo 修好再合并"
                 % (len(missing), missing[0]))

    dst_img = os.path.join(dst, "images", args.split)
    dst_lbl = os.path.join(dst, "labels", args.split)
    os.makedirs(dst_img, exist_ok=True)
    os.makedirs(dst_lbl, exist_ok=True)

    planned = []
    skipped = 0
    for p in imgs:
        stem = os.path.splitext(os.path.basename(p))[0]
        new_stem = args.prefix + stem
        img_dst = os.path.join(dst_img, new_stem + ".jpg")
        n = 1
        # 撞名就换后缀，绝不覆盖数据集里已有的东西
        while os.path.exists(img_dst):
            n += 1
            new_stem = "%s%s_%d" % (args.prefix, stem, n)
            img_dst = os.path.join(dst_img, new_stem + ".jpg")
        planned.append((p, os.path.join(src_lbl, stem + ".txt"),
                        img_dst, os.path.join(dst_lbl, new_stem + ".txt")))
        if n > 1:
            skipped += 1

    label_paths = [t for _, _, _, t in planned]
    pos, neg = count_labels([s for _, s, _, _ in planned])
    print("待合并: %d 张（正 %d / 负 %d）" % (len(planned), pos, neg))
    if skipped:
        print("  其中 %d 张与数据集同名，已自动改名避免覆盖" % skipped)
    if args.dry_run:
        print("--dry-run：没有写入任何文件")
        return

    for s_img, s_lbl, d_img, d_lbl in planned:
        shutil.copy2(s_img, d_img)
        shutil.copy2(s_lbl, d_lbl)

    now_img = len(glob.glob(os.path.join(dst_img, "*.jpg")))
    now_lbl = glob.glob(os.path.join(dst_lbl, "*.txt"))
    now_pos, now_neg = count_labels(now_lbl)
    print("合并完成。%s 现在共 %d 张（正 %d / 负 %d）"
          % (args.split, now_img, now_pos, now_neg))

    # 记录来源，方便以后回溯这批数据是哪次审核进来的
    log = os.path.join(src, "merged.csv")
    new_file = not os.path.exists(log)
    with open(log, "a", newline="", encoding="utf-8-sig") as fh:
        w = csv.writer(fh)
        if new_file:
            w.writerow(["src", "dst_stem", "label_bytes"])
        for s_img, s_lbl, d_img, d_lbl in planned:
            w.writerow([os.path.basename(s_img),
                        os.path.splitext(os.path.basename(d_img))[0],
                        os.path.getsize(s_lbl)])
    print("来源记录:", log)
    print("\n下一步：同步预缩放数据集，然后训练")
    print("  python training/_prescale.py")
    print("  python training/_train.py --name glider_v13 --epochs 120 --batch 24 "
          "--data video/yolo_ds_640/data.yaml")


if __name__ == "__main__":
    main()
