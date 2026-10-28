import sys

# ============================================================ 地图 / 截图 分隔条
xaml_path = r"F:\GENSHIN1\VideoPathTool.UI\MainWindow.xaml"
xaml = open(xaml_path, encoding="utf-8").read()

old = '''                            <Grid Grid.Row="1">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>

                                <Border Grid.Column="0" Background="#0F172A" CornerRadius="4"
                                        BorderBrush="{StaticResource Line}" BorderThickness="1">
                                    <local:MapView x:Name="MapCanvas" />
                                </Border>

                                <!-- 选中点位时，显示录制当时的小地图与画面 -->
                                <Border Grid.Column="1" Width="258" Margin="8,0,0,0"
                                        Background="White" CornerRadius="4"
                                        BorderBrush="{StaticResource Line}" BorderThickness="1"
                                        Padding="8">'''

new = '''                            <Grid Grid.Row="1">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*" MinWidth="280" />
                                    <ColumnDefinition Width="Auto" />
                                    <ColumnDefinition Width="258" MinWidth="150" MaxWidth="700" />
                                </Grid.ColumnDefinitions>

                                <Border Grid.Column="0" Background="#0F172A" CornerRadius="4"
                                        BorderBrush="{StaticResource Line}" BorderThickness="1">
                                    <local:MapView x:Name="MapCanvas" />
                                </Border>

                                <!-- 拖动这条分隔条调整地图与截图的比例 -->
                                <GridSplitter Grid.Column="1" Width="7"
                                              HorizontalAlignment="Stretch" VerticalAlignment="Stretch"
                                              Background="Transparent"
                                              ResizeBehavior="PreviousAndNext" ResizeDirection="Columns"
                                              ShowsPreview="False"
                                              ToolTip="拖动调整地图与截图的宽度比例" />

                                <!-- 选中点位时，显示录制当时的小地图与画面 -->
                                <Border Grid.Column="2" Margin="0"
                                        Background="White" CornerRadius="4"
                                        BorderBrush="{StaticResource Line}" BorderThickness="1"
                                        Padding="8">'''

if old not in xaml:
    sys.exit("map grid anchor missing")
xaml = xaml.replace(old, new, 1)
open(xaml_path, "w", encoding="utf-8", newline="\n").write(xaml)
print("xaml ok")

# ============================================================ 播放：总是对齐播放头
path = r"F:\GENSHIN1\VideoPathTool.UI\MainWindow.xaml.cs"
src = open(path, encoding="utf-8").read()

old_play = '''        // 用解码器自带的时长判断"已经播到结尾"，而不是滑块的最大值：
        // 那个值在视频还没载入时是 1，会把任何位置都判成结尾，于是一点播放就回到 0。
        var duration = _videoDuration > 0
            ? _videoDuration
            : _playerTotalFrames / Math.Max(1e-6, _playerFps);
        var start = _playheadSec;
        if (duration > 0.1 && start >= duration - 0.05)
        {
            start = 0; // 停在结尾时再按播放，从头开始
        }

        var current = _playerFrame / Math.Max(1e-6, _playerFps);
        if (Math.Abs(start - current) > 0.25)
        {'''

new_play = '''        // 用解码器自带的时长判断"已经播到结尾"，而不是滑块的最大值：
        // 那个值在视频还没载入时是 1，会把任何位置都判成结尾，于是一点播放就回到 0。
        var duration = _videoDuration > 0
            ? _videoDuration
            : _playerTotalFrames / Math.Max(1e-6, _playerFps);
        var start = _playheadSec;
        if (duration > 0.1 && start >= duration - 0.05)
        {
            start = 0; // 停在结尾时再按播放，从头开始
        }

        // 每次都把解码器挪到播放头位置。_playerFrame 只是我们自己的记账，
        // 拖动进度条走的是另一条解码路径（scrub），它并不跟着变，所以
        // "记账接近播放头"不等于"解码器真的在那儿"——之前正是靠这个判断
        // 跳过定位，于是播放总从文件开头开始。代理副本上定位约 0.12 秒，
        // 这点代价换"一定从当前画面播"是值得的。
        var current = _playerFrame / Math.Max(1e-6, _playerFps);
        if (true)
        {'''

if old_play not in src:
    sys.exit("play anchor missing")
src = src.replace(old_play, new_play, 1)

open(path, "w", encoding="utf-8", newline="\n").write(src)
print("code ok")
