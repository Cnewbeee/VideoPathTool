"""Find the training configuration that actually uses the GPU.

The pre-shrunk 640x640 dataset already helped (120.7 s -> 105.4 s for 3
epochs, GPU 23% -> 31%), but the GPU is still mostly idle. The remaining
bottleneck is the batch size of 8: with 1058 images that is 132 steps per
epoch, and each step is small enough that the GPU is idle while the next
batch is prepared.

Larger batches amortise the fixed per-step cost. A larger model also uses
the GPU better. This compares:
  - batch size 8 / 16 / 32 on the pre-shrunk data
  - yolov8n vs yolov8s (the 's' model is ~3x the compute of 'n')
"""
from _paths import data  # 数据根目录，见 training/README.md
import os, time, subprocess
import numpy as np

PY = data('.venv-yolo', 'Scripts', 'python.exe')
TRAIN = data('scripts', '_train.py')
DS_640 = data('yolo_ds_640', 'data.yaml')
INIT_N = data('output', 'yolo', 'glider_v4', 'weights', 'best.pt')
INIT_S = data('weights', 'yolov8s.pt')

def gpu_util():
    try:
        r = subprocess.run(['nvidia-smi', '--query-gpu=utilization.gpu',
                            '--format=csv,noheader,nounits'],
                           capture_output=True, text=True, timeout=10)
        return int(r.stdout.strip().split('\n')[0])
    except Exception:
        return -1

def run(name, init, batch, epochs=3, workers=0):
    env = dict(os.environ)
    print('\n=== %s (batch=%d) ===' % (name, batch), flush=True)
    samples = []
    t0 = time.time()
    p = subprocess.Popen([PY, '-u', TRAIN, '--name', 'bench2_' + name,
                          '--epochs', str(epochs), '--data', DS_640,
                          '--init', init, '--batch', str(batch),
                          '--workers', str(workers)],
                         stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                         text=True, env=env)
    while p.poll() is None:
        samples.append(gpu_util())
        time.sleep(1.0)
    out = p.stdout.read()
    el = time.time() - t0
    ok = 'CUDA out of memory' not in out and 'out of memory' not in out
    avg = float(np.mean(samples)) if samples else -1
    peak = int(np.max(samples)) if samples else -1
    print('  wall %.1f s, GPU avg %.0f%% peak %d%%, ok=%s' % (el, avg, peak, ok), flush=True)
    if not ok:
        for line in out.split('\n'):
            if 'memory' in line.lower():
                print('   ', line.strip()[:120])
    return (name, el, avg, peak, ok)

if __name__ == '__main__':
    results = []
    results.append(run('n_b8',  INIT_N, 8))
    results.append(run('n_b16', INIT_N, 16))
    results.append(run('n_b32', INIT_N, 32))
    results.append(run('s_b16', INIT_S, 16))
    print('\n=== summary (3 epochs) ===')
    print('%-8s %8s %9s %8s %6s' % ('config', 'seconds', 'gpu_avg', 'gpu_peak', 'ok'))
    for name, el, avg, peak, ok in results:
        print('%-8s %8.1f %8.0f%% %7d%% %6s' % (name, el, avg, peak, ok))
    good = [r for r in results if r[4]]
    best = min(good, key=lambda r: r[1])
    print('\nfastest: %s (%.1f s)' % (best[0], best[1]))
    # cost per second of wall clock, to compare model sizes fairly
    for name, el, avg, peak, ok in good:
        print('  %-8s %.1f s/epoch, gpu avg %.0f%%' % (name, el/3, avg))
