#!/usr/bin/env bash
# Import only the configured persistent identity into an ephemeral hosted runner.
set -euo pipefail
[[ "${GITHUB_ACTIONS:-}" == true && "${RUNNER_ENVIRONMENT:-}" == github-hosted ]] || { echo 'This wrapper only runs on disposable GitHub-hosted runners.' >&2; exit 1; }
: "${TBOX_MAC_P12_BASE64:?Missing Mac signing certificate}"
: "${TBOX_MAC_P12_PASSWORD:?Missing Mac certificate password}"
: "${TBOX_MAC_CERT_SHA1:?Missing expected Mac certificate fingerprint}"
export TBOX_SIGN_IDENTITY="${TBOX_MAC_SIGN_IDENTITY:-Tbox Local Development}"
[[ "$TBOX_MAC_CERT_SHA1" =~ ^[A-Fa-f0-9]{40}$ ]] || { echo 'Invalid certificate fingerprint.' >&2; exit 1; }
KEYCHAIN="$RUNNER_TEMP/tbox-signing.keychain-db"
P12="$RUNNER_TEMP/tbox-signing.p12"
KEYCHAIN_PASSWORD="$(openssl rand -hex 32)"
cleanup() {
  rm -f "$P12"
  security delete-keychain "$KEYCHAIN" >/dev/null 2>&1 || true
}
trap cleanup EXIT
umask 077
python3 - <<'PY'
import base64, os, pathlib
pathlib.Path(os.environ['RUNNER_TEMP'], 'tbox-signing.p12').write_bytes(base64.b64decode(os.environ['TBOX_MAC_P12_BASE64'], validate=True))
PY
security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security set-keychain-settings -lut 21600 "$KEYCHAIN"
security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security import "$P12" -P "$TBOX_MAC_P12_PASSWORD" -k "$KEYCHAIN" -T /usr/bin/codesign >/dev/null
rm -f "$P12"
unset TBOX_MAC_P12_BASE64 TBOX_MAC_P12_PASSWORD
security list-keychains -d user -s "$KEYCHAIN" login.keychain-db
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KEYCHAIN_PASSWORD" "$KEYCHAIN" >/dev/null
security find-identity -p codesigning "$KEYCHAIN" | grep -Fqi "$TBOX_MAC_CERT_SHA1" || { echo 'Imported identity differs from the persistent signing certificate.' >&2; exit 1; }
bash scripts/package-macos.sh osx-arm64
# Check the actual bundle signer as well as the imported identity.
CERT_DIR="$(mktemp -d "$RUNNER_TEMP/tbox-signer.XXXXXX")"
codesign -d --extract-certificates "$CERT_DIR/cert" TEMP/build/osx-arm64/Tbox.app
ACTUAL="$(openssl x509 -inform DER -in "$CERT_DIR/cert0" -noout -fingerprint -sha1 | cut -d= -f2 | tr -d ':')"
[[ "$(printf '%s' "$ACTUAL" | tr 'a-f' 'A-F')" == "$(printf '%s' "$TBOX_MAC_CERT_SHA1" | tr 'a-f' 'A-F')" ]] || { echo 'Bundle signer mismatch.' >&2; exit 1; }
