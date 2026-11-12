# 地狱之门 着色器模组支持 启动器

语言：[English](./README.md) 中文

## 目录
- [地狱之门 着色器模组支持 启动器](#地狱之门-着色器模组支持-启动器)
  - [目录](#目录)
  - [使用指南](#使用指南)
  - [运行细节](#运行细节)
    - [预处理](#预处理)
    - [后处理](#后处理)
    - [模组预设](#模组预设)
    - [Mod 订阅](#mod-订阅)
    - [注意事项](#注意事项)
  - [开发指南](#开发指南)
  - [致谢](#致谢)
  - [许可证](#许可证)
  - [支持我的工作](#支持我的工作)
---

## 使用指南

请查看此启动器的创意工坊页面以获取用户指南。  
https://steamcommunity.com/sharedfiles/filedetails/?id=3410344592

## 运行细节

* 启动时，程序将从 %LOCALAPPDATA%\GOHSMSLauncher\settings.conf 加载缓存。
* 无论选择何种启动选项，程序都将遵循以下流程：
0. 将 Environment.CurrentDirectory 移至游戏目录
1. 预处理
2. 使用启动参数启动游戏
3. 隐藏窗口，让程序在后台运行
4. 等待游戏程序结束
5. 游戏结束后，运行后处理
6. 根据设置决定退出程序或重新显示窗口

* 退出时，保存缓存

### 预处理

* 如果没有缓存游戏路径，则运行搜索方法
* * 如果程序位于 steamapps 的子文件夹中，则通过硬编码路径搜索游戏
* * 否则，从注册表中获取 steam 路径，并在所有游戏库中搜索游戏路径
* * 将 Environment.CurrentDirectory 移至游戏目录
* [仅限文件替换方法] 替换游戏根目录中的文件
* * 如果没有找到修改过的 shader.pak，则从程序中提取 pak
* * 否则使用缓存的 pak 替换原始 shader.pak
* 在玩家配置文件中将凹凸贴图质量强制设置为视差

### 后处理

* [仅文件替换方法][需要在设置中启用] 还原原始着色器文件，修改后的文件将保留为缓存
* [需要在设置中启用] 清除 C:\Users\你的用户名\Documents\my games\gates of hell\shader_cache 中的着色器缓存

### 模组预设

模组管理器的预设工具栏可执行以下操作：  
- 将当前加载的列表保存为预设
- 使用当前加载的列表覆盖选中的预设
- 加载已保存的预设
- 重命名预设
- 删除预设

预设是位于启动器可执行文件旁 `presets` 目录中的 UTF-8 TOML 文件。  
在加载预设的过程中，缺失的模组将被跳过，并在警告信息中列出。  
预设文件名即为其显示名称。  

一个预设文件包含：  
- `version = 1`，用于标记预设格式版本
- 一个有序的 `[[mods]]` 数组

```toml
version = 1

[[mods]]
id = “mod_123456”
name = “Workshop 示例”

[[mods]]
id = “local_mod”
name = “本地示例”
```

### Mod 订阅

打开 **Mod订阅**，无需账户即可浏览 ModDB 上的《地狱之门》模组，查看作者概览、文章、图片和文件。收藏与订阅独立保存；订阅前由你选择需要下载的文件，不会自动选中全部文件。

下载后可以检查压缩包，并将标准模组安装到所选游戏的 `mods` 目录。安装前需要选择原始模组目录，并确认这是完整安装包。补丁、安装器和不支持的目录结构可以仅下载后手动安装；启动器不会执行其中的脚本，也不会修改 `mods` 外的游戏文件。

首次成功检查文件时建立基线，后续检查提示新增文件及文件修订。已读、忽略状态与实际安装版本分别保存。可以手动检查更新；启动器运行期间也会定期检查订阅。检查失败会保留之前的文件列表与更新提示。

取消订阅前会展示将删除的受管文件和将保留的文件，收藏继续保留。手动修改、归属无法确认的文件，以及 `mods` 外的安装步骤需要单独处理。安装、替换或删除模组前请关闭游戏和编辑器。

ModDB 请求使用 Windows 原生 WinHTTP 后端和 Windows 用户代理设置，禁用 Cookie 和账户凭据。请求失败时，页面会显示 HTTP 状态码或具体连接错误类别。给 EXE 添加 `--check-moddb` 参数，可以直接测试该 EXE 的联网与解析，不打开界面、不修改订阅或游戏文件。结果与连接详情写入 `%LOCALAPPDATA%\GOHSMSLauncher\Logs\diag.log`（LocalAppData 不可用时使用临时日志目录）；退出码 0 表示已成功读取实时列表。日志不记录带签名的下载地址或凭据。

### 注意事项
* 强制改变凹凸贴图质量是一项特殊措施，用于支持我的着色器mod
* 将 Environment.CurrentDirectory 移至游戏目录总是必要的，这样steam就不会截断我们的启动指令
* 设置保存在用户 LocalAppData 目录，首次使用会兼容读取 EXE 旁的旧 settings.conf。
* 启动游戏时可选附带-showmodinfo参数，这是我发现的增强指令，可以显示详细的mod信息
* 自动更新不会检测新版本的可用性，请自行在更新后删除备份的exe文件

## 开发指南

我使用 Visual Studio 2026 构建这个程序，环境配置如下:
* 仅支持 Windows x64；构建 AOT 需要 .NET 10 SDK 和 Visual Studio C++ 构建工具。
* Avalonia Desktop 与 Fluent Theme 12.1.3、DataGrid 12.1.2
* 执行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\Publish-Aot.ps1`，只分发 `bin/net10-aot-single-file/` 中的 EXE。
* EXE 已包含 .NET 运行时和原生 UI 库，目标电脑无需安装 .NET。
* 首次启动会校验原生 DLL 并释放到 `%LOCALAPPDATA%\GOHSMSLauncher\Native\<压缩包 SHA-256>`；必要时使用 `%TEMP%`。缺失或损坏的文件会在下次启动时恢复。
* 使用 `--software-rendering` 启用软件渲染；`--validate-native-bundle` 可以在不打开界面的情况下校验封装；`--self-test-ui` 检查七页、稳定导航、中英文与模组操作，不联网，也不会启动游戏。
* DataGrid 12.1.2 的反射功能会产生两条上游 AOT 分析摘要。本项目仅使用固定只读列和编译绑定；发布脚本会拒绝其他裁剪或 AOT 警告。
* `Properties/i18n.Designer.cs` 保留了内嵌中文资源查找逻辑；新增资源键时需同步维护该文件。
* 从提交a120cb0开始本项目使用Codex辅助开发

## 致谢
特别感谢
* @𝙆𝙄𝙍𝙄𝙉 𝙎𝙏𝙍𝙊𝙉𝙂 提供了启动器图标的高清素材
* 参与启动器测试的玩家们

在开发过程中做出的贡献！

## 许可证
根据 Apache 许可证 2.0 版授权。
详情请参阅 [LICENSE](LICENSE) 文件。

## 支持我的工作
如果你喜欢我的成果，请给本仓库一个star，我不胜感激 =)  

如果可能，可以通过以下途径直接支持我的工作：

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/N4N2ZJR4A)  
[![mbd.pub](./img/mbd.png)](https://mbd.pub/o/fedStudio)  