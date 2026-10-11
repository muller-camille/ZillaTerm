#!/usr/bin/env bash
# SSH.NET patché pour ZillaTerm : la version 2026.0.0 publiée, plus deux commits, en attendant une version de SSH.NET
# qui les intègre : la ligne « longname » des listes SFTP version 3 (noms du propriétaire et du groupe) et le transfert
# X11 des shells (comme ssh -X, cookie factice vérifié puis remplacé avant d'atteindre le serveur X local).
#
# Paquet compilé par le workflow « release » du fork muller-camille/SSH.NET (branche ci) depuis un commit figé, et
# publié dans une release ; téléchargé ici, vérifié par sa somme SHA-256 figée ci-dessous et par l'attestation de
# provenance de GitHub (voir tools/fork-release.sh), puis déposé dans local-packages/, où nuget.config prend SSH.NET et
# lui seul. À lancer une fois avant de compiler ZillaTerm (Git Bash sous Windows) ; la CI, la publication et l'image de
# l'OSS Scanner le lancent aussi.
set -euo pipefail

repository=muller-camille/SSH.NET
version=2026.0.1-zillaterm.2.gc3245ef61d
sha256=a336b523da95a2bbe6aba2a1adbc1fcc51c04f0fb9172f7d7d7d287ceb88f80c

root=$(cd "$(dirname "$0")/.." && pwd)
. "$root/tools/fork-release.sh"
package="$root/local-packages/SSH.NET.$version.nupkg"
if [ -f "$package" ] && echo "$sha256  $package" | sha256sum -c --status; then
  echo "SSH.NET $version : déjà dans local-packages."
  exit 0
fi

mkdir -p "$root/local-packages"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
download="$work/$(basename "$package")"
fork_download "$repository" "v$version" "SSH.NET.$version.nupkg" "$download"
fork_check_sha256 "$download" "$sha256"
fork_check_provenance "$download" "$repository"
mv "$download" "$package"
echo "SSH.NET $version : release v$version de $repository."
