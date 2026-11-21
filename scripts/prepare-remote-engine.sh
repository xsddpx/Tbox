#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLIENT=95cca3f661f635b3cc85c04935fe8d96d8f432ef
PROTOCOL=fae47e024dd83cef6aaecde0aa7d11731df11649
mkdir -p "$ROOT/TEMP"
DESTINATION="$(mktemp -d "$ROOT/TEMP/rustdesk-client.XXXXXX")"

git -C "$DESTINATION" init --quiet
git -C "$DESTINATION" fetch --depth 1 https://github.com/rustdesk/rustdesk.git "$CLIENT"
git -C "$DESTINATION" checkout --quiet --detach "$CLIENT"
git -C "$DESTINATION" submodule update --init --depth 1 libs/hbb_common >&2
if [[ "$(git -C "$DESTINATION/libs/hbb_common" rev-parse HEAD)" != "$PROTOCOL" ]]; then
    echo "RustDesk protocol revision does not match the Tbox pin." >&2
    exit 1
fi

SOURCE="$ROOT/src/Tbox.Remote"
git -C "$DESTINATION" apply --check "$SOURCE/rustdesk/rustdesk-tbox.patch"
git -C "$DESTINATION" apply "$SOURCE/rustdesk/rustdesk-tbox.patch"
cp "$SOURCE/rustdesk/"*.rs "$DESTINATION/src/"
mkdir -p "$DESTINATION/libs/tbox-remote/src"
cp "$SOURCE/Cargo.toml" "$DESTINATION/libs/tbox-remote/"
cp "$SOURCE"/src/*.rs "$DESTINATION/libs/tbox-remote/src/"
printf '%s\n' "$DESTINATION"
