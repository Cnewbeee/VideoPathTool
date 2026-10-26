"""Scan both videos at 5 fps with a LOW detection threshold to find every
candidate, then report them grouped, sorted by confidence.

Two purposes:
  1. collect HIGH-CONFIDENCE frames -- these are the safest positives to
     add to the training set (almost no label noise)
  2. reveal glide events the current windows miss, and reveal any window
     that is actually mislabelled

The v3 model is used, but with conf lowered so that weak-but-real detections
still show up. Only the high-confidence ones get promoted to training data.
"""
from _paths import data  # 数据根目录，见 training/README.md
import os, json, time
os.environ['YOLO_VERBOSE'] = '0'
import numpy as np
import cv2
from ultralytics import YOLO

OUT = data('output', 'yolo')
W = os.path.join(OUT, 'glider_v3', 'weights', 'best.pt')
VIDS = {'p5': data('p5.mp4'), 'p3': data('p3.mp4')}

# windows that are currently labelled
P5_WIN = [(16.0, 32.0), (43.0, 52.0), (85.5, 90.5), (214.0, 218.5),
           (235.5, 240.5), (316.0, 318.5), (359.5, 362.0)]
P3_WIN = [(318.0, 323.5), (331.5, 339.0), (344.5, 347.0), (371.5, 379.5)]

def scan(vid, fps_scan=5.0, conf=0.10, batch=24):
    cap = cv2.VideoCapture(vid)
    vfps = cap.get(cv2.CAP_PROP_FPS) or 30
    step = max(1, int(round(vfps / fps_scan)))
    hits = []
    buf, buft = [], []
    i = 0
    def flush():
        if not buf: return
        res = model.predict(buf, conf=conf, verbose=False, imgsz=640, device=0)
        for r, t in zip(res, buft):
            if len(r.boxes) > 0:
                b = max(r.boxes, key=lambda x: float(x.conf))
                hits.append((t, float(b.conf)))
        buf.clear(); buft.clear()
    while True:
        ok, f = cap.read()
        if not ok: break
        if i % step == 0:
            buf.append(f); buft.append(i/vfps)
            if len(buf) >= batch: flush()
        i += 1
    flush()
    cap.release()
    return hits

def group(hits, max_gap=0.8):
    if not hits: return []
    t = np.array([h[0] for h in hits]); c = np.array([h[1] for h in hits])
    o = np.argsort(t); t, c = t[o], c[o]
    segs = []; i = 0
    while i < len(t):
        j = i
        while j+1 < len(t) and t[j+1]-t[j] <= max_gap: j += 1
        segs.append({'start': float(t[i]), 'end': float(t[j]),
                     'n': j-i+1,
                     'max': float(c[i:j+1].max()),
                     'mean': float(c[i:j+1].mean())})
        i = j+1
    return segs

model = YOLO(W)
result = {}
for tag, vid in VIDS.items():
    t0 = time.time()
    hits = scan(vid)
    segs = group(hits)
    result[tag] = hits
    wins = P5_WIN if tag == 'p5' else P3_WIN
    print()
    print('=== %s: %d detections, %d groups (%.0fs) ===' % (
        tag, len(hits), len(segs), time.time()-t0))
    for s in sorted(segs, key=lambda x: -x['max']):
        mid = (s['start']+s['end'])/2
        inwin = any(a <= mid <= b for a, b in wins)
        print('  %7.2f..%7.2f  n=%3d  max=%.3f  mean=%.3f  %s' % (
            s['start'], s['end'], s['n'], s['max'], s['mean'],
            'in-window' if inwin else '*** OUTSIDE ***'))

np.save(os.path.join(OUT, 'scan5fps_p5.npy'), np.array(result['p5']))
np.save(os.path.join(OUT, 'scan5fps_p3.npy'), np.array(result['p3']))
print()
print('saved 5 fps scans')
