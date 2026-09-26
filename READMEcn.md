# 地狱之门 着色器模组支持 启动器

语言：[English](./README.md) 中文

## 目录
- [地狱之门 着色器模组支持 启动器](#地狱之门-着色器模组支持-启动器)
  - [目录](#目录)
  - [使用指南](#使用指南)
  - [运行细节](#运行细节)
    - [预处理](#预处理)
    - [后处理](#后处理)
    - [注意事项](#注意事项)
  - [开发指南](#开发指南)
  - [致谢](#致谢)
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

### 注意事项
* 强制改变凹凸贴图质量是一项特殊措施，用于支持我的着色器mod
* 将 Environment.CurrentDirectory 移至游戏目录总是必要的，这样steam就不会截断我们的启动指令
* 设置保存在用户 LocalAppData 目录，首次使用会兼容读取 EXE 旁的旧 settings.conf。
* 启动游戏时总是附带-showmodinfo参数，这是我发现的增强指令，可以显示详细的mod信息

## 开发指南

我使用 Visual Studio 2026 构建这个程序，环境配置如下:
* 仅支持 Windows x64；构建 AOT 需要 .NET 10 SDK 和 Visual Studio C++ 构建工具。
* Avalonia Desktop 与 Fluent Theme 12.1.3、DataGrid 12.1.2
* 执行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\Publish-Aot.ps1`，只分发 `bin/net10-aot-single-file/` 中的 EXE。
* EXE 已包含 .NET 运行时和原生 UI 库，目标电脑无需安装 .NET。
* 首次启动会校验原生 DLL 并释放到 `%LOCALAPPDATA%\GOHSMSLauncher\Native\<压缩包 SHA-256>`；必要时使用 `%TEMP%`。缺失或损坏的文件会在下次启动时恢复。
* 使用 `--software-rendering` 启用软件渲染；`--validate-native-bundle` 可以在不打开界面的情况下校验封装；`--self-test-ui` 检查六页、中英文与模组操作，不会启动游戏。
* DataGrid 12.1.2 的反射功能会产生两条上游 AOT 分析摘要。本项目仅使用固定只读列和编译绑定；发布脚本会拒绝其他裁剪或 AOT 警告。
* `Properties/i18n.Designer.cs` 保留了内嵌中文资源查找逻辑；新增资源键时需同步维护该文件。
* 从提交a120cb0开始本项目使用Codex辅助开发

## 致谢
特别感谢
* @𝙆𝙄𝙍𝙄𝙉 𝙎𝙏𝙍𝙊𝙉𝙂 提供了启动器图标的高清素材
* 参与启动器测试的玩家们

在开发过程中做出的贡献！

## 支持我的工作
如果你喜欢我的成果，请给本仓库一个star，我不胜感激 =)  

如果可能，可以通过以下途径直接支持我的工作：

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/N4N2ZJR4A)  
[![mbd.pub](./img/mbd.png)](https://mbd.pub/o/fedStudio)  