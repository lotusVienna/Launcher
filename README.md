# Lofla Launcher（启动器）

一个只有几十 KB 的 Windows 小工具：把常用的脚本和程序集中在一个窗口里，每一项都有“启动”和“关闭”按钮。启动 `.ps1` 时不会一闪而过，也不用改系统的执行策略。

## 功能

- 添加任意路径并自定义名称，支持 `.ps1`、`.bat`、`.cmd`、`.exe`，也可以直接拖文件进窗口
- 每一项有“启动”和“关闭”按钮，运行中的项目显示绿色圆点；“关闭”会连同子进程一起结束
- **右键 → 重命名**（也可以双击名称），路径不变，自动保存
- **只允许运行一个启动器**：再次双击 exe 不会开新窗口，而是把已有窗口（包括收在托盘里的）唤出来
- 启动 `.ps1` 自动绕过执行策略，可选“结束后保持窗口”
- 支持多开：同一项可同时启动多个实例，名称后显示 ×数量
- 托盘：关闭/最小化时收到托盘，双击托盘图标显示/隐藏，右键可直接启动、关闭全部或退出
- **程序内更新**：点“检查更新”（或启动时自动检查），发现 GitHub 新 Release 后一键下载、替换并自动重启

## 运行环境

Windows 10 / 11，使用系统自带的 .NET Framework 4.x，单文件 exe，无需安装。

## 配置文件

```
%AppData%\Launcher\launcher.cfg
```

`#` 开头的行是选项（`keep` / `multi` / `tray` / `autoupdate`），其余每行 `名称|路径`。复制该文件即可迁移到别的电脑。

## 自行编译

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /codepage:65001 /win32icon:Launcher.ico /out:Launcher.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll Launcher.cs
```

## 上传 GitHub 与发布

1. 打开 `Launcher.cs`，把 `AppInfo.Repo` 改成你的 `用户名/仓库名`。
2. 新建 GitHub 仓库（公开仓库，程序内更新才能免登录读取），把本目录全部内容（含 `.github` 文件夹）推上去。
3. 发布新版本：修改 `AppInfo.Version`（如 `1.1.2`），提交后打 tag 并推送：
   ```
   git tag v1.1.2
   git push origin v1.1.2
   ```
   `.github/workflows/release.yml` 会自动编译并创建 Release，附件为 `Launcher.exe`。
4. **tag 版本号必须和源码里的 `AppInfo.Version` 一致**，否则更新检测会不准。

## 更新机制说明

- 读取 `https://api.github.com/repos/<Repo>/releases/latest` 的 tag 与第一个 `.exe` 附件
- 下载到 `<exe>.new`，把当前 exe 改名为 `<exe>.old`，换上新文件后用 `--restart` 重启，新进程启动时清理 `.old`
- 已启动的脚本不受影响；未签名程序首次运行可能触发 SmartScreen 提示

## 注意事项

- “关闭”使用 `taskkill /T /F` 强制结束，被关闭的脚本没有机会做收尾
- 未配置 `AppInfo.Repo`（仍是 `YOUR_...`）时，更新功能会提示先配置，且不会自动检查
