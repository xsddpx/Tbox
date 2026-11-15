#!/usr/bin/env bash
set -euo pipefail

select_destination() {
  local requested="$1" system_app="$2" user_app="$3"
  if [[ -n "$requested" ]]; then
    if [[ "$requested" != "$system_app" && "$requested" != "$user_app" ]]; then
      echo "安装位置须为 $system_app 或 $user_app。" >&2
      return 1
    fi
    printf '%s\n' "$requested"
  elif [[ ( -e "$system_app" || -L "$system_app" ) && ( -e "$user_app" || -L "$user_app" ) ]]; then
    echo "发现两个 Tbox 安装位置。请通过 --destination 指定同一个安装路径：" >&2
    printf '  %s\n' "$system_app" "$user_app" >&2
    return 1
  elif [[ -e "$system_app" || -L "$system_app" ]]; then
    printf '%s\n' "$system_app"
  else
    printf '%s\n' "$user_app"
  fi
}

main() {
  local ROOT SOURCE DESTINATION ALLOW_MIGRATION="" requested=""
  local IDENTIFIER EXISTING_ID REQUIREMENT BACKUP=""
  local system_app="/Applications/Tbox.app" user_app="$HOME/Applications/Tbox.app"
  ROOT="$(cd "$(dirname "$0")/.." && pwd)"
  SOURCE="$ROOT/TEMP/build/osx-arm64/Tbox.app"
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --destination)
        if [[ $# -lt 2 || -z "$2" || -n "$requested" ]]; then
          echo "请为 --destination 指定一个安装路径。" >&2
          return 1
        fi
        requested="$2"
        shift 2 ;;
      --migrate-signature)
        ALLOW_MIGRATION="$1"
        shift ;;
      *)
        echo "用法：$0 [--destination 路径] [--migrate-signature]" >&2
        return 1 ;;
    esac
  done
  DESTINATION="$(select_destination "$requested" "$system_app" "$user_app")"
  if [[ -L "$DESTINATION" || ( -e "$DESTINATION" && ! -d "$DESTINATION" ) ]]; then
    echo "目标不是普通 Tbox 应用包：$DESTINATION" >&2
    return 1
  fi
  echo "Tbox 安装目标：$DESTINATION"
  [[ -d "$SOURCE" ]] || { echo "先执行 Mac 构建。" >&2; exit 1; }
  bash "$ROOT/scripts/check-macos-identity.sh" "$SOURCE" >/dev/null
  IDENTIFIER="$(/usr/libexec/PlistBuddy -c 'Print CFBundleIdentifier' "$SOURCE/Contents/Info.plist")"
  [[ "$IDENTIFIER" == "com.xsddpx.tbox" ]] || { echo "应用标识发生变化，停止安装。" >&2; exit 1; }
  if [[ -d "$DESTINATION" ]]; then
    EXISTING_ID="$(/usr/libexec/PlistBuddy -c 'Print CFBundleIdentifier' "$DESTINATION/Contents/Info.plist")"
    [[ "$EXISTING_ID" == "$IDENTIFIER" ]] || { echo "目标路径属于其他应用，停止安装。" >&2; exit 1; }
    REQUIREMENT="$(codesign -d -r- "$DESTINATION" 2>&1 | sed -n 's/^#\{0,1\} *designated => //p')"
    # A leading '=' marks literal requirement source; without it codesign treats
    # the expression as a filename and rejects even an identical signing identity.
    if [[ -z "$REQUIREMENT" ]] || ! codesign --verify --strict -R "=$REQUIREMENT" "$SOURCE"; then
      [[ "$ALLOW_MIGRATION" == "--migrate-signature" ]] || {
        echo "新包不满足已安装应用的签名身份，会影响权限。停止覆盖安装；首次迁移需显式指定 --migrate-signature。" >&2; exit 1;
      }
    fi
  fi
  local destination_parent
  destination_parent="$(dirname "$DESTINATION")"
  mkdir -p "$destination_parent" "$ROOT/TEMP/install-backups"
  STAGE="$(mktemp -d "$destination_parent/.tbox-install.XXXXXX")"
  trap 'rm -rf "$STAGE"' EXIT
  ditto "$SOURCE" "$STAGE/Tbox.app"
  codesign --verify --deep --strict "$STAGE/Tbox.app"
  # The quit command contacts the single running instance regardless of bundle path.
  local pid command running_pid=""
  while read -r pid command; do
    case "$command" in
      "$DESTINATION/Contents/MacOS/Tbox.Desktop") running_pid="$pid" ;;
      "$system_app/Contents/MacOS/Tbox.Desktop"|"$user_app/Contents/MacOS/Tbox.Desktop")
        echo "另一位置的 Tbox 正在运行，请退出后再安装：$command" >&2
        return 1 ;;
    esac
  done < <(ps -axo pid=,comm=)
  if [[ -n "$running_pid" ]]; then
    "$DESTINATION/Contents/MacOS/Tbox.Desktop" --quit
    for ((attempt=0; attempt<50; attempt++)); do
      if ! kill -0 "$running_pid" 2>/dev/null; then break; fi
      sleep 0.1
    done
    if kill -0 "$running_pid" 2>/dev/null; then
      echo "Tbox 尚未退出，安装已停止。" >&2
      return 1
    fi
  fi
  if [[ -d "$DESTINATION" ]]; then
    BACKUP="$ROOT/TEMP/install-backups/Tbox-$(date +%Y%m%d-%H%M%S)-$$.app"
    mv "$DESTINATION" "$BACKUP"
  fi
  if ! mv "$STAGE/Tbox.app" "$DESTINATION"; then
    if [[ -n "${BACKUP:-}" ]]; then mv "$BACKUP" "$DESTINATION"; fi
    return 1
  fi
  if ! codesign --verify --deep --strict "$DESTINATION"; then
    mv "$DESTINATION" "$STAGE/Tbox.app"
    if [[ -n "${BACKUP:-}" ]]; then mv "$BACKUP" "$DESTINATION"; fi
    return 1
  fi
  echo "$DESTINATION"
}
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  main "$@"
fi
