#!/usr/bin/env bash
# Instalador do Sond Client para Linux.
#
#   curl -fsSL {{BASE_URL}}/linux | bash
#
# Baixa o launcher do manifesto assinado do Linux, confere a assinatura (mesma chave do
# Windows) e o hash de cada arquivo, instala em ~/.local/share/SondClient/app e cria o atalho
# no menu de aplicativos. Não precisa de root. Rodar de novo atualiza/repara a instalação.
set -euo pipefail

BASE="{{BASE_URL}}"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
APP_DIR="$DATA/SondClient/app"
DESKTOP_FILE="$DATA/applications/sondclient.desktop"
ICON_FILE="$DATA/icons/hicolor/256x256/apps/sondclient.png"
BIN_LINK="$HOME/.local/bin/sondclient"

# Chave pública que assina as atualizações (a mesma embutida no launcher).
PUBKEY='-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEEXiQekHPrS5e1RRaAQQE9GYYnY3J
SAVIirhvm/FT4u2tBM0KFRXpHDIj6UBZ7MNgjMJIKlEOgWmGMUbpQgdjyA==
-----END PUBLIC KEY-----'

say() { printf '\033[1;35m▸\033[0m %s\n' "$*"; }
fail() { printf '\033[1;31m✗ %s\033[0m\n' "$*" >&2; exit 1; }

[ "$(uname -s)" = "Linux" ] || fail "Este instalador é para Linux."
case "$(uname -m)" in
    x86_64|amd64) ;;
    *) fail "Por enquanto o Sond Client para Linux é só x86_64 (seu sistema: $(uname -m))." ;;
esac

for tool in openssl base64 sha256sum od sed; do
    command -v "$tool" >/dev/null 2>&1 || fail "Falta o programa '$tool'. Instale e rode de novo."
done
if command -v curl >/dev/null 2>&1; then
    fetch() { curl -fsSL --retry 3 -o "$2" "$1"; }
elif command -v wget >/dev/null 2>&1; then
    fetch() { wget -q -O "$2" "$1"; }
else
    fail "Instale o curl (ou wget) e rode de novo."
fi

WORK="$(mktemp -d)"
trap 'rm -rf -- "$WORK"' EXIT

say "Baixando a lista de arquivos do Sond Client..."
fetch "$BASE/updates/linux/manifest.json" "$WORK/envelope.json" \
    || fail "Nenhuma versão para Linux publicada ainda (ou sem internet)."

# O envelope vem do DataContractJsonSerializer: '/' sai como '\/' e '+' como '+'.
field() {
    sed -e 's#[\]/#/#g' -e 's#[\]u002[Bb]#+#g' "$1" | sed -n "s/.*\"$2\":\"\([^\"]*\)\".*/\1/p"
}
field "$WORK/envelope.json" manifest | base64 -d > "$WORK/manifest.json"
field "$WORK/envelope.json" signature | base64 -d > "$WORK/sig.raw"

# Assinatura ECDSA P-256 no formato do .NET (r||s, 64 bytes) -> DER para o openssl.
SIG="$(od -An -v -tx1 < "$WORK/sig.raw" | tr -d ' \n')"
[ "${#SIG}" -eq 128 ] || fail "Assinatura da atualização inválida."
printf 'asn1=SEQUENCE:sig\n[sig]\nr=INTEGER:0x%s\ns=INTEGER:0x%s\n' "${SIG:0:64}" "${SIG:64:64}" > "$WORK/sig.cnf"
openssl asn1parse -genconf "$WORK/sig.cnf" -out "$WORK/sig.der" -noout >/dev/null
printf '%s\n' "$PUBKEY" > "$WORK/pub.pem"
openssl dgst -sha256 -verify "$WORK/pub.pem" -signature "$WORK/sig.der" "$WORK/manifest.json" >/dev/null 2>&1 \
    || fail "Assinatura da atualização NÃO confere — instalação cancelada por segurança."

VERSION="$(sed -n 's/.*"version":"\([^"]*\)".*/\1/p' "$WORK/manifest.json")"
say "Versão $VERSION (assinatura conferida)."

mkdir -p "$APP_DIR"
# Um arquivo por linha: caminho<TAB>sha256
sed -e 's#[\]/#/#g' "$WORK/manifest.json" \
    | grep -o '"path":"[^"]*","sha256":"[0-9a-f]\{64\}"' \
    | sed 's/"path":"\([^"]*\)","sha256":"\([0-9a-f]*\)"/\1\t\2/' > "$WORK/files.tsv"
[ -s "$WORK/files.tsv" ] || fail "Lista de arquivos vazia."

while IFS="$(printf '\t')" read -r path sha; do
    case "$path" in
        /*|*..*) fail "Caminho inválido no manifesto: $path" ;;
    esac
    dest="$APP_DIR/$path"
    if [ -f "$dest" ] && [ "$(sha256sum "$dest" | cut -d' ' -f1)" = "$sha" ]; then
        continue
    fi
    name="$(basename "$path")"
    say "Baixando $path..."
    fetch "$BASE/updates/files/$sha/$name" "$WORK/download"
    [ "$(sha256sum "$WORK/download" | cut -d' ' -f1)" = "$sha" ] || fail "Arquivo corrompido: $path"
    mkdir -p "$(dirname "$dest")"
    mv -f "$WORK/download" "$dest"
done < "$WORK/files.tsv"

chmod +x "$APP_DIR/SondClient"
# Marca de "instalado pelo instalador": com ela o launcher se atualiza sozinho.
touch "$APP_DIR/.sond-install"

say "Criando o atalho no menu..."
mkdir -p "$(dirname "$ICON_FILE")" "$(dirname "$DESKTOP_FILE")" "$(dirname "$BIN_LINK")"
fetch "$BASE/linux/icon.png" "$ICON_FILE" || true
cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=Sond Client
Comment=Launcher de Minecraft com mods e cosméticos
Exec="$APP_DIR/SondClient"
Icon=sondclient
Terminal=false
Categories=Game;
StartupWMClass=SondClient
EOF
ln -sf "$APP_DIR/SondClient" "$BIN_LINK"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$(dirname "$DESKTOP_FILE")" >/dev/null 2>&1 || true

printf '\n\033[1;32m✓ Sond Client %s instalado!\033[0m\n' "$VERSION"
printf '  Abra pelo menu de aplicativos ("Sond Client") ou rode: sondclient\n'
printf '  O launcher se atualiza sozinho daqui pra frente.\n'
