"""从其它视频里挖掘新样本：高置信度检出 → 自动打标 → 进训练集。

用法（仓库根目录）：

    python training/_harvest.py --videos nt --conf 0.55 --max-per-video 25
    python training/_harvest.py --videos all --conf 0.60

目的不是"多喂数据"，而是**换场景**。p3/p5 是蒙德+龙脊，纳塔的光照、地形、
天空都不一样；只在旧场景上训练，模型到了新地图就会掉精度。

规则
----
* 只在 conf >= --conf 时才自动打标（默认 0.55，宁可少收也不收噪声）
* 同一视频内相邻帧去重（--gap 秒内只留一张），避免几十张近乎重复的图
* 每个视频另取 --negs 张"无检出"的帧当负样本 —— 新场景里"什么样不是翼"
  和"什么是翼"同样重要
* 文件名带来源和时间：auto_<序号>_<视频尾号>，对应关系写在 manifest.csv

产物
----
  video/yolo_ds/images/train/auto_*.jpg
  video/yolo_ds/labels/train/auto_*.txt     （负样本是空文件）
  <out>/manifest.csv                        每张图的来源、时间、置信度

自动标注不一定准，跑完后建议在 labelme 里过一遍（label-train.bat）。
"""
import argparse
import csv
import glob
import os
import sys

import cv2
import numpy as np

from _paths import data


def short_code(path):
    """从超长文件名里取出尾号，例如 ..._P4.mp4 -> P4。"""
    stem = os.path.splitext(os.path.basename(path))[0]
    for token in reversed(stem.replace("-", "_").split("_")):
        if token and len(token) <= 4 and any(c.isdigit() for c in token):
            return token
    return "v"


def dhash(img, size=8):
    """64 位差异哈希：缩到 9x8 灰度，比较相邻像素得到 64 个比特。

    用不着更强的哈希：这里只需要回答"这张画面是不是已经收过了"。
    同一段视频里相邻几秒的画面几乎一样，dHash 对亮度/压缩噪声不敏感，
    但对真实的镜头变化（转视角、换场景）很敏感，正好是我们要的粒度。
    """
    g = cv2.cvtColor(cv2.resize(img, (size + 1, size),
                                interpolation=cv2.INTER_AREA), cv2.COLOR_BGR2GRAY)
    return np.packbits(g[:, 1:] > g[:, :-1]).tobytes()


class Dedup:
    """把数据集里已有图的哈希装成一张表，新帧靠汉明距离查重。

    不查重的话，同一批视频跑第二遍会把上一轮已经收过的帧再收一遍 ——
    数据集是变大了，但只是把同一张图存了两次，对泛化没有任何帮助，
    还要多付训练时间。
    """

    def __init__(self, folders, threshold=4):
        self.threshold = threshold
        self.hashes = np.zeros((0, 8), np.uint8)
        rows = []
        for folder in folders:
            for path in glob.glob(os.path.join(folder, "*.jpg")):
                im = cv2.imread(path)
                if im is not None:
                    rows.append(np.frombuffer(dhash(im), np.uint8))
        if rows:
            self.hashes = np.vstack(rows)
        print(f"查重表：{len(self.hashes)} 张已有图，阈值汉明距离 <= {threshold}"
              f"（64 位中约 {threshold * 1.6:.0f}%）\n")

    def is_dup(self, img):
        if len(self.hashes) == 0:
            return False
        bits = np.unpackbits(np.frombuffer(dhash(img), np.uint8))
        best = (bits != np.unpackbits(self.hashes, axis=1)).sum(axis=1).min()
        return best <= self.threshold

    def add(self, img):
        self.hashes = np.vstack([self.hashes,
                                 np.frombuffer(dhash(img), np.uint8)])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--weights", default=None)
    ap.add_argument("--videos", default="nt",
                    help="nt / ls / all，或逗号分隔的视频路径关键字")
    ap.add_argument("--conf", type=float, default=0.55)
    ap.add_argument("--fps", type=float, default=2.0)
    ap.add_argument("--gap", type=float, default=2.0, help="同视频去重的秒数")
    ap.add_argument("--max-per-video", type=int, default=25)
    ap.add_argument("--negs", type=int, default=8, help="每个视频取多少负样本")
    ap.add_argument("--imgsz", type=int, default=480)
    ap.add_argument("--no-dedup", action="store_true",
                    help="关掉与已有数据集的感知查重（默认开启）")
    args = ap.parse_args()

    root = data("yolo_ds")
    img_dir = os.path.join(root, "images", "train")
    lbl_dir = os.path.join(root, "labels", "train")
    os.makedirs(img_dir, exist_ok=True)
    os.makedirs(lbl_dir, exist_ok=True)

    weights = args.weights or os.path.join(
        data("output", "yolo"), "glider_v10", "weights", "best.pt")

    # 选视频：*.proxy.mp4 是预览副本，跳过
    pool = [p for p in glob.glob(os.path.join(data(), "downloads", "*.mp4"))
            if not p.endswith(".proxy.mp4")]
    if args.videos == "nt":
        pool = [p for p in pool if "纳塔" in p]
    elif args.videos == "ls":
        pool = [p for p in pool if "龙脊" in p]
    elif args.videos != "all":
        # 逗号分隔是"或"，组内用 + 连接表示"且"：
        #   --videos "龙脊+P4,龙脊+P6"  只要龙脊第 4 和第 6 集
        def terms(k):
            return [s for s in k.split("+") if s]
        pool = [p for p in pool
                if any(all(s in p for s in terms(k))
                       for k in args.videos.split(",") if k.strip())]
    pool.sort()
    if not pool:
        sys.exit("没有匹配的视频")

    print(f"权重: {weights}")
    print(f"视频 {len(pool)} 个，conf>={args.conf}，每视频最多 {args.max_per_video} 正 + {args.negs} 负\n")

    from ultralytics import YOLO
    model = YOLO(weights)

    outdir = os.path.join(data("output", "yolo"), "harvest")
    os.makedirs(outdir, exist_ok=True)
    manifest = []

    # 分多次跑不同的视频组，序号必须接着上次的，否则 auto_0001 会被覆盖
    counter = 0
    for p in glob.glob(os.path.join(img_dir, "auto_*.jpg")):
        tail = os.path.basename(p)[5:9]
        if tail.isdigit():
            counter = max(counter, int(tail))
    if counter:
        print(f"接续已有序号，从 auto_{counter + 1:04d} 开始\n")

    # train 和 val 都要进查重表：否则从视频里重新采到一帧跟验证集几乎一样的画面，
    # 就会把它塞进训练集，验证分数立刻虚高，而且没人看得出来。
    dedup = None
    if not args.no_dedup:
        dedup = Dedup([img_dir, os.path.join(root, "images", "val")])

    for vpath in pool:
        name = os.path.basename(vpath)
        code = short_code(vpath)
        cap = cv2.VideoCapture(vpath)
        fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
        step = max(1, int(round(fps / args.fps)))
        total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
        print(f"[{code}] {name[:50]}… {total / fps:.0f}s")

        kept = 0
        negs = 0
        dups = 0
        last_keep = -1e9
        idx = 0
        buf, buf_t = [], []

        def flush():
            nonlocal kept, negs, last_keep, counter, dups
            if not buf:
                return
            results = model.predict(buf, conf=args.conf * 0.5, imgsz=args.imgsz,
                                    verbose=False, device=0)
            for frame, t, res in zip(buf, buf_t, results):
                has = res.boxes is not None and len(res.boxes)
                conf = float(res.boxes.conf.max()) if has else 0.0

                if has and conf >= args.conf and kept < args.max_per_video and t - last_keep >= args.gap:
                    if dedup is not None and dedup.is_dup(frame):
                        dups += 1
                        continue
                    i = int(res.boxes.conf.argmax())
                    cx, cy, bw, bh = res.boxes.xywhn[i].cpu().numpy()
                    counter += 1
                    stem = f"auto_{counter:04d}_{code}"
                    cv2.imwrite(os.path.join(img_dir, stem + ".jpg"), frame)
                    with open(os.path.join(lbl_dir, stem + ".txt"), "w", encoding="utf-8") as fh:
                        fh.write(f"0 {cx:.6f} {cy:.6f} {bw:.6f} {bh:.6f}\n")
                    manifest.append({"file": stem, "video": name, "time": round(t, 2),
                                     "kind": "pos", "conf": round(conf, 3)})
                    if dedup is not None:
                        dedup.add(frame)
                    kept += 1
                    last_keep = t
                elif not has and negs < args.negs:
                    # 负样本也要分散取，简单按固定间隔
                    if int(t) % 20 == 0:
                        if dedup is not None and dedup.is_dup(frame):
                            dups += 1
                            continue
                        counter += 1
                        stem = f"auto_{counter:04d}_{code}"
                        cv2.imwrite(os.path.join(img_dir, stem + ".jpg"), frame)
                        open(os.path.join(lbl_dir, stem + ".txt"), "w").close()
                        manifest.append({"file": stem, "video": name, "time": round(t, 2),
                                         "kind": "neg", "conf": 0.0})
                        if dedup is not None:
                            dedup.add(frame)
                        negs += 1
            buf.clear()
            buf_t.clear()

        while True:
            ok, frame = cap.read()
            if not ok:
                break
            if idx % step == 0:
                buf.append(frame)
                buf_t.append(idx / fps)
                if len(buf) >= 12:
                    flush()
            idx += 1
        flush()
        cap.release()
        print(f"   取正样本 {kept} 张，负样本 {negs} 张"
              + (f"，跳过 {dups} 张（与已有数据重复）" if dups else ""))

    # 追加而不是覆盖：分多次跑不同视频组时，上一批的来源记录不该被冲掉
    mpath = os.path.join(outdir, "manifest.csv")
    new_file = not os.path.exists(mpath)
    with open(mpath, "a", newline="", encoding="utf-8-sig") as fh:
        w = csv.DictWriter(fh, fieldnames=["file", "video", "time", "kind", "conf"])
        if new_file:
            w.writeheader()
        w.writerows(manifest)

    pos = sum(1 for m in manifest if m["kind"] == "pos")
    print(f"\n共新增 {len(manifest)} 张（正 {pos}，负 {len(manifest) - pos}）")
    print("清单:", os.path.join(outdir, "manifest.csv"))
    print("建议先在 labelme 里过一遍自动标注，再训练。")


if __name__ == "__main__":
    main()
