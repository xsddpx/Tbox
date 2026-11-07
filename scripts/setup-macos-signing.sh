#!/usr/bin/env bash
set -euo pipefail
# One-time local identity setup. Subsequent builds must reuse it, never silently replace it.
NAME="Tbox Local Development"
KEYCHAIN="$(security default-keychain -d user | tr -d '"' | sed 's/^ *//;s/ *$//')"
if security find-certificate -c "$NAME" "$KEYCHAIN" >/dev/null 2>&1; then
  echo "已有 Tbox 本地开发证书；保留当前身份，不重新生成。"
  exit 0
fi
TBOX_SIGN_TMP="$(mktemp -d "${TMPDIR:-/tmp}/tbox-signing.XXXXXX")"
chmod 700 "$TBOX_SIGN_TMP"
trap 'rm -rf "$TBOX_SIGN_TMP"' EXIT
cat > "$TBOX_SIGN_TMP/openssl.cnf" <<'EOF'
[req]
distinguished_name = subject
prompt = no
x509_extensions = codesign
[subject]
CN = Tbox Local Development
O = Tbox
OU = Local Development
[codesign]
basicConstraints = critical,CA:FALSE
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid
EOF
/usr/bin/openssl req -new -newkey rsa:2048 -nodes -x509 -sha256 -days 3650 \
  -config "$TBOX_SIGN_TMP/openssl.cnf" -keyout "$TBOX_SIGN_TMP/key.pem" -out "$TBOX_SIGN_TMP/cert.pem" 2>/dev/null
TBOX_CERT_PASSWORD="$(/usr/bin/openssl rand -hex 32)"
export TBOX_CERT_PASSWORD
/usr/bin/openssl pkcs12 -export -inkey "$TBOX_SIGN_TMP/key.pem" -in "$TBOX_SIGN_TMP/cert.pem" \
  -name "$NAME" -out "$TBOX_SIGN_TMP/identity.p12" -passout env:TBOX_CERT_PASSWORD
security import "$TBOX_SIGN_TMP/identity.p12" -k "$KEYCHAIN" -P "$TBOX_CERT_PASSWORD" -T /usr/bin/codesign
unset TBOX_CERT_PASSWORD
echo "Tbox 本地开发签名身份已存入登录钥匙串；私钥临时文件已清理。不改变系统根证书信任。"
