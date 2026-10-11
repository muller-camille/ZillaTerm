# Fonctions communes aux scripts qui récupèrent un binaire publié par un fork (tools/freerdp-download.sh,
# tools/sshnet-patched.sh) : téléchargement depuis une release GitHub, somme SHA-256 figée par l'appelant, attestation
# de provenance (compilé par le workflow « release » du fork, depuis sa branche ci).

# fork_download <dépôt> <tag> <fichier> <destination> : télécharge un fichier de la release.
fork_download() {
  curl -sSfL --retry 3 -o "$4" "https://github.com/$1/releases/download/$2/$3"
}

# fork_check_sha256 <fichier> <empreinte> : arrête tout si le fichier n'a pas l'empreinte figée.
fork_check_sha256() {
  if ! echo "$2  $1" | sha256sum -c --status; then
    echo "$(basename "$1") : somme SHA-256 inattendue ($(sha256sum "$1" | cut -d' ' -f1), attendue $2)." >&2
    exit 1
  fi
}

# fork_check_provenance <fichier> <dépôt> : attestation de provenance de GitHub (gh attestation verify) : le fichier
# a été compilé par le workflow « release » du fork, depuis sa branche ci. Obligatoire sur la CI (GH_TOKEN fourni) ;
# en local, tentée si gh est installé, sans bloquer : la somme SHA-256 figée garantit déjà le fichier.
fork_check_provenance() {
  local name
  name=$(basename "$1")
  if command -v gh > /dev/null \
    && gh attestation verify "$1" --repo "$2" --signer-workflow "$2/.github/workflows/release.yml" \
      --source-ref refs/heads/ci --deny-self-hosted-runners > /dev/null; then
    echo "$name : provenance vérifiée (workflow release de $2, branche ci)."
  elif [ "${GITHUB_ACTIONS:-}" = true ]; then
    echo "$name : attestation de provenance absente ou invalide (workflow release de $2, branche ci)." >&2
    exit 1
  else
    echo "$name : somme SHA-256 vérifiée ; provenance non vérifiée (gh absent, non connecté ou hors ligne)."
  fi
}
