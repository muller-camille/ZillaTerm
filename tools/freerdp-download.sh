#!/usr/bin/env bash
# Client Bureau à distance de ZillaTerm : wfreerdp (client Windows de FreeRDP), compilé par le workflow « release » du
# fork muller-camille/FreeRDP (branche ci) : FreeRDP 3.32.2, ses correctifs génériques et l'option /frame-pipe du client
# (bureau décodé vers ZillaTerm ; branche patches-3.32.2, à un commit figé), lié statiquement à OpenSSL (version
# figée) ; un seul exécutable, qui n'utilise que des DLL de Windows.
#
# Téléchargé depuis la release figée ci-dessous : SHA256SUMS.txt est vérifié par sa somme figée et par l'attestation
# de provenance de GitHub (voir tools/fork-release.sh), puis chaque fichier par SHA256SUMS.txt (exécutable, licences
# de FreeRDP, d'OpenSSL et de mingw-w64, BUILD.txt). Déposé dans local-packages/freerdp/ : ZillaTerm l'embarque s'il y
# est au moment de la compilation.
set -euo pipefail

repository=muller-camille/FreeRDP
release=wfreerdp-3.32.2-4
sums_sha256=f1438d0e51e6c866234d8c4205bb08bc9f0bcf67a4750d55d4271547387d6843

root=$(cd "$(dirname "$0")/.." && pwd)
. "$root/tools/fork-release.sh"
out="$root/local-packages/freerdp"
if [ -f "$out/SHA256SUMS.txt" ] && echo "$sums_sha256  $out/SHA256SUMS.txt" | sha256sum -c --status \
  && (cd "$out" && sha256sum -c --quiet --status SHA256SUMS.txt); then
  echo "FreeRDP ($release) : déjà dans local-packages/freerdp."
  exit 0
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
fork_download "$repository" "$release" SHA256SUMS.txt "$work/SHA256SUMS.txt"
fork_check_sha256 "$work/SHA256SUMS.txt" "$sums_sha256"
fork_check_provenance "$work/SHA256SUMS.txt" "$repository"
while read -r _ name; do
  case "$name" in
    *[!A-Za-z0-9._-]* | "" | .*) echo "Nom de fichier inattendu dans SHA256SUMS.txt : $name" >&2; exit 1 ;;
  esac
  fork_download "$repository" "$release" "$name" "$work/$name"
done < "$work/SHA256SUMS.txt"
(cd "$work" && sha256sum -c --quiet SHA256SUMS.txt)
test -f "$work/wfreerdp.exe"

rm -rf "$out"
mkdir -p "$out"
mv "$work"/* "$out/"
echo "FreeRDP : release $release de $repository."
cat "$out/BUILD.txt"
