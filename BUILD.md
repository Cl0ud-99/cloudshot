# Cloudshot 构建说明（换机开发用）

本目录是完整源码包，可直接在另一台 Windows 电脑上继续开发。

## 1. 环境要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 1903+ / Windows 11（x64） |
| 构建工具 | **.NET SDK 8.0+**（只用它编译，程序运行时不依赖它） |
| 运行环境 | .NET Framework 4.8（Win10 1903+/Win11 系统自带，无需安装） |
| 网络 | 首次还原 NuGet 包需要联网 |

> 目标框架是 `net48`，但用 SDK 式工程 + `Microsoft.NETFramework.ReferenceAssemblies` 编译，
> 所以**不需要装 .NET Framework 4.8 Developer Pack**，只要 SDK 8 即可。

检查 SDK 是否就绪：

```powershell
dotnet --list-sdks
```

## 2. 构建

```powershell
cd Cloudshot-src-2026-10-23
dotnet build Cloudshot.csproj -c Release
```

产物：

```
bin\Release\net48\Cloudshot.exe      ← 单文件，约 120 KB
```

首次构建会从 NuGet 拉取两个包：

- `Microsoft.NETFramework.ReferenceAssemblies` 1.0.3（提供 net48 引用程序集）
- `Microsoft.Windows.SDK.Contracts` 10.0.18362.2005（提供 OCR/翻译用到的 WinRT API）

如果还原失败（公司网络/离线），可以预置 NuGet 缓存，或在有网机器上先执行一次
`dotnet restore`，再把 `~\.nuget\packages` 拷过来。

清理重建：

```powershell
dotnet build Cloudshot.csproj -c Release -t:Rebuild
# 或彻底清掉
Remove-Item -Recurse -Force bin, obj
```

## 3. 运行与调试参数

```powershell
.\bin\Release\net48\Cloudshot.exe             # 常驻托盘
.\bin\Release\net48\Cloudshot.exe --capture   # 启动后立刻截一次
.\bin\Release\net48\Cloudshot.exe --diag      # 写 diag.txt（环境诊断：DPI/系统版本/权限/目录）
.\bin\Release\net48\Cloudshot.exe --translate-test 图片.png   # 跑一次 OCR+翻译，结果写 translate-test.txt
.\bin\Release\net48\Cloudshot.exe --set-youdao <appId> <密钥>  # 保存有道凭据（DPAPI 加密）
.\bin\Release\net48\Cloudshot.exe --autostart-on / --autostart-off
```

日志与配置目录（固定，与 exe 位置无关）：

```
%APPDATA%\Cloudshot\debug.log     每次截图追加，超过 256 KB 自动截断
%APPDATA%\Cloudshot\settings.ini  热键、翻译服务、加密后的凭据
```

> 注意：程序是**单实例**的。如果托盘里已经在跑，带参数再启动会直接退出（参数不生效）。
> 做命令行测试前先 `Stop-Process -Name Cloudshot`。

## 4. 部署

绿色单文件，把 `Cloudshot.exe` 放到任意普通目录（桌面、`C:\Tools`）即可。
开机自启写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，失败时自动退回
`shell:startup` 快捷方式。

**不要**放在受保护/沙箱化目录（例如某些同步盘、受控的 Program Files 子目录），
否则会无法写配置和注册表。

## 5. 源码结构

| 文件 | 行数级 | 职责 |
| --- | --- | --- |
| `src/Program.cs` | ~300 | 入口、单实例互斥、命令行参数分发（--capture/--diag/--translate-test/--set-youdao/--autostart-*）、诊断输出 |
| `src/OverlayWindow.cs` | ~2500 | 全屏遮罩：截图显示、选区/控制点、窗口悬停识别、放大镜取色、六种标注、文字编辑与拖动、取字模式、翻译面板、工具栏、Toast、键盘/鼠标全部交互 |
| `src/Annotations.cs` | ~250 | 标注数据模型（矩形/椭圆/箭头/画笔/文字/马赛克）与自绘层 `AnnotationLayer` |
| `src/ScreenCapture.cs` | ~120 | 整屏位图抓取（BitBlt）与 DPI 缩放换算 |
| `src/WindowEnumerator.cs` | ~130 | 枚举 Z 序窗口，用 `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` 取真实边界 |
| `src/Ocr.cs` | ~340 | Windows OCR 封装：枚举系统所有识别语言、按脚本打分挑选、词框返回、小图放大 |
| `src/Translator.cs` | ~240 | 有道 v3（SHA256 签名）与 MyMemory 兜底 |
| `src/Settings.cs` | ~200 | 配置读写、DPAPI 加解密 |
| `src/SettingsWindow.cs` | ~330 | 设置界面（热键录制、翻译服务、开机自启） |
| `src/TrayController.cs` | ~340 | 托盘图标与菜单 |
| `src/AutoStart.cs` | ~200 | 注册表 / 启动文件夹两种自启方式 |
| `src/NativeMethods.cs` | ~130 | P/Invoke 声明 |
| `src/RightClickBuffer.cs` | ~90 | 右键取消后的 250ms 低级鼠标钩子缓冲 |

资源：`assets/cloudshot.ico`（图标）、`app.manifest`（PerMonitorV2 + 管理员权限声明为 asInvoker）。
`tools/ui-test.ps1` 是开发期用的自动化 UI 测试脚本（模拟热键、框选、右键），可参考。

## 6. 二次开发常见坑

1. **中文乱码**：工程设置了 `<CodePage>65001</CodePage>`。如果用其它工具改源码，
   务必保持 UTF-8 无 BOM；用 PowerShell 的 `Get-Content/Set-Content` 直接改容易按 GBK 解码而损坏文件。
2. **坐标系统**：遮罩窗口内部用物理像素；窗口根节点加了 `ScaleTransform(1/scale)` 抵消 DPI 缩放。
   新增 UI 时不要自己做 DPI 换算，直接按物理像素布局。
3. **WinRT 调用**：OCR 走 `SoftwareBitmap.CreateCopyFromBuffer` + `InMemoryRandomAccessStream`，
   **不要**用 `StorageFile.GetFileFromPathAsync`（桌面应用会 Access Denied）。
4. **剪贴板**：`Clipboard.SetDataObject(data, false)` 后不要强依赖 `Flush()` 成功（`CLIPBRD_E_CANT_OPEN` 很常见）。
5. **输入法**：单键快捷键（C/O/T/+/−）必须同时处理 `Key.ImeProcessed`
   （`e.Key == Key.ImeProcessed` 时看 `e.ImeProcessedKey`），否则中文输入法会吞键。
6. **有道时间戳**：`curtime` 必须是 **UTC 秒**（用本地时间会报 errorCode 206）。
   `ToUnixTimeSeconds()` 之后不要再做时区换算。
7. **DPI**：窗口声明 PerMonitorV2；混合 DPI 多屏仍未完全验证。
