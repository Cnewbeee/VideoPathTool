# 人工标注检查（labelme）

## 为什么要有这一步

数据集里的框是脚本伪标注出来的，必然有错：漏标（有风之翼却没框）、误标（披风、
传送道具被当成翼）。**训练前先人工过一遍，比调任何超参都有效。**

## 文件在哪，谁能改它们

```
video/yolo_ds/
├── images/train/<图>.jpg      图片
├── images/train/<图>.json     ← labelme 的标注，人工改的是这个
├── labels/train/<图>.txt      ← YOLO 训练真正读的
└── json_backup/<时间戳>/      覆盖前的自动备份
```

**只有 `label-apply.bat` 会把 json 写回 txt。** 其余脚本都只读：

| 脚本 | 读 | 写 |
|---|---|---|
| `_yolo2labelme.py` | txt | json（默认跳过已存在的） |
| `_labelme2yolo.py --dry-run` | json | 什么都不写 |
| `_labelme2yolo.py` | json | tv（先备份 txt） |
| `_train.py` | 图片 + txt | `output/yolo/` |

所以**在 labelme 里改完、确认之前，训练集不受影响**——那也意味着不跑
`label-apply.bat` 就不会生效。

## 三步

**1. 打开**

```
training/label-train.bat     # 训练集 804 张
training/label-val.bat       # 验证集 259 张
```

框已经预画好。判断标准：

- 框贴合风之翼 → 不用动，按 `D` 下一张
- 框偏了 → 拖角点修正
- 有翼没框 → 画矩形，标签填 `glider`
- 没翼有框（披风、传送道具、载具）→ 删掉框

改完按 `Ctrl+S`。一个框都不留 = 负样本（这张图确实没有风之翼），也是有效样本。

**2. 写回**

```
training/label-apply.bat
```

先打印"会改哪些文件、几个框"，确认后才真正写。原 txt 备份在
`labels_backup/<split>/`。

**3. 训练**

```
python training/_train.py --name glider_v9 --epochs 100 --batch 8
```

## 配置里两个必须关掉的开关

`~/.labelmerc`：

```yaml
store_data: false        # 否则每张 json 内嵌几百 KB 的图片 base64
with_image_data: false   # labelme 7 的新名字
auto_save: false         # 浏览时不自动改文件
```

开着 `store_data` 会写出巨长的 json（实测一张 400KB+），加载也变慢。

## 一条教训

整理数据集时**不要批量删除 `.json`**。那些文件里是人工补的框，删了就没了——
labelme 不留历史，`Remove-Item -Force` 也不进回收站。
`_yolo2labelme.py --overwrite` 现在会先把现有 json 复制到
`json_backup/<时间戳>/`，但手动删除依然没有后悔药。
