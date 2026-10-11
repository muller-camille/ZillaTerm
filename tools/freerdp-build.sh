#!/usr/bin/env bash
# Client Bureau à distance de ZillaTerm : wfreerdp (client Windows de FreeRDP), compilé depuis le fork
# muller-camille/FreeRDP au commit figé ci-dessous (vérifié après le téléchargement) : FreeRDP 3.32.1 et les correctifs
# de ZillaTerm (branche zillaterm-3.32.1). Lié statiquement à OpenSSL (version figée, archive vérifiée par sa somme
# SHA-256). Un seul exécutable, sans DLL à côté.
#
# Correctifs (commits de la branche) : application distante (RemoteApp) qui répond à l'échange initial du serveur, et
# mode ZillaTerm : réglages de la connexion lus sur l'entrée standard (jamais sur la ligne de commande
# ni sur le disque), certificats vérifiés par ZillaTerm, jamais l'ancienne sécurité RDP sans TLS, reconnexion
# automatique interrompue dès la fermeture de l'onglet, aucune fenêtre de Windows (identifiants, certificats,
# messages de passerelle), identifiants enregistrés de Windows jamais lus, applications distantes (RemoteApp)
# affichées dans la fenêtre de la session quand elle est dans ZillaTerm, aucune bibliothèque chargée hors de
# System32, sensibilité aux DPI de la fenêtre parente.
#
# Compilation croisée depuis Linux (mingw-w64, cmake, ninja, perl, make) : la CI le lance sur ubuntu-latest et met le
# résultat en cache ; ZillaTerm l'embarque s'il est présent dans local-packages/freerdp/ au moment de la compilation.
set -euo pipefail

freerdp_repository=https://github.com/muller-camille/FreeRDP
freerdp_version=3.32.1
freerdp_commit=cfc0d6a988f506b8bc95fcc925f00e532a985e67
openssl_version=3.5.9
openssl_sha256=603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a
openssl_url=https://github.com/openssl/openssl/releases/download/openssl-$openssl_version/openssl-$openssl_version.tar.gz

root=$(cd "$(dirname "$0")/.." && pwd)
tools="$root/tools/freerdp"
out="$root/local-packages/freerdp"

# Clé de la compilation : versions et commit figés (dans ce script) et chaîne de compilation. Même clé : rien à refaire.
key=$(cat "$0" "$tools/mingw64.cmake" | sha256sum | cut -c1-16)
if [ -f "$out/wfreerdp.exe" ] && [ "$(cat "$out/BUILD-KEY" 2>/dev/null)" = "$key" ]; then
  echo "FreeRDP $freerdp_version (ZillaTerm $key) : déjà dans local-packages/freerdp."
  exit 0
fi

for tool in x86_64-w64-mingw32-gcc-posix x86_64-w64-mingw32-objdump cmake ninja perl make git curl sha256sum; do
  command -v "$tool" >/dev/null || { echo "Outil manquant : $tool (paquets mingw-w64, cmake, ninja-build)" >&2; exit 1; }
done

work=${ZILLATERM_FREERDP_WORK:-}
if [ -z "$work" ]; then
  work=$(mktemp -d)
  trap 'rm -rf "$work"' EXIT
fi
mkdir -p "$work/dl"
jobs=$(nproc 2>/dev/null || echo 2)

# ---- OpenSSL, statique : ni module, ni moteur, ni DSO (rien n'est chargé à l'exécution), pas de fichier de
# configuration lu automatiquement. Les dossiers compilés dans la bibliothèque sont ceux d'OpenSSL sous Windows,
# réservés aux administrateurs (et non des chemins Linux qu'un utilisateur pourrait créer sur C:).
archive="$work/dl/openssl-$openssl_version.tar.gz"
if [ ! -f "$archive" ] || ! echo "$openssl_sha256  $archive" | sha256sum -c --status; then
  curl -sSfL -o "$archive" "$openssl_url"
fi
echo "$openssl_sha256  $archive" | sha256sum -c
ssl_prefix="$work/openssl-install"
if [ ! -f "$ssl_prefix/lib/libcrypto.a" ]; then
  rm -rf "$work/openssl"
  mkdir -p "$work/openssl"
  tar xf "$archive" --strip-components=1 -C "$work/openssl"
  (
    cd "$work/openssl"
    CC=gcc-posix ./Configure mingw64 no-shared no-module no-dso no-engine no-autoload-config no-tests no-docs \
      no-apps no-ui-console --cross-compile-prefix=x86_64-w64-mingw32- --prefix="$ssl_prefix" --libdir=lib
    make -j"$jobs" build_libs \
      "OPENSSLDIR=C:/Program Files/Common Files/SSL" \
      "ENGINESDIR=C:/Program Files/OpenSSL/lib/engines-3" \
      "MODULESDIR=C:/Program Files/OpenSSL/lib/ossl-modules"
    make install_dev
  )
fi
if strings "$ssl_prefix/lib/libcrypto.a" | grep -q "$work"; then
  echo "OpenSSL : un chemin de compilation est resté dans la bibliothèque." >&2
  exit 1
fi

# ---- FreeRDP et les correctifs de ZillaTerm, au commit figé (vérifié).
src="$work/freerdp"
rm -rf "$src"
git init --quiet "$src"
git -C "$src" fetch --quiet --depth 1 "$freerdp_repository" "$freerdp_commit"
git -C "$src" checkout --quiet --detach FETCH_HEAD
test "$(git -C "$src" rev-parse HEAD)" = "$freerdp_commit"

build="$work/freerdp-build"
rm -rf "$build"
cmake -S "$src" -B "$build" -G Ninja \
  -DCMAKE_TOOLCHAIN_FILE="$tools/mingw64.cmake" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_EXE_LINKER_FLAGS="-static -static-libgcc" \
  -DOPENSSL_ROOT_DIR="$ssl_prefix" -DOPENSSL_USE_STATIC_LIBS=ON \
  -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTING=OFF -DBUILD_TESTING_INTERNAL=OFF \
  -DWITH_CLIENT_WINDOWS=ON -DWITH_CLIENT_SDL=OFF -DWITH_CLIENT_SDL2=OFF -DWITH_CLIENT_SDL3=OFF \
  -DWITH_SERVER=OFF -DWITH_SHADOW=OFF -DWITH_PROXY=OFF -DWITH_SAMPLE=OFF -DWITH_PLATFORM_SERVER=OFF \
  -DWITH_WINPR_TOOLS=OFF -DWITH_MANPAGES=OFF -DWITH_CCACHE=OFF -DWITH_CLANG_FORMAT=OFF -DUSE_UNWIND=OFF \
  -DWITH_FFMPEG=OFF -DWITH_SWSCALE=OFF -DWITH_DSP_FFMPEG=OFF -DWITH_VIDEO_FFMPEG=OFF -DWITH_CAIRO=OFF \
  -DWITH_JPEG=OFF -DWITH_OPUS=OFF -DWITH_FDK_AAC=OFF -DWITH_LAME=OFF -DWITH_FAAD2=OFF -DWITH_FAAC=OFF \
  -DWITH_SOXR=OFF -DWITH_OPENH264=OFF -DWITH_WEBVIEW=OFF \
  -DWITH_JSON_DISABLED=ON -DWITH_AAD=OFF -DWITH_SMARTCARD_EMULATE=OFF -DWITH_PKCS11=OFF \
  -DCHANNEL_URBDRC=OFF -DWITH_FREERDP_DEPRECATED_COMMANDLINE=OFF \
  -DWITH_INTERNAL_RC4=ON -DWITH_INTERNAL_MD4=ON -DWITH_INTERNAL_MD5=ON -DWITH_VERBOSE_WINPR_ASSERT=OFF
cmake --build "$build" --target wfreerdp -j "$jobs"

exe=$(find "$build" -name wfreerdp.exe -type f | head -n 1)
test -n "$exe"
x86_64-w64-mingw32-strip "$exe"

# Rien d'autre que des DLL de Windows : aucune bibliothèque ne doit être cherchée à côté de l'exécutable.
x86_64-w64-mingw32-objdump -p "$exe" | sed -n 's/^\s*DLL Name: //p' | sort -u > "$work/imports.txt"
cat "$work/imports.txt"
if grep -viE '^(advapi32|bcrypt|comctl32|comdlg32|crypt32|credui|dbghelp|gdi32|imm32|iphlpapi|kernel32|msimg32|msvcrt|ncrypt|ntdll|ole32|oleaut32|rpcrt4|secur32|setupapi|shell32|shlwapi|user32|userenv|uuid|version|winmm|winscard|winspool\.drv|ws2_32|wtsapi32|dwmapi|uxtheme|mfplat|mfuuid|mf|api-ms-win-[a-z0-9-]+)\.dll$|^winspool\.drv$' "$work/imports.txt"; then
  echo "wfreerdp.exe dépend d'une DLL qui n'est pas de Windows (ci-dessus)." >&2
  exit 1
fi

rm -rf "$out"
mkdir -p "$out"
cp "$exe" "$out/wfreerdp.exe"
cp "$src/LICENSE" "$out/LICENSE.FreeRDP.txt"
cp "$work/openssl/LICENSE.txt" "$out/LICENSE.OpenSSL.txt" 2>/dev/null || tar xOf "$archive" "openssl-$openssl_version/LICENSE.txt" > "$out/LICENSE.OpenSSL.txt"
# Bibliothèques d'exécution de mingw-w64 liées statiquement (winpthreads…) : leurs licences.
cp /usr/share/doc/mingw-w64-x86-64-dev/copyright "$out/LICENSE.mingw-w64.txt"
{
  echo "FreeRDP $freerdp_version + correctifs de ZillaTerm ($freerdp_repository, commit $freerdp_commit)"
  echo "OpenSSL $openssl_version ($openssl_url, SHA-256 $openssl_sha256)"
  echo "wfreerdp.exe SHA-256 $(sha256sum "$out/wfreerdp.exe" | cut -d' ' -f1)"
} > "$out/BUILD.txt"
echo "$key" > "$out/BUILD-KEY"
cat "$out/BUILD.txt"
