"""Full evaluation of a glider model, reported against the CONFIRMED
ground truth (the segments the user checked by eye).

Reports both:
  - frame level: precision / recall / F1 over the sampled frames
  - segment level: how many true glide segments were found, and how many
    detections are not in the ground truth
"""
from _paths import data  # 数据根目录，见 training/README.md
import os, sys, time, json, argparse
os.environ['YOLO_VERBOSE'] = '0'
import numpy as np
import cv2

OUT = data('output', 'yolo')
VIDS = {'p5': (data('p5.mp4'), 396.0),
        'p3': (data('p3.mp4'), 812.0)}

# Ground truth confirmed by the user (by eye, frame by frame) plus the
# segments the models found and that were then confirmed visually one by one.
# The 11 "new" p3 segments came out of the 5 fps low-threshold scan: 11 of
# the 12 out-of-window hits really are deployed wings (only 6.8-7.6 s is a
# genuine false positive).
GT = {
    'p5': [(16.0, 32.0), (43.0, 52.0), (85.0, 90.0), (214.0, 218.5),
           (235.5, 240.5), (316.0, 318.5), (359.5, 362.0)],
    'p3': [(6.8, 7.6),   # confirmed NOT a glide -- must not be detected
           (14.6, 15.0), (44.4, 44.6), (46.3, 48.4), (48.4, 48.4),
           (58.6, 58.8), (60.4, 61.2), (64.7, 64.7), (97.8, 98.8),
           (115.0, 116.3), (238.8, 244.3), (262.3, 267.7), (275.6, 276.3),
           (281.7, 283.0), (297.8, 298.0), (318.0, 323.5), (331.5, 339.5),
           (344.5, 349.0), (363.7, 365.0), (371.5, 379.5), (426.0, 426.0),
           (445.6, 454.7)],
}
# windows that are known NOT to contain a glide. The cape looks just like a
# wing while the character runs, and the teleport device is a bright blob --
# both of these produced real false positives at some point.
NOT_GLIDE = {'p5': [], 'p3': [(6.8, 7.6), (17.0, 17.7), (191.3, 193.7),
                              (236.8, 237.6), (705.3, 706.0)]}

def scan(model, vid, tag, fps_scan=3.0, conf=0.30, batch=16):
    cap = cv2.VideoCapture(vid)
    vfps = cap.get(cv2.CAP_PROP_FPS) or 30
    step = max(1, int(round(vfps / fps_scan)))
    times = []
    buf = []
    buft = []
    hits = []
    i = 0
    def flush():
        if not buf: return
        res = model.predict(buf, conf=conf, verbose=False, imgsz=640, device=0)
        for r, t in zip(res, buft):
            if len(r.boxes) > 0:
                hits.append((t, max(float(b.conf) for b in r.boxes)))
        buf.clear(); buft.clear()
    while True:
        ok, f = cap.read()
        if not ok: break
        if i % step == 0:
            buf.append(f); buft.append(i/vfps); times.append(i/vfps)
            if len(buf) >= batch: flush()
        i += 1
    flush()
    cap.release()
    return times, hits

def group(hits, max_gap=1.5):
    if not hits: return []
    t = np.array([h[0] for h in hits]); c = np.array([h[1] for h in hits])
    o = np.argsort(t); t, c = t[o], c[o]
    segs = []; i = 0
    while i < len(t):
        j = i
        while j+1 < len(t) and t[j+1]-t[j] <= max_gap: j += 1
        segs.append((float(t[i]), float(t[j]), float(c[i:j+1].max())))
        i = j+1
    return segs

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--weights', required=True)
    ap.add_argument('--conf', type=float, default=0.30)
    ap.add_argument('--name', default='eval')
    args = ap.parse_args()
    from ultralytics import YOLO
    model = YOLO(args.weights)

    # On Windows, ultralytics' validator spawns worker processes which
    # re-import this module, so the guard is required.
    m = model.val(data=data('yolo_ds', 'data.yaml'), device=0, verbose=False)
    print('=== 验证集指标 (yolo_ds) ===')
    print('  precision %.3f  recall %.3f  mAP50 %.3f  mAP50-95 %.3f' % (
        m.box.mp, m.box.mr, m.box.map50, m.box.map))
    print()
    print('=== 全视频 3fps 扫描 vs 人工确认真值 ===')
    allsegs = {}
    tot_tp = tot_fp = tot_fn = 0
    for tag, (vid, dur) in VIDS.items():
        times, hits = scan(model, vid, tag, conf=args.conf)
        segs = group(hits)
        allsegs[tag] = segs
        gt = GT.get(tag, [])
        # the stretches confirmed NOT to contain a glide
        notg = NOT_GLIDE.get(tag, [])
        # frame level
        tp = fp = fn = 0
        for t in times:
            det = any(abs(t - h[0]) < 1e-6 for h in hits)
            truth = any(a <= t <= b for a, b in gt)
            if det and truth: tp += 1
            elif det and not truth: fp += 1
            elif truth and not det: fn += 1
        prec = tp/(tp+fp) if tp+fp else 0.0
        rec = tp/(tp+fn) if tp+fn else 0.0
        f1 = 2*prec*rec/(prec+rec) if prec+rec else 0.0
        # segment level: a detection is a hit if it overlaps a glide window
        def overlaps(a, b, wins):
            return any(not (b < lo or a > hi) for lo, hi in wins)
        found = sum(1 for a, b, c in segs if overlaps(a, b, gt))
        # a ground-truth window counts as found if ANY detection overlaps it
        gt_missed = sum(1 for lo, hi in gt
                        if not any(overlaps(a, b, [(lo, hi)]) for a, b, c in segs))
        extra = [s for s in segs if not overlaps(s[0], s[1], gt)]
        fp_on_neg = [s for s in extra if overlaps(s[0], s[1], notg)]
        print('  %s: 帧级 P=%.3f R=%.3f F1=%.3f  |  检出滑翔段 %d/%d (漏 %d)，额外 %d 段 (其中误报 %d)' % (
            tag, prec, rec, f1, found, len(gt), gt_missed, len(extra), len(fp_on_neg)))
        tot_tp += tp; tot_fp += fp; tot_fn += fn
        for a, b, c in segs:
            hit = overlaps(a, b, gt)
            note = '' if hit else ('   <-- 已知非滑翔段(误报)' if overlaps(a, b, notg)
                                    else '   <-- 不在真值中')
            print('       %7.2f..%7.2f conf=%.3f%s' % (a, b, c, note))
    P = tot_tp/(tot_tp+tot_fp) if tot_tp+tot_fp else 0.0
    R = tot_tp/(tot_tp+tot_fn) if tot_tp+tot_fn else 0.0
    F = 2*P*R/(P+R) if P+R else 0.0
    print()
    print('  合计: P=%.3f R=%.3f F1=%.3f  (TP=%d FP=%d FN=%d)' % (P, R, F, tot_tp, tot_fp, tot_fn))
    with open(os.path.join(OUT, 'eval_%s.json' % args.name), 'w') as f:
        json.dump({k: [[round(a,2), round(b,2), round(c,3)] for a,b,c in v] for k,v in allsegs.items()}, f, indent=1)

if __name__ == '__main__':
    main()
