<img src="docs/icone.png" alt="" width="72" align="right">

# ZillaTerm

**Français** · [English](README.md) · [Italiano](README.it.md)

[![build](https://github.com/muller-camille/ZillaTerm/actions/workflows/build.yml/badge.svg)](https://github.com/muller-camille/ZillaTerm/actions/workflows/build.yml)
[![release](https://github.com/muller-camille/ZillaTerm/actions/workflows/release.yml/badge.svg)](https://github.com/muller-camille/ZillaTerm/releases/latest)

**Client Windows multi-sessions pour CyberArk.** ZillaTerm se connecte à votre PVWA, liste les comptes auxquels vous
avez accès et ouvre vos sessions en un double-clic : bureau à distance via **PSM**, ou terminal SSH via **PSM for SSH
(PSMP)** avec un **navigateur de fichiers** intégré pour déposer des fichiers sur le serveur.

> Anciennement **CyberArkTerm** : vos réglages sont repris automatiquement au premier démarrage
> ([détails](docs/guide.fr.md#passage-de-cyberarkterm-à-zillaterm)).

![Session SSH via le PSMP, avec l'onglet Fichiers qui suit le dossier du terminal](docs/captures/fr/main-window.png)

**[Télécharger](https://github.com/muller-camille/ZillaTerm/releases/latest)** ·
**[Guide d'utilisation](docs/guide.fr.md)** · [Notes de version](https://github.com/muller-camille/ZillaTerm/releases) ·
[Politique de sécurité](SECURITY.md)

> Les captures proviennent d'un environnement de démonstration (données fictives).

## Sommaire

- [Fonctionnalités](#fonctionnalités)
- [Installation](#installation)
- [Prise en main](#prise-en-main)
- [Sécurité](#sécurité)
- [Développement](#développement)
- [Limites et pistes](#limites-et-pistes)
- [Licence](#licence)

## Fonctionnalités

| | |
| --- | --- |
| **Connexion CyberArk** | Authentification CyberArk, LDAP, RADIUS (challenge / OTP compris) ou Windows (session courante). |
| **Disponibles** | Tous les comptes visibles dans le coffre CyberArk, groupés par safe, plateforme ou type de cible, avec recherche instantanée ; mot de passe (CPM, copie), membres d'un safe, ajout, modification et import de comptes. |
| **Mes serveurs** | Vos serveurs de travail, rangés en dossiers et sous-dossiers, chacun avec sa propre configuration et une **étiquette** d'environnement (PROD, QA, DEV…) dont la couleur marque l'onglet et la session ; export, import et **listes partagées** sur un partage réseau (chacun ajoute ou retire, historique des modifications et des versions). |
| **Reprise d'un autre logiciel** | Les sessions de PuTTY, KiTTY, WinSCP, mRemoteNG, RDCMan, SecureCRT, OpenSSH, de fichiers `.rdp` ou `.mxtsessions` reprises dans « Mes serveurs » avec les mêmes dossiers, chacune associée à son compte du PVWA (sessions déjà via PSM ou le PSMP décodées) et ouverte via PSM ou le PSMP, jamais en direct ; résultat par serveur exportable en CSV, aucun mot de passe lu. |
| **Environnement partagé** | La configuration de l'équipe (PVWA, PSMP par domaine, composants PSM, listes partagées, clés des PSMP) dans un fichier `ZillaTerm.env.json` : à côté de l'exécutable, importé, ou central sur un partage ; chaque changement est montré et confirmé, rien de personnel ni aucun mot de passe. |
| **Sessions PSM** | Bureau à distance via le PSM (comme le bouton « Connect » du PVWA), dans la Connexion Bureau à distance de Windows ou, en option, dans un onglet de ZillaTerm avec le client FreeRDP embarqué (bureau Windows ou application distante à la taille de l'onglet, certificat du PSM vérifié par ZillaTerm, sans l'avertissement des fichiers .rdp) : composant, machine cible (demandée pour un compte de domaine, et gardée dans « Mes serveurs » si vous le souhaitez), motif, ticket. |
| **Sessions SSH (PSMP)** | Terminal intégré en onglet (compatible xterm : couleurs, vim, less, top…), MFA, menu du clic droit, recherche, fenêtres séparées, « Reconnecter » en fin de session, un PSMP par domaine de serveurs. Fichiers seuls (SFTP, sans terminal) pour les plateformes « SFTP » ou à la demande. |
| **Onglet Fichiers** | Navigateur SFTP du serveur : dépôt (SFTP, ou SCP, l'autre prenant le relais si le serveur refuse) et téléchargement par glisser-déposer, vérification SHA-256 de chaque fichier, file d'attente (résultats gardés) et historique des transferts, tri par colonne, renommage, modification dans votre éditeur de texte, droits, suivi en direct (`tail -f`), comparaison, envoi vers plusieurs serveurs. |
| **Vue parallèle** | Jusqu'à 8 sessions SSH côte à côte (un dossier de « Mes serveurs » s'ouvre d'un clic), saisie simultanée en option. |
| **Accès d'urgence (KeePass)** | Sans CyberArk : bases KeePass (.kdbx) dans « Mes serveurs », connexions SSH, bureau à distance et VNC directes, fichiers en SFTP, FTP ou FTPS dans l'onglet Fichiers, journal local. |
| **Clavier et accessibilité** | Raccourcis pour les onglets, le panneau de gauche et la connexion rapide, confirmations aux boutons explicites, lecteurs d'écran, contraste élevé. |
| **Langues** | Français, anglais et italien : langue de Windows par défaut, modifiable à tout moment. |

<table>
<tr>
<td width="50%"><img src="docs/captures/fr/available.png" alt="Onglet Disponibles"><br><sub>« Disponibles » : tous les comptes du coffre CyberArk, recherche instantanée</sub></td>
<td width="50%"><img src="docs/captures/fr/my-servers.png" alt="Onglet Mes serveurs"><br><sub>« Mes serveurs » : vos serveurs en dossiers, bases KeePass en tête</sub></td>
</tr>
<tr>
<td><img src="docs/captures/fr/terminal-menu.png" alt="Menu du clic droit dans le terminal"><br><sub>Clic droit dans le terminal : copier, coller, rechercher, actions de l'onglet</sub></td>
<td><img src="docs/captures/fr/keepass-vault.png" alt="Accès d'urgence avec KeePass"><br><sub>Accès d'urgence : SSH direct depuis une base KeePass</sub></td>
</tr>
</table>

## Installation

### Télécharger l'exécutable

1. Ouvrez la [dernière version](https://github.com/muller-camille/ZillaTerm/releases/latest) dans les
   *Releases* du dépôt.
2. Téléchargez **`ZillaTerm-<version>-win-x64.zip`** et décompressez-le (l'empreinte SHA256 est dans
   `SHA256SUMS.txt`).
3. Lancez `ZillaTerm.exe` : un seul fichier, aucun runtime à installer, aucun droit administrateur requis.

Depuis la version 0.30.0, l'archive a une attestation de provenance : elle prouve qu'elle a été compilée par le
workflow `release.yml` de ce dépôt, ce que `SHA256SUMS.txt` seul ne peut pas prouver. Pour la vérifier avec l'outil
GitHub en ligne de commande : `gh attestation verify ZillaTerm-<version>-win-x64.zip --repo muller-camille/ZillaTerm`.

Version de développement : l'exécutable de chaque compilation est aussi disponible en artefact
`ZillaTerm-win-x64` dans l'onglet [Actions](https://github.com/muller-camille/ZillaTerm/actions/workflows/build.yml).

L'exécutable n'est pas signé : au premier lancement, Windows SmartScreen peut afficher un avertissement
(« Informations complémentaires » → « Exécuter quand même »).

### Mettre à jour

Bouton Paramètres → **« À propos de ZillaTerm… »** : version, liens du projet, dossier des paramètres, et
« Rechercher maintenant ». Si une version plus récente existe, « Télécharger et vérifier » enregistre l'archive dans
le dossier Téléchargements puis la compare à `SHA256SUMS.txt` de la même version (gardée seulement si elle est
identique). Rien n'est installé automatiquement : fermez ZillaTerm et remplacez l'exécutable ; vos paramètres sont
conservés. L'option « Rechercher une nouvelle version au démarrage » (Paramètres › Général, désactivée par défaut)
fait cette recherche au plus une fois par jour et affiche un lien dans la barre d'état.

**Depuis CyberArkTerm** : téléchargez ZillaTerm une fois depuis la page des versions (CyberArkTerm le signale mais ne
peut pas le télécharger lui-même) ; vos réglages sont repris au premier démarrage, puis vous pouvez supprimer
`CyberArkTerm.exe`.

### Prérequis

**Poste de travail**

- Windows 10 ou 11 (x64).
- Le client Bureau à distance de Windows (présent par défaut) : la Connexion Bureau à distance (`mstsc`) pour les
  sessions PSM, son contrôle intégré pour le bureau à distance direct des bases KeePass. Les sessions PSM dans un
  onglet (option) utilisent le client FreeRDP inclus dans ZillaTerm.
- Facultatif : Windows Terminal et le « Client OpenSSH » de Windows, uniquement si vous choisissez d'ouvrir
  le SSH hors de ZillaTerm.

**Côté CyberArk**

- PVWA **v10 ou supérieur** (API REST `/PasswordVault/API/...`), joignable en **HTTPS** avec un certificat
  approuvé par le poste.
- Droit **List accounts** sur les safes concernés : l'application n'affiche que ce que l'API vous laisse voir.
- PSM configuré sur les plateformes à utiliser (composants `PSM-RDP`, `PSM-SSH`…).
- Pour le SSH : un **PSM for SSH (PSMP)**, avec SFTP autorisé pour l'onglet Fichiers (et SCP si vous
  choisissez le dépôt en SCP).
- Facultatif : **MFA caching** activé sur le PVWA, pour éviter de ressaisir mot de passe et MFA au PSMP.

## Prise en main

1. **S'identifier** : adresse du PVWA (`pvwa.mondomaine.local` suffit), méthode d'authentification, utilisateur et mot
   de passe. L'adresse, la méthode et l'utilisateur sont mémorisés ; le mot de passe jamais. Pour reprendre la
   configuration de votre équipe : « Importer un environnement… » sur le même écran, ou un fichier `ZillaTerm.env.json`
   posé à côté de l'exécutable.
2. **Trouver un compte** dans l'onglet « Disponibles » : la recherche porte sur tous les champs (`prd sql`). Clic droit
   sur un compte pour son mot de passe (vérifier, changer, réconcilier, copier), les membres de son safe, ou pour
   ajouter, modifier et importer des comptes.
3. **Se connecter** d'un double-clic : un compte Windows ouvre une session PSM dans la Connexion Bureau à distance (un
   compte de domaine demande d'abord le serveur) ; un compte Unix ouvre un terminal SSH en onglet, via le PSMP de son
   domaine réglé dans les **Paramètres**. Clic droit dans le terminal
   pour copier, coller, rechercher et les actions de l'onglet.
4. **Onglet Fichiers** (à côté d'une session SSH) : parcourez le serveur, glissez des fichiers depuis l'Explorateur
   pour les déposer, vers l'Explorateur pour les télécharger. Chaque fichier est vérifié (SHA-256) ; le bouton
   **Transferts** de la barre d'outils garde chaque transfert et ses sommes de contrôle. Un clic sur l'en-tête d'une
   colonne trie la liste.
5. **Mes serveurs** : rangez vos serveurs de travail en dossiers, chacun avec sa configuration (PSM ou SSH, composant,
   machine cible, dossier de départ) ; ouvrez tout un dossier dans la **vue parallèle**. Vos sessions PuTTY, WinSCP,
   mRemoteNG… s'y importent d'un clic, chacune avec son compte du PVWA.
6. **Accès d'urgence** : quand CyberArk est indisponible, « Accès d'urgence (KeePass) » dans la fenêtre de connexion
   ouvre vos bases KeePass et se connecte directement en SSH, en bureau à distance ou en VNC, ou aux fichiers en
   SFTP, FTP ou FTPS (sans enregistrement par le PSM, noté dans un journal sur ce poste).

Le **[guide d'utilisation](docs/guide.fr.md)** décrit chaque onglet en détail, les
[raccourcis](docs/guide.fr.md#raccourcis), les
[paramètres et le fichier de configuration](docs/guide.fr.md#paramètres-et-fichier-de-configuration), le
[fonctionnement technique](docs/guide.fr.md#fonctionnement-technique) et le [dépannage](docs/guide.fr.md#dépannage).

## Sécurité

- **HTTPS obligatoire** vers le PVWA ; la validation du certificat n'est jamais désactivée.
- **Aucun secret sur disque** : mot de passe CyberArk, jeton de session, clé MFA et mot de passe PSMP restent en mémoire ;
  la session PVWA est fermée à la sortie. Le fichier de configuration ne contient aucun mot de passe, jeton ni clé
  privée.
- **Sessions CyberArk standard** : les sessions PSM et PSMP ouvertes par ZillaTerm sont enregistrées et auditées par
  le PSM comme celles ouvertes depuis le PVWA.
- **Mots de passe copiés** : directement dans le presse-papiers Windows, exclus de son historique et de sa
  synchronisation, effacés après 20 s ; jamais affichés ni journalisés.
- **Clés d'hôte du PSMP** mémorisées à la première connexion, avec une alerte si elles changent (de même pour les
  serveurs SSH et les certificats FTPS des entrées KeePass).
- **Protocoles non chiffrés** (VNC, FTP sans TLS) signalés par un bandeau permanent ; FTP ne passe en clair qu'après
  votre accord.
- **Bases KeePass** : le mot de passe maître n'est jamais enregistré, sauf dans le coffre local si vous le demandez
  (Argon2id, AES-256-GCM, protégé par votre compte Windows) ; chaque ouverture et connexion est notée dans un journal
  local.
- **Import d'un autre logiciel** : seuls le serveur, le port, le protocole, l'utilisateur et le dossier sont lus, jamais
  un mot de passe ; chaque serveur importé passe par PSM ou le PSMP avec un compte du PVWA.
- **Aucune requête vers Internet** sans votre action ou l'option de mise à jour (désactivée par défaut) ; une mise à jour
  téléchargée n'est gardée que si sa somme SHA-256 correspond à `SHA256SUMS.txt`, et rien n'est installé
  automatiquement.
- **Journal de débogage** désactivé par défaut ; il ne contient jamais de mot de passe, de jeton ni le contenu des
  sessions.

Tous les détails : [guide d'utilisation → Sécurité](docs/guide.fr.md#sécurité). Pour signaler une vulnérabilité, voir
[SECURITY.md](SECURITY.md) (signalement privé, pas de ticket public).

## Développement

### Structure

| Projet | Rôle |
| --- | --- |
| `src/ZillaTerm.Core` | Logique sans interface, multiplateforme : client de l'API PVWA, classement des comptes, émulateur de terminal xterm, connexions PSMP et navigateur SFTP/SCP (SSH.NET), navigateur FTP/FTPS (FluentFTP), client VNC, « Mes serveurs » en dossiers, bases KeePass (KDBX), coffre local, préférences. |
| `src/ZillaTerm.App` | Application WPF : fenêtres, onglets, contrôle terminal, contrôle Bureau à distance (onglets RDP), lancement de `mstsc`, icône (`Assets`). |
| `tests/ZillaTerm.Core.Tests` | Tests xUnit de Core (faux PVWA HTTP, terminal, PSMP, dossiers…). |
| `tests/ZillaTerm.App.Tests` | Tests Windows de l'application (vrai contrôle Bureau à distance, DPAPI). |

Dépendances externes : [SSH.NET](https://github.com/sshnet/SSH.NET), [FluentFTP](https://github.com/robinrodricks/FluentFTP)
et [Konscious.Security.Cryptography](https://github.com/kmaragon/Konscious.Security.Cryptography) (Argon2), toutes
sous licence MIT. SSH.NET est la version 2026.0.0 plus deux correctifs, l'un qui garde les noms du propriétaire et du
groupe des listes SFTP, l'autre qui ajoute le transfert X11 des shells, compilée depuis
[un fork](https://github.com/muller-camille/SSH.NET) à un commit figé par son workflow de release, puis téléchargée et
vérifiée par `tools/sshnet-patched.sh` (somme SHA-256 figée, attestation de provenance), en attendant une version de
SSH.NET qui les intègre.

### Traductions

Les textes de l'interface sont dans `src/ZillaTerm.Core/Localization/CoreStrings*.resx` et
`src/ZillaTerm.App/Localization/Strings*.resx` : anglais dans le fichier neutre, puis `.fr` et `.it`.
Les classes `*.Designer.cs` sont générées par Visual Studio (`PublicResXFileCodeGenerator`) ; un test vérifie
que chaque langue a toutes les clés, les mêmes paramètres `{0}` et les mêmes touches d'accès `_`.
Pour ajouter une langue : copier les `.resx` avec le nouveau code (`.de.resx`…), traduire, puis ajouter le
code à `UiLanguage.Supported`.

### Compiler et tester

Avec le [SDK .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) et Git (Git Bash sous Windows) ; la première
commande télécharge le paquet SSH.NET patché dans `local-packages` (une fois suffit), la deuxième le client FreeRDP
embarqué pour les sessions PSM dans un onglet (facultatif : sans lui, l'application utilise le contrôle de Windows) :

```powershell
bash tools/sshnet-patched.sh
bash tools/freerdp-download.sh
dotnet test ZillaTerm.sln
dotnet run --project src/ZillaTerm.App
```

Le projet compile aussi sous Linux ou macOS (`EnableWindowsTargeting`) ; l'application ne s'exécute que sous
Windows. Les tests de `tests/ZillaTerm.App.Tests` (dont un test du vrai contrôle Bureau à distance) ne
s'exécutent que sous Windows ; ailleurs, lancez `dotnet test tests/ZillaTerm.Core.Tests`.

### Publier l'exécutable

```powershell
# Autonome (~65 Mo) : aucun runtime à installer sur le poste
dotnet publish src/ZillaTerm.App -c Release -r win-x64 -p:SelfContained=true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish

# Léger : nécessite le « .NET Desktop Runtime 10 » sur le poste
dotnet publish src/ZillaTerm.App -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -o publish
```

La CI ([`.github/workflows/build.yml`](.github/workflows/build.yml)) exécute les tests et publie l'exécutable
autonome en artefact `ZillaTerm-win-x64` pour chaque pull request et chaque push sur `main`.

### Publier une version

Depuis GitHub : **Actions → release → Run workflow** sur `main`, en indiquant le numéro `X.Y.Z`. Le workflow
[`release.yml`](.github/workflows/release.yml) exécute les tests, compile l'exécutable avec ce numéro de version
(avec un jeton en lecture seule), puis un second job, qui ne compile rien, crée le tag s'il n'existe pas et publie la
*Release* GitHub avec le zip, `SHA256SUMS.txt` et une attestation de provenance du zip. Relancé pour une version
déjà publiée, il ne met à jour que les notes, et échoue si les fichiers publiés ne viennent pas de ce workflow (sans
attestation, fichiers autres ou manquants). Les notes de version sont lues dans `docs/releases/vX.Y.Z.md` si
ce fichier existe. « Vérifier sans publier » fait toute la chaîne sans rien publier, depuis n'importe quelle branche.

## Limites et pistes

**Limites actuelles**

- **Privilege Cloud** (connexion via CyberArk Identity) et **SAML** ne sont pas gérés.
- L'API Accounts n'indique pas quels composants PSM une plateforme propose : le composant est déduit de la plateforme,
  puis réglable par plateforme dans les Paramètres.
- Bases KeePass : chiffrement Twofish et clés YubiKey non pris en charge ; pas de création de base (créez-la
  avec KeePass ou KeePassXC) ; pièces jointes gardées mais non affichées.
- Le suivi du dossier du terminal nécessite bash, zsh ou tcsh sur le serveur.
- VNC : mot de passe VNC ou aucune authentification seulement (ni authentification propre à un éditeur, ni
  chiffrement). FTPS : un serveur qui exige la reprise de session TLS sur les connexions de données peut refuser les
  transferts.
- PSM Gateway (HTML5), double validation (dual control) et accès exclusif ne sont pas gérés.

**Pistes**

- Exécutable signé et installateur MSI.
- Plusieurs PVWA (profils de connexion), Privilege Cloud.
- Autres idées, gardées pour plus tard : voir [IDEAS.md](IDEAS.md).

## Licence

[MIT](LICENSE) © 2026 muller-camille
