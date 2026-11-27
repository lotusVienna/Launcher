# Lofla Launcher（启动器）

一个只有几十 KB 的 Windows 小工具：平时是贴在屏幕边缘的一个绿色小方块，鼠标靠近就滑出来，点一下展开一个小巧的列表，里面每一项都有“启动”和“关闭”。

## 使用

- **小方块**：平时收在屏幕边缘，只露出一条细边；鼠标移到那里停留片刻，它会滑出来；鼠标离开后自动收回
- **点击小方块**：打开列表；再点一次，或点列表外面，列表就收起
- **拖动小方块**：松手后自动吸附到最近的屏幕边缘（上下左右都可以），位置会被记住
- **列表**：每行有运行状态点、名称、▶ 启动、■ 关闭；双击名称也能启动；右键可重命名、关闭最近一个、关闭全部、移除
- **添加项目**：把文件直接拖到小方块或列表上，或在托盘右键菜单选“＋ 添加项目…”
- 右上角出现橙色小点，表示有项目正在运行
- 前台是全屏程序（视频、游戏）时，小方块不会自动滑出
- 再次双击 exe 不会开第二个：会把已有的列表唤出来（即使小方块被隐藏）

## 托盘右键菜单

打开列表 / 隐藏或显示小方块 / 直接启动某一项 / 添加项目 / 三个选项开关（结束后保持窗口、允许多开、启动时检查更新）/ 检查更新 / 关闭全部运行项 / 退出。
双击托盘图标也能打开列表。

## 其他功能

- 支持 `.ps1`、`.bat`、`.cmd`、`.exe`；启动 `.ps1` 自动绕过执行策略，可选“结束后保持窗口”，报错信息不会一闪而过
- “关闭”会连同子进程（例如脚本里开的 `ssh`）一起结束
- 支持多开：同一项可同时启动多个实例，名称后显示 ×数量
- 程序内更新：发现 GitHub 新 Release 后一键下载、替换并自动重启

## 运行环境

Windows 10 / 11，使用系统自带的 .NET Framework 4.x，单文件 exe，无需安装。

## 配置文件

```
%AppData%\Launcher\launcher.cfg
```

`#` 开头的行是选项（`keep` / `multi` / `autoupdate` / `edge` / `pos`），其余每行 `名称|路径`。复制该文件即可迁移到别的电脑。想让小方块回到默认位置，删掉 `#edge` 和 `#pos` 两行即可。

## 自行编译

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /codepage:65001 /win32icon:Launcher.ico /out:Launcher.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll Launcher.cs
```

## GitHub 发布

1. 源码里 `AppInfo.Repo` 已设为 `lotusVienna/Launcher`，换仓库时改这里。
2. 修改 `AppInfo.Version` 后提交，在 GitHub 新建 Release，tag 形如 `v1.2.0`，`.github/workflows/release.yml` 会自动编译并上传 `Launcher.exe`。
3. **tag 版本号必须和源码里的 `AppInfo.Version` 一致。**

## 更新机制说明

- 读取 `https://api.github.com/repos/<Repo>/releases/latest` 的 tag 与第一个 `.exe` 附件
- 下载到 `<exe>.new`，当前 exe 改名为 `<exe>.old`，换上新文件后用 `--restart` 重启，新进程启动时清理 `.old`
- 已启动的脚本不受影响；未签名程序首次运行可能触发 SmartScreen 提示

## 注意事项

- “关闭”使用 `taskkill /T /F` 强制结束，被关闭的脚本没有机会做收尾
- 个别程序启动后会自己拉起另一个进程并退出，这种情况绿点会很快熄灭，“关闭”也关不掉真正的程序
