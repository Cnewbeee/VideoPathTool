"""Verify that a hand-rolled letterbox + onnxruntime reproduces
ultralytics' own predict() output on the same frame.

If the scores match, the C# implementation (which uses the same
preprocessing) will agree with the Python measurements.
"""
from _paths import data  # 数据根目录，见 training/README.md
import numpy as np, cv2, onnxruntime as ort
from ultralytics.data.augment import LetterBox

ONNX = data('output', 'yolo', 'glider_v2', 'weights', 'best.onnx')
VID = data('p5.mp4')
TIMES = [20.0, 27.0, 47.0, 88.43, 216.08, 239.49, 317.0, 360.7, 112.0, 280.0]

sess = ort.InferenceSession(ONNX, providers=['CPUExecutionProvider'])
inp = sess.get_inputs()[0]
print('input:', inp.name, inp.shape)

cap = cv2.VideoCapture(VID)
fps = cap.get(cv2.CAP_PROP_FPS) or 30
for t in TIMES:
    cap.set(cv2.CAP_PROP_POS_FRAMES, int(t*fps))
    ok, frame = cap.read()
    if not ok: continue

    # --- ultralytics' own preprocessing ---
    lb = LetterBox(new_shape=(640, 640), auto=False, scaleup=True, stride=32)
    u = lb(image=frame)
    ub = u[:, :, ::-1].transpose(2, 0, 1)[None].astype(np.float32)/255.0

    # --- my C#-style preprocessing (same algorithm, re-implemented) ---
    h, w = frame.shape[:2]
    scale = min(640/w, 640/h)
    nw, nh = int(round(w*scale)), int(round(h*scale))
    r = cv2.resize(frame, (nw, nh), interpolation=cv2.INTER_LINEAR)
    canvas = np.full((640, 640, 3), 114, np.uint8)
    ox, oy = (640-nw)//2, (640-nh)//2
    canvas[oy:oy+nh, ox:ox+nw] = r
    cb = canvas[:, :, ::-1].transpose(2, 0, 1)[None].astype(np.float32)/255.0

    diff = np.abs(ub-cb).max()
    out_u = sess.run(None, {inp.name: ub})[0]
    out_c = sess.run(None, {inp.name: cb})[0]
    dscore = np.abs(out_u-out_c).max()

    def best(o):
        o = o[0]
        c, a = o.shape
        return float(o[4:, :].max())
    print('  t=%7.2f  input diff=%.4f  output diff=%.6f  scoreU=%.3f scoreC=%.3f' % (
        t, diff, dscore, best(out_u), best(out_c)))
cap.release()
