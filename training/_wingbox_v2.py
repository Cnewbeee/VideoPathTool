"""Build the training labels from the CONFIRMED glide windows, using a box
that is derived from the WING itself rather than from "the largest blob".

The earlier heuristic picked the largest connected component, which is wrong
whenever something else in the frame is bigger than the wing (distant
buildings, effects). The wing is always the structure that
  * is centred on the screen (the character is pinned to the centre while
    gliding), and
  * spans a wide horizontal band just above the character's hips.

So: search a narrow horizontal band around the centre, pick the component
whose CENTRE is closest to the screen centre, and require it to be wide.
"""
from _paths import data  # 数据根目录，见 training/README.md
import cv2, numpy as np, os

OUT = data('output', 'analysis')

def wing_box_v2(frame):
    """Box the wing using 'centred and wide', not 'largest'."""
    H, W = frame.shape[:2]
    cx = W // 2
    # the wing sits at chest height; scan a vertical band around it
    y0, y1 = int(H*0.26), int(H*0.50)
    x0, x1 = int(W*0.24), int(W*0.76)
    roi = frame[y0:y1, x0:x1]
    if roi.size == 0:
        return None
    hsv = cv2.cvtColor(roi, cv2.COLOR_BGR2HSV)
    gray = cv2.cvtColor(roi, cv2.COLOR_BGR2GRAY)
    med = np.median(gray)
    med_sat = np.median(hsv[:, :, 1])
    fg = ((gray < med - 24) | ((hsv[:, :, 1] > med_sat + 40) & (hsv[:, :, 2] > 85))).astype(np.uint8)
    fg = cv2.morphologyEx(fg, cv2.MORPH_CLOSE, np.ones((5, 5), np.uint8))

    n, lab, st, ce = cv2.connectedComponentsWithStats(fg, 8)
    best = None
    best_score = -1e9
    for k in range(1, n):
        x, y, w, h, a = st[k][0], st[k][1], st[k][2], st[k][3], st[k][4]
        if a < 200 or w < 60:
            continue
        if w < 1.5 * h:              # must be wide
            continue
        ccx = x + w / 2
        # how close the component's centre is to the screen centre
        off = abs(ccx - (cx - x0))
        # prefer wide AND centred
        score = w - 2.2 * off
        if score > best_score:
            best_score = score
            best = (x, y, w, h, a)
    if best is None:
        return None
    x, y, w, h, a = best
    if w < 0.10 * (x1 - x0):
        return None
    pad = 16
    bx0 = max(0, x0 + x - pad); by0 = max(0, y0 + y - pad)
    bx1 = min(W, x0 + x + w + pad); by1 = min(H, y0 + y + h + pad)
    if bx1 - bx0 < 40 or by1 - by0 < 15:
        return None
    return (bx0, by0, bx1, by1)

if __name__ == '__main__':
    CHECK = [
        (data('p5.mp4'), 20.0, 'p5 20.0 good'),
        (data('p5.mp4'), 47.0, 'p5 47.0 good'),
        (data('p5.mp4'), 88.43, 'p5 88.4 new'),
        (data('p5.mp4'), 216.08, 'p5 216.1 new'),
        (data('p5.mp4'), 239.49, 'p5 239.5 new'),
        (data('p5.mp4'), 317.0, 'p5 317.0 it2'),
        (data('p5.mp4'), 360.7, 'p5 360.7 it2'),
        (data('p3.mp4'), 345.67, 'p3 345.7 it2'),
        (data('p3.mp4'), 332.5, 'p3 332.5 good'),
    ]
    tiles = []
    for vid, t, label in CHECK:
        cap = cv2.VideoCapture(vid)
        cap.set(cv2.CAP_PROP_POS_FRAMES, int(t*30))
        ok, f = cap.read()
        cap.release()
        if not ok: continue
        bb = wing_box_v2(f)
        if bb:
            x0, y0, x1, y1 = bb
            cv2.rectangle(f, (x0, y0), (x1, y1), (0, 255, 0), 5)
        c = cv2.resize(f, (420, 236), interpolation=cv2.INTER_AREA)
        cv2.putText(c, label, (6, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.62, (0, 255, 255), 2)
        cv2.putText(c, 'box:%s' % ('YES' if bb else 'NONE'), (6, 42),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.62, (0, 255, 0) if bb else (0, 0, 255), 2)
        tiles.append(c)
    rows = []
    for i in range(0, len(tiles), 3):
        r = tiles[i:i+3]
        while len(r) < 3: r.append(np.zeros_like(tiles[0]))
        rows.append(np.hstack(r))
    cv2.imwrite(os.path.join(OUT, 'label_boxes_v2.png'), np.vstack(rows))
    print('saved label_boxes_v2.png')
