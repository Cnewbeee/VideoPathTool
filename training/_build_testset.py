"""从一段**没参与训练**的视频里生成测试集候选，交给 labelme 人工确认。

和 `_harvest.py` 的区别只有一个，但很关键：harvest 产出的是训练数据，
这里产出的是**裁判**。所以：

* 正样本框只是"预填"，必须人工确认后才算数（labelme 过一遍）；
* 负样本要求所有模型在很低的阈值下都没有反应 —— 否则就是把漏检当负样本，
  评出来的召回率会虚高；
* 正样本按 dHash 去重，避免几十张几乎一样的滑翔帧；
* 不写进 yolo_ds，放在 `video/yolo_test/<名字>`，训练读不到。

用法

    python training/_build_testset.py --video <路径> --out p10_huanghai
    python training/_yolo2labelme.py --root video/yolo_test/p10_huanghai --split val
    # labelme 里确认，再 _labelme2yolo.py 写回
    python training/_eval_labels.py --weights <模型> --root video/yolo_test/p10_huanghai --split val
"""
import argparse
import glob
import os
import sys

import cv2
import numpy as np

from _paths import data


def dhash(img, size=8):
    g = cv2.cvtColor(cv2.resize(img, (size + 1, size),
                                interpolation=cv2.INTER_AREA), cv2.COLOR_BGR2GRAY)
    return np.packbits(g[:, 1:] > g[:, :-1]).tobytes()


def ham(a, b):
    return int((np.unpackbits(np.frombuffer(a, np.uint8))
                != np.unpackbits(np.frombuffer(b, np.uint8))).sum())


def resolve(tag):
    if os.path.exists(tag):
        return tag
    hits = glob.glob(os.path.join(data("output", "yolo"), f"glider_{tag}",
                                  "weights", "best.pt"))
    if hits:
        return hits[0]
    sys.exit("找不到模型: " + tag)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--video", required=True)
    ap.add_argument("--out", required=True, help="目录名，落在 video/yolo_test/ 下")
    ap.add_argument("--model", default="v12,v10", help="用于预填框的模型（取并集）")
    ap.add_argument("--fps", type=float, default=2.0)
    ap.add_argument("--pos-conf", type=float, default=0.30)
    ap.add_argument("--low-conf", type=float, default=0.08,
                    help="负样本要求所有模型都低于这个值")
    ap.add_argument("--max-pos", type=int, default=60)
    ap.add_argument("--negs", type=int, default=90)
    ap.add_argument("--imgsz", type=int, default=480)
    ap.add_argument("--batch", type=int, default=24)
    args = ap.parse_args()

    from ultralytics import YOLO

    tags = args.model.split(",")
    models = [(t, YOLO(resolve(t))) for t in tags]
    root = os.path.join(data("yolo_test"), args.out)
    img_dir = os.path.join(root, "images", "val")
    lbl_dir = os.path.join(root, "labels", "val")
    if os.path.isdir(img_dir) and glob.glob(os.path.join(img_dir, "*.jpg")):
        sys.exit("目标目录已有图片，换个 --out 名字或先清空: " + img_dir)
    os.makedirs(img_dir, exist_ok=True)
    os.makedirs(lbl_dir, exist_ok=True)

    cap = cv2.VideoCapture(args.video)
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    step = max(1, int(round(fps / args.fps)))
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
    print(f"{os.path.basename(args.video)}: {total / fps:.0f}s")

    frames, times, idx = [], [], 0
    while True:
        ok, fr = cap.read()
        if not ok:
            break
        if idx % step == 0:
            frames.append(fr)
            times.append(idx / fps)
        idx += 1
    cap.release()

    # 每个采样帧跑一遍所有模型，记下最高置信框
    best = [None] * len(frames)      # (conf, box_xyxy, tag)
    maxconf = [0.0] * len(frames)
    for i in range(0, len(frames), args.batch):
        chunk = frames[i:i + args.batch]
        for tag, model in models:
            res = model.predict(chunk, conf=args.low_conf, imgsz=args.imgsz,
                                verbose=False, device=0)
            for j, rr in enumerate(res):
                if not len(rr.boxes):
                    continue
                k = int(rr.boxes.conf.argmax())
                cf = float(rr.boxes.conf[k])
                g = i + j
                maxconf[g] = max(maxconf[g], cf)
                if cf >= args.pos_conf and (best[g] is None or cf > best[g][0]):
                    best[g] = (cf, rr.boxes.xyxy[k].cpu().numpy(), tag)

    # 正样本：有框的帧，按 dHash 去重后落盘
    hashes, pos = [], 0
    for i, b in enumerate(best):
        if b is None or pos >= args.max_pos:
            continue
        h = dhash(frames[i])
        if any(ham(h, o) <= 6 for o in hashes):
            continue
        hashes.append(h)
        stem = "t_%07.1fs" % times[i]
        cv2.imwrite(os.path.join(img_dir, stem + ".jpg"), frames[i])
        h_img, w_img = frames[i].shape[:2]
        x1, y1, x2, y2 = b[1]
        cx, cy = (x1 + x2) / 2 / w_img, (y1 + y2) / 2 / h_img
        bw, bh = (x2 - x1) / w_img, (y2 - y1) / h_img
        with open(os.path.join(lbl_dir, stem + ".txt"), "w", encoding="utf-8") as fh:
            fh.write("0 %.6f %.6f %.6f %.6f\n" % (cx, cy, bw, bh))
        pos += 1

    # 负样本：所有模型都没反应的帧，均匀取样
    silent = [i for i in range(len(frames)) if maxconf[i] < args.low_conf]
    print(f"采样 {len(frames)} 帧：正样本候选 {sum(1 for b in best if b)}，"
          f"全静默 {len(silent)}")
    take = silent[::max(1, len(silent) // args.negs)][:args.negs] if silent else []
    for i in take:
        stem = "t_%07.1fs" % times[i]
        cv2.imwrite(os.path.join(img_dir, stem + ".jpg"), frames[i])
        open(os.path.join(lbl_dir, stem + ".txt"), "w").close()

    open(os.path.join(root, "data.yaml"), "w", encoding="utf-8").write(
        "path: %s\ntrain: images/val\nval: images/val\nnames:\n  0: glider\n"
        % root.replace("\\", "/"))

    print(f"生成 {pos} 张正样本候选 + {len(take)} 张负样本 -> {root}")
    print("\n下一步：在 labelme 里确认框，然后写回")
    print(f"  python training/_yolo2labelme.py --root {root} --split val")
    print(f"  python training/_eval_labels.py --weights v12 --root {root} --split val")


if __name__ == "__main__":
    main()
