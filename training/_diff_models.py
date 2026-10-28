"""对比两个模型的逐帧检出，导出判定不一致的片段供人工审核。

用法（仓库根目录）：

    python training/_diff_models.py
    python training/_diff_models.py --a v9 --b v10 --conf 0.3 --fps 3

默认对比"当前部署的 v9 末轮"和"v10 的 best"，也就是：
v10 到底在哪些地方和现在用的模型看法不同。

输出（video/output/yolo/diff_<a>_<b>/）：
  diff.csv        每段差异的起止时间、方向、最高置信度
  frames/         每段差异挑一帧存图，文件名带时间戳，直接翻图审核

为什么不用 _eval.py 的那 29 段真值来判断：那份真值本身不完整，
人工标注时已经发现训练集和验证集里都有漏标/错标。这里不预设谁对，
只把分歧摆出来。
"""
import argparse
import csv
import os
import sys

import numpy as np

from _paths import data


def load(weight_path):
    from ultralytics import YOLO
    if not os.path.exists(weight_path):
        sys.exit("找不到权重: " + weight_path)
    return YOLO(weight_path)


def scan(model, frames, imgsz, conf, batch=16):
    """返回每个采样帧的 (是否有检出, 最高置信度)。"""
    out = []
    for i in range(0, len(frames), batch):
        chunk = frames[i:i + batch]
        results = model.predict(chunk, conf=conf, imgsz=imgsz, verbose=False, device=0)
        for res in results:
            if res.boxes is not None and len(res.boxes):
                out.append((True, float(res.boxes.conf.max())))
            else:
                out.append((False, 0.0))
    return out


def merge_windows(flags, times, gap):
    """把逐帧的布尔序列合并成时间段。"""
    windows = []
    start = None
    for i, on in enumerate(flags):
        if on and start is None:
            start = times[i]
        elif not on and start is not None:
            windows.append((start, times[i - 1]))
            start = None
    if start is not None:
        windows.append((start, times[-1]))
    # 合并间隔小于 gap 的相邻段
    merged = []
    for w in windows:
        if merged and w[0] - merged[-1][1] <= gap:
            merged[-1] = (merged[-1][0], w[1])
        else:
            merged.append(w)
    return merged


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--a", default="v9", help="模型 A 的代号（用于输出命名）")
    ap.add_argument("--b", default="v10", help="模型 B 的代号")
    ap.add_argument("--weight-a", default=None)
    ap.add_argument("--weight-b", default=None)
    ap.add_argument("--conf", type=float, default=0.30)
    ap.add_argument("--fps", type=float, default=3.0)
    ap.add_argument("--imgsz", type=int, default=480)
    ap.add_argument("--gap", type=float, default=1.0, help="相邻帧合并窗口的间隔秒数")
    ap.add_argument("--videos", default="p5,p3")
    args = ap.parse_args()

    wa = args.weight_a or os.path.join(
        data("output", "yolo"), "glider_v9", "weights", "last.pt")
    wb = args.weight_b or os.path.join(
        data("output", "yolo"), "glider_v10", "weights", "best.pt")

    print("A =", wa)
    print("B =", wb)
    ma, mb = load(wa), load(wb)

    import cv2

    outdir = os.path.join(data("output", "yolo"), f"diff_{args.a}_{args.b}")
    framedir = os.path.join(outdir, "frames")
    os.makedirs(framedir, exist_ok=True)

    rows = []
    for key in args.videos.split(","):
        vpath = data(key + ".mp4")
        if not os.path.exists(vpath):
            print("跳过（没有这个视频）:", vpath)
            continue

        cap = cv2.VideoCapture(vpath)
        fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
        step = max(1, int(round(fps / args.fps)))
        frames, times = [], []
        idx = 0
        while True:
            ok, frame = cap.read()
            if not ok:
                break
            if idx % step == 0:
                frames.append(frame)
                times.append(idx / fps)
            idx += 1
        cap.release()
        print(f"{key}: {len(frames)} 帧采样（{args.fps}fps，视频 {fps:.0f}fps）")

        da = scan(ma, frames, args.imgsz, args.conf)
        db = scan(mb, frames, args.imgsz, args.conf)

        fa = [d[0] for d in da]
        fb = [d[0] for d in db]

        for label, only_a, only_b in (
            (f"只有{args.a}检出", True, False),
            (f"只有{args.b}检出", False, True),
        ):
            if only_a:
                flags = [x and not y for x, y in zip(fa, fb)]
                other = db
            else:
                flags = [y and not x for x, y in zip(fa, fb)]
                other = da
            for start, end in merge_windows(flags, times, args.gap):
                # 取窗口内置信度最高的一帧存图
                best_i, best_c = -1, -1.0
                for i, t in enumerate(times):
                    if start <= t <= end:
                        on, c = (da[i] if only_a else db[i])
                        if on and c > best_c:
                            best_i, best_c = i, c
                if best_i < 0:
                    continue
                stamp = f"{times[best_i]:07.2f}s"
                name = f"{key}_{'A' if only_a else 'B'}_{stamp}.jpg"
                cv2.imwrite(os.path.join(framedir, name), frames[best_i])
                rows.append({
                    "video": key,
                    "only": args.a if only_a else args.b,
                    "start": round(start, 2),
                    "end": round(end, 2),
                    "conf": round(best_c, 3),
                    "frame": name,
                })
                print(f"  {label}: {start:7.2f}s -> {end:7.2f}s  最高置信度 {best_c:.3f}")

    with open(os.path.join(outdir, "diff.csv"), "w", newline="", encoding="utf-8-sig") as fh:
        writer = csv.DictWriter(fh, fieldnames=["video", "only", "start", "end", "conf", "frame"])
        writer.writeheader()
        writer.writerows(rows)

    print(f"\n差异 {len(rows)} 段 -> {outdir}")
    print("  审核方式：打开 frames/ 逐张看，或在编辑器里跳到对应时间点")


if __name__ == "__main__":
    main()
