"""把训练好的 .pt 导出成 C# 端用的 ONNX。

用法（仓库根目录）：

    python training/_export.py                              # 默认取 glider_v9
    python training/_export.py --name glider_v9
    python training/_export.py --weights 某处\\best.pt --out ..\\VideoPathTool\\glider.onnx

导出参数必须和现有模型一致，否则 C# 那边加载不了：

  * dynamic=True   输入尺寸可变（C# 默认喂 480，训练是 640）
  * opset 12       与已部署的 glider.onnx 相同
  * imgsz 640      训练分辨率

导出后会打印输入/输出形状做核对，并提示重新构建。
"""
import argparse
import os
import shutil
import sys

from _paths import data


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", default="glider_v9")
    ap.add_argument("--weights", default=None)
    ap.add_argument("--out", default=None)
    ap.add_argument("--opset", type=int, default=12)
    args = ap.parse_args()

    weights = args.weights or os.path.join(
        data("output", "yolo"), args.name, "weights", "best.pt")
    out = args.out or os.path.join(data(".."), "VideoPathTool", "glider.onnx")

    if not os.path.exists(weights):
        sys.exit("找不到权重: " + weights)

    from ultralytics import YOLO

    model = YOLO(weights)
    produced = model.export(format="onnx", dynamic=True, imgsz=640, opset=args.opset, simplify=False)
    produced = str(produced)

    if os.path.abspath(produced) != os.path.abspath(out):
        os.makedirs(os.path.dirname(out), exist_ok=True)
        shutil.copy2(produced, out)

    print("已导出 ->", out, f"({os.path.getsize(out) / 1e6:.2f} MB)")

    # 核对形状：动态输入应当是 [batch, 3, height, width]
    try:
        import onnx

        m = onnx.load(out)
        for i in m.graph.input:
            dims = [d.dim_value if d.dim_value else (d.dim_param or "?")
                    for d in i.type.tensor_type.shape.dim]
            print("  input ", i.name, dims)
        for o in m.graph.output:
            dims = [d.dim_value if d.dim_value else (d.dim_param or "?")
                    for d in o.type.tensor_type.shape.dim]
            print("  output", o.name, dims)
    except Exception as exc:
        print("  (形状检查跳过:", exc, ")")

    print("\n下一步: cd VideoPathTool.UI && dotnet build -c Debug")


if __name__ == "__main__":
    main()
