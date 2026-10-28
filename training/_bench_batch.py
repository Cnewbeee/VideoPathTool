"""一次性基准：在预缩放数据集上试几个 batch size，量每轮耗时与显存峰值。

只用 1 个 epoch、关掉验证，跑完就删。目的是回答"batch 还能加到多少"，
而不是产出模型 —— 所以 save=False、plots=False，不留垃圾。
"""
import os
import sys
import time

os.environ["YOLO_VERBOSE"] = "0"

import torch
from ultralytics import YOLO

DATA = r"F:\GENSHIN1\video\yolo_ds_640\data.yaml"
WEIGHTS = r"F:\GENSHIN1\video\weights\yolov8n.pt"
PROJECT = r"F:\GENSHIN1\video\output\yolo"

for batch in [int(b) for b in sys.argv[1:]]:
    torch.cuda.empty_cache()
    torch.cuda.reset_peak_memory_stats()
    try:
        model = YOLO(WEIGHTS)
        t0 = time.time()
        model.train(
            data=DATA, epochs=2, batch=batch, imgsz=640, device=0, workers=0,
            val=False, verbose=False, plots=False, save=False, seed=0,
            project=PROJECT, name="_bench", exist_ok=True,
        )
        dt = time.time() - t0
        # 第 1 个 epoch 含 dataloader 预热和 cuDNN 选核，不能代表稳态；
        # trainer.epoch_time 留着最后一个 epoch 的纯训练耗时，用它才对。
        steady = getattr(model.trainer, "epoch_time", None) or dt
        n = 1120
        print("RESULT batch=%-3d  step=%-3d  epoch=%5.1fs  %6.1f img/s  peak=%5.0f MiB"
              % (batch, -(-n // batch), steady, n / steady,
                 torch.cuda.max_memory_allocated() / 2 ** 20), flush=True)
    except RuntimeError as exc:
        print("RESULT batch=%-3d  FAILED: %s" % (batch, str(exc)[:90]), flush=True)
