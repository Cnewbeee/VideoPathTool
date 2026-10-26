"""TRAIN A YOLO MODEL on the tiny dataset and measure what it actually learns.

This is the decisive experiment for the user's question: is a learned
detector worth it with only ~69 positive samples available?

Train yolov8n (smallest) on the dataset, then evaluate:
  * precision / recall / mAP on the held-out split
  * behaviour on the FULL video: how many false positives, and does it find
    the three known glide windows?
"""
from _paths import data  # 数据根目录，见 training/README.md
import os, sys, time, json
os.environ['YOLO_VERBOSE'] = '0'
from ultralytics import YOLO
import numpy as np
import cv2

DS = data('yolo_ds')
OUT = data('output', 'yolo')
os.makedirs(OUT, exist_ok=True)

model = YOLO(data('weights', 'yolov8n.pt'))   # smallest, fastest to iterate
t0 = time.time()
res = model.train(
    data=os.path.join(DS, 'data.yaml'),
    epochs=int(os.environ.get('EPOCHS', 60)),
    imgsz=640,
    batch=8,
    device=0,
    workers=0,
    project=OUT,
    name='glider_v1',
    exist_ok=True,
    verbose=False,
    seed=0,
    patience=30,
)
print('training took %.1f s' % (time.time()-t0))

best = OUT + r'\glider_v1\weights\best.pt'
print('best weights:', best, os.path.exists(best))

m = YOLO(best)
metrics = m.val(data=os.path.join(DS, 'data.yaml'), device=0, verbose=False)
print()
print('=== validation metrics (held-out split) ===')
print('  precision  %.3f' % metrics.box.mp)
print('  recall     %.3f' % metrics.box.mr)
print('  mAP50      %.3f' % metrics.box.map50)
print('  mAP50-95   %.3f' % metrics.box.map)

# ---- run over the whole video and look for false positives ---------------
print()
print('=== scanning the whole p5.mp4 ===')
VID = data('p5.mp4')
GLIDES = [(15.0, 31.0), (44.0, 53.5), (110.0, 119.0)]
def in_glide(t):
    return any(a <= t <= b for a, b in GLIDES)

cap = cv2.VideoCapture(VID)
fps = cap.get(cv2.CAP_PROP_FPS) or 30
W = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)); H = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
i = 0
hits = []          # (t, conf)
n = 0
while True:
    ok, frame = cap.read()
    if not ok:
        break
    if i % 6 == 0:      # 5 fps
        r = m.predict(frame, conf=0.35, verbose=False, imgsz=640)
        boxes = r[0].boxes
        if len(boxes) > 0:
            for b in boxes:
                hits.append((i/fps, float(b.conf)))
                n += 1
    i += 1
cap.release()
print('detections: %d over %d frames' % (n, i//6))

if hits:
    times = np.array([h[0] for h in hits]); confs = np.array([h[1] for h in hits])
    # group into runs
    runs = []
    j = 0
    order = np.argsort(times)
    times = times[order]; confs = confs[order]
    while j < len(times):
        k = j
        while k+1 < len(times) and times[k+1]-times[k] <= 1.0:
            k += 1
        runs.append((times[j], times[k], confs[j:k+1].max()))
        j = k+1
    print()
    print('%-9s %-9s %-8s %s' % ('start', 'end', 'maxConf', 'verdict'))
    tp = fp = 0
    for a, b, c in runs:
        mid = (a+b)/2
        hit = in_glide(mid)
        print('%-9.2f %-9.2f %-8.3f %s' % (a, b, c, 'TRUE GLIDE' if hit else 'false positive'))
        if hit: tp += 1
        else: fp += 1
    # frame level
    gl_frames = set()
    for a, b in GLIDES:
        for t in np.arange(a, b, 0.2):
            gl_frames.add(round(float(t), 1))
    det_frames = set(round(float(t), 1) for t in times)
    tp_f = len(det_frames & gl_frames)
    fp_f = len(det_frames - gl_frames)
    fn_f = len(gl_frames - det_frames)
    p = tp_f/max(tp_f+fp_f, 1); r = tp_f/max(tp_f+fn_f, 1)
    print()
    print('frame-level: P=%.3f R=%.3f F1=%.3f (TP=%d FP=%d FN=%d)' % (
        p, r, 2*p*r/max(p+r,1e-9), tp_f, fp_f, fn_f))

