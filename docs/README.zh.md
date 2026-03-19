**[English](/README.md)** | **[Русский](/docs/README.ru.md)** | **[中文](/docs/README.zh.md)**

## 简介

一款轻量级、低延迟的实时通信应用程序。在单个可执行文件中提供**语音聊天、文本消息和硬件加速屏幕共享**功能，内置服务器 - 无需单独安装服务器。

<p align="center">
  <img src="/docs/MainWindow-zh.png" alt="主窗口截图">
</p>

## 主要功能

- **语音聊天**，带降噪功能（RNNoise）和丢包隐藏（Opus PLC）
- **屏幕共享**，30 fps，硬件 H.264 编码（NVENC → AMF → libx264 回退）
- **CoreAudio 音频栈**（WASAPI/MMDevice），用于低延迟捕获和播放
- **自适应音频播放**，带抖动估计和动态缓冲区大小调整
- **自适应屏幕共享播放**，带逐帧时间增量和目标缓冲区逻辑
- **UPnP 自动端口转发**，通过 Open.NAT（可选，一键操作）
- **端到端加密**：ECDH 密钥交换 → 每个参与者的 AES-256-CBC 会话密钥
- **TCP + UDP 双通道传输**（LiteNetLib）：控制信令使用 TCP，媒体流使用 UDP
- **透明重连**：连接丢失时有 5 秒缓冲时间，无需重新认证
- **内置服务器** - 仅在用户选择创建会话时启动；否则零启动成本
- **MVVM 架构**，采用动态 DI 容器（Microsoft.Extensions.DependencyInjection）

## 系统组件

### 客户端（WPF .NET 8）
- 用户界面带深色主题，支持俄语和中文本地化
- WASAPI 音频捕获（麦克风 + 系统回环），带 RNNoise 降噪
- Opus 编解码器（VOIP 模式，480 采样 / 10 毫秒帧，丢包时使用 PLC）
- 通过 FFmpeg 进行 H.264 编码/解码（avcodec、swscale）
- 桌面复制 API 捕获（SharpDX），带光标叠加
- TCP/UDP 网络客户端，支持透明重连

### 内置服务器（DLL .NET 8）
- 会话创建、密码验证、用户管理
- MSG/CMD 数据包路由 - 服务器永不解密消息内容
- 参与者之间的媒体（音频/视频）中继
- 每个会话的 AES 会话密钥，通过与每个客户端的 ECDH 共享密钥进行 XOR 分发
- 屏幕共享槽位仲裁（一次只能有一个演示者）

### 独立 Linux 服务器
- 独立仓库：**[talknado-server-linux](https://github.com/SodiumCXI/talknado-server-linux)**
- 与内置服务器相同的会话处理和媒体中继功能，独立的 linux-x64 二进制文件
- 安装方式：
```bash
curl -fsSL https://raw.githubusercontent.com/SodiumCXI/talknado-server-linux/main/install.sh | bash
```

## 安全性

连接握手分为七个步骤：

1. **版本检查** - 在交换任何密钥之前拒绝不兼容的客户端
2. **ECDH 密钥交换** - 客户端和服务器交换公钥；各自独立推导出共享密钥
3. **密码验证** - 使用共享密钥通过 AES-256-CBC 加密 SHA-256(密码)
4. **会话密钥传递** - 服务器将 32 字节会话密钥与客户端唯一的共享密钥进行 XOR；XOR 是自逆运算，因此不需要额外的解密步骤
5. **身份交换** - 用户名和分配的用户 ID，均使用会话密钥加密
6. **UDP 注册** - 通过 LiteNetLib 发送加密的 userId；服务器用 `#UCC` 确认
7. **状态同步** - 服务器传递参与者列表和当前屏幕共享状态

从第 4 步开始，所有流量（控制和媒体）都使用会话密钥（AES-256-CBC）加密。

## 如何通过互联网使用

### 方案 A - 自动（UPnP）
启动服务器时，勾选 **"UPnP 端口转发"**。Talknado 将尝试通过 Open.NAT 自动映射端口。服务器停止时映射将被移除。需要静态公网 IP。

### 方案 B - ZeroTier（始终有效）
1. 在每台设备上安装 [ZeroTier](https://www.zerotier.com/)。
2. 所有参与者加入同一个 ZeroTier 网络（Network ID）。
3. 启动 Talknado 并输入主机应用程序中的连接密钥。