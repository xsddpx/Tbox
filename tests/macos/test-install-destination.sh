#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
source "$ROOT/scripts/install-macos.sh"

fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT
system_app="$fixture/Applications/Tbox.app"
user_app="$fixture/User Home/Applications/Tbox.app"

[[ "$(select_destination '' "$system_app" "$user_app")" == "$user_app" ]]
mkdir -p "$system_app"
[[ "$(select_destination '' "$system_app" "$user_app")" == "$system_app" ]]
mkdir -p "$user_app"
if select_destination '' "$system_app" "$user_app" >"$fixture/destination" 2>"$fixture/error"; then
  echo "存在两个副本时应要求指定路径。" >&2
  exit 1
fi
[[ ! -s "$fixture/destination" ]]
grep -F -- '--destination' "$fixture/error" >/dev/null
[[ "$(select_destination "$system_app" "$system_app" "$user_app")" == "$system_app" ]]
[[ "$(select_destination "$user_app" "$system_app" "$user_app")" == "$user_app" ]]
if select_destination "$fixture/Other.app" "$system_app" "$user_app" 2>"$fixture/unsupported"; then
  echo "应拒绝未支持的安装路径。" >&2
  exit 1
fi
rmdir "$system_app"
[[ "$(select_destination '' "$system_app" "$user_app")" == "$user_app" ]]
echo "Mac 安装路径检查通过。"
