**[English](/README.md)** | **[Русский](/docs/README.ru.md)** | **[中文](/docs/README.zh.md)**

## 简介
一款轻量级、低延迟的实时通信应用程序。在单个 exe 可执行文件中提供**语音聊天、文本消息和硬件加速屏幕共享**功能。

![主界面截图](/docs/MainWindow-zh.png)

## 主要功能
- **语音聊天和屏幕共享**，延迟最小
- **H.264 视频流水线**，通过 FFmpeg 实现硬件编码（NVENC/AMF）
- **屏幕捕获**，使用桌面复制 API（仅主显示器，SharpDX）
- **传输方式**：控制信令使用 TCP，媒体流使用 UDP（LiteNetLib）
- **紧凑的控制协议**，带加密功能
- **会话密钥交换**，确保通信安全
- **MVVM 架构**，采用依赖注入
- **可选的内置服务器** — 仅在用户选择创建会话时启动

## 系统组件

### 客户端（WPF .NET 8）
- 用户界面、音频和屏幕捕获
- H.264 编码/解码
- TCP/UDP 网络客户端

### 内置服务器（DLL .NET 8）
- 会话和用户管理
- 客户端之间的媒体中继

### 独立 Linux 服务器
- 不包含在本仓库中
- 提供与内置服务器相同的会话管理和媒体中继功能
- 安装方式：
```bash
curl -fsSL https://raw.githubusercontent.com/SodiumCXI/talknado-server-linux/main/install.sh | bash
```

## 安全性
- 使用会话密钥进行对称加密
- 可选密码保护，用于加入会话

## 如何通过互联网使用
要在全球网络中使用 Talknado，所有参与者必须加入同一个 **ZeroTier** 虚拟网络：
1. 在每台设备上安装 ZeroTier。
2. 使用网络 ID 加入同一个 ZeroTier 网络。
3. 启动 Talknado，选择创建或加入会话。