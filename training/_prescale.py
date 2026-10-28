"""把数据集预缩放到训练分辨率，换掉每个 epoch 都在做的解码+缩放。

为什么是安全的
--------------
ultralytics 的 `BaseDataset.load_image()` 读图之后会**把长边缩到 imgsz、
保持宽高比**（rect_mode）。也就是说用 1920×1080 原图训练，模型真正看到的
一直是缩到 640×360 的图。预先把图存成 640×360，加载时 r == 1 直接跳过缩放，
喂给网络的像素与原图路径几乎逐像素一致，只剩"多存了一代 JPEG"的差别
（实测 q95 下 PSNR 中位数约 39 dB，远高于检测任务关心的量级）。

两个必须守住的前提
------------------
1. **不补灰边**。存成 640×640 带 padding 会改变 mosaic 的行为：拼图块
   从 640×360 变成 640×640 含灰框，训练分布和现在的模型就不是一回事了。
   补边应该交给加载器，它知道该补多少。
2. **尺寸要和训练用的 imgsz 对齐**。存了 640，以后改用 800 训练就等于
   先把图缩下去再拉上来，那才是真的掉精度。改 imgsz 就重跑一次这里。

标注不用动
----------
YOLO 的 txt 是归一化坐标（cx cy w h ∈ [0,1]），换分辨率完全不受影响，
所以这里只做原样复制。`data.yaml` 会按新目录重写一份。

用法（仓库根目录）

    python training/_prescale.py                    # 640，写到 yolo_ds_640
    python training/_prescale.py --imgsz 800        # 换尺寸（注意上面第 2 条）
    python training/_prescale.py --check 8          # 顺便验证保真度
    python training/_prescale.py --force            # 全部重做

可反复跑：默认只处理比源文件旧的目标文件，所以收完新样本再跑一次就等于增量同步。
"""
import argparse
import glob
import os
import shutil
import sys

import cv2
import numpy as np

from _paths import data

IMG_EXT = (".jpg", ".jpeg", ".png")


def psnr(a, b):
    mse = np.mean((a.astype(np.float64) - b.astype(np.float64)) ** 2)
    return 99.0 if mse == 0 else 10 * np.log10(255 * 255 / mse)


def letterbox(im, new=640, pad=114):
    """复刻 ultralytics 的 letterbox，只为了 --check 里做对照。"""
    h, w = im.shape[:2]
    r = min(new / h, new / w)
    nh, nw = int(round(h * r)), int(round(w * r))
    im = cv2.resize(im, (nw, nh), interpolation=cv2.INTER_LINEAR)
    out = np.full((new, new, 3), pad, np.uint8)
    top, left = (new - nh) // 2, (new - nw) // 2
    out[top:top + nh, left:left + nw] = im
    return out


def scaled_size(h, w, imgsz):
    r = imgsz / max(h, w)
    return max(1, int(round(h * r))), max(1, int(round(w * r)))


def read_names(src_root, fallback=("glider",)):
    yml = os.path.join(src_root, "data.yaml")
    names = []
    if os.path.exists(yml):
        with open(yml, encoding="utf-8") as fh:
            started = False
            for line in fh:
                if line.strip().startswith("names:"):
                    started = True
                    continue
                if started:
                    if ":" in line and line.startswith(" "):
                        names.append(line.split(":", 1)[1].strip())
                    elif line.strip() and not line.startswith(" "):
                        break
    return names or list(fallback)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default=data("yolo_ds"))
    ap.add_argument("--dst", default=data("yolo_ds_%d" % 640))
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--quality", type=int, default=95,
                    help="JPEG 质量；q95 实测 PSNR 约 39 dB")
    ap.add_argument("--force", action="store_true", help="忽略时间戳，全部重做")
    ap.add_argument("--prune", action="store_true",
                    help="删掉目标目录里源已不存在的图/标注（默认只报告）")
    ap.add_argument("--interp", default="linear", choices=("linear", "area", "cubic"),
                    help="缩放插值；linear 与 ultralytics 加载器一致（默认），"
                         "area 抗锯齿更好但等于换了训练数据")
    ap.add_argument("--check", type=int, default=0,
                    help="额外抽 N 张，量出'预缩放+补边'与'原图+补边'的 PSNR")
    args = ap.parse_args()

    if not os.path.isdir(args.src):
        sys.exit("找不到源数据集: %s" % args.src)

    src_root = os.path.abspath(args.src)
    dst_root = os.path.abspath(args.dst)
    if os.path.normcase(src_root) == os.path.normcase(dst_root):
        sys.exit("目标目录不能等于源目录：这里会就地覆盖原图")

    args.dst = dst_root
    total_new = total_skip = 0
    bytes_src = bytes_dst = 0

    for split in ("train", "val"):
        src_img = os.path.join(src_root, "images", split)
        src_lbl = os.path.join(src_root, "labels", split)
        if not os.path.isdir(src_img):
            continue
        dst_img = os.path.join(dst_root, "images", split)
        dst_lbl = os.path.join(dst_root, "labels", split)
        os.makedirs(dst_img, exist_ok=True)
        os.makedirs(dst_lbl, exist_ok=True)

        # 先核一遍：图片和标注必须一一对应，否则缩完才发现缺标注就白跑了
        stems = []
        for path in sorted(glob.glob(os.path.join(src_img, "*"))):
            if not path.lower().endswith(IMG_EXT):
                continue
            stem = os.path.splitext(os.path.basename(path))[0]
            if not os.path.exists(os.path.join(src_lbl, stem + ".txt")):
                sys.exit("缺标注: %s/%s.txt，先修好再缩放" % (split, stem))
            stems.append(stem)

        made = skipped = 0
        for stem in stems:
            src = None
            for ext in IMG_EXT:
                cand = os.path.join(src_img, stem + ext)
                if os.path.exists(cand):
                    src = cand
                    break
            dst = os.path.join(dst_img, stem + ".jpg")

            fresh = (not args.force and os.path.exists(dst)
                     and os.path.getmtime(dst) >= os.path.getmtime(src))
            if fresh:
                skipped += 1
            else:
                im = cv2.imread(src)
                if im is None:
                    sys.exit("读不了: %s" % src)
                h, w = im.shape[:2]
                nh, nw = scaled_size(h, w, args.imgsz)
                # 默认跟加载器用同一个插值：只有这样才能说"预缩放不改变训练输入"。
                # INTER_AREA 抗锯齿更好，但它是另一种重采样，等于悄悄换了训练数据
                # （实测两者差约 31 dB —— 肉眼看不出，却已经不是同一份数据了）。
                interp = {"linear": cv2.INTER_LINEAR,
                          "area": cv2.INTER_AREA,
                          "cubic": cv2.INTER_CUBIC}[args.interp]
                small = cv2.resize(im, (nw, nh), interpolation=interp)
                cv2.imwrite(dst, small,
                            [cv2.IMWRITE_JPEG_QUALITY, args.quality])
                made += 1
                bytes_src += os.path.getsize(src)
                bytes_dst += os.path.getsize(dst)

            # 标注是归一化坐标，复制即可
            lbl_dst = os.path.join(dst_lbl, stem + ".txt")
            lbl_src = os.path.join(src_lbl, stem + ".txt")
            if args.force or not os.path.exists(lbl_dst) \
                    or os.path.getmtime(lbl_dst) < os.path.getmtime(lbl_src):
                shutil.copy2(lbl_src, lbl_dst)

        total_new += made
        total_skip += skipped
        print("  %-5s 生成 %d 张，跳过 %d 张，共 %d" % (split, made, skipped, len(stems)))

        # 源里删掉的样本，目标里还留着就成了"训练集里多出来的幽灵"。
        # 只报告不删，除非显式 --prune：这套东西以前误删过标注，删文件必须是个明确的动作。
        want = {s + ".jpg" for s in stems}
        want_lbl = {s + ".txt" for s in stems}
        orphans = [p for p in glob.glob(os.path.join(dst_img, "*"))
                   if os.path.basename(p) not in want]
        orphans_lbl = [p for p in glob.glob(os.path.join(dst_lbl, "*"))
                       if os.path.basename(p) not in want_lbl]
        if orphans or orphans_lbl:
            if args.prune:
                for p in orphans + orphans_lbl:
                    os.remove(p)
                print("        清理孤儿 %d 张图 / %d 个标注" % (len(orphans), len(orphans_lbl)))
            else:
                print("        注意：有 %d 张图 / %d 个标注在源里已不存在，加 --prune 清理"
                      % (len(orphans), len(orphans_lbl)))

    # data.yaml 指向新目录；类别名从源 yaml 抄，避免两处不一致
    names = read_names(src_root)
    yml = os.path.join(dst_root, "data.yaml")
    with open(yml, "w", encoding="utf-8") as fh:
        fh.write("path: %s\n" % dst_root.replace("\\", "/"))
        fh.write("train: images/train\n")
        fh.write("val: images/val\n")
        fh.write("names:\n")
        for i, n in enumerate(names):
            fh.write("  %d: %s\n" % (i, n))

    print("\n共生成 %d 张，跳过 %d 张" % (total_new, total_skip))
    if total_new:
        print("新图平均 %.0f KB（原图 %.0f KB，约 %.1f 倍）"
              % (bytes_dst / total_new / 1024, bytes_src / total_new / 1024,
                 bytes_src / max(1, bytes_dst)))
    print("data.yaml:", yml)
    print("\n训练时指过去：\n  python training/_train.py --name glider_v12 "
          "--epochs 120 --batch 12 --data %s" % yml)

    if args.check:
        print("\n保真度抽检（预缩放+补边 vs 原图+补边）：")
        files = sorted(glob.glob(os.path.join(dst_root, "images", "train", "*.jpg")))
        step = max(1, len(files) // args.check)
        vals = []
        for path in files[::step][:args.check]:
            stem = os.path.splitext(os.path.basename(path))[0]
            src = None
            for ext in IMG_EXT:
                cand = os.path.join(src_root, "images", "train", stem + ext)
                if os.path.exists(cand):
                    src = cand
                    break
            if src is None:
                continue
            ref = letterbox(cv2.imread(src), args.imgsz)
            got = letterbox(cv2.imread(path), args.imgsz)
            if ref.shape != got.shape:
                continue
            vals.append(psnr(ref, got))
            print("  %-18s PSNR %5.2f dB" % (stem, vals[-1]))
        if vals:
            print("  → 最差 %.2f dB，中位 %.2f dB"
                  % (min(vals), float(np.median(vals))))


if __name__ == "__main__":
    main()
