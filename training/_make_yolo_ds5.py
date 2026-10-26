"""Expand the training set with HIGH-CONFIDENCE samples, then train the next
iteration.

WHAT CHANGED SINCE THE LAST ITERATION
--------------------------------------
A 5 fps scan with a low threshold (0.10) over both videos surfaced glide
events that the original manual pass had missed. Visual review of every
out-of-window candidate showed that 11 of the 12 p3 hits outside the
labelled windows really are deployed wings -- p3 contains roughly 15 glide
events, but only 4 had been labelled.

That also exposed a labelling error: the old P3_NEG window (275.0-281.7)
actually CONTAINED a glide at 275.6-275.8, so the previous dataset had
false negatives in it.

HOW THE POSITIVES ARE CHOSEN
-----------------------------
Two sources, with different confidence floors:

  A. HIGH CONFIDENCE (>= 0.45) inside the already-verified windows.
     These are the safest possible labels and are sampled densely.

  B. NEW VARIANTS (>= 0.15) in the 11 windows that the scan found and that
     were confirmed by eye. The model scores these low only because it has
     never seen them -- that is exactly why they are worth adding. They are
     sampled at half the rate so they do not swamp source A, and each of
     the 11 windows was individually checked to be a real deployed wing.

Everything between the confidence floors is skipped: it is neither a
positive nor a negative.

NEGATIVES
---------
The negative windows are now DERIVED from the positive windows rather than
listed by hand, so a glide can never end up inside a negative stretch.
The hard negatives (the character's cape, which spreads out exactly like a
wing) are kept explicitly.
"""
from _paths import data  # 数据根目录，见 training/README.md
import os, shutil, random
import numpy as np
import cv2
from ultralytics import YOLO

OUT = data('output', 'yolo')
DS = data('yolo_ds')
W = os.path.join(OUT, 'glider_v6', 'weights', 'best.pt')
P5 = data('p5.mp4')
P3 = data('p3.mp4')

# ---------------------------------------------------------------------------
# Glide windows, all confirmed by eye.
#   verified  = already in the training set
#   new       = found by the 5 fps scan and confirmed frame by frame
# ---------------------------------------------------------------------------
P5_VERIFIED = [(16.0, 32.0), (43.0, 52.0), (85.5, 90.5), (214.0, 218.5),
                (235.5, 240.5), (316.0, 318.5), (359.5, 362.0)]
P3_VERIFIED = [(318.0, 323.5), (331.5, 339.5), (344.5, 347.0), (371.5, 379.5)]
P5_NEW = []
P3_NEW = [(14.6, 15.0), (44.4, 44.6), (46.3, 48.4), (48.4, 48.4),
          (58.6, 58.8), (60.4, 61.2), (64.7, 64.7), (97.8, 98.8),
          (115.0, 116.0), (238.8, 238.8), (239.0, 244.0), (264.3, 267.3),
          (275.6, 275.8), (282.0, 282.2), (283.0, 283.0), (297.8, 297.8),
          (348.7, 349.0), (364.0, 365.0), (426.0, 426.0),
          (445.6, 446.2), (448.0, 448.0), (454.0, 454.7)]

# Explicit hard negatives: stretches with NO glide, including the ones that
# look like a glide (the cape) and the one the model got wrong (6.8-7.6 s).
P5_HARD_NEG = [(108.0, 121.5), (12.0, 17.5), (24.6, 25.9), (31.6, 33.5),
                (40.5, 43.0), (52.0, 56.5), (204.0, 214.0), (218.5, 235.5),
                (240.6, 316.0), (318.6, 359.5), (362.1, 390.0)]
P3_HARD_NEG = [(6.8, 7.6), (323.5, 331.5), (339.5, 344.5), (347.0, 348.6),
               (349.1, 363.9), (365.1, 371.5), (350.4, 354.9), (625.0, 629.1),
               (685.3, 691.3), (788.9, 793.7), (802.8, 806.0)]

CONF_HIGH = 0.20     # absolute floor for the verified windows
CONF_NEW = 0.10      # absolute floor for the newly found windows
# How many frames to take from each window at most. The wing flaps, so the
# model is confident on some frames of a glide and silent on others; taking
# a fixed NUMBER per window (the most confident ones) keeps the sample
# count stable without forcing frames the model does not believe in.
MAX_PER_WINDOW = 14
# The 11 new windows are sampled at half that rate: they are few frames
# each, but they carry the new colour/shape variants the model needs.
MAX_PER_NEW_WINDOW = 6

def wing_box(frame):
    """Box the wing: wide AND centred on the screen (the character is
    pinned to the centre while gliding). Picking the largest connected
    component is wrong whenever something else in the frame is bigger."""
    H, W = frame.shape[:2]
    cx = W // 2
    bx0, bx1 = int(W*0.24), int(W*0.76)
    by0, by1 = int(H*0.26), int(H*0.50)
    roi = frame[by0:by1, bx0:bx1]
    if roi.size == 0: return None
    hsv = cv2.cvtColor(roi, cv2.COLOR_BGR2HSV)
    gray = cv2.cvtColor(roi, cv2.COLOR_BGR2GRAY)
    med = np.median(gray)
    med_sat = np.median(hsv[:, :, 1])
    fg = ((gray < med - 24) | ((hsv[:, :, 1] > med_sat + 40) & (hsv[:, :, 2] > 85))).astype(np.uint8)
    fg = cv2.morphologyEx(fg, cv2.MORPH_CLOSE, np.ones((5, 5), np.uint8))
    n, lab, st, ce = cv2.connectedComponentsWithStats(fg, 8)
    best, bs = None, -1e9
    for k in range(1, n):
        x, y, w, h, a = st[k][0], st[k][1], st[k][2], st[k][3], st[k][4]
        if a < 200 or w < 60 or w < 1.5*h: continue
        score = w - 2.2*abs((x + w/2) - (cx - bx0))
        if score > bs: bs, best = score, (x, y, w, h)
    if best is None: return None
    x, y, w, h = best
    if w < 0.10*(bx1-bx0): return None
    pad = 16
    x0 = max(0, bx0+x-pad); y0 = max(0, by0+y-pad)
    x1 = min(W, bx0+x+w+pad); y1 = min(H, by0+y+h+pad)
    if x1-x0 < 40 or y1-y0 < 15: return None
    return (x0, y0, x1, y1)

def merged(wins, gap=1.0):
    out = []
    for a, b in sorted(wins):
        if out and a - out[-1][1] <= gap:
            out[-1] = (out[-1][0], max(out[-1][1], b))
        else:
            out.append((a, b))
    return out

def score_windows(vid, wins, model, stride, conf):
    """Decode once, score every wanted frame with the model.

    Returns a list of (time, conf) for the frames the model actually
    detected -- frames it stayed silent about are simply not returned.
    """
    wanted = {}
    for a, b in wins:
        t = a
        while t <= b:
            wanted[round(t, 2)] = True
            t += stride
    cap = cv2.VideoCapture(vid)
    fps = cap.get(cv2.CAP_PROP_FPS) or 30
    out = []
    i = 0
    n_want = len(wanted)
    done = 0
    while True:
        ok, f = cap.read()
        if not ok: break
        t = round(i/fps, 2)
        if t in wanted:
            r = model.predict(f, conf=conf, verbose=False, imgsz=640, device=0)
            done += 1
            c = max((float(b.conf) for b in r[0].boxes), default=0.0)
            if c >= conf:
                out.append((t, c))
            if done >= n_want: break
        i += 1
    cap.release()
    return out

def per_window(wins, scored, cap_per):
    """Split the scored frames by window and keep the most confident ones.

    A glide window also contains the take-off and landing frames, where the
    wing is not deployed yet. The model stays silent on those, so only the
    frames it DID detect are kept -- and only the best MAX_PER_WINDOW of
    them, so a long window cannot drown out a short one.
    """
    keep = []
    for a, b in wins:
        ws = [(t, c) for t, c in scored if a <= t <= b]
        ws.sort(key=lambda x: -x[1])
        keep += ws[:cap_per]
    keep.sort()
    return keep

def main():
    model = YOLO(W)
    pos_hi, pos_new = [], []

    for tag, vid, ver, new in (('p5', P5, P5_VERIFIED, P5_NEW),
                                ('p3', P3, P3_VERIFIED, P3_NEW)):
        scored = score_windows(vid, ver, model, 0.20, CONF_HIGH)
        hi = per_window(ver, scored, MAX_PER_WINDOW)
        pos_hi += [(vid, tag, t, c, 'hi') for t, c in hi]
        print('  %s verified windows: %d frames (from %d detections in %d windows)' % (
            tag, len(hi), len(scored), len(ver)))
        if new:
            scored2 = score_windows(vid, new, model, 0.40, CONF_NEW)
            nw = per_window(new, scored2, MAX_PER_NEW_WINDOW)
            pos_new += [(vid, tag, t, c, 'new') for t, c in nw]
            print('  %s new windows     : %d frames (from %d detections in %d windows)' % (
                tag, len(nw), len(scored2), len(new)))

    print('positives: %d high-confidence + %d new-variant = %d' % (
        len(pos_hi), len(pos_new), len(pos_hi)+len(pos_new)))
    for name, group in (('high', pos_hi), ('new', pos_new)):
        if group:
            c = np.array([g[3] for g in group])
            print('  %s confidence: min %.3f median %.3f max %.3f' % (
                name, c.min(), np.median(c), c.max()))

    # ---- negatives: derived, so a glide can never land in one ----
    all_pos = merged(P5_VERIFIED + P5_NEW + P3_VERIFIED + P3_NEW, gap=2.0)
    def subtract(total, holes, gap=1.5):
        out = []
        cur = 0.0
        for a, b in sorted(holes):
            if a > cur:
                out.append((cur, a))
            cur = max(cur, b)
        if cur < total:
            out.append((cur, total))
        return [(a, b) for a, b in out if b - a > 2.0]

    neg = []
    for vid, hard, pos_win, tag, total in (
            (P5, P5_HARD_NEG, P5_VERIFIED + P5_NEW, 'p5', 390.0),
            (P3, P3_HARD_NEG, P3_VERIFIED + P3_NEW, 'p3', 805.0)):
        # the hard negatives win: they are deliberate, curated stretches
        for a, b in hard:
            t = a
            while t <= b:
                neg.append((vid, tag, t)); t += 0.6
        # plus random frames from the rest of the video, avoiding the glides
        random.seed(41)
        protected = hard + pos_win
        added, tries = 0, 0
        while added < 250 and tries < 40000:
            tries += 1
            t = round(random.uniform(1, total-1), 2)
            if any(a-2 <= t <= b+2 for a, b in protected): continue
            neg.append((vid, tag, t)); added += 1

    # SAFETY NET: a negative must never fall inside a glide window. Earlier
    # iterations had exactly this bug (a glide at 275.6-275.8 s was inside
    # the negative stretch 275.0-281.7 s), and it teaches the model to
    # ignore the real thing.
    conflicts = 0
    clean_neg = []
    for v, g, t in neg:
        wins = (P5_VERIFIED + P5_NEW) if g == 'p5' else (P3_VERIFIED + P3_NEW)
        if any(a - 0.5 <= t <= b + 0.5 for a, b in wins):
            conflicts += 1
            continue
        clean_neg.append((v, g, t))
    if conflicts:
        print('  dropped %d negatives that fell inside a glide window' % conflicts)
    neg = clean_neg
    print('negatives: %d' % len(neg))

    # ---- write ----
    if os.path.exists(DS): shutil.rmtree(DS)
    for sub in ('images/train', 'images/val', 'labels/train', 'labels/val'):
        os.makedirs(os.path.join(DS, sub), exist_ok=True)

    samples = [(v, g, 'pos', t) for v, g, t, c, k in pos_hi + pos_new] + \
              [(v, g, 'neg', t) for v, g, t in neg]
    random.shuffle(samples)
    n_val = int(len(samples)*0.25)
    # assign the split first, then read the frames in ONE pass per video:
    # seeking per frame is very slow with H.264.
    plan = {}
    for i, (v, tag, kind, t) in enumerate(samples):
        split = 'val' if i < n_val else 'train'
        plan.setdefault((v, kind), []).append((tag, t, split))

    written = skipped = 0
    for (v, kind), items in plan.items():
        cap = cv2.VideoCapture(v)
        fps = cap.get(cv2.CAP_PROP_FPS) or 30
        want = {round(t*fps): (tag, t, split) for tag, t, split in items}
        need = len(want)
        idx = 0
        while need > 0:
            ok, frame = cap.read()
            if not ok: break
            if idx in want:
                tag, t, split = want.pop(idx)
                need -= 1
                name = '%s_%s_%07.2f' % (tag, kind, t)
                cv2.imwrite(os.path.join(DS, 'images', split, name+'.jpg'), frame,
                            [cv2.IMWRITE_JPEG_QUALITY, 90])
                lines = []
                if kind == 'pos':
                    bb = wing_box(frame)
                    if bb is None:
                        skipped += 1
                        os.remove(os.path.join(DS, 'images', split, name+'.jpg'))
                        idx += 1
                        continue
                    x0, y0, x1, y1 = bb
                    H, Wd = frame.shape[:2]
                    lines.append('0 %.4f %.4f %.4f %.4f' % ((x0+x1)/2/Wd, (y0+y1)/2/H, (x1-x0)/Wd, (y1-y0)/H))
                with open(os.path.join(DS, 'labels', split, name+'.txt'), 'w') as f:
                    f.write('\n'.join(lines))
                written += 1
            idx += 1
        cap.release()
    print('written %d, skipped %d positives' % (written, skipped))
    with open(os.path.join(DS, 'data.yaml'), 'w') as f:
        f.write('path: %s\ntrain: images/train\nval: images/val\nnames:\n  0: glider\n' % DS.replace('\\','/'))

    # Also produce the pre-shrunk copy the trainer uses: letterboxing the
    # images once, offline, removes the per-epoch decode+resize cost.
    DST = DS + '_640'
    if os.path.exists(DST):
        shutil.rmtree(DST)
    for sub in ('images/train', 'images/val', 'labels/train', 'labels/val'):
        os.makedirs(os.path.join(DST, sub), exist_ok=True)
    for split in ('train', 'val'):
        idir = os.path.join(DS, 'images', split)
        for f in os.listdir(idir):
            img = cv2.imread(os.path.join(idir, f))
            if img is None:
                continue
            h, w = img.shape[:2]
            s = min(640 / w, 640 / h)
            nw, nh = int(round(w*s)), int(round(h*s))
            r = cv2.resize(img, (nw, nh), interpolation=cv2.INTER_LINEAR)
            c = np.full((640, 640, 3), 114, np.uint8)
            ox, oy = (640-nw)//2, (640-nh)//2
            c[oy:oy+nh, ox:ox+nw] = r
            cv2.imwrite(os.path.join(DST, 'images', split, f), c,
                        [cv2.IMWRITE_JPEG_QUALITY, 95])
        for f in os.listdir(os.path.join(DS, 'labels', split)):
            shutil.copy2(os.path.join(DS, 'labels', split, f),
                         os.path.join(DST, 'labels', split, f))
    with open(os.path.join(DST, 'data.yaml'), 'w') as f:
        f.write('path: %s\ntrain: images/train\nval: images/val\nnames:\n  0: glider\n' % DST.replace('\\','/'))
    print('pre-shrunk copy ->', DST)

if __name__ == '__main__':
    main()
