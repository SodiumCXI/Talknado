# Talknado

## Description
A lightweight low-latency application for real-time communication consisting of a WPF client and server. Provides voice chat, text messaging, and hardware-accelerated screen sharing.

## Key Features
- **Voice chat and screen sharing** with low latency
- **H.264 video pipeline** with hardware encoding (NVENC/AMF) via FFmpeg
- **Screen capture** through Desktop Duplication API
- **Transport**: TCP for control, UDP for media (LiteNetLib)
- **Compact control protocol** with encryption
- **Session key exchange** for encryption
- **MVVM architecture** with dependency injection

## System Components

### Client (WPF .NET 8)
- UI, audio/screen capture
- H.264 encoding/decoding
- TCP/UDP network client

### Server (headless .NET 8)
- Session and user management
- Media relaying between clients

## Security
- Symmetric encryption with session keys
- Optional server join password

## How to Use Over the Internet
To use Talknado over a global network, all participants must join the same **ZeroTier** virtual network:
1. Install ZeroTier on each device.
2. Join the same ZeroTier network using its Network ID.
3. Enjoy!

---

## Описание
Легковесное приложение для связи в реальном времени с низкой задержкой, состоящее из клиента (WPF) и сервера. Обеспечивает голосовую связь, текстовые сообщения и аппаратно-ускоренную демонстрацию экрана.

## Основные возможности
- **Голосовая связь и демонстрация экрана** с низкой задержкой
- **Видеопоток H.264** с аппаратным кодированием (NVENC/AMF) через FFmpeg
- **Захват экрана** через Desktop Duplication API
- **Транспорт**: TCP для управления, UDP для медиа (LiteNetLib)
- **Компактный протокол управления** с шифрованием
- **Обмен сессионными ключами** для шифрования
- **Архитектура MVVM** с внедрением зависимостей

## Компоненты системы

### Клиент (WPF .NET 8)
- UI, захват аудио/экрана
- Кодирование/декодирование H.264
- Сетевой клиент TCP/UDP

### Сервер (headless .NET 8)
- Управление сессиями и пользователями
- Ретрансляция медиа между клиентами

## Безопасность
- Симметричное шифрование с сессионными ключами
- Опциональный пароль для подключения к серверу

## Как использовать через интернет
Чтобы использовать Talknado через глобальную сеть, всем участникам необходимо подключиться к одной и той же виртуальной сети **ZeroTier**:
1. Установите ZeroTier на каждое устройство.
2. Подключитесь к одной и той же сети ZeroTier, используя её Network ID.
3. Пользуйтесь!
