**[English](/README.md)** | **[Русский](/docs/README.ru.md)** | **[中文](/docs/README.zh.md)**

## Description
A lightweight low-latency real-time communication application. Provides **voice chat, text messaging, and hardware-accelerated screen sharing** in a single executable.

<p align="center">
  <img src="/docs/MainWindow-en.png" alt="Main window screenshot">
</p>

## Key Features
- **Voice chat and screen sharing** with minimal latency
- **H.264 video pipeline** with hardware encoding (NVENC/AMF) via FFmpeg
- **Screen capture** via Desktop Duplication API (primary display, SharpDX)
- **Transport**: TCP for control, UDP for media (LiteNetLib)
- **Compact control protocol** with encryption
- **Session key exchange** for secure communication
- **MVVM architecture** with dependency injection
- **Optional embedded server** — starts only if the user chooses to host

## System Components

### Client (WPF .NET 8)
- UI, audio/screen capture
- H.264 encoding/decoding
- TCP/UDP network client

### Embedded Server (DLL .NET 8)
- Session and user management
- Media relaying between clients

### Dedicated Linux Server
- Not part of this repository: [talknado-server-linux](https://github.com/SodiumCXI/talknado-server-linux)
- Provides the same connection handling and media relaying functionality as the embedded server
- Installation:
```bash
curl -fsSL https://raw.githubusercontent.com/SodiumCXI/talknado-server-linux/main/install.sh | bash
```

## Security
- Symmetric encryption with session keys
- Optional password for joining a session

## How to Use Over the Internet
To use Talknado over a global network, all participants must join the same **ZeroTier** virtual network:
1. Install ZeroTier on each device.
2. Join the same ZeroTier network using its Network ID.
3. Start Talknado and choose host or join a session.