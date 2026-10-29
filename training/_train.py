"""Train one iteration and report -- no scanning, no val().

Kept deliberately separate from _semi_semisup.py so that the training run
cannot be disturbed by the evaluation stage (ultralytics' val() spawns
worker processes on Windows, which used to overwrite the results of a
concurrent run).
"""
from _paths import data  # 数据根目录，见 training/README.md
import _progress
import os, time, argparse
os.environ['YOLO_VERBOSE'] = '0'

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--data', default=data('yolo_ds', 'data.yaml'))
    ap.add_argument('--init', default=None)
    ap.add_argument('--name', required=True)
    ap.add_argument('--epochs', type=int, default=40)
    ap.add_argument('--project', default=data('output', 'yolo'))
    ap.add_argument('--workers', type=int,
                    default=int(os.environ.get('YOLO_WORKERS', '0')))
    ap.add_argument('--batch', type=int, default=8)
    ap.add_argument('--cache', default='false',
                    choices=['false', 'ram', 'disk'],
                    help='图片缓存。ram 把解码彻底省掉，但 train+val 全进内存，'
                         '这套数据约 1.2 GB，内存吃紧时会开始换页反而更慢')
    ap.add_argument('--lr0', type=float, default=None,
                    help='初始学习率。默认从 --init 推断（带 init 用 0.001 微调，'
                         '不带用 0.01）。换骨干网络做对比实验时显式给定，'
                         '否则学习率不同会让结果没法比')
    args = ap.parse_args()

    from ultralytics import YOLO
    base = args.init or data('weights', 'yolov8n.pt')
    cache = False if args.cache == 'false' else args.cache
    lr0 = args.lr0 if args.lr0 is not None else (0.001 if args.init else 0.01)
    print('training from %s, workers=%d, batch=%d, cache=%s, lr0=%g' % (
        base, args.workers, args.batch, cache, lr0), flush=True)
    model = YOLO(base)
    _progress.attach(model, args.name)
    t0 = time.time()
    model.train(
        data=args.data,
        epochs=args.epochs, imgsz=640, batch=args.batch, device=0,
        workers=args.workers,
        cache=cache,
        project=args.project, name=args.name, exist_ok=True, verbose=False,
        seed=0, patience=60, cos_lr=True,
        lr0=lr0,
    )
    print('training took %.1f s' % (time.time()-t0), flush=True)
    print('weights ->', os.path.join(args.project, args.name, 'weights', 'best.pt'), flush=True)

if __name__ == '__main__':
    main()
