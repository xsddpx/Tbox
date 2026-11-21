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
grep -F -- "$user_app" "$fixture/unsupported" >/dev/null
rmdir "$system_app"
[[ "$(select_destination '' "$system_app" "$user_app")" == "$user_app" ]]
macos_version_supported 26.0 26.0
macos_version_supported 26.0 26.2.1
macos_version_supported 26.0.0 26.0
if macos_version_supported 26.0 15.7.2 || macos_version_supported invalid 26.2.1; then
  echo "应拒绝最低系统版本无效或不满足要求的安装包。" >&2
  exit 1
fi
echo "Mac 安装路径与系统版本检查通过。"
