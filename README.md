# Talknado

## Description
A lightweight low-latency real-time communication application. Provides **voice chat, text messaging, and hardware-accelerated screen sharing** in a single executable.

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

### Embedded Server (DLL inside client)
- Session and user management
- Media relaying between clients

## Security
- Symmetric encryption with session keys
- Optional password for joining a session

## How to Use Over the Internet
To use Talknado over a global network, all participants must join the same **ZeroTier** virtual network:
1. Install ZeroTier on each device.
2. Join the same ZeroTier network using its Network ID.
3. Start Talknado and choose host or join a session.

---

## Описание
Легковесное приложение для связи в реальном времени с низкой задержкой. Обеспечивает **голосовую связь, текстовые сообщения и аппаратно-ускоренную демонстрацию экрана** в одном exe-файле.

## Основные возможности
- **Голосовая связь и демонстрация экрана** с минимальной задержкой
- **Видеопоток H.264** с аппаратным кодированием (NVENC/AMF) через FFmpeg
- **Захват экрана** через Desktop Duplication API (только основной дисплей, SharpDX)
- **Транспорт**: TCP для управления, UDP для медиа (LiteNetLib)
- **Компактный протокол управления** с шифрованием
- **Обмен сессионными ключами** для безопасной связи
- **Архитектура MVVM** с внедрением зависимостей
- **Встроенный сервер по желанию** — запускается только если пользователь выбирает быть сервером

## Компоненты системы

### Клиент (WPF .NET 8)
- UI, захват аудио и экрана
- Кодирование/декодирование H.264
- Сетевой клиент TCP/UDP

### Встроенный сервер (DLL внутри клиента)
- Управление сессиями и пользователями
- Ретрансляция медиа между клиентами

## Безопасность
- Симметричное шифрование с сессионными ключами
- Опциональный пароль для подключения к сессии

## Как использовать через интернет
Чтобы использовать Talknado через глобальную сеть, всем участникам необходимо подключиться к одной и той же виртуальной сети **ZeroTier**:
1. Установите ZeroTier на каждом устройстве.
2. Подключитесь к одной сети ZeroTier, используя её Network ID.
3. Запустите Talknado и создайте или присоединитесь к сессии.
