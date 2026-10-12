#!/usr/bin/env bash
# SSH.NET patché pour ZillaTerm : la version 2026.0.0 publiée, plus quatre commits, en attendant une version de SSH.NET
# qui les intègre : la ligne « longname » des listes SFTP version 3 (noms du propriétaire et du groupe), le transfert
# X11 des shells (comme ssh -X, cookie factice vérifié puis remplacé avant d'atteindre le serveur X local), la fermeture
# du shell signalée quand le serveur coupe la connexion, et un maintien de la connexion auquel le serveur doit répondre
# (KeepAliveCountMax, comme ServerAliveCountMax d'OpenSSH : connexion morte détectée).
#
# Paquet compilé par le workflow « release » du fork muller-camille/SSH.NET (branche ci) depuis un commit figé, et
# publié dans une release ; téléchargé ici, vérifié par sa somme SHA-256 figée ci-dessous et par l'attestation de
# provenance de GitHub (voir tools/fork-release.sh), puis déposé dans local-packages/, où nuget.config prend SSH.NET et
# lui seul. À lancer une fois avant de compiler ZillaTerm (Git Bash sous Windows) ; la CI, la publication et l'image de
# l'OSS Scanner le lancent aussi.
set -euo pipefail

repository=muller-camille/SSH.NET
version=2026.0.1-zillaterm.4.ge29f02320b
sha256=22d71ba03bd47f63f78d773122f016685d878bc0d77fdc101802c2297a62b405

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
