"""把一个视频按固定帧率过一遍模型，输出每帧的置信度并合并成"有翼窗口"。

用途是**评测**而不是训练：拿一段没进过数据集的视频，看模型在陌生地形上
到底检不检得出来。不做任何写盘动作（除了可选的 CSV 和拼图），
所以可以放心对着别人的视频跑。

用法：

    python training/_scan_video.py --video <路径> --model v10,v12 --fps 2
    python training/_scan_video.py --video <路径> --model v10 --crop-out <目录>

多个模型会跑在同一批帧上（帧只解码一次），最后打印一张对比表 ——
同一段视频上谁多检出、谁多误检，比验证集那 50 张更能说明问题。
"""
import argparse
import csv
import glob
import os
import sys

import cv2
import numpy as np

from _paths import data


def resolve_model(tag):
    """--model 既接受 v10 这样的代号，也接受 .pt/.onnx 路径。"""
    if os.path.exists(tag):
        return tag
    hits = glob.glob(os.path.join(data("output", "yolo"), f"glider_{tag}",
                                  "weights", "best.pt"))
    if hits:
        return hits[0]
    sys.exit(f"找不到模型: {tag}")


def merge_windows(times, flags, gap=1.0):
    """把断断续续的检出合并成连续窗口，中间小于 gap 秒的空隙算作同一段。"""
    windows = []
    for t, hit in zip(times, flags):
        if not hit:
            continue
        if windows and t - windows[-1][1] <= gap:
            windows[-1][1] = t
        else:
            windows.append([t, t])
    return windows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--video", required=True)
    ap.add_argument("--model", default="v10", help="逗号分隔的代号或权重路径")
    ap.add_argument("--fps", type=float, default=2.0)
    ap.add_argument("--conf", type=float, default=0.10,
                    help="统计用的低阈值；窗口判定另外用 --window-conf")
    ap.add_argument("--window-conf", type=float, default=0.30)
    ap.add_argument("--imgsz", type=int, default=480)
    ap.add_argument("--gap", type=float, default=1.0)
    ap.add_argument("--batch", type=int, default=24)
    ap.add_argument("--out", default=None, help="CSV 输出路径")
    args = ap.parse_args()

    if not os.path.exists(args.video):
        sys.exit("视频不存在: " + args.video)

    from ultralytics import YOLO

    tags = args.model.split(",")
    models = [(t, YOLO(resolve_model(t))) for t in tags]
    print("视频:", os.path.basename(args.video))
    for t, _ in models:
        print("  模型", t, "->", resolve_model(t))

    cap = cv2.VideoCapture(args.video)
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
    step = max(1, int(round(fps / args.fps)))
    print(f"  {total / fps:.0f}s, {fps:.1f}fps, 每 {step} 帧取一张")

    times, confs = [], {t: [] for t in tags}
    buf, buf_t = [], []
    idx = 0

    def flush():
        if not buf:
            return
        for tag, model in models:
            results = model.predict(buf, conf=args.conf, imgsz=args.imgsz,
                                    verbose=False, device=0)
            for res in results:
                c = float(res.boxes.conf.max()) if len(res.boxes) else 0.0
                confs[tag].append(c)
        buf.clear()
        buf_t.clear()

    while True:
        ok, frame = cap.read()
        if not ok:
            break
        if idx % step == 0:
            buf.append(frame)
            buf_t.append(idx / fps)
            if len(buf) >= args.batch:
                times.extend(buf_t)
                flush()
        idx += 1
    times.extend(buf_t)
    flush()
    cap.release()

    n = len(times)
    print(f"\n采样 {n} 帧")
    print(f"{'模型':<6}{'检出帧(>={:.2f})'.format(args.window_conf):>16}"
          f"{'有翼时长':>10}{'窗口数':>8}{'最高置信':>10}")
    summary = {}
    for tag, _ in models:
        flags = [c >= args.window_conf for c in confs[tag]]
        wins = merge_windows(times, flags, args.gap)
        covered = sum(b - a for a, b in wins)
        summary[tag] = (flags, wins)
        print(f"{tag:<6}{sum(flags):>16}{covered:>9.1f}s{len(wins):>8}"
              f"{max(confs[tag] or [0]):>10.3f}")

    if len(tags) == 2:
        a, b = tags[0], tags[1]
        fa, fb = summary[a][0], summary[b][0]
        only_a = sum(1 for x, y in zip(fa, fb) if x and not y)
        only_b = sum(1 for x, y in zip(fa, fb) if y and not x)
        both = sum(1 for x, y in zip(fa, fb) if x and y)
        print(f"\n{a} 与 {b}: 都检出 {both} 帧, 只有 {a} {only_a} 帧, 只有 {b} {only_b} 帧")

    if args.out:
        os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)
        with open(args.out, "w", newline="", encoding="utf-8") as fh:
            w = csv.writer(fh)
            w.writerow(["time"] + [f"conf_{t}" for t in tags])
            for i, t in enumerate(times):
                w.writerow([round(t, 2)] + [round(confs[tag][i], 4) for tag in tags])
        print("明细:", args.out)

    # 把每个窗口的中点标出来，方便挑帧人工确认
    print("\n各模型的窗口（秒）：")
    for tag, _ in models:
        wins = summary[tag][1]
        print(f"  {tag}: " + (", ".join(f"{a:.1f}-{b:.1f}" for a, b in wins) or "（无）"))


if __name__ == "__main__":
    main()
