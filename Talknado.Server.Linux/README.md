# Talknado Server for Linux

Dedicated server with the same session handling and media relay as the server embedded in the desktop app. Self-contained linux-x64 binary, built from the same `Talknado.Server` code as the embedded server, so the two never drift apart.

## Version Compatibility

| Server Version | Supported Client Versions      |
|----------------|--------------------------------|
| v1.1.2         | v1.2.4, v1.2.5, v1.3.0, v1.3.1 |
| v1.1.3         | v1.3.2, v1.3.3, v1.3.4         |
| v1.1.4         | v1.3.5, v1.3.6, v1.4.0, v1.4.1 |
| v1.1.5         | v1.5.0                         |
| v1.1.6         | v1.5.1, v1.5.2, v1.5.3, v1.5.4 |
| v1.2.0         | v2.0.0 and newer               |

## Installation

### Install the latest version

```bash
curl -fsSL https://raw.githubusercontent.com/SodiumCXI/Talknado/main/Talknado.Server.Linux/install.sh | bash
```

### Install a specific version

Replace **vX.X.X** with the desired server version:

```bash
curl -fsSL https://raw.githubusercontent.com/SodiumCXI/Talknado/main/Talknado.Server.Linux/install.sh | bash -s vX.X.X
```

## Usage

```bash
talknado set-password 'password'   # required before the first start
talknado start                     # prints the connection key
talknado status
talknado stop
```

### Session settings

The same settings as the host panel in the desktop app. Quality and bandwidth are saved in `~/.talknado/settings` and apply right away if the server is running.

```bash
talknado users                 # connected users: ID, ping, address, name
talknado kick 3                # kick a user and ban their address until restart
talknado quality 1080p60       # screen share quality cap: 720p30, 1080p30, 1080p60
talknado bandwidth 25          # file transfer limit in Mbit/s, 0 = unlimited
```

Server output goes to `~/.talknado/server.log`.

## Releasing

Releases are built by GitHub Actions ([release.yml](/.github/workflows/release.yml)) when a release with a `vX.Y.Z` tag is published on GitHub. One release for everything, tagged with the client version: the portable client, the installer and `talknado-server-<server version>-linux-x64.tar.gz`, even if the server did not change. The files are attached to that release a few minutes after publishing; the server is started on a Linux runner first.

1. Raise `<Version>` in `Talknado.Client/Talknado.Client.csproj` - the tag must match it.
2. If the server changed, raise `<Version>` in `Talknado.Server/Talknado.Server.csproj`; raise `MinClientVersion` in `Talknado.Server/Core/ServerInfo.cs` only if the change breaks older clients.
3. Create and publish a release with the tag `vX.Y.Z` on GitHub; the files are attached to it automatically.

To build the server archive locally:

```bash
dotnet publish Talknado.Server.Linux -c Release -r linux-x64
```
