# VideoPathTool

把一段《原神》跑图录像，转成 [BetterGI] 能直接执行的**路径 JSON**。

[![build](https://github.com/Cnewbeee/VideoPathTool/actions/workflows/build.yml/badge.svg)](https://github.com/Cnewbeee/VideoPathTool/workflows/build.yml)
[![license](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

[English](README.md)

![概览](docs/images/ui-overview.png)

## 它解决什么问题

博主录的路线上只有画面，没有坐标。BetterGI 需要的是一串游戏坐标点。手工抄一遍不现实，
所以这个工具做了三件事：

1. **把画面变成坐标**：逐帧用小地图模板匹配定位，模板匹配跟丢时用 SIFT 重同步。
   跨度大到不可能走过去的跳变会被识别成传送，而不是在两点之间画一条穿过山体的直线。
2. **把坐标变成路径点**：RDP 简化，但带硬约束——简化后的折线偏离真实轨迹的距离有上限，
   短促的拐角也不会被相邻点吞掉。
3. **把路径点变成可执行的路线**：识别滑翔段（`move_mode: "fly"`），支持给任意时刻挂
   BetterGI 动作，最后导出 JSON。

![滑翔窗口](docs/images/ui-glide-windows.png)

## 需要什么

| | |
|---|---|
| 系统 | Windows 10 / 11（WPF 界面 + OpenCV 原生库） |
| 运行时 | 编译要 [.NET 8 SDK](https://dotnet.microsoft.com/download)，运行要 .NET 8 Desktop Runtime |
| 地图资源 | **不含**，见下 |
| yt-dlp | 可选，只有"给链接直接下"才需要 |

### 地图资源不随仓库分发

匹配需要游戏自己的地图图片（`...\Assets\Map\Teyvat`）。那是游戏版权资源，
所以仓库里没有。把配置里的 `map_assets_dir` 指向你已经有的那份即可
（BetterGI 自带的、或 grasscutter 系资产包都行）。

**这一步不做，录制会一个点都匹配不到**，是首先要设的东西。

## 编译

```powershell
git clone https://github.com/Cnewbeee/VideoPathTool.git
cd VideoPathTool

dotnet build VideoPathTool\VideoPathTool.csproj -c Release      # 控制台
dotnet build VideoPathTool.UI\VideoPathTool.UI.csproj -c Release # 界面
```

界面程序在 `VideoPathTool.UI\bin\Release\net8.0-windows\VideoPathTool.UI.exe`。

首次运行会在程序旁边生成 `VideoPathTool.config.json`。所有数据默认落在程序旁边的
`video\downloads`、`video\routes`、`tools`，不往系统别处写。

## 上手

1. 把 `yt-dlp.exe` 放进 `tools\`（或配置 `yt_dlp_path`，或让它出现在 PATH 里）。
2. `map_assets_dir` 指向地图资源。
3. 打开界面，粘贴路线视频链接 → 点「下载并识别」：下载、检测小地图圆、录制、导出一次做完。
4. 到「视频与标定」页：播放，看到漏识别的滑翔就按 **F8**（起点、终点各一次），
   需要挂动作按 **F9**。
5. 点「重新导出」——毫秒级重写 JSON，不用再读视频。

## 输出格式

标准的 BetterGI 路径任务。`fly` 让 BetterGI 打开风之翼，`action` 在到达该点时执行。

```json
{
  "info": { "name": "p5", "type": "collect", "map_name": "Teyvat" },
  "positions": [
    { "id": 1, "x": 9191.09, "y": 2852.12, "type": "teleport", "move_mode": "run" },
    { "id": 2, "x": 9205.61, "y": 2857.72, "action": "use_gadget",
      "action_params": "5", "move_mode": "run", "type": "path" }
  ]
}
```

BetterGI 的反序列化会忽略不认识的字段，所以这里多写的记账字段不影响它。

## 目录结构

```
video\downloads\<标题>.mp4          源视频
video\routes\<名称>\<名称>.json      交付物，一个视频一个
video\routes\<名称>\debug.csv        录制采样点（免重录重建）
video\routes\<名称>\project.json     编辑状态：滑翔窗口 + 点位操作
video\routes\<名称>\previews\        匹配预览帧，排查用
video\routes\_archive\              被取代的旧版本
```

## 快捷键（视频页）

| 按键 | 作用 |
|---|---|
| `空格` | 播放 / 暂停 |
| `←` / `→` | 逐帧 |
| `Shift` + `←` / `→` | 逐秒 |
| `F8` | 标记滑翔（起点、终点各按一次） |
| `F9` | 在当前帧添加点位操作 |
| `Ctrl` + `B` | 收起 / 展开左侧参数栏 |

标记用功能键而不是字母，是因为中文输入法会把字母键吞掉——`M`、`A` 根本到不了窗口，
只有空格和方向键能过。这是实测算出来的结论，不是拍脑袋。

## 文档

| 文档 | 内容 |
|---|---|
| [VideoPathTool.UI/README.md](VideoPathTool.UI/README.md) | 界面、执行调度器、标注时间轴、点位操作 |
| [VideoPathTool/PATHSIMPLIFY.md](VideoPathTool/PATHSIMPLIFY.md) | 路径简化怎么做的，为什么这么定参数 |
| [VideoPathTool/GLIDER_YOLO.md](VideoPathTool/GLIDER_YOLO.md) | 滑翔检测模型：数据、训练、评测结果 |
| [VideoPathTool/GLIDER_ANALYSIS.md](VideoPathTool/GLIDER_ANALYSIS.md) | 为什么学习式检测器比几何启发式强 |
| [VideoPathTool/GLIDER_ROUTEA.md](VideoPathTool/GLIDER_ROUTEA.md) | 几何启发式检测器 |

## 已知限制

- **BetterGI 没有"按任意键"这种动作。** 它的 action 是固定枚举，写别的直接抛异常。
  所以这里给的是它真实支持的那份列表，键盘只能间接触发
  （`use_gadget` = Z 键，`stop_flying` = 跳跃 + 普攻，……）。
- 从已导出的 JSON 反推滑翔窗口时，依据是"哪些路径点被标成了 fly"。一段很短的滑翔如果
  一个路径点都没落进去，就反推不出来——按 F8 补一次。
- 只支持 Windows，界面依赖 WPF / Desktop 运行时。

## 仓库里没有的东西

| 内容 | 原因 |
|---|---|
| 游戏地图图片 | 游戏版权资源 |
| 路线视频与抽出的帧 | 画面属于录制者 |
| YOLO 训练集 | 由上述视频派生的数 GB 帧 |
| `yt-dlp.exe` | 到 yt-dlp 项目自行下载 |
| BetterGI 源码 | 是另一个项目，这里只对齐它的 JSON 格式 |

仓库里的 `VideoPathTool\glider.onnx` 只有权重，训练用的是作者自己提供的路线视频帧，
没有附带任何视频、帧或标注。想训练自己的模型，步骤见
[GLIDER_YOLO.md](VideoPathTool/GLIDER_YOLO.md)。

## 致谢

- [BetterGI](https://github.com/babalae/better-genshin-impact)：路径格式的定义者，
  也是最终执行这些路线的工具。
- [yt-dlp](https://github.com/yt-dlp/yt-dlp)、[OpenCvSharp](https://github.com/shimat/opencvsharp)、
  [ONNX Runtime](https://github.com/microsoft/onnxruntime)、
  [Ultralytics](https://github.com/ultralytics/ultralytics)。

## 许可

[MIT](LICENSE)。随附的模型权重同样按 MIT；游戏资源与第三方工具不在此列。
