"""Compare detection quality and speed at different inference resolutions.

The exported ONNX model has a dynamic input size, so the resolution can be
chosen at run time. Lower resolutions are much faster, but the model was
trained at 640, so the accuracy at other sizes has to be measured rather
than assumed.

This runs the whole p5 and p3 videos at 320 / 416 / 480 / 512 / 640 and
reports the segment-level recall against the confirmed ground truth.
"""
from _paths import data  # 数据根目录，见 training/README.md
import os, time
os.environ['YOLO_VERBOSE'] = '0'
import numpy as np
import cv2
import onnxruntime as ort

ONNX = data('output', 'yolo', 'glider_v5', 'weights', 'best.onnx')
VIDS = {'p5': data('p5.mp4'), 'p3': data('p3.mp4')}

GT = {
    'p5': [(16.0, 32.0), (43.0, 52.0), (85.0, 90.0), (214.0, 218.5),
           (235.5, 240.5), (316.0, 318.5), (359.5, 362.0)],
    'p3': [(6.8, 7.6), (14.6, 15.0), (44.4, 44.6), (46.3, 48.4),
           (58.6, 58.8), (60.4, 61.2), (64.7, 64.7), (97.8, 98.8),
           (238.8, 238.8), (275.6, 275.8), (282.0, 282.2), (297.8, 297.8),
           (318.0, 323.5), (331.5, 339.5), (344.5, 347.0), (348.7, 349.0),
           (371.5, 379.5), (445.6, 446.2), (448.0, 448.0), (454.0, 454.7)],
}
NOT_GLIDE = {'p5': [], 'p3': [(6.8, 7.6)]}

def letterbox(f, sz):
    h, w = f.shape[:2]
    sc = min(sz/w, sz/h)
    nw, nh = int(round(w*sc)), int(round(h*sc))
    r = cv2.resize(f, (nw, nh), interpolation=cv2.INTER_LINEAR)
    c = np.full((sz, sz, 3), 114, np.uint8)
    ox, oy = (sz-nw)//2, (sz-nh)//2
    c[oy:oy+nh, ox:ox+nw] = r
    return c[:, :, ::-1].transpose(2, 0, 1).astype(np.float32)/255.0

def overlaps(a, b, wins):
    return any(not (b < lo or a > hi) for lo, hi in wins)

def group(hits, gap=1.5):
    if not hits: return []
    t = np.array([h[0] for h in hits]); c = np.array([h[1] for h in hits])
    o = np.argsort(t); t, c = t[o], c[o]
    segs = []; i = 0
    while i < len(t):
        j = i
        while j+1 < len(t) and t[j+1]-t[j] <= gap: j += 1
        segs.append((float(t[i]), float(t[j]), float(c[i:j+1].max())))
        i = j+1
    return segs

so = ort.SessionOptions(); so.intra_op_num_threads = 8
sess = ort.InferenceSession(ONNX, so, providers=['CPUExecutionProvider'])
inp = sess.get_inputs()[0]

for sz in (320, 416, 480, 512, 640):
    print('\n=== resolution %d x %d ===' % (sz, sz))
    tot_found = tot_gt = tot_extra = tot_fpneg = 0
    t_start = time.time()
    for tag, vid in VIDS.items():
        cap = cv2.VideoCapture(vid)
        vfps = cap.get(cv2.CAP_PROP_FPS) or 30
        step = 10
        hits = []
        buf = []; buft = []
        i = 0
        def flush():
            if not buf: return
            d = np.stack([letterbox(x, sz) for x in buf])
            o = sess.run(None, {inp.name: d})[0]
            for k, tt in enumerate(buft):
                sc = float(o[k][4:, :].max())
                if sc >= 0.30:
                    hits.append((tt, sc))
            buf.clear(); buft.clear()
        while True:
            ok, f = cap.read()
            if not ok: break
            if i % step == 0:
                buf.append(f); buft.append(i/vfps)
                if len(buf) >= 16: flush()
            i += 1
        flush()
        cap.release()
        segs = group(hits)
        gt = GT[tag]; notg = NOT_GLIDE[tag]
        found = sum(1 for a, b, c in segs if overlaps(a, b, gt))
        extra = [x for x in segs if not overlaps(x[0], x[1], gt)]
        fpneg = [x for x in extra if overlaps(x[0], x[1], notg)]
        tot_found += found; tot_gt += len(gt); tot_extra += len(extra); tot_fpneg += len(fpneg)
        print('  %s: 检出 %2d/%2d 段, 额外 %d 段 (误报 %d), 平均分 %.3f' % (
            tag, found, len(gt), len(extra), len(fpneg),
            np.mean([c for _, _, c in segs]) if segs else 0))
    print('  合计: %d/%d 段, 额外 %d (误报 %d)   用时 %.0f s' % (
        tot_found, tot_gt, tot_extra, tot_fpneg, time.time()-t_start))
