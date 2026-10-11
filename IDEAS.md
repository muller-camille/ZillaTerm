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

### Retenir un refus SCP pour l'onglet

Quand le PSMP refuse SCP dès la commande `scp` (composant PSMP-SCP absent de la plateforme), l'onglet le retient :
les fichiers suivants partent directement par l'autre protocole, sans rouvrir à chaque fichier une session PSMP qui
sera refusée (une par fichier pour un dossier de 1 500 fichiers). Le bilan expliquerait l'erreur `118E` en clair.

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

### Titre ou ticket de l'intervention

Champ facultatif au lancement de l'enregistrement (« INC0012345 – disque plein prd-lnx01 »), qui nomme l'intervention
dans la liste (aujourd'hui une simple date) et sert de titre au rapport.

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

## KeePass (accès d'urgence)

- Coffres chiffrés en Twofish.
- Clés YubiKey (challenge-response).
- Création d'un coffre depuis ZillaTerm (aujourd'hui : avec KeePass ou KeePassXC).
- Affichage des pièces jointes (aujourd'hui gardées mais non affichées).
- **VNC chiffré** : VeNCrypt (TLS) ou passage par un tunnel SSH vers le serveur, pour ne plus rien envoyer en clair.
- **FTPS avec reprise de session TLS** : certains serveurs (vsftpd avec `require_ssl_reuse`) refusent une connexion
  de données qui ne reprend pas la session TLS de la connexion de commande.

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

- **Sessions PSM dans un onglet** (contrôle Bureau à distance intégré, puis fenêtres d'application distante
  rattachées à l'onglet) : retirées en 0.6.2. Le PSM refusait le mode bureau, et les fenêtres d'application distante
  rattachées réagissaient mal (souris inactive après un passage en plein écran). Les sessions PSM s'ouvrent dans la
  Connexion Bureau à distance de Windows. À reprendre seulement avec un PSM qui accepte le mode bureau.
