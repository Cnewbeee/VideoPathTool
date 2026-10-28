"""训练进度心跳：往 run 目录持续写 progress.json，给 UI 的「训练」页看。

为什么用文件而不是日志
----------------------
ultralytics 的每轮汇总写在 results.csv 里，但那是**一轮结束**才有一行；
想看"这一轮跑到第几个 batch、每秒几张、还要多久"就只剩终端里那条会自我
覆写的进度条。把日志重定向再正则去抠那行太脆（版本一变格式就没了），
所以直接在训练进程里挂回调，自己写一份结构化快照。

页面只读这个文件，两边完全解耦：
* 命令行照常跑训练，页面只是旁观者，不参与生命周期；
* 没有心跳（例如用 yolo 官方命令跑的）时，页面退化成读 results.csv，
  只是刷新粒度变成一轮一次。

写文件用 tmp + os.replace，避免页面正好读到半个 JSON。
"""
import json
import os
import time

# 每个 batch 都写一次太浪费（一轮 270 次），限流到 0.4 s 一次；
# 一轮结束时无条件写，保证最后状态一定落盘。
_MIN_INTERVAL = 0.4


def _num(x, nd=6):
    """把 torch 标量 / numpy 标量 / dict 里的值压成 JSON 能接受的样子。"""
    try:
        v = float(x)
    except (TypeError, ValueError):
        return None
    if v != v or v in (float("inf"), float("-inf")):  # NaN / inf
        return None
    return round(v, nd)


def _flatten(d, nd=6):
    """{'box_loss': tensor} -> {'box_loss': 1.23}；非数值项丢掉。"""
    if not d:
        return {}
    out = {}
    for k, v in dict(d).items():
        n = _num(v, nd)
        if n is not None:
            out[str(k)] = n
    return out


def attach(model, run_name, verbose=True):
    """在 model 上挂心跳回调，返回 state 字典（便于调用方排查）。"""

    state = {
        "path": None,
        "t0": time.time(),
        "batch": 0,
        "batches": 0,
        "last_write": 0.0,
        "writes": 0,
    }

    def _path(trainer):
        # save_dir 在训练开始时才最终确定，所以每次现算
        try:
            save_dir = str(trainer.save_dir)
        except AttributeError:
            return None
        return os.path.join(save_dir, "progress.json")

    def _emit(trainer, status="train", force=False, extra=None):
        now = time.time()
        if not force and now - state["last_write"] < _MIN_INTERVAL:
            return
        path = _path(trainer)
        if not path:
            return

        epoch = int(getattr(trainer, "epoch", 0) or 0) + 1  # ultralytics 是 0 基
        epochs = int(getattr(trainer, "epochs", 0) or 0)
        batches = state["batches"] or 1
        batch = state["batch"]

        # 整体进度：已完成的整轮 + 当前轮内已跑的比例
        done = (epoch - 1) + (batch / batches if batches else 0.0)
        frac = min(1.0, done / epochs) if epochs else 0.0
        elapsed = now - state["t0"]
        eta = (elapsed / frac - elapsed) if frac > 0.02 else None

        snap = {
            "status": status,
            "name": run_name,
            "epoch": epoch,
            "epochs": epochs,
            "batch": batch,
            "batches": batches,
            "fraction": round(frac, 5),
            "elapsed": round(elapsed, 2),
            "eta_sec": _num(eta, 1),
            "epoch_time": _num(getattr(trainer, "epoch_time", None), 2),
            "iters_per_sec": _num(batches / trainer.epoch_time, 3)
            if getattr(trainer, "epoch_time", None) else None,
            "loss": _flatten(getattr(trainer, "loss_items", None)),
            "epoch_loss": _flatten(getattr(trainer, "tloss", None)),
            "lr": _num(max(dict(getattr(trainer, "lr", {}) or {}).values(),
                           default=None) if getattr(trainer, "lr", None) else None),
            "best_fitness": _num(getattr(trainer, "best_fitness", None)),
            "metrics": _flatten(getattr(trainer, "metrics", None)),
            "updated": round(now, 2),
        }
        if extra:
            snap.update(extra)

        tmp = path + ".tmp"
        try:
            with open(tmp, "w", encoding="utf-8") as fh:
                json.dump(snap, fh, ensure_ascii=False)
            os.replace(tmp, path)
            state["path"] = path
            state["last_write"] = now
            state["writes"] += 1
        except OSError:
            # 目录还没建好、或者被杀毒软件锁住：心跳不是关键任务，别让训练挂掉
            pass

    def on_train_start(trainer):
        state["t0"] = time.time()
        state["batch"] = 0
        try:
            state["batches"] = len(trainer.train_loader)
        except (AttributeError, TypeError):
            state["batches"] = 0
        _emit(trainer, "train", force=True)

    def on_train_epoch_start(trainer):
        state["batch"] = 0
        if not state["batches"]:
            try:
                state["batches"] = len(trainer.train_loader)
            except (AttributeError, TypeError):
                pass
        _emit(trainer, "train", force=True)

    def on_train_batch_end(trainer):
        state["batch"] += 1
        _emit(trainer, "train")

    def on_fit_epoch_end(trainer):
        state["batch"] = state["batches"]
        _emit(trainer, "train", force=True)

    def on_train_end(trainer):
        _emit(trainer, "done", force=True)

    model.add_callback("on_train_start", on_train_start)
    model.add_callback("on_train_epoch_start", on_train_epoch_start)
    model.add_callback("on_train_batch_end", on_train_batch_end)
    model.add_callback("on_fit_epoch_end", on_fit_epoch_end)
    model.add_callback("on_train_end", on_train_end)

    if verbose:
        print("progress heartbeat -> <run>/progress.json", flush=True)
    return state
