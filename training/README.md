# 滑翔检测：数据集与训练脚本

这里是 `VideoPathTool/GLIDER_YOLO.md` 里描述的那条流水线。

**这些是研究脚本，不是成品工具。** 它们硬编码了两次实验之间的假设，参数也是为特定
视频调的；能复现出仓库里那个 `glider.onnx`，但不要指望一条命令跑通所有情况。

## 数据从哪来

仓库**不附带**任何视频、帧或标注。你需要自己准备：

1. 几段《原神》跑图录像（工具本体就能下载/录制）
2. 人工确认真值：哪些时间段风之翼确实展开了

`_make_yolo_ds5.py` 负责从视频里按真值抠出小地图、生成正负样本并写 YOLO 数据集。

## 环境

```powershell
python -m venv .venv-yolo
.\.venv-yolo\Scripts\activate
pip install ultralytics opencv-python
```

数据根目录默认是**本仓库**（即 `training/..`），也可以用环境变量指到别处：

```powershell
$env:VPT_ROOT = "D:\my-genshin-data"   # 该目录下需要有 video\
```

脚本里所有路径都写成 `data('yolo_ds', 'data.yaml')` 这种形式（见 `_paths.py`），
不再绑定某个人的机器。

## 顺序

| 脚本 | 作用 |
|---|---|
| `_make_yolo_ds5.py` | 构建数据集（伪标签 + 负样本安全网），输出到 `video\yolo_ds` |
| `_train.py` | 训练一轮。**与评估分开**，否则 ultralytics 的 `val()` 会在 Windows 上抢同一目录 |
| `_scan5.py` | 5fps 低阈值全视频扫描，找模型漏掉的滑翔段 |
| `_eval.py` | 对人工真值评估：帧级 P/R/F1 + 段级命中 + 误报定位 |
| `_wingbox_v2.py` | 翼框选（宽且居中）的方案对比 |
| `_verify_lb.py` | 校验 C# 预处理与 ultralytics 逐帧一致 |
| `_bench_train.py` / `_bench_train2.py` | 训练配置对比（分辨率预缩放 / batch / workers） |
| `_bench_res.py` | 推理分辨率对比 |
| `_train_yolo.py` | 最早的那次决定性实验：小数据集到底学不学得会 |

典型的训练循环：

```powershell
$env:EPOCHS = "40"
python training\_train.py --name glider_v8 --epochs 40
python training\_eval.py --name glider_v8
```

导出 ONNX 后拷到 `VideoPathTool\glider.onnx`，构建时会自动复制到输出目录。

## 踩过的坑

- **评估和训练不能并行**：ultralytics 的 `val()` 在 Windows 上会 fork 进程，
  两个进程写同一个 `results.csv`，epoch 计数会乱。
- **`model.val()` 要放在 `if __name__ == '__main__'` 里**，否则 Windows 上会无限递归。
- **numpy 2.x 与 torch 2.3.1 不兼容**，专门的 `.venv-yolo` 里钉的是 numpy 1.26.4。
- **AV1 视频抽帧很慢**（约 85ms/帧），做数据集时优先用 H.264 源。

## 与主程序的关系

训练只在有新的误检/漏检样本时才需要重跑。日常使用完全不用碰这个目录——
`glider.onnx` 已经在仓库里，缺失时工具会自动退回几何启发式检测器。
