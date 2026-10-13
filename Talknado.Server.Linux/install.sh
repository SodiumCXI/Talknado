#!/bin/bash

set -e

if [ "$EUID" -ne 0 ]; then
    echo "Error: Root access required"
    exit 1
fi

# Сервер лежит в каждом релизе Talknado архивом с версией сервера в имени:
# talknado-server-<версия>-linux-x64.tar.gz. Без аргумента ставится сервер
# из последнего релиза, с аргументом — из самого нового релиза, где лежит
# сервер этой версии. Серверы 1.1.x живут в прежнем отдельном репозитории,
# там тег релиза совпадает с версией сервера.
GITHUB_REPO="SodiumCXI/Talknado"
LEGACY_REPO="SodiumCXI/talknado-server-linux"
INSTALL_DIR="/usr/local/bin"
VERSION="${1:-latest}"
VERSION="${VERSION#v}"

echo "Installing Talknado Server..."

if [ "$VERSION" = "latest" ]; then
    echo "Fetching latest release..."
    PACKAGE_URL=$(curl -fsSL "https://api.github.com/repos/$GITHUB_REPO/releases/latest" \
        | grep -o 'https://[^"]*/talknado-server-[0-9.]*-linux-x64\.tar\.gz' | head -n 1)
else
    echo "Looking for server v$VERSION..."
    PACKAGE_URL=$(curl -fsSL "https://api.github.com/repos/$GITHUB_REPO/releases?per_page=100" \
        | grep -o "https://[^\"]*/talknado-server-${VERSION//./\\.}-linux-x64\.tar\.gz" | head -n 1)
fi

if [ -n "$PACKAGE_URL" ]; then
    echo "Downloading $(basename "$PACKAGE_URL")"
    TEMP_DIR=$(mktemp -d)
    trap 'rm -rf "$TEMP_DIR"' EXIT

    curl -fsSL "$PACKAGE_URL" -o "$TEMP_DIR/server.tar.gz" || {
        echo "Error: Failed to download the server"
        exit 1
    }
    tar -xzf "$TEMP_DIR/server.tar.gz" -C "$TEMP_DIR"

    # install заменяет файл, а не пишет поверх: так обновляется и запущенный сервер.
    install -m 755 "$TEMP_DIR/talknado" "$TEMP_DIR/talknado-server-bin" "$INSTALL_DIR/"
elif [ "$VERSION" != "latest" ]; then
    echo "Not found in Talknado releases, trying $LEGACY_REPO..."
    BASE_URL="https://github.com/$LEGACY_REPO/releases/download/v$VERSION"

    curl -fsSL "$BASE_URL/talknado" -o "$INSTALL_DIR/talknado" || {
        echo "Error: Server v$VERSION not found"
        exit 1
    }

    curl -fsSL "$BASE_URL/talknado-server-bin" -o "$INSTALL_DIR/talknado-server-bin" || {
        rm -f "$INSTALL_DIR/talknado"
        echo "Error: Failed to download binary"
        exit 1
    }

    chmod +x "$INSTALL_DIR/talknado" "$INSTALL_DIR/talknado-server-bin"
else
    echo "Error: Could not find the server in the latest release"
    exit 1
fi

echo "Done!"

"$INSTALL_DIR/talknado" version || true
