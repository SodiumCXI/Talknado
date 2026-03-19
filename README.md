**[English](/README.md)** | **[Русский](/docs/README.ru.md)** | **[中文](/docs/README.zh.md)**

## Description

A lightweight, low-latency real-time communication application. Provides **voice chat, text messaging, and hardware-accelerated screen sharing** in a single executable with an embedded server - no separate server installation required.

<p align="center">
  <img src="/docs/MainWindow-en.png" alt="Main window screenshot">
</p>

## Key Features

- **Voice chat** with noise suppression (RNNoise) and packet loss concealment (Opus PLC)
- **Screen sharing** at 30 fps with hardware H.264 encoding (NVENC → AMF → libx264 fallback)
- **CoreAudio audio stack** (WASAPI/MMDevice) for low-latency capture and playback
- **Adaptive audio playback** with jitter estimation and dynamic buffer sizing
- **Adaptive screen-share playback** with per-frame delta timing and target-buffer logic
- **UPnP automatic port forwarding** via Open.NAT (optional, one click)
- **End-to-end encryption**: ECDH key exchange → AES-256-CBC session key per participant
- **TCP + UDP dual-channel transport** (LiteNetLib): control over TCP, media over UDP
- **Transparent reconnect**: 5-second grace window on connection loss, no re-authentication
- **Embedded server** - starts only when the user chooses to host; zero startup cost otherwise
- **MVVM architecture** with dynamic DI containers (Microsoft.Extensions.DependencyInjection)

## System Components

### Client (WPF .NET 8)
- UI with dark theme, Russian and Chinese localization
- WASAPI audio capture (microphone + system loopback) with RNNoise noise suppression
- Opus codec (VOIP mode, 480-sample / 10 ms frames, PLC on packet loss)
- H.264 encode/decode via FFmpeg (avcodec, swscale)
- Desktop Duplication API capture (SharpDX) with cursor overlay
- TCP/UDP network client with transparent reconnect

### Embedded Server (DLL .NET 8)
- Session creation, password verification, user management
- MSG/CMD packet routing - server never decrypts message content
- Media (audio/video) relay between participants
- Per-session AES session key, distributed via XOR with each client's ECDH shared secret
- Screen-share slot arbitration (one presenter at a time)

### Dedicated Linux Server
- Separate repository: **[talknado-server-linux](https://github.com/SodiumCXI/talknado-server-linux)**
- Same session handling and media relay as the embedded server, self-contained linux-x64 binary
- Installation:
```bash
curl -fsSL https://raw.githubusercontent.com/SodiumCXI/talknado-server-linux/main/install.sh | bash
```

## Security

The connection handshake runs in seven steps:

1. **Version check** - incompatible clients are rejected before any key material is shared
2. **ECDH key exchange** - client and server exchange public keys; each independently derives the shared secret
3. **Password verification** - SHA-256(password) encrypted with AES-256-CBC using the shared secret
4. **Session key delivery** - server XORs the 32-byte session key with the client's unique shared secret; XOR is self-inverse, so no extra decryption step is needed
5. **Identity exchange** - username and assigned user ID, both encrypted with the session key
6. **UDP registration** - encrypted userId sent over LiteNetLib; server confirms with `#UCC`
7. **State sync** - server delivers participant list and current screen-share state

From step 4 onward all traffic - control and media - is encrypted with the session key (AES-256-CBC).

## How to Use Over the Internet

### Option A - Automatic (UPnP)
When starting a server, check **"Enable UPnP port forwarding"**. Talknado will attempt to map the port automatically via Open.NAT. The mapping is removed when the server stops. Requires a static public IP.

### Option B - ZeroTier (always works)
1. Install [ZeroTier](https://www.zerotier.com/) on each device.
2. All participants join the same ZeroTier network (Network ID).
3. Start Talknado and enter the connection key from the host's application.