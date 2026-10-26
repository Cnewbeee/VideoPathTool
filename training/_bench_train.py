"""Compare training configurations to find the one that actually uses the GPU.

The previous run used 6% of the GPU. The cause is the data loader: every
epoch re-decoded 1058 JPEGs at 1920x1080 and scaled them to 640x640.

Three variables are compared:
  A. full-resolution images, workers=0   (the old way)
  B. pre-shrunk 640x640 images, workers=0
  C. pre-shrunk 640x640 images, workers=4

Each configuration runs 3 epochs and reports the wall clock time and the
average GPU utilisation, which is what actually matters.
"""
from _paths import data  # 数据根目录，见 training/README.md
import os, time, subprocess, sys, json
import numpy as np

OUT = data('output', 'yolo')
PY = data('.venv-yolo', 'Scripts', 'python.exe')
TRAIN = data('scripts', '_train.py')
DS_FULL = data('yolo_ds', 'data.yaml')
DS_640 = data('yolo_ds_640', 'data.yaml')
INIT = data('output', 'yolo', 'glider_v4', 'weights', 'best.pt')

CONFIGS = [
    ('A_full_w0',  DS_FULL, 0),
    ('B_640_w0',   DS_640,  0),
    ('C_640_w4',   DS_640,  4),
    ('D_640_w8',   DS_640,  8),
]

def gpu_util():
    try:
        r = subprocess.run(['nvidia-smi', '--query-gpu=utilization.gpu',
                            '--format=csv,noheader,nounits'],
                           capture_output=True, text=True, timeout=10)
        return int(r.stdout.strip().split('\n')[0])
    except Exception:
        return -1

results = []
for name, data, workers in CONFIGS:
    # pass the worker count through an env var the train script reads
    env = dict(os.environ)
    env['YOLO_WORKERS'] = str(workers)
    env['YOLO_EPOCHS'] = '3'
    env['YOLO_NAME'] = 'bench_' + name
    print('\n=== %s (data=%s workers=%d) ===' % (
        name, os.path.basename(os.path.dirname(data)), workers), flush=True)
    samples = []
    t0 = time.time()
    p = subprocess.Popen([PY, '-u', TRAIN, '--name', 'bench_' + name,
                          '--epochs', '3', '--data', data, '--init', INIT],
                         stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                         text=True, env=env)
    while p.poll() is None:
        samples.append(gpu_util())
        time.sleep(1.0)
    out = p.stdout.read()
    el = time.time() - t0
    avg = float(np.mean(samples)) if samples else -1
    peak = int(np.max(samples)) if samples else -1
    print('  wall %.1f s, GPU avg %.0f%% peak %d%%' % (el, avg, peak), flush=True)
    results.append((name, el, avg, peak))

print('\n=== summary (3 epochs each) ===')
print('%-14s %8s %10s %8s' % ('config', 'seconds', 'gpu_avg', 'gpu_peak'))
for name, el, avg, peak in results:
    print('%-14s %8.1f %9.0f%% %7d%%' % (name, el, avg, peak))
best = min(results, key=lambda r: r[1])
print('\nfaster: %s (%.1f s)' % (best[0], best[1]))
