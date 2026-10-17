# Idées pour plus tard

Pistes proposées mais pas encore réalisées, gardées ici pour ne pas les perdre. Rien n'est promis : chaque idée sera
réévaluée (utilité, sécurité, effort) avant d'être faite.

## Sessions SSH et vue parallèle

### Commandes enregistrées

Bibliothèque de commandes, globales ou propres à un serveur « Courants » (par ex. `systemctl status nginx`,
`df -h`, `tail -n 100 /var/log/app.log`), à envoyer d'un clic dans la session active ou dans toute la vue parallèle.

- Confirmation avant l'envoi vers plusieurs sessions, avec la liste des serveurs visés.
- Variables simples (`{serveur}`, `{utilisateur}`), jamais de mot de passe dans une commande enregistrée.
- Rangement par dossiers, recherche, import/export (sans secret).
- Intérêt : éviter les fautes de frappe quand la même commande part sur 8 serveurs.

### Démarrer le serveur X du poste

Le transfert X11 suppose un serveur X déjà lancé (VcXsrv, X410…) ; sinon, une ligne grise dit qu'aucun serveur X
n'écoute. Piste : le démarrer automatiquement quand une session avec X11 s'ouvre et que rien n'écoute.

- Seulement un serveur X installé et choisi dans les Paramètres (chemin complet), jamais trouvé au hasard.
- Lancé pour n'écouter que sur ce poste (127.0.0.1), sans désactiver son contrôle d'accès (pas d'option `-ac`).

## Onglet Fichiers et transferts

### Extraire automatiquement une archive .tar.gz envoyée

Aujourd'hui, l'archive proposée pour les gros envois est envoyée puis vérifiée, et la barre d'état donne la commande
d'extraction à coller dans le terminal. Piste : l'extraire automatiquement une fois l'envoi vérifié.

- Il faudrait lancer une commande sur le serveur par une session SSH de plus (nouvelle session PSMP, enregistrée,
  avec peut-être une validation MFA).
- Option à activer explicitement, désactivée par défaut, avec la commande affichée avant d'être lancée.

### Progression globale de la file des transferts

La barre de chaque élément repart de zéro. Piste : une progression d'ensemble (« 3 sur 7 éléments, 1,2 Go sur 2 Go »)
au-dessus de la file.

### Reprendre un envoi interrompu

Si la connexion tombe pendant l'envoi d'une grosse archive (plusieurs Go par le PSMP), il faut aujourd'hui tout
renvoyer. Piste : reprendre l'envoi SFTP là où il s'est arrêté (écriture à partir de la taille déjà reçue).

- Rien n'est lancé sur le serveur : tout passe par la session SFTP de l'onglet.
- La vérification SHA-256 porte toujours sur le fichier complet, pour ne jamais garder un fichier recollé faux.
- À proposer seulement si le fichier du serveur est plus petit que l'original et n'a pas changé depuis l'échec.

### Comparer et synchroniser un dossier

Comparer un dossier de ce poste avec un dossier du serveur (nouveaux, modifiés, absents), puis n'envoyer que les
différences : utile pour redéployer un dossier de configuration sans tout renvoyer.

- Comparaison par taille et date, ou par SHA-256 à la demande (plus lent : relecture des fichiers du serveur).
- Liste des différences affichée avant tout envoi ; jamais de suppression sur le serveur sans confirmation.

### Bilan de livraison exportable

Depuis l'Historique, exporter une livraison dans un fichier texte ou CSV à joindre au ticket de changement : serveur,
dossier, date, protocole, liste des fichiers et leurs sommes SHA-256. Aujourd'hui, seules les sommes se copient (au
format `sha256sum -c`).

### Notification en fin de transfert

Notification Windows quand une file de transferts se termine alors que la fenêtre n'est pas au premier plan, avec le
résultat (vérifié, différent, en échec).

### Signets de dossiers par serveur

Garder des dossiers favoris par serveur « Courants » (`/opt/app/logs`, dossier de livraison…) et la liste des
derniers dossiers visités, pour y revenir en un clic dans l'onglet Fichiers.

### Rechercher un fichier par nom

Chercher un fichier par son nom dans le dossier affiché et ses sous-dossiers, par SFTP seulement (sans lancer de
commande sur le serveur), avec une limite de profondeur et de nombre de dossiers lus.

## PSMP

### Tester l'accès d'un serveur

Un bouton « Tester l'accès » sur un serveur : vérifie ce que le PSMP autorise pour ce compte (SSH, SFTP, SCP), avec
le message du PSMP en cas de refus (par ex. l'erreur `118E … PSMP-SCP does not contain the target settings
definitions`, composant de connexion absent de la plateforme).

- Chaque test ouvre une session PSMP (enregistrée, avec peut-être une validation MFA) : seulement à la demande.

## Suivi de fichier (tail -f)

### Suivre le fichier le plus récent d'un dossier

Pour les journaux à rotation par date (`app-2026-10-05.log`, `app-2026-10-06.log`…) : suivre « le plus récent fichier
correspondant à `app-*.log` » dans un dossier, et passer automatiquement au nouveau fichier quand il apparaît, avec un
repère dans la fenêtre.

### Suivre un fichier lisible seulement par root (sudo tail)

Les journaux de `/var/log` sont souvent réservés à root. Le suivi passe aujourd'hui par SFTP avec les droits du
compte. Piste : un suivi par une commande `sudo tail -F` dans une session SSH dédiée.

- C'est une commande lancée sur le serveur (enregistrée par le PSMP), contrairement au suivi par SFTP.
- La politique sudo du serveur s'applique ; aucun mot de passe sudo n'est gardé par ZillaTerm.
- À proposer seulement quand la lecture par SFTP est refusée (droits), avec une explication.

## CyberArk (API du PVWA)

### Demandes d'accès (double validation)

Pour les plateformes qui imposent une validation (dual control) :

- Demander l'accès à un compte avec un motif, un numéro de ticket et une période.
- Suivre l'état des demandes (en attente, accordée, refusée, expirée), puis se connecter une fois l'accès accordé.
- Pour les approbateurs : liste des demandes reçues, approuver ou refuser avec un commentaire.
- API du PVWA : `MyRequests`, `IncomingRequests`, `Confirm`, `Reject`.

### Activité d'un compte

Qui a utilisé ou récupéré un compte, et quand : utile pour une enquête ou un audit.

- API du PVWA : `GET /API/Accounts/{id}/Activities`.
- Droit nécessaire : « Voir le journal d'audit » sur le safe.

### Accès exclusif (check-in)

Pour les comptes à usage exclusif : voir qui détient le compte, et le restituer une fois le travail fini.

- API du PVWA : `POST /API/Accounts/{id}/CheckIn`.
- Le droit dépend de la plateforme.

### Comptes découverts

Comptes trouvés par la découverte CyberArk et en attente d'intégration : les lister, puis les intégrer dans un safe.

- API du PVWA : `GET /API/DiscoveredAccounts`.
- Réservé aux administrateurs.

### Comptes à clé SSH

L'ajout et la modification de compte ne gèrent que les mots de passe. Piste : créer un compte avec une clé privée SSH.
La clé ne serait jamais gardée sur le poste ni écrite dans le journal, comme les mots de passe.

### Copier plusieurs comptes à la fois

La copie du serveur ou de l'utilisateur de plusieurs comptes à la fois a disparu avec l'onglet « Tous les comptes ».
Piste : une sélection multiple dans « Disponibles » (Ctrl+clic, comme dans « Courants »).

### Se connecter à la console (sessions PSM Windows)

La case « admin connect » de la fenêtre de connexion du PVWA : la session s'ouvre sur la console du serveur
(l'équivalent de `mstsc /admin`). Utile quand toutes les sessions Bureau à distance du serveur sont prises, ou pour une
application qui ne tourne que sur la console.

- API du PVWA : paramètre `AllowConnectToConsole` (`Yes`) dans `ConnectionParams` de `PSMConnect`, à côté de
  `PSMRemoteMachine` et `AllowMappingLocalDrives`.
- Au cas par cas, jamais par défaut : la session peut prendre la place de celle déjà ouverte sur la console. Piste : un
  clic droit « Se connecter à la console » sur un serveur Windows, avec une confirmation qui le dit.
- Seulement si le composant de connexion déclare ce paramètre ; sinon, nouvelle demande sans lui (comme pour les
  disques du poste), notée au journal de débogage.

## Connexion au coffre

- **Plusieurs PVWA** : profils de connexion (production, recette…), choisis dans la fenêtre de connexion.
- **Privilege Cloud** : connexion via CyberArk Identity.
- **SAML** : authentification par le fournisseur d'identité de l'entreprise.
- **PSM Gateway (HTML5)** : PVWA qui ouvre les sessions dans le navigateur au lieu de fournir un fichier `.rdp`.

## Étiquettes de serveurs

### Garde-fous pour les étiquettes sensibles

Une case « sensible » par étiquette dans les Paramètres (cochée pour PROD par défaut, diffusée par le fichier
d'environnement), qui ajoute des confirmations là où une erreur coûte cher :

- vue parallèle : la « saisie simultanée » avertit quand elle mélange des environnements (« 2 serveurs PROD et 3 DEV
  recevront cette commande ») ;
- envoi vers plusieurs serveurs : même avertissement si la liste mélange PROD et autre chose ;
- collage de plusieurs lignes dans une session sensible : toujours confirmé, même si l'option est décochée.

C'est l'avertissement au moment de taper qui évite l'erreur ; la couleur seule ne suffit pas.

### Règles d'étiquetage de l'équipe

Règles explicites dans le fichier d'environnement (« safe `*-PRD` → PROD », « plateforme `WinDomain-Prod` → PROD »,
« nom `*rec*` → QA »), appliquées avant la proposition d'après le nom, à l'ajout comme à l'import : tout le monde a
les mêmes étiquettes sans dépendre d'une devinette.

### Étiquettes et Intervention

Ouvrir une session sur un serveur à étiquette sensible propose de démarrer l'enregistrement (voir « Proposer
d'enregistrer ») ; le rapport donne l'étiquette de chaque serveur touché et un résumé (« 3 serveurs PROD »).

### Retrouver ses serveurs par étiquette

Filtre par étiquette dans « Mes serveurs » (`tag:PROD` dans la recherche, ou pastilles cliquables) ; pastille dans
les sessions récentes de l'accueil et dans « Disponibles » pour les comptes déjà rangés dans « Mes serveurs ».

### Étiquette des sessions PSM

Les sessions PSM s'ouvrent dans la Connexion Bureau à distance de Windows, que ZillaTerm ne peut ni colorer ni cadrer.
Piste : l'étiquette en tête du nom de la session dans la fenêtre (selon ce que le composant PSM permet) et dans la
barre d'état de ZillaTerm.

## Intervention et consignes d'astreinte

### Serveurs touchés en tête du rapport

Tableau récapitulatif : une ligne par serveur avec l'arrivée, le départ, la durée, le nombre de commandes et de
transferts. C'est ce qu'on recopie à la main dans un compte rendu d'incident.

### Note rapide depuis le terminal

Un raccourci (Ctrl+Maj+N) ouvre un petit champ et ajoute la note au journal sans ouvrir la fenêtre Intervention, avec
des modèles en un clic (« Début incident », « Escalade », « Résolu »).

### Consignes mises à jour visibles

Date de modification du fichier des consignes affichée ; si son contenu a changé depuis la dernière lecture, une
pastille « Nouvelles consignes » sur le bouton Intervention, pour qu'un numéro d'urgence changé ne passe pas inaperçu.

### Proposer d'enregistrer

Proposer l'enregistrement à l'ouverture d'une session sur une cible de production (étiquette sensible, ou règle
définie par l'équipe dans le fichier d'environnement, par ex. les safes `*-PRD`), et demander s'il faut l'arrêter à
la déconnexion de CyberArk.

### Durée de conservation des journaux

Effacement automatique des journaux de plus de N jours, réglable par l'équipe (fichier d'environnement) : ils
contiennent tout ce qu'ont affiché les terminaux et ne doivent pas rester indéfiniment sur le poste.

### Empreinte du rapport

SHA-256 du rapport écrit en bas de page et affiché à l'export, pour qu'un auditeur vérifie qu'il n'a pas été retouché.

### Recherche dans les interventions passées

« Quand suis-je allé sur srv01 ? » : recherche dans les journaux, déchiffrés en mémoire seulement, jamais sur le disque.

### Résumé pour la relève

Bouton de passation : les serveurs touchés et les notes marquées « à suivre », à transmettre à la personne
d'astreinte suivante.

### Consignes en liste à cocher

Les lignes des consignes commençant par `[ ]` deviennent des cases à cocher ; chaque case cochée est notée dans le
journal (« Ticket ouvert ✓ »). Le texte reste du texte brut : rien d'autre n'est interprété.

### Lecteur des écrans enregistrés : repères

L'écran des Bureaux à distance dans un onglet est enregistré en tuiles modifiées, et le rapport a un lecteur avec la
courbe d'activité et les notes sous le curseur. Restent à y ajouter :

- un **cadre** autour de la zone modifiée à chaque image ;
- les **clics** de l'opérateur (position connue de l'onglet, jamais les touches tapées) ;
- l'**enregistrement par mstsc** (sessions PSM ouvertes avec Connexion Bureau à distance), aujourd'hui gardées par les
  seules captures à la demande.

### Écran enregistré : encore moins de place

Déjà en place : seules les tuiles modifiées, aux couleurs exactes (16 ou 24 bits), en différence pour un petit
changement, défilements et fenêtres déplacées en copies d'écran, Brotli d'une image clé à la suivante (0.42) ; un bureau
immobile ne coûte rien, ni place ni calcul (0.43). Pistes à mesurer sur de vraies interventions avec « Mesurer la
compression de l'écran » (0.43, journal de débogage) :

- **tuiles en palette** : une tuile d'interface a souvent peu de couleurs ; l'outil mesure le gain une fois Brotli
  passé ;
- **recompression en fin d'intervention** : Brotli 11, trop lent pour l'enregistrement en direct, une fois
  l'intervention terminée ; l'outil mesure le gain et le temps ;
- **zones qui bougent sans arrêt** (horloge avec les secondes, graphe du Gestionnaire des tâches, barre de progression
  animée) gardées une fois toutes les 10 secondes ;
- **budget par période** plutôt qu'un seuil unique à 100 Mo : un pic d'activité ralentit les captures un moment, sans
  brider le reste de l'intervention.

### Bureau à distance : fin de session, pause, dernière image

- **Fin de session précise** dans le journal : déconnectée par le serveur, inactivité, reprise depuis une autre
  connexion, fermeture de session, erreur (code FreeRDP déjà lu par ZillaTerm).
- Bouton **« Pause des captures »** pour un écran sensible (l'onglet affiche déjà « ● Écran enregistré »), la pause
  et la reprise étant notées au journal.
- **Dernière image avant une coupure** : les dernières images gardées en mémoire, écrites au journal seulement si la
  session se termine en erreur.
- **Temps actif par session** (clavier et souris dans l'onglet, jamais les touches) pour le tableau « Serveurs
  touchés ».

### Rapport : commandes, fichiers modifiés, texte pour le ticket

- **Commandes tapées** en liste à part (heure, serveur), sans leur retour : ce qu'on recopie dans le ticket.
- **Fichiers modifiés avant / après** : un fichier modifié par « Modifier » dans l'onglet Fichiers, sa différence dans
  le rapport (fichiers texte, taille plafonnée, masquage des secrets appliqué).
- **« Copier le résumé »** : titre, période, serveurs, notes et transferts en texte brut, à coller dans le ticket.
- **Titre proposé depuis le ticket PSM** : le ticket d'une connexion PSM (déjà au journal) proposé comme titre d'un
  enregistrement qui n'en a pas.
- **Jalons d'incident** : une note marquée « Début », « Cause trouvée », « Corrigé », « Validé » ; le rapport calcule les
  délais et les pose sur la ligne de temps du lecteur.

### Rapport : retirer un passage, rapport protégé

- **Retirer un passage avant l'export** : des blocs de l'historique exclus du rapport, qui dit qu'un passage a été retiré
  et à quelle heure.
- **Rapport protégé par mot de passe** : un seul fichier HTML déchiffré par le navigateur à l'ouverture.

## Partage de sessions

Le partage est fait (terminal SSH, Bureau à distance dans un onglet, VNC ; accord avec code de vérification, main
donnée et reprise pour un terminal). Suites possibles :

### Pointer et annoter

Un pointeur ou un cadre que la personne qui regarde pose sur l'écran partagé (« là, cette ligne »), vu des deux côtés,
sans jamais agir sur la session.

### Discussion dans l'onglet

Quelques lignes de discussion à côté de la session partagée, notées au journal de l'intervention, pour ne pas passer
par une autre messagerie pendant une astreinte.

### Main sur un Bureau à distance partagé

Donner la main (clavier et souris) sur une session Bureau à distance ou VNC partagée, comme pour un terminal.

- Même accord explicite, reprise à tout moment, rappel permanent chez la personne qui a la main.
- Plus délicat qu'un terminal : la souris et les raccourcis de Windows doivent rester dans la session.

### Invitation pour une personne précise

Limiter une invitation au compte Windows ou CyberArk attendu (refus automatique d'un autre nom), en plus du code de
vérification.

## KeePass (accès d'urgence)

- Coffres chiffrés en Twofish.
- Clés YubiKey (challenge-response).
- Création d'un coffre depuis ZillaTerm (aujourd'hui : avec KeePass ou KeePassXC).
- Affichage des pièces jointes (aujourd'hui gardées mais non affichées).
- **VNC chiffré** : VeNCrypt (TLS) ou passage par un tunnel SSH vers le serveur, pour ne plus rien envoyer en clair.
- **FTPS avec reprise de session TLS** : certains serveurs (vsftpd avec `require_ssl_reuse`) refusent une connexion
  de données qui ne reprend pas la session TLS de la connexion de commande.

## Linux et macOS

Le cœur (`ZillaTerm.Core` : API du PVWA, SSH/SFTP/SCP/FTP, émulateur du terminal, KeePass, partage chiffré, journal
d'intervention et rapport, imports) cible déjà `net10.0` et ses tests passent sous Linux. C'est l'interface qui ne
tourne que sous Windows : WPF (environ 54 000 lignes, 50 fenêtres) et des appels directs à Windows.

- **Interface** : porter WPF vers Avalonia (XAML, Windows, Linux et macOS), fenêtre par fenêtre ; le rendu du
  terminal (`TerminalView`) se porte assez directement. Avalonia pourrait aussi remplacer WPF sous Windows, pour ne
  garder qu'une interface.
- **PSM dans un onglet** : `xfreerdp` intégré sous X11 ; sous Wayland et macOS, fenêtre séparée seulement. Le repli
  vers la Connexion Bureau à distance de Windows devient `xfreerdp` ou Remmina avec le fichier `.rdp` du PVWA.
- **Secrets** (coffre local, journal d'intervention, aujourd'hui DPAPI) : trousseau du système (libsecret/GNOME
  Keyring, KWallet, Trousseau de macOS). Point de sécurité à concevoir avec soin.
- **Verrouillage du poste** (arrêt du partage, secrets effacés) : signal de logind (D-Bus) sous Linux.
- **Glisser vers le gestionnaire de fichiers** : par un fichier temporaire, moins direct que vers l'Explorateur.
- **Imports** : les sessions PuTTY de Linux sont des fichiers (`~/.putty/sessions`), pas le registre.
- Par étapes : connexion, « Disponibles », « Mes serveurs », terminal et onglet Fichiers d'abord ; puis PSM via
  FreeRDP ; puis Intervention, partage (compatible entre Windows et Linux, le protocole est dans Core), KeePass.
- **Version légère en ligne de commande**, plus rapide : connexion au PVWA, recherche des comptes, puis `ssh` du
  système vers le PSMP et `xfreerdp` pour PSM. Sans onglet Fichiers, Intervention ni partage.
- L'application tourne déjà sous Wine (captures des guides), mais pas pour un usage réel : la protection des secrets
  par Wine n'est pas celle de Windows, et PSM dans un onglet n'y est pas testé.

## Distribution et projet

- **Exécutable signé** : sans signature, SmartScreen, l'antivirus ou AppLocker peuvent bloquer l'application. Il
  faut un certificat de signature de code ; la signature se brancherait dans le workflow de release.
- **Installateur MSI** : déploiement sur les postes par les outils de l'entreprise.
- **Dependabot** : une PR à chaque nouvelle version de SSH.NET et des actions GitHub.
- **Liste de tests en conditions réelles** : une issue à cocher pour un vrai PVWA, PSM et PSMP (méthodes de connexion,
  MFA, transferts, suivi en session indépendante, saisie simultanée…), puisque les tests automatiques tournent contre
  des serveurs simulés.

## Déjà essayé, abandonné

Gardé pour mémoire, pour ne pas le reproposer sans raison nouvelle.

- **Sessions PSM dans un onglet avec le contrôle Bureau à distance de Windows** (puis fenêtres d'application distante
  rattachées à l'onglet) : retirées en 0.6.2. Le PSM refusait le mode bureau, et les fenêtres d'application distante
  rattachées réagissaient mal (souris inactive après un passage en plein écran). Les sessions PSM dans un onglet sont
  revenues en 0.28, en option, avec un client FreeRDP intégré ; la Connexion Bureau à distance de Windows reste le repli.
