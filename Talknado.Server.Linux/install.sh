#!/bin/bash

set -e

if [ "$EUID" -ne 0 ]; then
    echo "Error: Root access required"
    exit 1
fi

# Сервер лежит в каждом релизе Talknado архивом с версией сервера в имени:
# talknado-server-v<версия>-linux-x64.tar.gz. Без аргумента ставится сервер
# из последнего релиза, с аргументом — из самого нового релиза, где лежит
# сервер этой версии. Серверы 1.1.x живут в прежнем отдельном репозитории,
# там тег релиза совпадает с версией сервера. --archive — из готового архива.
GITHUB_REPO="SodiumCXI/Talknado"
LEGACY_REPO="SodiumCXI/talknado-server-linux"
INSTALL_DIR="/usr/local/bin"
UNIT_FILE="/etc/systemd/system/talknado.service"
CONFIG_DIR="/etc/talknado"
CONFIG_FILE="$CONFIG_DIR/talknado.conf"

TEMP_DIR=$(mktemp -d)
trap 'rm -rf "$TEMP_DIR"' EXIT

download_release() {
    local version="${1:-latest}"
    version="${version#v}"
    local url

    if [ "$version" = "latest" ]; then
        echo "Fetching latest release..."
        url=$(curl -fsSL "https://api.github.com/repos/$GITHUB_REPO/releases/latest" \
            | grep -o 'https://[^"]*/talknado-server-v[0-9.]*-linux-x64\.tar\.gz' | head -n 1)
    else
        echo "Looking for server v$version..."
        url=$(curl -fsSL "https://api.github.com/repos/$GITHUB_REPO/releases?per_page=100" \
            | grep -o "https://[^\"]*/talknado-server-v${version//./\\.}-linux-x64\.tar\.gz" | head -n 1)
    fi

    if [ -n "$url" ]; then
        echo "Downloading $(basename "$url")"
        curl -fsSL "$url" -o "$TEMP_DIR/server.tar.gz" || {
            echo "Error: Failed to download the server"
            exit 1
        }
        return 0
    fi

    if [ "$version" = "latest" ]; then
        echo "Error: Could not find the server in the latest release"
        exit 1
    fi

    echo "Not found in Talknado releases, trying $LEGACY_REPO..."
    local base="https://github.com/$LEGACY_REPO/releases/download/v$version"
    curl -fsSL "$base/talknado" -o "$TEMP_DIR/talknado" || {
        echo "Error: Server v$version not found"
        exit 1
    }
    curl -fsSL "$base/talknado-server-bin" -o "$TEMP_DIR/talknado-server-bin" || {
        echo "Error: Failed to download binary"
        exit 1
    }
}

# До 1.2.1 номер процесса хранился в ~/.talknado; после перезагрузки под ним
# может быть чужой процесс, поэтому номер сверяется с именем.
LEGACY_WAS_RUNNING=false
migrate_legacy() {
    local source=""

    for dir in /root/.talknado /home/*/.talknado; do
        [ -d "$dir" ] || continue

        if [ -f "$dir/talknado.pid" ]; then
            local pid
            pid=$(cat "$dir/talknado.pid")
            if [ -n "$pid" ] && grep -qa talknado-server-bin "/proc/$pid/cmdline" 2>/dev/null; then
                echo "Stopping the server started by the previous version (PID: $pid)"
                kill "$pid" 2>/dev/null || true
                for i in $(seq 10); do
                    kill -0 "$pid" 2>/dev/null || break
                    sleep 1
                done
                kill -9 "$pid" 2>/dev/null || true
                LEGACY_WAS_RUNNING=true
            fi
            rm -f "$dir/talknado.pid" "$dir/control.sock" "$dir/connection.key"
        fi

        if [ -z "$source" ] && [ -f "$dir/password" ]; then
            source="$dir"
        fi
    done

    if [ -f "$CONFIG_FILE" ] || [ -z "$source" ]; then
        return 0
    fi

    local password quality bandwidth
    password=$(cat "$source/password")
    quality=$(grep '^quality=' "$source/settings" 2>/dev/null | tail -n 1 | cut -d '=' -f 2-)
    bandwidth=$(grep '^bandwidth=' "$source/settings" 2>/dev/null | tail -n 1 | cut -d '=' -f 2-)

    mkdir -p "$CONFIG_DIR"
    chmod 700 "$CONFIG_DIR"
    {
        if [[ "$password" == *"'"* ]]; then
            echo "Warning: the old password contains a single quote, set a new one with 'talknado set-password'" >&2
        else
            echo "TALKNADO_PASSWORD='$password'"
        fi
        echo "TALKNADO_QUALITY='${quality:-1080p60}'"
        echo "TALKNADO_BANDWIDTH='${bandwidth:-0}'"
    } > "$CONFIG_FILE"
    chmod 600 "$CONFIG_FILE"
    echo "Moved password and settings from $source to $CONFIG_FILE"
}

install_service() {
    if ! command -v systemctl > /dev/null || [ ! -d /run/systemd/system ]; then
        echo "Error: systemd is required"
        exit 1
    fi

    migrate_legacy

    local was_running=false
    systemctl is-active --quiet talknado && was_running=true

    # install заменяет файл, а не пишет поверх: так обновляется и запущенный сервер.
    install -m 755 "$TEMP_DIR/talknado" "$TEMP_DIR/talknado-server-bin" "$INSTALL_DIR/"
    install -m 644 "$TEMP_DIR/talknado.service" "$UNIT_FILE"

    mkdir -p "$CONFIG_DIR"
    chmod 700 "$CONFIG_DIR"
    if [ ! -f "$CONFIG_FILE" ]; then
        printf "TALKNADO_QUALITY='1080p60'\nTALKNADO_BANDWIDTH='0'\n" > "$CONFIG_FILE"
        chmod 600 "$CONFIG_FILE"
    fi

    systemctl daemon-reload

    if $was_running || $LEGACY_WAS_RUNNING; then
        if grep -q '^TALKNADO_PASSWORD=' "$CONFIG_FILE"; then
            "$INSTALL_DIR/talknado" restart
        else
            echo "Set a password with 'talknado set-password', then run 'talknado start'"
        fi
    elif ! grep -q '^TALKNADO_PASSWORD=' "$CONFIG_FILE"; then
        echo "Next: talknado set-password 'password', then talknado start"
    fi
}

# Серверы до 1.2.1 запускаются скриптом сами, служба им мешала бы.
install_plain() {
    if [ -f "$UNIT_FILE" ]; then
        echo "This server version runs without systemd, removing the service"
        systemctl disable --now --quiet talknado 2>/dev/null || true
        rm -f "$UNIT_FILE"
        systemctl daemon-reload
        echo "Set the password again with 'talknado set-password': this version keeps it in ~/.talknado"
    fi

    install -m 755 "$TEMP_DIR/talknado" "$TEMP_DIR/talknado-server-bin" "$INSTALL_DIR/"
}

echo "Installing Talknado Server..."

if [ "$1" = "--archive" ]; then
    cp "$2" "$TEMP_DIR/server.tar.gz"
else
    download_release "$1"
fi

if [ -f "$TEMP_DIR/server.tar.gz" ]; then
    tar -xzf "$TEMP_DIR/server.tar.gz" -C "$TEMP_DIR"
fi

if [ -f "$TEMP_DIR/talknado.service" ]; then
    install_service
else
    install_plain
fi

echo "Done!"

"$INSTALL_DIR/talknado" version || true
