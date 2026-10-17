# Guide d'utilisation de ZillaTerm

**Français** · [English](guide.md) · [Italiano](guide.it.md) · [← Retour au README](../README.fr.md)

## Sommaire

- [1. S'identifier au coffre CyberArk](#1-sidentifier-au-coffre-cyberark)
- [2. Trouver un compte : onglet « Disponibles »](#2-trouver-un-compte--onglet--disponibles-)
- [3. Ouvrir une session PSM (bureau à distance)](#3-ouvrir-une-session-psm-bureau-à-distance)
- [4. Ouvrir une session SSH via le PSMP](#4-ouvrir-une-session-ssh-via-le-psmp)
- [5. Parcourir et déposer des fichiers : onglet « Fichiers »](#5-parcourir-et-déposer-des-fichiers--onglet--fichiers-)
- [6. Organiser ses serveurs : onglet « Mes serveurs »](#6-organiser-ses-serveurs--onglet--mes-serveurs-)
- [7. Accès d'urgence hors CyberArk : bases KeePass](#7-accès-durgence-hors-cyberark--bases-keepass)
- [8. Intervention : enregistrement et consignes d'astreinte](#8-intervention--enregistrement-et-consignes-dastreinte)
- [9. Partager une session avec un collègue](#9-partager-une-session-avec-un-collègue)
- [Raccourcis](#raccourcis)
- [Paramètres et fichier de configuration](#paramètres-et-fichier-de-configuration)
- [Sécurité](#sécurité)
- [Fonctionnement technique](#fonctionnement-technique)
- [Dépannage](#dépannage)

## 1. S'identifier au coffre CyberArk

<img src="captures/fr/sign-in.png" alt="Fenêtre d'identification" width="440">

Saisissez l'adresse du PVWA (`pvwa.mondomaine.local` suffit : `https://` et `/PasswordVault` sont ajoutés),
choisissez la méthode d'authentification, puis votre identifiant et votre mot de passe. Si le serveur RADIUS pose
une question (code OTP), la fenêtre l'affiche et attend votre réponse ; « Retour » (ou `Échap`) revient aux champs
d'identification sans y répondre. Pendant la saisie d'un mot de passe, l'avertissement
« Verr. Maj est activé. » s'affiche si la touche est active (de même pour les bases KeePass et le coffre local).

L'adresse, la méthode et l'identifiant sont mémorisés ; **le mot de passe ne l'est jamais**.

La liste en bas à gauche change la langue de l'interface (Français, English, Italiano) ; la fenêtre se rouvre
aussitôt dans la langue choisie, sans perdre l'adresse ni l'identifiant saisis.

### Fenêtre principale

- **Panneau de gauche** : onglets « Disponibles », « Mes serveurs », « Fichiers » et « Intervention » (`Ctrl+1` à
  `Ctrl+4`) ; l'onglet « Fichiers » n'apparaît que lorsqu'une session SSH ou de fichiers est ouverte, l'onglet
  « Intervention » pendant l'enregistrement d'une intervention
  ([section 8](#8-intervention--enregistrement-et-consignes-dastreinte)). Glissez le séparateur pour changer sa
  largeur ; `Ctrl+B` ou un double-clic sur le séparateur le replie (la bande des onglets reste : un clic sur un onglet
  le rouvre). `F6` passe du panneau à la session. La position et la taille de la fenêtre, la largeur du panneau et son
  état replié sont mémorisés.
- **Onglets de session** : une pastille donne l'état de la session par sa couleur et par sa forme, lisible aussi en
  contraste élevé : anneau orange pendant la connexion, pastille verte pleine une fois connectée, pastille orange à
  moitié pleine quand la connexion est interrompue, carré gris quand la session est terminée, croix rouge en cas
  d'échec (le nom est alors atténué). L'infobulle donne le nom complet, l'état et le mode : « Via le
  PSMP … : session gérée par CyberArk » ou « Accès direct d'urgence (KeePass) : hors CyberArk, noté dans
  urgence.log ». Un nom trop long est tronqué ; un nom déjà ouvert est numéroté (« srv01 (2) »). Quand les onglets
  ne tiennent plus, la bande défile (molette, l'onglet choisi reste visible) et « ⌄ » les liste tous avec leur
  état. `Ctrl+Tab` / `Ctrl+Maj+Tab` : onglet suivant / précédent ; `Ctrl+F4` ou `Ctrl+Maj+W` : fermer l'onglet.
- **Fermer une session connectée** (SSH, Bureau à distance, VNC) demande confirmation, avec la case « Ne plus
  demander à la fermeture d'une session » (réglage « Confirmer avant de fermer une session connectée », Paramètres ›
  Général). À « Se déconnecter de CyberArk » (qui ferme tous les onglets) et à la sortie, une seule fenêtre
  récapitule ce qui sera fermé : sessions, transferts en cours, fichiers modifiés non renvoyés.
- **Confirmations** : les boutons disent l'action (« Supprimer le compte », « Remplacer la clé et se connecter »…),
  « Annuler » est le bouton par défaut, et le serveur, le compte ou le safe concerné est nommé. Les valeurs à
  comparer (empreintes) s'affichent en police fixe avec « Copier ». Certaines actions irréversibles (supprimer un
  compte, accepter une clé de serveur qui a changé) demandent en plus de cocher une case.
- **Boutons grisés** : leur infobulle dit ce qui manque (sélection, PSMP, session SSH pour « Parallèle »…). En accès
  d'urgence, les boutons propres à CyberArk sont masqués.
- **Barre d'état** : un message ordinaire s'efface après 10 secondes ; une erreur reste affichée jusqu'au message
  suivant. Le nombre de comptes ne s'y affiche qu'avec l'onglet « Disponibles ».
- **Messages d'erreur** : les refus courants du PVWA (mot de passe refusé, compte suspendu, mot de passe expiré,
  motif ou ticket exigé, double validation, droits insuffisants, session expirée) et les erreurs courantes de
  connexion (authentification SSH refusée, nom de serveur introuvable, serveur qui ne répond pas ou refuse la
  connexion, connexion coupée) sont expliqués et disent quoi faire ; le texte d'origine suit (« Message du PVWA : … »
  ou « Détail : … »), à transmettre tel quel à l'équipe CyberArk ou au support.
- **Session CyberArk expirée** (délai d'inactivité du PVWA) : une fenêtre demande votre mot de passe (et la réponse
  RADIUS si besoin) pour vous identifier de nouveau, avec la même adresse, le même utilisateur et la même méthode. Onglets,
  sessions ouvertes et transferts restent en place ; l'action qui a rencontré l'expiration est relancée (chargement
  des comptes, connexion). « Plus tard » laisse travailler sans CyberArk : F5, ou la prochaine action CyberArk, le
  repropose. Si la session expire pendant que ZillaTerm est en arrière-plan, la fenêtre s'affiche quand vous y revenez.

## 2. Trouver un compte : onglet « Disponibles »

![Onglet « Disponibles » filtré sur plusieurs serveurs](captures/fr/available.png)

- La zone de recherche (« Filtrer les comptes… », `Ctrl+F`) filtre sur tous les champs (serveur, utilisateur, safe,
  plateforme, domaine…), plusieurs mots possibles (`prd sql`). Le ✕ au bout de la zone (comme dans chaque zone de
  recherche ou de filtre) ou `Échap` la vide. Quand le mot cherché ne se voit pas dans la ligne, la raison s'affiche
  en gris à droite du compte (« correspond : machine srv01 », « correspond : safe PROD »…).
- Plusieurs comptes affichés sous le même nom (`utilisateur@adresse`) dans un dossier se distinguent par un texte
  gris à droite : leurs machines autorisées (« → srv01, srv02 (+2) », « → toutes machines »), sinon leur
  plateforme, leur safe ou leur nom dans CyberArk. Les lecteurs d'écran le lisent avec le nom du compte.
- À la place d'une liste vide, l'onglet dit ce qui se passe : chargement des comptes, échec du chargement avec son
  message et « Réessayer », aucun compte accessible à votre utilisateur CyberArk, ou aucun compte ne correspondant
  au filtre, avec « Effacer le filtre ».
- **Chargement des comptes** : le PVWA les donne par pages de 1 000. Dès la deuxième connexion, la liste de la
  connexion précédente s'affiche aussitôt (« Liste du 15/10/2026 08:12 : mise à jour 12 000 / 50 000 ») : recherche,
  connexions et sessions récentes servent tout de suite ; la liste relue au PVWA la remplace d'un coup une fois
  complète, sélection, dossiers ouverts et défilement gardés, et la barre d'état dit ce qui a changé (« 14 nouveaux,
  3 retirés »). Un compte supprimé entre-temps est refusé par le PVWA à la connexion. Si la mise à jour échoue, la
  liste reste affichée avec sa date (« pas mise à jour ») et `F5` la relance. Sans liste précédente (première
  connexion, autre PVWA ou utilisateur), la liste se remplit au fur et à mesure des pages. La copie de la liste ne
  contient aucun mot de passe ; elle est chiffrée pour votre compte Windows, une par PVWA et utilisateur, supprimée
  après 30 jours (option des [Paramètres](#paramètres-et-fichier-de-configuration)).
- « Grouper par » range les comptes par safe, plateforme ou type de cible.
- Clic droit sur un compte → « Exporter les comptes affichés (CSV)… » enregistre en CSV les comptes affichés
  (filtrés par la recherche).
- **Membres d'un safe** : clic droit sur un compte (ou sur un safe quand les comptes sont groupés par safe, ou sur
  un serveur de « Mes serveurs ») → « Membres du safe ». La fenêtre liste les utilisateurs et groupes du safe avec leurs
  droits (lister, utiliser, récupérer, ajouter des comptes, modifier, supprimer, gérer les membres…), indique qui
  peut **ajouter des comptes**, et détaille tous les droits du membre sélectionné. Pendant la lecture, elle affiche
  « Chargement des membres… » ; en cas d'erreur, le message s'affiche au centre avec « Réessayer ». Le PVWA ne donne
  cette liste qu'à un compte qui a le droit « View Safe Members » sur le safe. `Ctrl+A` puis `Ctrl+C` copie le
  tableau. Avec le droit « Gérer les membres du safe », les boutons « Ajouter un membre… », « Modifier les droits… »
  (ou double-clic) et « Retirer… » gèrent les membres : nom, type (utilisateur ou groupe), annuaire (« Vault » ou le
  domaine LDAP), date de fin éventuelle et les 22 droits, regroupés comme dans le PVWA. La liste « Profil » coche
  d'un coup les droits d'un usage courant (lecture seule, utilisateur des comptes, gestionnaire des comptes,
  complet) ; les droits sensibles sont signalés et leur attribution demande confirmation ; « Modifications : +n /
  −n » résume ce qui change. Retirer un membre est confirmé (« Retirer le membre »).
- **Ajouter un compte** : clic droit sur un compte (ou sur un safe quand les comptes sont groupés par safe) →
  « Ajouter un compte dans le safe… ». Safe, plateforme, adresse et utilisateur sont obligatoires ; domaine de
  connexion, nom du compte, mot de passe, machines autorisées et gestion par le CPM sont facultatifs. Le compte
  cliqué sert de modèle (safe, plateforme, domaine) et le curseur est placé sur le premier champ obligatoire vide.
  Le compte est créé avec les droits de votre session : il faut le droit « Ajouter des comptes » sur le safe, et en
  général « Modifier le contenu des comptes » pour fournir le mot de passe. La liste est rechargée ensuite et le
  nouveau compte sélectionné.
- **Importer des comptes (CSV)** : clic droit sur un compte ou un safe → « Importer des comptes (CSV)… ». Une
  fenêtre demande le fichier (« Enregistrer un modèle… » donne un exemple), le safe et la plateforme par défaut, puis
  montre un aperçu de toutes les lignes du fichier (lignes en erreur en rouge, case « Erreurs seulement ») et les
  safes visés ; rien n'est envoyé avant le bouton « Créer N comptes ».
  Colonnes obligatoires : adresse et utilisateur (plus safe et plateforme, sinon les valeurs par défaut) ;
  facultatives : nom, domaine, mot de passe, machines autorisées, gestion CPM (oui/non), motif. Séparateur `;`, `,`
  ou tabulation, noms de colonnes en français, anglais ou italien ; un fichier produit par « Exporter les
  comptes affichés » se réimporte. Une seconde fenêtre crée ensuite les comptes ligne par ligne et affiche l'état de chacune (créé, refusé
  avec le message du PVWA, non importé, non envoyé ; « Arrêter » possible). La fermer pendant l'import demande
  confirmation : « Continuer » (par défaut) ou « Arrêter l'import » (le compte en cours est terminé, les comptes déjà
  créés restent dans le coffre CyberArk). À la fin, elle propose d'enregistrer le
  résultat en CSV (sans les mots de passe). Les mots de passe du fichier ne sont jamais affichés ; supprimez le
  fichier après l'import.
- **Modifier / supprimer un compte** : clic droit → « Modifier le compte… » (plateforme, adresse, utilisateur,
  domaine, nom, machines autorisées, gestion par le CPM ; seuls les champs changés sont envoyés) ou « Supprimer le
  compte… » : la confirmation rappelle que le compte est supprimé pour tous les utilisateurs et que son mot de passe
  ne sera plus récupérable ; il faut cocher « Je comprends que le mot de passe actuel ne sera plus récupérable ».
  Droits « Modifier les propriétés des comptes » et « Supprimer des comptes ».
- **État du mot de passe (CPM)** : l'info-bulle d'un compte indique s'il est géré par le CPM, la date du dernier
  changement, de la dernière vérification et de la dernière réconciliation ; un **⚠** signale un compte dont la
  dernière opération du CPM a échoué.
- **Clic droit → « Mot de passe »** (comptes de « Disponibles » et serveurs de « Mes serveurs ») :
  - « Vérifier (CPM) », « Changer (CPM)… », « Réconcilier (CPM)… » demandent l'opération au CPM (confirmation pour
    changer et réconcilier, boutons « Changer le mot de passe » et « Réconcilier » ; droit « Lancer les opérations
    CPM »). Le CPM la traite ensuite : `F5` pour voir le nouvel état.
  - « Copier le mot de passe… » : la fenêtre nomme le compte et son safe, et annonce que la récupération est
    inscrite dans l'audit CyberArk et que le mot de passe reste 20 secondes dans le presse-papiers. Motif et ticket
    sont facultatifs, sauf si la plateforme les exige ; « Récupérer et copier » copie le mot de passe **sans
    l'afficher**, puis la barre d'état décompte les secondes avant son effacement (droit « Récupérer les comptes »).
- Sur l'accueil, la **connexion rapide** (`Ctrl+K` ; le curseur y est au démarrage) trouve un serveur au fil de la
  frappe : Entrée pour s'y connecter. Elle dit quand aucun compte ne correspond, et n'affiche que les 50 premiers
  résultats (« 50 premiers comptes sur N : précisez la recherche. »). Les **sessions récentes** sont datées
  (« Aujourd'hui 09:28 », « Hier 18:02 ») ; clic droit → « Retirer de la liste » (ou `Suppr`) en retire une. Elles
  restent grisées tant que les comptes ne sont pas chargés depuis le PVWA (« en attente des comptes du PVWA… »), sauf
  quand la liste de la connexion précédente est affichée ; ce
  sont celles du PVWA où vous êtes connecté (sur un autre PVWA, le même ID de compte désigne un autre compte).

## 3. Ouvrir une session PSM (bureau à distance)

<img src="captures/fr/psm-connect.png" alt="Options avancées de connexion : machine cible, motif, ticket" width="520">

Double-cliquez sur le compte (ou Entrée, ou bouton « Se connecter ») ; un compte Unix s'ouvre en SSH via le PSMP
quand son adresse est renseignée, ou en fichiers seuls pour une plateforme « SFTP » (voir 4.), et « Options
avancées… » permet alors de choisir le PSM. ZillaTerm
demande la connexion au PVWA et ouvre la session dans la **Connexion Bureau à distance** de Windows (`mstsc`),
exactement comme le bouton « Connect » du PVWA : le fichier RDP du PVWA lui est donné tel quel. Un composant en
application distante (RemoteApp) ouvre ses fenêtres sur le bureau du poste.

**Sessions PSM dans un onglet** (option « Ouvrir les sessions PSM Windows dans un onglet de ZillaTerm », Paramètres ›
Bureau à distance, décochée par défaut) : ZillaTerm lit lui-même le fichier du PVWA et ouvre la session dans un onglet avec le
client Bureau à distance FreeRDP qu'il embarque : ni fichier `.rdp`, ni avertissement de Windows sur son éditeur, ni
fenêtre à part.

- **Ouverture** : l'onglet s'ouvre tout de suite, comme pour une session PSMP ; une barre de progression y reste
  pendant que ZillaTerm demande la session au PVWA puis se connecte au PSM, jusqu'à l'affichage de la session. Si le
  PVWA refuse la demande (motif exigé, composant inconnu…), l'onglet se ferme et la fenêtre de connexion le dit.
- **Bureau ou application distante** : un bureau Windows comme une application distante (RemoteApp :
  `||PSMInitSession`, composants PSM-RDP, PSM-SSH, WinSCP…) s'affichent dans l'onglet. Les fenêtres de l'application
  y sont placées comme sur un bureau ; aucune ne s'ouvre sur celui du poste.
- **Taille** : la session prend la taille de l'onglet à la connexion et la garde. Si la fenêtre change de taille,
  l'image est mise à l'échelle : le PSM a ouvert sa connexion vers le serveur à cette taille. La session ne passe
  jamais en plein écran, même si le fichier du PVWA le demande. Avec l'option « Adapter la résolution à la taille de
  l'onglet » (Paramètres › Bureau à distance, activée par défaut), la résolution de la session PSM suit
  l'onglet à chaque redimensionnement, sans mise à l'échelle ; selon la configuration du PSM, la connexion vers le
  serveur cible peut garder sa taille d'origine : désactivez alors l'option, la session garde une taille fixe et son
  image est mise à l'échelle de l'onglet.
- **Clavier** : quand l'onglet a le clavier (clic dans la session), toutes les touches vont à la session, raccourcis
  de Windows compris (Alt+Tab, touche Windows). Cliquez ailleurs dans ZillaTerm pour les retrouver. Un onglet choisi
  au clavier (`Ctrl+Tab`, `Ctrl+F4`…) ne prend pas le clavier : `Ctrl+Tab` traverse ainsi les onglets Bureau à
  distance ; `Entrée` sur l'onglet, `F6` ou un clic dans la session le lui donnent.
- **Certificat du PSM** : lu par ZillaTerm pendant la barre de progression, avant tout envoi au PSM (ni jeton, ni
  utilisateur), et vérifié avec les autorités approuvées par Windows (nom du PSM, dates, révocation quand elle est
  joignable). Approuvé : rien à confirmer. Sinon (certificat auto-signé, émis pour un autre nom), ZillaTerm affiche son
  empreinte et demande de confirmer une fois, puis le retient, comme une clé SSH. L'adresse d'un PSM en répartition de
  charge peut cacher plusieurs serveurs, chacun avec son certificat : un autre certificat est signalé, puis accepté
  comme autre serveur PSM (les certificats connus restent acceptés) ou à la place des certificats connus
  (renouvellement), après confirmation. Après une question, la connexion part avec un nouveau jeton demandé au PVWA.
  Jamais de connexion sans vérification, jamais de question pendant la connexion, jamais de question de Windows. Un
  certificat du PSM émis par l'autorité de l'entreprise, à demander à l'équipe CyberArk, évite les confirmations.
- **Redirections** (presse-papiers, disques…) : celles demandées par le PVWA, jamais plus. Aucune image de la session
  n'est gardée sur le disque.
- **Disques du poste** : Paramètres › Bureau à distance › « Disques du poste partagés avec les sessions PSM
  Windows » : **Aucun**, **Tous les disques** (par défaut) ou **Ces dossiers seulement**. Avec des disques ou des
  dossiers, ZillaTerm les demande au PVWA, comme la case « Map local drives » de sa fenêtre de connexion (paramètre
  `AllowMappingLocalDrives`) ; si le composant de connexion le permet, le PVWA les écrit dans le fichier RDP, et jamais
  plus n'est partagé que ce qu'il permet.
  - *Tous les disques* : ils apparaissent dans la session (Explorateur › Ce PC), dans l'onglet comme dans la
    Connexion Bureau à distance.
  - *Ces dossiers seulement* : seuls les dossiers de la liste (nom vu dans la session sous `\\tsclient\Nom`, chemin sur
    ce poste, complet ou commençant par une variable comme `%USERPROFILE%\Partage`), avec le client de ZillaTerm dans
    un onglet. Un dossier absent à la connexion n'est pas partagé et la barre d'état le dit. Connexion Bureau à
    distance et le contrôle Bureau à distance de Windows ne savent partager que des disques entiers : aucun disque
    pour eux dans ce mode, plutôt que tous (pour Connexion Bureau à distance, ZillaTerm demande au PVWA un fichier
    sans disques).
  - *Aucun* : aucun disque n'est partagé dans l'onglet, même si le PVWA le demande (choix gardé dans le PVWA, réglage
    du composant) ; la Connexion Bureau à distance suit le fichier signé du PVWA, qui ne peut pas être modifié.
- **Affichage en pause** : l'option « Suspendre l'affichage des sessions que personne ne regarde »
  (Paramètres › Bureau à distance, cochée par défaut) demande au serveur de ne plus envoyer l'image d'une session dans un
  onglet quand l'onglet est en arrière-plan, ZillaTerm réduit ou Windows verrouillé depuis 1 minute, ou quand ni le
  clavier ni la souris n'ont servi depuis 15 minutes ; l'onglet affiche alors « Affichage en pause ». La session reste
  ouverte : l'image revient dès le retour sur l'onglet ou au premier mouvement de souris, et le clavier n'est rendu à
  la session qu'ensuite (la touche qui réveille l'onglet ne lui est pas envoyée). Ni trafic d'affichage, ni encodage
  de l'image par le PSM, pour une session que personne ne voit. Jamais pour l'onglet affiché pendant l'enregistrement
  d'une intervention ou le partage de la session. Pendant un enregistrement, la pause d'un onglet en arrière-plan y est
  marquée : le lecteur du rapport montre « Affichage en pause » sur l'écran et une bande grise sur la frise (voir
  [Intervention](#8-intervention--enregistrement-et-consignes-dastreinte)).
- **Reconnecter**, « Dupliquer l'onglet » : nouvelle demande au PVWA (le jeton d'une session PSM ne sert qu'une
  fois) ; motif et ticket restent ceux de la première connexion. Si la session CyberArk a expiré entre-temps, la
  fenêtre d'identification est proposée, puis la demande repart.
- L'onglet porte l'étiquette du serveur (PROD, QA…) et son cadre de couleur ; « Capture » ajoute une image au
  journal d'une intervention en cours.
- Une connexion par une passerelle Bureau à distance s'ouvre toujours dans la Connexion Bureau à distance ; la session
  aussi si le client FreeRDP ne peut pas être lancé sur le poste (stratégie du poste, par exemple) : la barre d'état
  le dit.

- **Composant PSM** : déduit de la plateforme (`PSM-RDP` pour Windows, `PSM-SSH` pour Unix et réseau,
  `PSM-SQLServerMgmtStudio`, `PSM-SQLPlus`…). Cochez « Mémoriser ce composant » pour le conserver pour toute la
  plateforme. Votre PVWA peut nommer ses composants autrement (par exemple `WIN-PSM`) : saisissez le nom que propose
  son bouton « Connect » ; la liste propose ensuite les composants déjà utilisés, celui de la plateforme en premier.
  Pour tous vos comptes Windows, réglez une fois le composant dans **Paramètres › CyberArk** (« Composant des comptes Windows »,
  par exemple `WIN-PSM`) ; un composant mémorisé pour une plateforme reste prioritaire. Les composants par
  plateforme se voient et se modifient au même endroit (« Composant par plateforme »).
- **Comptes de domaine** : un compte enregistré pour son domaine n'a pas de serveur. Il est reconnu par sa plateforme
  de domaine, la limitation à ses machines autorisées, ou son adresse : son domaine de connexion, un domaine sous lequel
  se trouvent d'autres serveurs (`corp.local` quand un compte vise `srv01.corp.local`), ou le domaine du PVWA ou du
  poste. Un compte local dont le domaine de connexion est le nom du serveur (`srv01.corp.local`, `SRV01`) reste un
  compte de ce serveur quand un autre compte du coffre le vise (compte local, compte Unix) ou que sa plateforme est une
  plateforme de serveurs ou de postes (« Server », « Srv », « Desktop », « Workstation »). Un compte qui a seulement
  une liste de machines, sans y être limité, s'ouvre sur son adresse ; la machine cible reste facultative. Jamais
  de session vers le domaine lui-même : le serveur est toujours demandé, « Options avancées… » compris. La fenêtre « Choisir le serveur » demande sur lequel ouvrir la session :
  la liste propose les serveurs déjà utilisés avec ce compte (sessions récentes, « Mes serveurs ») puis ses machines
  autorisées ; un compte limité à ses machines refuse les autres. « Garder ce serveur dans « Mes serveurs » », avec
  le dossier et l'[étiquette](#étiquettes-prod-qa-dev) voulus (choisis dans la même fenêtre), l'y ajoute après une
  connexion réussie, nommé `compte@serveur` (choix et dossier mémorisés pour la fois suivante ; la case disparaît si
  ce serveur y est déjà). « Options avancées… » ouvre la fenêtre complète avec ce serveur.
- **Motif et ticket** : si le PVWA refuse la demande (motif ou ticket exigé, composant non configuré…), la fenêtre
  dit quoi faire, suivi du message d'origine du PVWA, et vous pouvez corriger puis réessayer.
- Le bouton « Options avancées… » de la barre d'outils (ou clic droit → « Options avancées… ») ouvre cette fenêtre,
  « Options avancées de connexion », à la demande. Les deux champs du ticket (système, numéro) sont nommés pour les
  lecteurs d'écran ; la colonne des étiquettes s'élargit avec leur texte. Le curseur est placé sur le premier champ utilisable ; en SSH ou en fichiers seuls, les champs qui ne
  servent qu'au PSM sont grisés et leur infobulle le dit ; sans composant, la fenêtre demande d'en choisir un.

## 4. Ouvrir une session SSH via le PSMP

Renseignez une fois le PSMP dans **Paramètres › SSH** (voir [PSMP par domaine](#psmp-par-domaine) s'il y en a
plusieurs). Le double-clic (ou Entrée) choisit alors d'après le nom de la plateforme du compte :

| Nom de la plateforme | Ouverture par défaut |
| --- | --- |
| contient « SFTP » (`UnixSFTP`, `SFTP-Partenaires`…) | **fichiers seuls** en SFTP via le PSMP (voir ci-dessous) |
| contient « SSH » (`UnixSSH`, `CiscoSSH`…), ou autre plateforme Unix | **SSH** via le PSMP |
| autre | **PSM** (bureau à distance) |

Le clic droit propose toujours les trois (l'ouverture par défaut est en gras) : « Se connecter (PSM) », « Se
connecter en SSH (PSMP) » (ou bouton « SSH ») et « Ouvrir les fichiers (SFTP, PSMP) ».

**Fichiers seuls** : une seule session PSMP SFTP, sans terminal. Un onglet montre son état, le serveur et un bouton
« Afficher les fichiers », qui ouvre l'onglet « Fichiers » du panneau de gauche et l'élargit au besoin pour que toutes
ses colonnes tiennent. Les fichiers y sont, avec les mêmes fonctions (transferts vérifiés, file d'attente, éditeur,
comparaison, suivi en direct, droits), sauf ce qui a besoin d'un terminal (suivi du dossier du terminal, extraction
d'une archive `.tar.gz`). Utile pour simplement déposer ou récupérer des fichiers, ou quand la plateforme autorise
PSMP-SFTP mais pas le shell. Comme toute session PSMP, elle est enregistrée et auditée par CyberArk.

La session s'ouvre **dans un onglet de ZillaTerm**, tout de suite : une barre de progression s'affiche pendant que
ZillaTerm demande la clé MFA au PVWA et se connecte au PSMP (les questions de clé du serveur, de mot de passe ou de
code arrivent entre-temps). L'identifiant est l'identifiant PSMP standard `<vous>@<compte cible>[#domaine]@<serveur
cible>`. Les noms d'utilisateur contenant des espaces (`Jean Dupont`, `Admin Local`) sont
acceptés. Le panneau de gauche passe sur l'onglet « Fichiers » du serveur (s'il est replié, il le reste).

### Applications graphiques (X11)

Les applications graphiques lancées dans une session SSH (`xclock`, installeurs Oracle, `xterm`…) peuvent s'afficher
sur votre poste, comme avec `ssh -X` :

1. Installez un **serveur X** sur le poste (VcXsrv, X410, Xming…) et lancez-le en autorisant les **connexions TCP
   locales** : l'affichage `:0` écoute sur le port 6000. Le serveur X se règle dans **Paramètres › SSH ›
   Serveur X local** (`:0` par défaut ; ce poste seulement, le protocole X n'étant pas chiffré).
2. Cochez **« Transfert X11 (applications graphiques) »** pour le serveur : clic droit → « Propriétés… » dans « Mes
   serveurs », ou dans « Options avancées… » pour une seule connexion. Sessions SSH seulement ; décoché par défaut.
   Cochée, la case rappelle son effet juste en dessous (« ⚠ Effet : un serveur compromis pourrait voir les autres
   fenêtres X de ce poste et y envoyer des frappes. »).
3. Ouvrez la session : une ligne grise dit si le transfert est actif, s'il a été refusé (par le serveur, dont
   `sshd_config` doit avoir `X11Forwarding yes`, ou par le PSMP) ou si aucun serveur X n'écoute sur le poste.

ZillaTerm donne au serveur un **cookie factice** : chaque connexion X11 doit le présenter, et il est retiré avant que
quoi que ce soit n'atteigne le serveur X du poste ; une connexion sans ce cookie est refermée. ⚠ À réserver aux
serveurs de confiance : un serveur compromis pourrait voir les autres fenêtres X du poste et y envoyer des frappes. Le
choix est personnel : il n'est ni exporté, ni mis dans une liste partagée.

### PSMP par domaine

Avec un PSMP par domaine, déclarez-les dans **Paramètres › SSH** : un **PSMP par défaut** et la liste **PSMP
par domaine** (« Ajouter un PSMP », adresse, port ; le domaine servi est celui de l'adresse, modifiable). Chaque
serveur passe par le PSMP du domaine **le plus proche du sien** :

| PSMP configurés | Serveur | PSMP utilisé |
| --- | --- | --- |
| `psmp.xxx.corp.com`, `psmp.zzz.corp.com` | `srv01.xxx.corp.com` | `psmp.xxx.corp.com` |
| idem | `srv02.zzz.corp.com` | `psmp.zzz.corp.com` |
| `psmp.xxx.corp.com` seulement | `srv03.zzz.xxx.corp.com` | `psmp.xxx.corp.com` (pas de PSMP pour `zzz.xxx.corp.com`) |
| idem | `srv04.autre.org`, une adresse IP | le PSMP par défaut ; sans lui, le seul PSMP de la liste s'il n'y en a qu'un |

Sans PSMP pour un serveur (plusieurs PSMP, aucun par défaut, domaine non couvert), la connexion est refusée et la
barre d'état le dit. « Quel PSMP pour le serveur » donne la réponse avant d'enregistrer ; deux PSMP pour le même
domaine sont refusés. Seuls les PSMP de la liste reçoivent votre mot de passe CyberArk, jamais une adresse déduite d'un
nom de serveur ; la clé de chaque PSMP est vérifiée à sa première connexion. L'infobulle de l'onglet nomme le PSMP
utilisé.

<img src="captures/fr/psmp-authentication.png" alt="Question d'authentification posée par le PSMP" width="49%"> <img src="captures/fr/terminal-menu.png" alt="Menu du clic droit dans le terminal SSH" width="49%">

- **Authentification** : si le PVWA fournit une clé « MFA caching », aucune question n'est posée. Sinon, les
  questions du PSMP (mot de passe, code MFA) s'affichent dans une fenêtre qui nomme la session concernée, avec une
  aide selon la question : sans doute le mot de passe de votre compte CyberArk (réutilisé pour les connexions SFTP
  et SCP du même onglet, jamais enregistré), ou le code MFA (redemandé à chaque connexion). Une réponse refusée est
  signalée en rouge au-dessus du champ avec le numéro de l'essai (« Essai 2 sur 3 ») ; après trois refus, la
  connexion s'arrête pour ne pas verrouiller votre compte. « Verr. Maj est activé. » s'affiche pendant la saisie.
  Quand plusieurs sessions s'ouvrent ensemble (dossier, sélection, vue parallèle), la fenêtre propose « Utiliser aussi
  ce mot de passe pour les autres sessions en cours d'ouverture » : coché, il n'est demandé qu'une fois (jamais pour un
  code MFA), gardé en mémoire seulement le temps de leur connexion. Les mots de passe gardés pour les onglets sont
  oubliés au verrouillage de Windows.
- **Clé du PSMP** : à la première connexion, une fenêtre montre son empreinte SHA-256 en police fixe, avec
  « Copier » : comparez-la avec celle publiée par votre équipe CyberArk avant « Faire confiance et se connecter »
  (« Annuler la connexion » est le bouton par défaut). L'empreinte est ensuite mémorisée sur le poste. Si la clé
  change, un bandeau rouge avertit d'une possible interception, l'empreinte mémorisée et la nouvelle sont affichées,
  et « Remplacer la clé et se connecter » n'est possible qu'après avoir coché « J'ai confirmé ce changement avec
  l'équipe CyberArk ». Une clé refusée arrête la connexion (« Connexion annulée : la clé du serveur n'a pas été
  acceptée. »). Un PSMP peut avoir des clés de plusieurs types (RSA, ed25519…) : chacune est mémorisée à part, et
  ZillaTerm demande d'abord le type déjà connu. Une clé d'un type jamais vu pour ce PSMP n'est ni acceptée d'office
  ni prise pour un changement : la fenêtre « Nouvelle clé du PSMP, d'un autre type » montre les empreintes déjà
  acceptées et la nouvelle, et « Accepter cette clé et se connecter » n'est possible qu'après avoir coché « J'ai
  vérifié cette empreinte avec l'équipe CyberArk ».
- **Terminal** : la sélection copie, la molette ou la barre de défilement à droite parcourt l'historique ; après
  avoir remonté, « ↓ Revenir à la fin » (ou une frappe au clavier) ramène à la fin. Le défilement d'un pavé tactile
  de précision est suivi finement (historique, `less`, `vim`), comme le zoom avec `Ctrl`. Une commande, un chemin ou
  une URL trop longs pour la largeur, repliés sur plusieurs lignes, se copient sur une seule ligne, « Enregistrer le
  contenu » les écrit entiers, et la recherche trouve un mot coupé par le repli. Une fenêtre qui s'ouvre pendant une
  sélection à la souris l'arrête et la copie. `\e[3J` (`tput E3`) n'efface que l'historique, pas l'écran. AltGr
  fonctionne sur clavier français. Fermez l'onglet avec la croix, un clic molette ou `Ctrl+F4` (confirmation si la
  session est connectée).
- **Fin de session** : un bandeau en haut du terminal donne la raison de la fin, avec « Reconnecter » ; les
  dernières lignes restent lisibles, sélectionnables et copiables.
- **Clic droit dans le terminal** (ou touche Menu du clavier) : copier, coller, tout sélectionner, rechercher,
  enregistrer le contenu, effacer l'historique (sur ce poste seulement, rien n'est envoyé au serveur), taille de
  police, et les actions de l'onglet (reconnecter, dupliquer, détacher, vue parallèle, ajouter à « Mes serveurs »,
  fermer). Pour coller d'un simple clic droit, cochez « Le clic droit dans le terminal colle le presse-papiers » dans
  les Paramètres ; Maj+clic droit ouvre alors le menu.
- **Coller plusieurs lignes** : quand le shell exécuterait les lignes une à une (pas de collage protégé, « bracketed
  paste »), une fenêtre montre les lignes et demande confirmation (« Coller », « Annuler » par défaut), avec la case
  « Ne plus avertir avant de coller plusieurs lignes » (réglage « Avertir avant de coller plusieurs lignes quand le
  shell les exécuterait une à une », Paramètres › Terminal).
- **Apparence** : palette de couleurs et taille de police dans les Paramètres (Campbell, One Half, Solarized, en
  sombre ou en clair) ; `Ctrl+molette` agrandit ou réduit un terminal, `Ctrl+0` revient à la taille par défaut.
- **Rechercher** dans le terminal, historique compris : `Ctrl+Maj+F` (ou clic droit dans le terminal ou sur
  l'onglet). Les occurrences sont surlignées ; `Entrée` remonte vers les plus anciennes, `Maj+Entrée` redescend,
  `Échap` ferme.
- **Enregistrer le contenu** du terminal (historique et écran) dans un fichier texte : `Ctrl+Maj+S` (ou clic droit
  dans le terminal ou sur l'onglet). Seulement à votre demande : le fichier peut contenir des informations
  sensibles.
- **Détacher un onglet** (autre écran) : glissez l'onglet SSH hors de la fenêtre, ou clic droit → « Détacher dans
  une nouvelle fenêtre ». Le terminal passe dans une fenêtre séparée, la session continue. L'onglet garde sa place
  (« Afficher la fenêtre », « Ramener dans l'onglet ») et l'onglet Fichiers travaille sur cette session quand il est
  sélectionné. Fermer la fenêtre séparée ramène le terminal dans son onglet, sans fermer la session. Les onglets
  Bureau à distance ne se détachent pas : agrandissez la fenêtre de ZillaTerm sur l'écran voulu (« Plein écran »
  n'existe qu'avec le contrôle Bureau à distance de Windows) ; une session PSM ouverte dans la Connexion Bureau à
  distance de Windows est déjà une fenêtre à part.
- **Vue parallèle** (jusqu'à 8 sessions à l'écran) : bouton « Parallèle » de la barre d'outils (grisé tant
  qu'aucune session SSH n'est ouverte), ou clic droit sur un
  onglet SSH → « Ajouter à la vue parallèle ». Cochez les sessions SSH ouvertes à afficher ensemble (8 au plus) :
  elles s'affichent en grille dans l'onglet « Parallèle », côte à côte jusqu'à 3, puis sur deux lignes. Chaque
  session a son titre et son état (anneau pendant la connexion, pastille pleine ensuite) ; « ⤢ » (ou double-clic
  sur le titre) l'agrandit seule, « ✕ » la renvoie dans son onglet. La session où vous tapez a un cadre plus épais
  et la marque « ⌨ Saisie ici ». L'onglet Fichiers suit la session où vous travaillez. « Fermer la vue » rend chaque
  terminal à son onglet, sans fermer les sessions. Les sessions Bureau à distance n'y vont pas.
  - **Saisie simultanée** : bouton « Saisie simultanée » de la vue. Ce que vous tapez dans une session cochée
    (« Reçoit la saisie ») est aussi envoyé aux autres sessions cochées et connectées : la même commande sur
    plusieurs serveurs. Elle est **désactivée à chaque ouverture de la vue** ; active, le bouton passe en ambre avec
    « ACTIVE (n) », un bandeau ambre donne le nombre et le nom des sessions qui reçoivent la saisie, et un cadre
    ambre les entoure ; les sessions décochées sont atténuées et marquées « exclue ». Une session ajoutée
    pendant qu'elle est active n'est pas cochée ; ce qui est tapé dans une session décochée ne va qu'à elle. Chaque
    touche est encodée par la session qui la reçoit (les flèches fonctionnent dans un shell comme dans vim). La
    molette n'est pas recopiée, et coller plusieurs lignes dans plusieurs sessions demande confirmation.
  - **Depuis « Mes serveurs »** : clic droit sur un dossier → « Ouvrir en vue parallèle » connecte ses serveurs SSH
    (sous-dossiers compris) et les place directement dans la vue ; ou choisissez des serveurs avec `Ctrl+clic`
    (`Maj+clic` pour une suite) puis clic droit → « Ouvrir les N serveurs en vue parallèle ». Au-delà des places
    libres (8 au plus), une fenêtre demande lesquels ouvrir ; les serveurs Windows (PSM) sont laissés de côté.
    Chaque connexion reste une session PSMP distincte, avec ses questions habituelles.
  - **Fenêtre séparée** : bouton « Fenêtre séparée » de la vue, clic droit sur l'onglet « Parallèle », ou glissez
    l'onglet hors de la fenêtre. La fermer ramène la vue dans son onglet, sans fermer les sessions.
  - **Envoyer des fichiers** aux sessions de la vue : bouton « Envoyer des fichiers… » (voir l'onglet Fichiers).

![Vue parallèle : la même commande envoyée à trois serveurs par la saisie simultanée](captures/fr/parallel-view.png)

## 5. Parcourir et déposer des fichiers : onglet « Fichiers »

L'onglet **Fichiers** du panneau de gauche (`Ctrl+3`) suit l'onglet SSH actif et s'affiche à l'ouverture d'une session
SSH ; il n'apparaît que lorsqu'une session SSH ou de fichiers est ouverte. Il sert aussi aux sessions de fichiers seuls, qui l'affichent à leur ouverture : comptes CyberArk en
SFTP via le PSMP ([section 4](#4-ouvrir-une-session-ssh-via-le-psmp)) et
entrées KeePass SFTP, FTP, FTPS ([section 7](#7-accès-durgence-hors-cyberark--bases-keepass)).

![Onglet Fichiers trié par date, à côté du terminal](captures/fr/main-window.png)

- **Pastille de l'onglet** : sur l'onglet « Fichiers », une pastille donne le nombre de transferts en cours ou en
  attente, ou « ! » pour un transfert en échec ou différent que vous n'avez pas encore vu (afficher l'onglet le
  marque vu). Le bouton de ZillaTerm dans la barre des tâches de Windows montre aussi l'activité ou l'échec. Sans
  session ouverte, l'onglet est caché : le « ! » passe sur le bouton « Transferts » de la barre d'outils, et ouvrir
  l'historique des transferts le marque vu.
- **Barre de navigation** : chemin courant, modifiable (tapez un chemin puis Entrée). Double-clic sur un dossier
  pour y entrer, `..` pour remonter, boutons « dossier parent » (icône distincte de celle d'« Envoyer ») et
  « dossier personnel ».
- **Colonnes et tri** : cliquez sur l'en-tête d'une colonne (Nom, Taille, Modifié, Droits, Propriétaire, Groupe) ; un
  second clic inverse l'ordre (une flèche l'indique). La taille et la date commencent par les plus gros et les plus
  récents. Les dossiers restent en tête ; le tri est gardé d'un dossier et d'une session à l'autre. La colonne Nom
  prend la largeur laissée par les autres ; quand le panneau est étroit, les colonnes Groupe, Propriétaire puis Droits
  sont masquées plutôt que coupées (elles reviennent en élargissant le panneau) : « +n » au bout de l'en-tête le
  signale, avec leur nom en infobulle.
- **Choisir les colonnes** : clic droit sur l'en-tête de la liste (ou clic sur « +n ») : une case par colonne
  (Taille, Modifié, Droits, Propriétaire, Groupe), gardée d'une fois sur l'autre ; une colonne cochée sans la place
  est marquée « faute de place ». « Élargir le panneau pour tout afficher » donne au panneau la largeur qui manque
  (la session garde au moins 360 pixels). Décocher Taille ou Modifié fait de la place pour Droits et Propriétaire
  dans un panneau étroit. Les colonnes et les fichiers cachés se choisissent aussi dans Paramètres › Fichiers.
- **Propriétaire et Groupe** : au survol, `propriétaire:groupe` (comme pour `chown`). Ce sont les noms que le serveur
  envoie avec la liste des fichiers, comme ceux de `ls -l`. En SFTP, si la ligne qui les contient n'a pas la forme
  habituelle (nom avec une espace…), ZillaTerm affiche à la place les numéros (UID et GID, comme `ls -n`) ; en FTP, les
  colonnes restent vides si le serveur ne donne pas les noms.
- **Boutons de la barre** : Télécharger, Modifier, Renommer, Droits et Supprimer sont grisés, comme dans le menu,
  tant que la sélection ne convient pas ; leur infobulle dit quoi choisir (« Choisissez un seul fichier (pas un
  dossier). »…).
- **Déposer des fichiers** : glissez-les depuis l'Explorateur sur la liste (ou bouton « Envoyer »). Envoi en **SFTP**
  par défaut (SCP au choix dans les Paramètres), dossiers compris ; un lecteur entier (`E:\`) ou la racine d'un
  partage est refusé, avec un message. Déposés sur la ligne d'un dossier, ils vont dans
  ce dossier : la ligne est mise en surbrillance et la barre d'état indique la destination (« Déposer dans
  serveur:/chemin »). Si des éléments existent déjà sur le serveur (fichiers cachés compris, même non affichés), une
  confirmation nomme le serveur et les éléments, et propose « Remplacer », « Ignorer les existants » ou « Annuler »
  (par défaut) ; un fichier remplacé est réécrit sur place et reste incomplet si l'envoi est annulé ou échoue.
  Si le serveur refuse ce protocole pour un fichier avant de le recevoir (règle du PSMP, SFTP en lecture seule…),
  l'autre prend le relais aussitôt, sans question ni attente : la barre d'état et le bilan l'indiquent avec la
  réponse du serveur, l'historique des transferts aussi (« SCP (SFTP refusé) »). En SCP, après un refus à l'annonce d'un fichier, les
  fichiers au moins aussi gros partent directement en SFTP jusqu'à la fermeture de l'onglet.
  Dans un dossier envoyé (ou mis en archive .tar.gz), les liens symboliques et jonctions vers des dossiers ne sont
  pas suivis, comme au téléchargement : une jonction peut boucler ou mener à un dossier interdit (« Ma musique » des
  anciens profils Windows). Ils sont signalés dans le bilan (« ↷ n lien(s) vers un dossier non suivi(s) ») et dans
  « Détails », avec leur cible ; un lien vers un fichier est envoyé avec le contenu de ce fichier.
- **Télécharger** : bouton « Télécharger » ou clic droit. Un fichier demande où l'enregistrer ; plusieurs vont dans un
  dossier choisi, avec une seule question (« Remplacer ») pour ceux qui y sont déjà. Un fichier local n'est remplacé qu'une fois son
  téléchargement complet : un téléchargement coupé ou annulé le laisse tel qu'il était. Le fichier en cours arrive
  dans une copie temporaire du même dossier (`nom.xxxxxxxx.part`, ou `zillaterm.xxxxxxxx.part` pour un nom de plus de
  241 caractères : les noms vont jusqu'à 255 caractères, la limite de Windows). « Télécharger » ne prend
  que des fichiers : pour un dossier, la barre d'état rappelle de le glisser vers l'Explorateur ou sur le bureau.
- **Télécharger en glissant** : glissez des fichiers ou des dossiers de la liste vers l'Explorateur ou le bureau.
  Rien n'est téléchargé pendant le glissement : au dépôt, une fenêtre montre la progression (Annuler l'interrompt),
  puis l'Explorateur copie les fichiers là où vous les avez déposés. Les noms Unix sont rendus valides pour Windows
  (`\`, `:`, `..`, `CON`… remplacés), sans jamais écrire hors du dossier de dépôt ; le dossier temporaire du
  téléchargement est effacé ensuite.
- **File d'attente des transferts** : envois et téléchargements s'exécutent un par un, dans l'ordre des demandes ;
  ce qui est demandé pendant un transfert s'ajoute à la file au lieu d'être ignoré. La destination d'un envoi est le
  dossier affiché au moment du dépôt (ou le dossier sur lequel les fichiers ont été déposés), et la confirmation
  d'écrasement tient compte des envois encore en attente. La fenêtre « Envoyer » et la question « Nouveau dossier »
  nomment le serveur et le dossier : l'envoi, le téléchargement ou le nouveau dossier vont toujours là, même si vous
  changez de session pendant la question (vue parallèle, terminal détaché) ou si le panneau suit le terminal. Le panneau « Transferts », au-dessus de la barre d'état,
  montre chaque élément avec son serveur (en attente, avancement et fichier n/N, vérification, résultat) : ✕ retire
  un élément en attente, « Annuler » arrête celui en cours, « Tout annuler » annule tout ce qui reste. Les résultats
  restent affichés une fois les transferts finis : « ✓ terminé · SHA-256 vérifié (n/n) », « ⚠ terminé · non
  vérifié : n sur N », ou l'échec en rouge ; « Détails » sur une ligne terminée montre la somme SHA-256 de chaque
  fichier, et « Effacer les terminés » retire de la liste les transferts terminés, en échec ou annulés
  (l'historique des transferts les garde). Un
  transfert arrêté supprime le fichier en cours, incomplet (sur le serveur pour un envoi, sur le poste pour un
  téléchargement) ; les fichiers déjà transférés restent. Un envoi arrêté à travers un lien symbolique du serveur
  garde le lien et le fichier vers lequel il pointe, et le bilan le dit. Un téléchargement s'arrête aussi quand le
  serveur envoie bien plus que la taille annoncée (le double, ou 64 Mo de plus) : la copie incomplète est supprimée.
  Attention : si l'envoi remplaçait un fichier existant et
  avait commencé à l'écrire, son ancien contenu est perdu ; arrêté avant tout contenu, le fichier du serveur reste
  tel quel. En SCP, l'arrêt ne concerne que ce transfert : les éléments suivants continuent sur la
  même connexion ; un transfert qui n'avance plus (serveur qui ne lit plus) s'arrête 2 s après « Annuler » et les
  suivants partent sur une nouvelle connexion. Un fichier envoyé par SCP prend sur le serveur la date de l'envoi (comme `scp` sans `-p`, et comme en SFTP). Une
  erreur est affichée en rouge dans la file et la file continue ; à la fin, un seul bilan dans la barre d'état de
  l'onglet (trois lignes au plus, texte complet en infobulle). La navigation, la suppression, les
  droits, l'éditeur et le glisser vers l'Explorateur passent entre deux fichiers. Fermer l'onglet avec des
  transferts en cours demande confirmation (« Annuler les transferts et fermer » ou « Continuer les transferts ») ;
  à la déconnexion de CyberArk et à la sortie, ils figurent dans le récapitulatif.
- **Beaucoup de fichiers d'un coup : archive .tar.gz** : à partir de 200 fichiers déposés (seuil réglable, option
  « Proposer une archive .tar.gz » des Paramètres), ZillaTerm propose de les envoyer dans une seule archive : un
  fichier à transférer et à vérifier au lieu de milliers, beaucoup plus rapide via le PSMP. L'archive est créée sur
  le poste (dans la file, annulable), envoyée et vérifiée (SHA-256), puis supprimée du poste ; chaque élément déposé
  est à la racine de l'archive (droits 0644 et 0755). Rien n'est exécuté sur le serveur : un encadré orange apparaît
  en bas de l'onglet Fichiers avec la commande d'extraction, par exemple `cd '/opt/app' && /usr/bin/gzip -dc
  './deploy.tar.gz' | tar xof - && rm -f './deploy.tar.gz'` (l'archive est supprimée du serveur une fois extraite).
  Les fichiers extraits appartiennent à qui extrait (`o`), et seuls les dossiers vides ont une entrée dans l'archive :
  un dossier qui existe déjà garde ses droits et son propriétaire. L'archive n'est pas proposée quand le dépôt
  remplace des éléments du serveur : l'envoi un par un les réécrit sur place (droits, propriétaire et ACL gardés), et
  la question de remplacement le dit.
  « Copier la commande », ou « Écrire dans le terminal » qui la tape à l'invite de la session sans l'exécuter :
  vérifiez-la, puis appuyez sur Entrée. L'encadré reste affiché (pour la session concernée) jusqu'à ce que vous le
  fermiez. « Envoyer les fichiers un par un » garde l'envoi habituel ; « Ne plus proposer » décoche l'option.
  - **Tous les Unix** (Red Hat 5 à 9, HP-UX 11.11 et 11.31, Solaris, AIX…) : l'archive est au format tar POSIX
    standard (ustar), lu par tous les tar, et la commande utilise `gzip` et `tar xof` séparément, sans option propre
    à GNU tar. Elle se tape dans n'importe quel shell (sh, ksh, bash, zsh, csh, tcsh).
  - **gzip** est cherché sur le serveur (par SFTP) là où chaque système l'installe : `/bin`, `/usr/bin`,
    `/usr/contrib/bin` (HP-UX), `/usr/local/bin`, `/opt/freeware/bin` (AIX), `/usr/sfw/bin` et `/opt/csw/bin`
    (Solaris). Introuvable : l'archive est envoyée sans compression (`.tar`), extraite par `tar` seul.
  - Un nom de plus de 100 caractères (hors dossiers) ou un fichier de plus de 8 Go ne tient pas dans ce format : les
    fichiers sont alors envoyés un par un, avec un message.
- **Envoyer vers plusieurs serveurs** : bouton (flèche vers trois serveurs) ou clic droit → « Envoyer vers plusieurs
  serveurs… ». Choisissez les fichiers ou dossiers, le dossier de destination (`~` = le dossier personnel du compte
  sur chaque serveur, par ex. `~/deploy`) et les sessions SSH destinataires. ZillaTerm vérifie d'abord sur chaque
  serveur que le dossier existe et ce qui serait remplacé (une seule question pour tous), puis met en file un envoi
  par serveur : même protocole, même vérification SHA-256 sur chaque serveur, un seul bilan à la fin.
- **Comparer** : clic droit sur un fichier → « Comparer avec… » : le même chemin (ou un autre) sur un serveur dont
  une session SSH est ouverte, ou un fichier de ce poste ; avec deux fichiers sélectionnés, « Comparer les 2
  fichiers ». « Parcourir… » à côté du chemin ouvre un explorateur de l'autre serveur, sur le même dossier, fichier
  présélectionné (ou sur le plus proche dossier parent qui existe) : double-clic sur un dossier pour y entrer,
  Retour arrière pour remonter, taper le début d'un nom sélectionne le fichier (espaces comprises), un chemin peut
  aussi être tapé ; le fichier choisi remplace le chemin. L'onglet
  Fichiers de ce serveur ne change pas de dossier. Le même fichier sur le même serveur est refusé ; sans autre
  serveur ouvert, un fichier de ce poste est proposé. Les fichiers sont lus **en mémoire** (50 Mo au plus chacun), sans copie sur le poste. La fenêtre
  montre les lignes côte à côte : retirées en rouge à gauche, ajoutées en vert à droite. `F7` / `Maj+F7` :
  différence suivante / précédente ; « Ignorer les espaces » ; « Seulement les différences » ; « Enregistrer le
  diff… » au format `diff -u`. Un fichier binaire (ou de plus de 10 Mo) est comparé par sa taille et sa somme
  SHA-256. Avec un outil de comparaison choisi dans les Paramètres (WinMerge, VS Code…), « Ouvrir dans … » lui donne
  deux copies temporaires, supprimées à la fermeture de la fenêtre.
- **Historique des transferts** : bouton « Transferts » de la barre d'outils (flèches montante et descendante avec
  une horloge, à gauche de « Paramètres »), disponible même sans session. Il liste les 200 derniers envois et
  téléchargements (y compris par glisser-déposer) : date, sens, serveur, élément, destination, nombre de fichiers,
  protocole (« SCP (SFTP refusé) » quand l'autre protocole a pris le relais), résultat, écrit comme dans la file ;
  les échecs et les fichiers différents sont en rouge, et le texte d'une colonne trop étroite s'affiche en
  infobulle. Filtre « Envois » / « Téléchargements » / « Échecs et non vérifiés » (transferts en échec, fichiers
  différents ou non relus), choisi d'office quand un échec signalé par « ! » n'a pas encore été vu ; « Sommes de
  contrôle… » (ou double-clic) montre, pour chaque
  fichier, la taille, les sommes SHA-256 et le résultat, et les copie au format de `sha256sum -c` pour revérifier
  sur le serveur ; « Ouvrir le dossier » pour un téléchargement ; « Effacer l'historique », à l'écart des autres
  boutons, demande confirmation (les fichiers eux-mêmes ne sont pas touchés).
- **Vérification des transferts (SHA-256)** : chaque fichier envoyé ou téléchargé est vérifié. À l'envoi (SCP ou
  SFTP), le fichier local est haché, puis le fichier arrivé sur le serveur est relu par SFTP et haché. Au
  téléchargement, les données reçues du serveur sont hachées, puis le fichier écrit sur le poste est relu. La barre
  d'état confirme « ✓ identique des deux côtés » ; les sommes de chaque fichier sont dans « Détails » de la file et
  dans l'historique des transferts (bouton « Transferts »). Si un fichier diffère, l'erreur est affichée et le détail s'ouvre de lui-même ; un
  téléchargement par glisser-déposer échoue plutôt que de livrer une copie fausse. Un fichier qui ne peut pas être
  relu (droits) est signalé « non vérifié ». La relecture d'un envoi double le volume échangé avec le serveur.
- **Supprimer** : sélection puis Suppr (ou clic droit → « Supprimer (rm) »), avec une confirmation qui nomme le
  serveur et rappelle qu'il n'y a pas de corbeille sur le serveur. Les dossiers doivent
  être vides. Un lien symbolique est supprimé lui-même, jamais le fichier ou le dossier vers lequel il pointe.
- **Renommer** : `F2`, clic droit → « Renommer… » ou bouton de la barre. Un fichier n'est jamais écrasé : un nom déjà
  pris est refusé avant tout envoi au serveur (en SFTP comme en FTP). « / », « . », « .. » et les caractères de
  contrôle (retour à la ligne, tabulation…) sont refusés, comme pour « Nouveau dossier ». Un lien symbolique est
  renommé lui-même, jamais le fichier ou le dossier vers lequel il pointe.
- **Modifier un fichier** : **double-clic** sur le fichier (ou `Entrée`, `F4`, clic droit → « Modifier », bouton
  crayon). Le fichier s'ouvre dans l'éditeur de texte choisi dans les Paramètres (Bloc-notes par défaut). Au
  double-clic, une archive, une image, un exécutable ou un document bureautique est téléchargé plutôt qu'ouvert, de
  même qu'un fichier dont les premiers octets sont binaires. À chaque enregistrement, ZillaTerm
  propose de le renvoyer sur le serveur (« Renvoyer » ou « Pas maintenant ») : envoi en SFTP, droits du fichier
  conservés. Si le fichier a changé sur le serveur depuis son ouverture, une alerte le dit et demande confirmation
  (« Remplacer par ma version ») avant de l'écraser. Rouvrir un fichier déjà ouvert le télécharge de nouveau, à chaque
  fois : si son contenu a changé sur le serveur (modifié dans le terminal, par quelqu'un d'autre, avant une
  reconnexion…), la copie ouverte dans l'éditeur est remplacée par la version actuelle ; sinon elle n'est pas touchée.
  Seule exception : si votre copie a des modifications non renvoyées et que le fichier a aussi changé sur le serveur,
  ZillaTerm demande s'il faut « Recharger la version du serveur » ou « Garder ma copie ». Pendant qu'un fichier
  modifié est renvoyé, fermer l'onglet, quitter ou se déconnecter attend la fin du renvoi (la barre d'état le dit),
  pour ne pas laisser un fichier à moitié écrit sur le serveur. Pour quitter ou se déconnecter, si le serveur ne
  répond plus, « Fermer sans attendre » est proposé après 20 secondes : la copie modifiée est gardée sur le poste
  au moins un jour et montrée dans l'Explorateur.
- **Suivre un fichier (tail -f)** : clic droit sur un ou plusieurs fichiers → « Suivre (tail -f) ». Une fenêtre
  montre la fin du fichier puis chaque nouvelle ligne dès qu'elle est écrite, comme `tail -f`, en lisant le fichier
  par SFTP chaque seconde : aucune commande n'est lancée sur le serveur. Un fichier tronqué ou remplacé par une
  rotation est relu depuis le début ; les 10 000 dernières lignes sont gardées. Une ligne de plus de 8 192 caractères
  est coupée, avec « … [ligne coupée après 8 192 caractères] » (aussi dans la copie et les enregistrements) ; la
  suite de cette ligne est ignorée.
  - **Couleurs et alertes** : erreurs (ERROR, FATAL, CRITICAL…) en rouge, avertissements (WARN) en orange ; mots
    surlignés en jaune au choix (« Surligner », séparés par des virgules). « Alerte si » (par ex. `ERROR,
    OutOfMemory, Connection refused`) : chaque nouvelle ligne qui contient l'un de ces mots est marquée, le compteur
    « ⚠ n alertes » augmente (un clic va à la ligne suivante) et le bouton de la fenêtre clignote dans la barre des
    tâches ; une notification Windows est possible, au plus une toutes les 30 s, avec le nombre de lignes et le nom
    du fichier seulement, **jamais le contenu des lignes** (elle peut s'afficher sur l'écran verrouillé). Ces
    réglages sont gardés pour les fenêtres suivantes ; ils sont aussi dans Paramètres › Fichiers.
  - **Vue combinée** : plusieurs fichiers sélectionnés s'ouvrent dans une seule fenêtre, et « Ajouter à une fenêtre
    de suivi » y ajoute un fichier d'un autre onglet, donc d'un autre serveur. Les lignes s'intercalent dans l'ordre
    d'arrivée, préfixées et colorées par fichier (`[root@srv01 app.log]`) ; en bas, chaque fichier a son état et un
    bouton pour arrêter de le suivre.
  - **Filtre et recherche** : filtre (comme `grep`), exclusion (comme `grep -v`), lignes de contexte (comme `grep -C
    3`), en texte simple ou en expressions régulières. Un champ de filtre ou d'exclusion rempli passe sur fond ambre,
    avec ✕ pour le vider, et la barre d'état indique « Filtre : n / N lignes affichées ». `Ctrl+F` cherche dans les
    lignes sans les filtrer (Entrée / `F3` : suivant, `Maj+F3` : précédent). Remonter dans les lignes arrête de
    suivre la fin.
  - **Coupures** : si la connexion est perdue, un repère l'indique ; quand l'onglet SSH se reconnecte (ou avec
    « Reconnecter »), le suivi reprend là où il s'était arrêté, avec les lignes écrites entre-temps. Fermer l'onglet
    arrête le suivi de ses fichiers ; la fenêtre garde les lignes reçues.
  - **Fichiers mémorisés** : sur un serveur de l'onglet « Mes serveurs », les fichiers suivis sont mémorisés (les 12
    derniers) ; à la connexion suivante, le bouton de suivi de l'onglet Fichiers les propose : « Tout suivre dans
    une fenêtre » en un clic, ou un seul.
  - **Garder une trace** : « Enregistrer… » écrit les lignes affichées dans un fichier de ce poste ; « Enregistrer
    en continu… » écrit les lignes déjà reçues puis chaque nouvelle ligne dès son arrivée, tant que la case est
    cochée ; « Repère » insère une ligne `—— 14:32:05 ——` pour retrouver un moment (avant une manipulation, par
    exemple).
  - **Connexion** : par défaut, le suivi utilise la connexion SFTP de l'onglet Fichiers ; il passe alors entre deux
    fichiers d'un transfert. L'option « Suivre dans une session indépendante » (Paramètres › Fichiers)
    lui donne sa propre connexion, une par fenêtre et par serveur : il n'attend plus les transferts, mais c'est une
    session PSMP de plus (enregistrée à part, et une validation MFA peut être demandée). Elle est fermée avec la
    fenêtre. Une connexion perdue n'est jamais rouverte en boucle.
- **Droits** : clic droit → « Droits… » (ou bouton cadenas). Cases lecture / écriture / exécution pour le
  propriétaire, le groupe et les autres, bits spéciaux (setuid, setgid, sticky) et valeur octale (`644`, `1777`…),
  pour un ou plusieurs éléments. Quand les éléments choisis n'ont pas tous les mêmes droits, une case laissée à
  l'état intermédiaire ne change pas ce droit sur chaque élément : seuls les droits modifiés sont appliqués. Pour un
  dossier, l'option « Appliquer aussi au contenu » propage les droits aux
  sous-dossiers et fichiers ; par défaut, l'exécution (x) n'est donnée qu'aux dossiers et aux fichiers déjà
  exécutables. Le bouton devient alors « Appliquer récursivement… » et une confirmation rappelle ce qui va se
  passer ; pendant la propagation, « Arrêter » dans l'onglet Fichiers l'interrompt (les éléments déjà traités gardent
  leurs nouveaux droits). Les liens symboliques ne sont pas suivis, le propriétaire n'est pas modifié. En FTP, un nom
  contenant un caractère de contrôle (il ajouterait une commande au serveur) est laissé tel quel et noté en erreur ;
  les autres éléments sont traités.
- **Opération en cours** : pendant une suppression, un renommage ou une modification des droits sur un serveur, les
  autres serveurs restent utilisables ; sur le même serveur, Supprimer, `F2` et Droits sont refusés jusqu'à sa fin,
  et la barre d'état le dit. Si vous êtes passé sur une autre session entre-temps, le bilan d'une suppression ou d'un
  changement de droits (erreurs comprises) s'affiche à votre retour sur celle-ci.
- **Filtrer** : le champ sous le chemin ne garde que les éléments du dossier dont le nom contient le texte (`nginx`),
  ou répond à un masque avec `*` et `?` (`*.log`, `app?.conf` ; plusieurs séparés par `;` : `*.log;*.gz`), sans tenir
  compte des majuscules. `..` reste pour remonter ; la barre d'état dit combien d'éléments sont affichés. Il est vidé
  quand vous changez de dossier ; `Ctrl+F` dans l'onglet Fichiers, `Échap` ou ✕ le vide, `Entrée` ou `↓` passe au
  premier élément.
- **Aller à un fichier au clavier** : dans la liste, tapez le début de son nom, comme dans l'Explorateur : `p`
  sélectionne l'élément suivant dont le nom commence par « p », `p` de nouveau celui d'après ; tapées à la suite, les lettres
  forment le début du nom (`pro` → `profile`). Après une seconde de pause, une lettre commence une nouvelle recherche.
- Aussi : nouveau dossier, téléchargement, copie du chemin, affichage des fichiers cachés.
- **Suivre le dossier du terminal** : quand la case est cochée, chaque `cd` dans le terminal déplace le navigateur
  dans le même dossier (voir [Fonctionnement technique](#fonctionnement-technique)). Après un `sudo -i` ou un `su`,
  recochez la case à l'invite du shell pour réactiver le suivi dans ce nouveau shell.

<img src="captures/fr/transfer-history.png" alt="Historique des transferts avec la vérification SHA-256 de chaque fichier" width="820">

## 6. Organiser ses serveurs : onglet « Mes serveurs »

![« Mes serveurs » rangés en dossiers](captures/fr/my-servers.png)

- **Ajouter** un compte : clic droit dans « Disponibles » → « Ajouter à Mes serveurs », ou bouton « Ajouter » de la
  barre d'outils. Une fenêtre demande le dossier (le dernier choisi est proposé ; tapez un nom pour en créer un,
  `Prod/Web` pour un sous-dossier) et l'[étiquette](#étiquettes-prod-qa-dev), proposée d'après son nom. Pour un
  compte de domaine, la même fenêtre demande le serveur (facultatif : sans serveur, il le sera à chaque connexion).
  Vous pouvez aussi glisser le compte sur un dossier de « Mes serveurs » : la fenêtre s'ouvre alors sur ce dossier
  (pas du tout pour un compte de serveur s'il n'y a aucune étiquette).
- **Ajouter une session ouverte** : clic droit sur l'onglet de la session (ou dans son terminal) → « Ajouter à Mes
  serveurs » : même fenêtre (dossier et étiquette). Le serveur garde le type de connexion et la machine cible ;
  l'entrée est grisée s'il y est déjà.
- **Ajouter une session récente** : clic droit dans « Sessions récentes » sur l'accueil → « Ajouter à Mes
  serveurs » : même fenêtre (dossier et étiquette). Le serveur garde le type de connexion (PSM, SSH ou fichiers
  seuls), le composant PSM et la machine cible utilisés.
- **Dossiers** : clic droit → nouveau dossier ou sous-dossier, renommer, supprimer ; glissez serveurs et dossiers
  pour les déplacer. Supprimer un dossier compte et supprime tous ses serveurs de ce PVWA, même ceux que la recherche
  masque ; ceux d'un autre PVWA restent. Retirer un serveur ou supprimer un dossier demande confirmation (les
  comptes restent dans « Disponibles »). Les boutons « Propriétés / renommer » et « Retirer le serveur ou supprimer
  le dossier », en haut de l'onglet, sont grisés tant que rien n'est sélectionné.
- **Rechercher** : champ en haut de l'onglet (ou `Ctrl+F` dans l'onglet). Il filtre les serveurs par nom, serveur,
  utilisateur, dossier, composant, machine cible, ainsi que les entrées des bases KeePass déverrouillées ; les
  dossiers des résultats sont dépliés. `Entrée` ou `↓` sélectionne le premier résultat, `Échap` ou le ✕ au bout du
  champ efface. Avec des milliers de serveurs, la liste est filtrée dès que vous arrêtez de taper.
- **Plusieurs serveurs à la fois** : `Ctrl+clic` ajoute ou retire un serveur (ou tous ceux d'un dossier), `Maj+clic`
  choisit une suite de serveurs ; `Échap` ou un clic simple annule. Au clavier : `Ctrl+Espace` ajoute ou retire le
  serveur sélectionné, `Maj+↑` / `Maj+↓` étend la suite ; les flèches seules gardent les serveurs choisis, et la
  touche Menu (ou `Maj+F10`) ouvre le menu de la sélection. Clic droit sur l'un d'eux → « Ouvrir les N
  serveurs en vue parallèle » ou « Se connecter aux N serveurs » (un onglet chacun, 10 serveurs au plus : au-delà,
  le menu est grisé). Clic droit sur un dossier → « Ouvrir en vue parallèle » seulement (serveurs SSH, 8 au plus) : un
  dossier n'ouvre jamais tous ses serveurs d'un coup, pour éviter des centaines de sessions ouvertes par erreur.
- **Étiquette** (PROD, QA, DEV…) : clic droit → « Étiquette ▸ », voir [ci-dessous](#étiquettes-prod-qa-dev).
- **Configuration propre à chaque serveur** (clic droit → « Propriétés… ») :

| Réglage | Effet |
| --- | --- |
| Nom, dossier | Affichage et rangement dans l'arbre. |
| Étiquette | Environnement du serveur (PROD, QA, DEV…) : sa couleur marque l'arbre, l'onglet et le contour de la session. |
| PSM, SSH via PSMP ou fichiers seuls (SFTP via PSMP) | Type de connexion ouvert au double-clic (au départ, d'après la plateforme). |
| Composant PSM | Composant à utiliser (vide : déduit de la plateforme). |
| Machine cible | Serveur sur lequel ouvrir la session pour un compte de domaine. |
| Motif par défaut | Motif d'accès envoyé automatiquement au PVWA. |
| Dossier SFTP de départ | Le terminal **et** le navigateur de fichiers s'ouvrent directement dans ce dossier. |
| Transfert X11 | Applications graphiques du serveur affichées sur le serveur X du poste (SSH seulement, voir [Applications graphiques (X11)](#applications-graphiques-x11)) ; choix personnel, jamais exporté ni partagé. |

<img src="captures/fr/server-properties.png" alt="Propriétés d'un serveur de « Mes serveurs »" width="540">

Sans PSMP dans les Paramètres, les types SSH et fichiers seuls sont grisés, comme ailleurs ; une valeur
incorrecte est signalée dans la fenêtre. Un serveur dont le compte est introuvable dans CyberArk (supprimé ou droits
retirés) porte la mention « ⚠ introuvable dans CyberArk », lue aussi par les lecteurs d'écran ; rien n'est signalé
tant que les comptes se chargent.

### Étiquettes (PROD, QA, DEV…)

Chaque serveur de « Mes serveurs » peut porter une étiquette d'environnement, de couleur : PROD en rouge, QA en
bleu, DEV en vert au départ. Elle rappelle d'un coup d'œil où vous travaillez :

- pastille devant le nom dans l'arbre « Mes serveurs » et les listes partagées ;
- pastille sur l'onglet de la session (et `[PROD]` en tête du titre d'une fenêtre détachée) ;
- cadre de sa couleur autour du terminal SSH ou de la session de fichiers (le nom reste sur l'onglet, sans
  prendre de place au terminal) ; pastille dans l'en-tête de chaque volet de la vue parallèle et dans celui de
  l'onglet Fichiers : vous voyez sur quel environnement vous déposez un fichier.

Une session ouverte depuis « Disponibles » ou les sessions récentes prend l'étiquette du serveur de « Mes serveurs »
qui a le même compte (et la même machine cible). L'étiquette s'applique à l'ouverture : changez-la, puis rouvrez la
session pour voir la nouvelle couleur.

- **À l'ajout** d'un serveur dans « Mes serveurs », l'étiquette est demandée dans la même fenêtre que le dossier (et
  que le serveur d'un compte de domaine) ; elle suit le dossier et le serveur saisis tant que vous n'en cochez pas une
  vous-même. Une étiquette est proposée d'après le
  nom du serveur, son adresse, son safe puis ses dossiers (« prd », « prod », « production » → PROD ; « rec »,
  « recette », « qual », « uat », « preprod », « staging », « test » → QA ; « dev » → DEV ; une étiquette ajoutée dans
  les Paramètres se reconnaît à son nom). Rien n'est proposé quand le texte nomme deux environnements. Vérifiez-la :
  ce n'est qu'une proposition. « Aucune étiquette » est possible ; « Annuler » n'ajoute pas le serveur.
- **Changer** : clic droit sur un serveur ou une sélection (`Ctrl+clic`) → « Étiquette ▸ », ou sur un dossier →
  « Étiqueter les serveurs du dossier ▸ », puis l'étiquette, ou « Aucune ». « Proposer d'après le nom » étiquette
  d'un coup les serveurs qui n'en ont pas encore (ceux sans proposition restent sans étiquette). Aussi dans
  « Propriétés… ».
- **Liste des étiquettes** : Paramètres › Mes serveurs › « Étiquettes des serveurs » (nom de 16 caractères au plus,
  couleur ; 20 étiquettes au plus). Renommer une étiquette renomme celle des serveurs ; la supprimer la retire des serveurs.
- L'étiquette suit le serveur dans l'export de « Mes serveurs » et dans les listes partagées ; une étiquette absente de
  vos Paramètres s'affiche en gris. La liste des étiquettes (noms et couleurs) se diffuse à l'équipe par le
  [fichier d'environnement](#environnement-partagé).

### Exporter, importer, partager

Deux menus en haut de l'onglet, « Importer ▾ » et « Partager ▾ », à gauche des boutons « Nouveau dossier »,
« Propriétés / renommer » et « Retirer le serveur ou supprimer le dossier » (`Tab` atteint ces boutons, les flèches
passent de l'un à l'autre) :

- **Partager › Exporter Mes serveurs dans un fichier…** enregistre « Mes serveurs » dans un fichier `.json` : dossiers (même vides), nom, compte CyberArk
  (ID), étiquette, type de connexion, composant, machine cible, motif par défaut, dossier SFTP de départ. Aucun mot de passe ni
  fichier suivi. Pratique pour changer de poste ou transmettre sa liste.
- **Importer › Fichier ZillaTerm (export de Mes serveurs)…** lit un fichier exporté (ou une liste partagée) et
  résume avant d'ajouter : serveurs ajoutés, serveurs
  déjà présents (même compte, type, composant, machine cible et dossier : ignorés), dossiers créés, serveurs ouverts
  sur une machine cible (à vérifier : la machine vient du fichier). Rien n'est retiré ni modifié dans « Mes
  serveurs ». Un fichier créé pour un autre PVWA est refusé : ses ID de comptes y désignent d'autres comptes.
- **Importer › Sessions d'un autre logiciel…** : voir [ci-dessous](#reprendre-les-sessions-dun-autre-logiciel).
- **Importer › Ajouter une base KeePass…** : voir [7.](#7-accès-durgence-hors-cyberark--bases-keepass)
- **Listes partagées** : une liste de serveurs dans un fichier sur un partage réseau, que toute l'équipe ouvre et
  complète.
  - « Partager › Créer une liste partagée… » : choisissez l'emplacement (partage réseau) et le nom affiché à tous ;
    « Importer › Ouvrir une liste partagée… » : ajoutez une liste créée par un collègue. Les listes ouvertes s'affichent en tête de l'onglet
    (après les bases KeePass), avec leurs dossiers ; la recherche les filtre aussi.
  - **Ajouter** : clic droit sur un serveur ou un dossier de « Mes serveurs » → « Partager dans une liste » (le
    dossier est gardé), ou glissez un serveur, un dossier ou un compte de « Disponibles » sur la liste ou l'un de ses
    dossiers (confirmation). Le motif par défaut reste personnel : il n'est jamais partagé.
  - **Retirer** : clic droit → « Retirer de la liste partagée… » (ou `Suppr`), après confirmation. Pendant une
    recherche, « Retirer le dossier » retire tous ses serveurs, y compris ceux que la recherche masque : la
    confirmation dit combien.
  - **Utiliser** : double-clic pour se connecter ; clic droit pour les options avancées, le mot de passe, les membres
    du safe ou « Copier dans Mes serveurs ». Chacun se connecte avec ses propres droits CyberArk : un compte que vous
    ne voyez pas dans le coffre CyberArk porte la mention « ⚠ introuvable dans CyberArk ». L'info-bulle montre le compte tel que CyberArk le décrit, la machine
    cible, qui a ajouté le serveur et quand.
  - **Machine cible** : un serveur partagé qui ouvre un compte de domaine sur une machine absente des machines
    autorisées du compte dans CyberArk demande confirmation à la première connexion (la liste peut être modifiée par
    quiconque a le droit d'écrire sur le partage). « Copier dans Mes serveurs » nomme ces serveurs et demande avant
    de les copier.
  - **Liste d'un autre PVWA** : une liste créée pour un autre coffre CyberArk s'affiche, mais ses serveurs ne s'ouvrent pas et
    ne se copient pas, et rien ne peut y être ajouté : connectez-vous à ce PVWA pour l'utiliser.
  - **Historique** : clic droit → « Historique des modifications… ». L'onglet « Modifications » liste qui a ajouté,
    retiré ou restauré quoi, et quand ; l'onglet « Versions » garde une copie de la liste à chaque révision (les 100
    dernières, dans le dossier `nom.versions` à côté du fichier). Sur cet onglet, « Restaurer cette version… » remet
    la liste dans cet état pour tout le monde, après confirmation ; la restauration est elle-même enregistrée, donc
    annulable.
  - Les modifications de chacun se cumulent : le fichier est relu et modifié en exclusivité (un poste qui écrit en
    même temps attend son tour), et la liste affichée se met à jour quand un collègue la modifie (`F5` la relit
    aussi). « Fermer la liste » la retire de votre onglet sans toucher au fichier.
  - Droits : ceux du partage réseau. En lecture seule, la liste reste utilisable mais ne peut pas être modifiée.

### Reprendre les sessions d'un autre logiciel

Pour passer à ZillaTerm sans retaper vos serveurs, et ne plus vous y connecter en direct : menu « Importer ▾ » ›
« Sessions d'un autre logiciel… » en haut de l'onglet, ou menu **Paramètres** › « Importer les sessions d'un autre
logiciel… ». Les comptes du PVWA doivent être chargés.

<img src="captures/fr/session-import.png" alt="Import des sessions d'un autre logiciel : aperçu avant l'import" width="820">

1. **Source** : choisissez le logiciel, puis « Lire… ».

   | Source | Lue dans |
   | --- | --- |
   | PuTTY, KiTTY, WinSCP | votre registre Windows, directement |
   | Export du registre (`.reg`) | les sessions PuTTY et KiTTY et les sites WinSCP qu'il contient (export d'un autre poste par exemple) |
   | KiTTY portable | le dossier de KiTTY (sous-dossier `Sessions`, un fichier par session) |
   | WinSCP | le fichier `WinSCP.ini` (version portable) |
   | Fichier `.mxtsessions` ou `.ini` | ses sections de sessions, avec leurs dossiers |
   | mRemoteNG | `confCons.xml` ; un fichier chiffré en entier est refusé : exportez les connexions sans ce chiffrement |
   | Remote Desktop Connection Manager | le fichier `.rdg` (groupes, identifiants hérités ou profils d'identifiants) |
   | SecureCRT | le dossier de configuration (`Config\Sessions`) ou l'export XML des réglages |
   | OpenSSH | le fichier `config` (`%USERPROFILE%\.ssh\config`) : chaque `Host` sans joker, réglages pris comme le fait `ssh` |
   | Fichiers Bureau à distance | un dossier de fichiers `.rdp` et ses sous-dossiers |

2. **Rapprochement** : chaque session est associée à un compte du PVWA.
   - Compte du serveur lui-même : même nom, ou nom court et nom complet (`srv01` et `srv01.corp.local`) ; avec le même
     utilisateur s'il est indiqué. Aucune résolution DNS : seuls les noms comptent.
   - D'un type adapté : Windows pour le bureau à distance, Unix ou réseau pour SSH ; un autre type seulement s'il n'y
     en a pas ; jamais un compte de base de données.
   - À défaut, compte de domaine du même utilisateur (`CORP\admin`, `admin@corp.local`), autorisé sur ce serveur : le
     serveur devient sa machine cible. `CORP\admin` ne désigne jamais le compte local `admin` du serveur. Les comptes
     du domaine du serveur passent avant les autres ; une session sans domaine indiqué qui ne trouve qu'un compte d'un
     autre domaine (`admin@corp.local` pour `web01.dmz.local`) le reçoit seulement en proposition, non cochée :
     « Compte d'un autre domaine que le serveur : vérifiez-le ».
   - Une session qui passait déjà par le PSMP (`coffre@cible@serveur@psmp`) ou par PSM (programme de démarrage
     `psm /u compte /a serveur /c composant` d'un fichier `.rdp`) est décodée : ce sont le compte et le serveur cibles
     qui comptent, et le composant PSM est gardé.
   - Plusieurs comptes possibles : le plus probable est proposé (« à vérifier »), mais la session n'est pas cochée ;
     choisir un compte dans la liste de la colonne « Compte PVWA » la coche (ou cochez-la pour garder celui proposé).
     Le bouton « Importer (n) » indique le nombre de sessions qui seront ajoutées. La case de l'en-tête coche les
     lignes affichées (celles du filtre « Seulement les problèmes » quand il est coché), ou les décoche si elles le
     sont toutes ; elle est à moitié remplie quand une partie seulement est cochée.
3. **Connexion enregistrée**, jamais directe : bureau à distance par PSM ; SSH et fichiers (SFTP, SCP) via le PSMP
   s'il y en a un pour ce serveur (fichiers seuls pour une plateforme « SFTP »), sinon par PSM (`PSM-WinSCP` pour les
   fichiers) ; Telnet par `PSM-Telnet`.
4. **Dossier dans Mes serveurs** : l'arborescence de l'autre logiciel est recréée sous ce dossier (« Importés » par
   défaut ; vide : à la racine), chaque serveur gardant son nom.
   **Étiquette** : la colonne « Étiquette » propose celle de chaque session d'après son nom, son serveur, le safe du
   compte associé puis ses dossiers dans l'autre logiciel (« Prod/Linux » → PROD, « Recette » → QA), comme
   [à l'ajout d'un serveur](#étiquettes-prod-qa-dev) ; vide quand rien n'est reconnu. Changez-la au besoin avant
   d'importer ; elle figure aussi dans le résultat exporté.
5. **Importer** ajoute les sessions cochées et prêtes. Le tableau montre ensuite le résultat de chaque serveur :
   « Importé », ou « Non importé » avec la raison (aucun compte dans le PVWA, type de connexion non pris en charge
   comme VNC, FTP ou port série, décoché). Un serveur déjà dans le même dossier avec le même compte n'est pas ajouté
   une seconde fois. « Seulement les problèmes » filtre le tableau.
6. **Exporter le résultat…** enregistre ce tableau en CSV (séparateur de la région Windows) : c'est la liste des
   serveurs sans compte, à faire ajouter dans CyberArk.
7. **Fichier des comptes manquants…** enregistre les comptes à créer pour les serveurs restés sans compte, prêts pour
   « Importer des comptes (CSV) » ([section 2](#2-trouver-un-compte--onglet--disponibles-)) : une ligne par serveur et
   utilisateur pour un compte local (plateforme `WinServerLocal` pour le bureau à distance, `UnixSSH` pour SSH et
   SFTP), une ligne par utilisateur de domaine (plateforme `WinDomain`, les serveurs dans les machines autorisées).
   Complétez le safe, vérifiez les plateformes (noms de votre coffre), puis importez-le. Aucun mot de passe dedans.

Aucun mot de passe n'est lu, ni dans le registre ni dans les fichiers : seuls le serveur, le port, le protocole,
l'utilisateur et le dossier le sont. Les mots de passe gardés par l'autre logiciel restent où ils sont : une fois la
migration faite, supprimez-les de cet outil pour ne plus contourner CyberArk.

## 7. Accès d'urgence hors CyberArk : bases KeePass

Quand CyberArk est indisponible, ZillaTerm ouvre vos bases KeePass (`.kdbx`) et se connecte **directement** aux
serveurs, en SSH, en bureau à distance ou en VNC, ou à leurs seuls fichiers (SFTP, FTP, FTPS), avec les comptes
qu'elles contiennent.

> Ces connexions **ne passent pas par le PSM** : ni enregistrement, ni règles CyberArk. Chaque ouverture de
> base, connexion et modification est notée dans le journal local `%APPDATA%\ZillaTerm\urgence.log`.

![Accès d'urgence : base KeePass déverrouillée dans « Mes serveurs »](captures/fr/keepass-vault.png)

- **Sans CyberArk** : sur l'écran d'identification, « Accès d'urgence (KeePass) » ouvre la fenêtre principale sans PVWA
  (seules les bases KeePass y figurent ; l'onglet « Disponibles » et les boutons propres à CyberArk sont masqués ;
  « Quitter l'accès d'urgence » verrouille les bases et ramène à l'identification).
  Avec CyberArk, les bases apparaissent aussi en tête de l'onglet « Mes serveurs ». L'infobulle d'un onglet de
  session ouvert depuis une base le rappelle : « Accès direct d'urgence (KeePass) : hors CyberArk, noté dans
  urgence.log ».
- **Ajouter une base** : menu « Importer ▾ » › « Ajouter une base KeePass… » de l'onglet « Mes serveurs » (en accès
  d'urgence, bouton coffre-fort), ou clic droit dans l'onglet → « Ajouter une base KeePass… ». La fenêtre « Ajouter une base KeePass » rappelle en bandeau que ces connexions sont hors CyberArk ;
  « Parcourir… » choisit le fichier `.kdbx`, puis le nom et un fichier clé éventuel.
- **Déverrouiller** : double-clic sur la base. Mot de passe maître et/ou fichier clé (tous les formats de
  KeePass). « Mémoriser le mot de passe maître dans le coffre local » évite de le ressaisir (voir ci-dessous).
- **Se connecter** : double-clic sur une entrée. Le protocole vient de son adresse (`ssh://serveur:22`,
  `rdp://serveur`, `vnc://serveur`, `sftp://`, `ftp://`, `ftpes://`, `ftps://`, ou `serveur:3389`), d'un champ
  « Protocol » / « Port » ou d'une étiquette (`ssh`, `rdp`, `vnc`, `sftp`, `ftp`, `ftpes`, `ftps`) ; sinon
  ZillaTerm demande le protocole. Le mot de passe de l'entrée est utilisé directement ; il n'est jamais affiché ni
  écrit sur disque.
  - **SSH** : onglet terminal + Fichiers. À la première connexion, l'empreinte de la clé du serveur est à comparer
    avec celle que donne son administrateur, dans la même fenêtre que pour le PSMP (voir
    [section 4](#4-ouvrir-une-session-ssh-via-le-psmp)). Le mot de passe de l'entrée n'est envoyé qu'une fois par
    connexion : un mot de passe périmé dans la base ne compte pas plusieurs échecs sur le serveur (risque de
    verrouillage du compte), et « Annuler » coupe la connexion sans rien envoyer.
  - **Bureau à distance** : onglet avec le client FreeRDP embarqué par ZillaTerm (à défaut, le contrôle Bureau à
    distance de Windows, qui propose aussi « Plein écran », `Ctrl+Alt+Pause` pour revenir), avec « Déconnecter » et
    « Reconnecter ». Avant chaque connexion, ZillaTerm lit le
    certificat du serveur. S'il n'est pas approuvé par Windows (auto-signé…), son sujet, son émetteur, ses dates et
    son empreinte SHA-256 sont montrés à la première connexion, puis il est mémorisé pour ce serveur ; s'il change
    ensuite, l'empreinte mémorisée et la nouvelle sont affichées, et il faut cocher « J'ai confirmé ce changement
    avec l'administrateur du serveur ». Le mot de passe n'est lu qu'après cette vérification ; FreeRDP le reçoit par
    son entrée standard (jamais sur la ligne de commande) et n'accepte que le certificat vérifié. Une entrée sans
    mot de passe échoue à l'authentification. Un serveur qui n'accepte pas le chiffrement TLS est refusé. Aucune
    image de la session n'est gardée sur le disque du poste, y compris dans la Connexion Bureau à distance ouverte
    sans client dans ZillaTerm (le mot de passe y est alors à saisir).
  - **VNC** (`vnc://serveur`, port 5900 ; `vnc://serveur:1` désigne l'écran 1, port 5901) : bureau dans un onglet,
    ajusté à la fenêtre ou en taille réelle (« Ajuster »), boutons « Ctrl+Alt+Suppr » (après confirmation : selon
    la machine, il ouvre l'écran de sécurité ou redémarre certaines consoles de machines virtuelles), « Envoyer le
    presse-papiers » et « Copier le texte distant » : le presse-papiers n'est échangé que par ces boutons. Authentification par mot de
    passe VNC (8 caractères au plus, limite du protocole) ou sans authentification. **VNC ne chiffre rien** : un
    bandeau le rappelle ; réservez-le à un réseau de confiance. Avant le premier envoi du mot de passe VNC, ZillaTerm
    demande votre accord (« Envoyer le mot de passe et se connecter », une fois par onglet, reconnexions comprises) :
    VNC ne vérifie pas l'identité du serveur, et un faux serveur ou un intermédiaire peut retrouver les 8 premiers
    caractères du mot de passe. Les bips du serveur sont limités à un par seconde.
  - **Fichiers** (`sftp://`, `ftp://`, `ftpes://` pour FTP avec TLS explicite, `ftps://` pour TLS implicite, port
    990) : un onglet d'état, sans terminal, et les fichiers dans l'onglet « Fichiers » avec les mêmes fonctions
    (transferts vérifiés par SHA-256, file d'attente, historique, éditeur, comparaison, suivi en direct, droits si
    le serveur accepte `SITE CHMOD`). Clic droit → « Ouvrir les fichiers (SFTP, FTP) » fait de même pour une entrée
    SSH, en SFTP. En `ftp://`, le chiffrement TLS est tenté d'abord ; si le serveur ne le propose pas, ZillaTerm
    demande avant de se connecter en clair (« Se connecter sans chiffrement », une fois par session) et un bandeau le
    rappelle. `ftpes://` et `ftps://` ne passent jamais en clair. Un certificat FTPS que Windows n'approuve pas
    (auto-signé…) est montré avec son sujet, son émetteur, ses dates de validité et son empreinte SHA-256 (avec
    « Copier »), puis mémorisé pour ce serveur si vous l'acceptez ; s'il change ensuite, l'empreinte mémorisée et la
    nouvelle sont affichées, et il faut cocher « J'ai confirmé ce changement avec l'administrateur du serveur ». Un
    serveur qui a déjà chiffré une connexion est mémorisé : s'il se présente ensuite sans TLS (ce qu'un intermédiaire
    peut obtenir), ce n'est plus la simple question mais l'alerte « Ce serveur FTP ne propose plus de chiffrement »,
    qui ne laisse continuer qu'après avoir coché « J'ai confirmé avec l'administrateur du serveur le retrait du
    chiffrement » ; sinon la connexion s'arrête sans envoyer le mot de passe.
- **Modifier la base** : clic droit → « Nouvelle entrée… », « Modifier… » (`F2`), « Supprimer » (`Suppr`, vers la
  corbeille de la base, après confirmation). L'adresse du serveur est obligatoire : une entrée sans adresse (ni
  dans le champ Adresse, ni dans ses champs personnalisés) n'est pas enregistrée. Les autres données de la base
  (pièces jointes, champs, réglages) sont gardées ; l'ancienne version d'une entrée va dans son historique, comme
  dans KeePass. Pendant l'enregistrement, la version précédente du fichier est gardée dans `base.kdbx.bak`, puis
  supprimée dès que le nouveau fichier est relu à l'identique : aucune ancienne copie, qui s'ouvrirait encore avec un
  ancien mot de passe maître, ne reste à côté de la base (partagée ou non). Exception : si un autre poste a enregistré
  la base au même moment, sa version est gardée dans le `.bak` plutôt que perdue.
- **Verrouiller** : clic droit → « Verrouiller ». Les bases se verrouillent aussi à la déconnexion de CyberArk, à
  la fermeture et au **verrouillage de Windows**.

**Coffre local** : les mots de passe maîtres que vous choisissez de mémoriser sont gardés dans
`%APPDATA%\ZillaTerm\coffre-local.dat`, chiffré avec un mot de passe à vous (8 caractères au moins) et lié à
votre compte Windows. Ce mot de passe est demandé au déverrouillage d'une base KeePass dont le mot de passe est
mémorisé ; « Plus tard » (proposé seulement à ce moment-là) permet de saisir plutôt le mot de passe de la base.
Si le coffre local n'est pas ouvert, la base s'ouvre quand même, et la barre d'état signale que son mot de passe
maître n'a pas été mémorisé. Gestion dans les **Paramètres**, page Sécurité : « Créer le coffre… »,
« Déverrouiller… », « Changer le mot de passe… », « Supprimer maintenant… » ; ces actions s'appliquent tout de
suite, sans « Enregistrer ». Il se verrouille à la déconnexion de CyberArk (« Accès d'urgence » ne rouvre ainsi jamais les
bases mémorisées sans mot de passe), à la fermeture et au verrouillage de Windows.

## 8. Intervention : enregistrement et consignes d'astreinte

<img src="captures/fr/intervention-window.png" alt="Fenêtre Intervention : consignes d'astreinte, enregistrement et interventions enregistrées" width="34%"> <img src="captures/fr/intervention.png" alt="Onglet Intervention pendant l'enregistrement : titre, note et historique à côté de la session" width="64%">

Le bouton **« Intervention »** de la barre d'outils ouvre une fenêtre utile à toute intervention, d'astreinte ou non :

- **Les consignes d'astreinte** : bonnes pratiques, numéros d'urgence, contacts… Elles viennent :
  - du texte de **Paramètres › Intervention › Consignes d'astreinte**, diffusé à toute l'équipe par le
    [fichier d'environnement](#environnement-partagé) ;
  - ou d'un **fichier texte** indiqué dans **Paramètres › Intervention › Fichier des consignes** (sur un partage réseau en
    général ; lui aussi diffusable par le fichier d'environnement). Il est relu à chaque ouverture de la fenêtre : les
    responsables le mettent à jour sans rediffuser l'environnement. Son chemin s'affiche sous le titre ; s'il est
    illisible (absent, accès refusé, partage qui ne répond pas en 5 secondes), le texte des Paramètres s'affiche, avec
    la raison. UTF-8 ou UTF-16 (le Bloc-notes convient) ; seuls les 64 premiers Ko sont lus.

  Elles s'affichent en texte brut (sélectionnable pour copier un numéro) : aucun lien ni mise en forme n'est
  interprété, et les caractères invisibles d'un fichier (qui pourraient inverser l'ordre des chiffres d'un numéro)
  sont retirés.
- **« Enregistrer l'intervention »** : à partir de là, ZillaTerm garde tout ce qui se passe, jusqu'à « Arrêter
  l'enregistrement » (ou « Terminer l'intervention » dans l'onglet Intervention) ou la fermeture de ZillaTerm.
  L'enregistrement continue après une déconnexion de CyberArk et en accès d'urgence. Une pastille rouge sur le bouton et
  « ● Intervention enregistrée » dans la barre d'état le rappellent. Sont gardés :
  - le **texte des terminaux SSH** (commandes tapées et leur résultat, ligne par ligne, avec l'heure ; pas l'écran
    de vim, less ou top, ni la commande que ZillaTerm tape lui-même pour suivre le dossier) ;
  - les **connexions** : ouverture, échec et fermeture des sessions SSH, fichiers, Bureau à distance et VNC, et les
    sessions PSM avec le composant, la machine cible, le motif et le ticket ;
  - les **actions** : mot de passe copié (jamais le mot de passe), demandes au CPM, comptes ajoutés, modifiés,
    supprimés ou importés, fenêtres X11, passage en accès d'urgence, déconnexion ;
  - les **transferts de fichiers**, avec le SHA-256 de chaque fichier ;
  - le **titre** de l'intervention et vos **notes** (onglet « Intervention », ci-dessous) ;
  - les **captures** : « Capturer un Bureau à distance… » (onglet « Intervention ») prend une image d'une fenêtre
    Connexion Bureau à distance (sessions PSM ; une fenêtre réduite ne peut pas l'être), et le bouton « Capture » des
    onglets Bureau à distance de ZillaTerm prend celle du bureau affiché ;
  - l'**écran des sessions Bureau à distance ouvertes dans un onglet**, comme le protocole RDP l'envoie : l'onglet
    affiché est capturé chaque seconde et seules les zones modifiées sont gardées (carrés de 64 × 64 pixels en couleurs
    16 bits, compressés ; une zone déjà vue, comme un menu qui revient, n'est gardée qu'une fois). « ● Écran
    enregistré » dans la barre de l'onglet le rappelle. Un onglet en arrière-plan, une fenêtre réduite et le plein écran
    ne sont pas capturés ; de retour sur l'onglet, ce qui a changé entre-temps est gardé. Un petit changement isolé (un
    caractère tapé) l'est dès que l'écran ne bouge plus pendant 3 secondes, au plus tard après une minute ; un écran
    immobile ne coûte rien. Au-delà de **100 Mo de journal**, l'écran n'est plus gardé qu'une fois toutes les 10
    secondes : le journal le note, l'onglet « Intervention » le dit et l'indicateur devient « ● Écran enregistré (toutes
    les 10 s) ». L'ouverture de la session (écran du PSM, ouverture de session Windows, bureau, application) coûte
    quelques écrans entiers, de 0,1 à 1 Mo chacun selon la taille de l'écran et le fond. Avec le journal de débogage,
    chaque minute un bilan donne ce que l'écran a coûté (captures, images gardées dont complètes, poids, taille du
    journal) : du poids sur un écran immobile montre une image qui change sans que cela se voie. Une session ouverte
    avec Connexion Bureau à distance (mstsc) n'est pas enregistrée ainsi : seule la capture la garde.
- **Onglet « Intervention » du panneau de gauche** (`Ctrl+4`) : il apparaît au début de l'enregistrement, avec une
  pastille rouge tant que l'enregistrement dure : titre, notes et historique restent à côté de la session. Ouvrir une
  session SSH ne le remplace pas par l'onglet « Fichiers ». Un clic sur « ● Intervention enregistrée » dans la barre
  d'état ou sur **« Titre, notes et historique »** dans la fenêtre Intervention y ramène. On y trouve :
  - le **titre de l'intervention** (ticket, incident, serveur…), gardé par `Entrée` ou en quittant le champ : il est
    écrit dans le journal, repris dans la liste « Interventions enregistrées », en tête du rapport et dans le nom
    proposé pour son fichier. Une ligne, 200 caractères au plus ;
  - la **note** : `Entrée` l'ajoute au journal, `Maj+Entrée` passe à la ligne. **« Modifier »** sur une note de
    l'historique (ou `F2`) la reprend dans le champ : `Entrée` ou « Enregistrer la modification » la remplace à sa
    place, `Échap` ou « Annuler » rend la saisie en cours. La note porte alors « modifiée à … » ; le journal garde le
    texte d'origine et le rapport montre les deux. Seules les notes de l'enregistrement en cours se modifient ;
  - **« Capturer un Bureau à distance… »** ;
  - l'**historique** de l'intervention, au fil de l'eau : début, titre, notes, connexions, actions, transferts (avec
    leur SHA-256), captures et texte des terminaux. Une commande et son retour forment un bloc (un nouveau bloc commence
    après 2 secondes sans sortie) ; un bloc de plus de 8 lignes est réduit à ses 6 premières, et **« Tout afficher
    (N lignes) »** le déplie en entier (texte sélectionnable), « Réduire » le replie. `Ctrl+C` copie l'événement choisi.
    La liste suit les nouveaux événements tant qu'elle est en bas. L'historique est gardé en mémoire seulement ; son
    texte des terminaux est limité à 8 millions de caractères (au-delà, un message le dit, et le texte reste dans le
    rapport).

  **« ■ Terminer l'intervention »**, en bas de l'onglet, arrête l'enregistrement après confirmation (`Entrée` confirme,
  `Échap` ou « Continuer l'intervention » annule) ; une note en cours de saisie est d'abord ajoutée au journal. À
  l'arrêt, d'ici ou depuis la fenêtre Intervention, l'onglet disparaît et la fenêtre Intervention montre l'intervention
  dans « Interventions enregistrées », prête pour « Exporter le rapport… ».
- **Rappels pendant l'intervention** (Paramètres › Intervention, ou fichier d'environnement de l'équipe) : une ligne par
  rappel, les minutes depuis le début de l'enregistrement puis le texte (« 30 Escalader au N3 »), 10 au plus. La barre
  d'état montre le prochain (« ● Intervention enregistrée · Escalader au N3 dans 12 min ») ; à l'heure dite, un bandeau
  s'affiche en tête de l'onglet « Intervention », la barre d'état le dit et le bouton de ZillaTerm clignote dans la
  barre des tâches, sans prendre le clavier (la frappe en cours ne part pas ailleurs). Le rappel est noté au journal, et
  **« Vu »** note que vous l'avez vu.
- **ZillaTerm arrêté pendant l'enregistrement** (plantage, poste redémarré) : au lancement suivant, dans les 12 heures,
  ZillaTerm propose **« Reprendre l'enregistrement »** : la suite s'ajoute au même journal (titre, historique et rappels
  repris ; un seul rapport), après un événement qui dit qui a repris et depuis quand rien n'a été gardé. **« Clore
  l'intervention »** la termine telle quelle (sa durée ne change pas) ; **« Plus tard »** la laisse, et la question
  revient au lancement suivant.
- **« Interventions enregistrées »** : la liste des enregistrements, du plus récent au plus ancien, avec leur titre. À
  l'arrêt, l'intervention reste sélectionnée : **« Exporter le rapport… »** (ou double-clic) en fait une page HTML :
  résumé, chronologie, blocs de texte des terminaux et captures, à joindre au compte rendu d'intervention. Les écrans
  enregistrés y ont chacun leur lecteur : « Lecture », « Vitesse » (1× à 60×) et un curseur de position, sous lequel un
  graphique montre l'activité de l'écran (part modifiée à chaque instant) : un clic sur un pic y mène. Les notes y sont
  posées à leur heure (repères orange) : un clic y mène, le survol montre leur texte ; la ligne de temps va jusqu'à la
  dernière trace de la session (fermeture de l'onglet…), si bien qu'une note prise devant un écran immobile y figure.
  Les pauses de l'affichage (onglet en arrière-plan, ZillaTerm réduit, Windows verrouillé : le serveur n'envoyait plus l'image) y sont des
  bandes grises, de leur début à leur fin, avec leur raison au survol : un clic y mène, et l'écran figé porte alors
  « Affichage en pause ». Le
  lecteur est le seul script de la page, autorisé par son empreinte ; il demande un navigateur récent (Edge, Chrome,
  Firefox). Une image manquante (journal abîmé) fige l'écran jusqu'à la prochaine image complète, avec un avis.
  **« Supprimer… »** efface un enregistrement terminé.

  Plusieurs interventions choisies ensemble (`Ctrl+clic`, `Maj+clic`) donnent **un seul rapport** : leurs événements à
  la suite et, en tête, la liste des enregistrements (une intervention sur la nuit et le matin, par exemple) ; elles se
  suppriment aussi ensemble. **« Masquer ce qui ressemble à un secret »** (coché par défaut) remplace dans le rapport,
  par « [masqué] », les mots de passe (`password=…`, `"client_secret": "…"`, `--db-password=…`, celui d'une adresse
  `https://jdoe:…@srv01.corp.local` ; un mot de passe qui commence par `$` ou `%` aussi, sauf s'il a la forme d'une
  variable, laissée lisible : `$DB_PASS`, `${VAULT_PASS}`, `$(cat …)`, `%DB_PASS%`), le corps des clés privées (`-----BEGIN … PRIVATE KEY-----`), les en-têtes `Bearer`
  et `Basic`, les jetons de format connu (JWT, GitHub, GitLab, Slack, AWS, Google) et les empreintes de mot de passe
  (`$6$…` de /etc/shadow) ; le rapport dit combien de passages l'ont été. Les images (captures, écrans enregistrés) ne
  sont pas masquées, et le journal chiffré garde tout. Le masquage est un filet : relisez le rapport avant de le
  transmettre.

Le journal est **chiffré pour votre compte Windows seul** (DPAPI) dans `%LOCALAPPDATA%\ZillaTerm\Interventions` (le
titre aussi, dans un fichier `.title` à côté, pour la liste), bloc par bloc au fil de l'enregistrement, son texte
compressé (un arrêt brutal ne perd que les deux dernières secondes, et le rapport le signale). Un bloc retiré, déplacé
ou modifié est signalé dans le rapport. ⚠ Les mots de passe tapés sans écho n'y figurent pas, mais **tout ce qu'affiche
un terminal ou un Bureau à distance dans un onglet y est** (contenu d'un fichier de configuration, par exemple) ; le
rapport exporté n'est **pas chiffré** : rangez-le en lieu sûr. Le texte des terminaux est plafonné à 64 millions de
caractères par intervention (les autres événements continuent d'être gardés au-delà) ; un très gros affichage est écrit
en plusieurs blocs, sans perdre la suite du rapport. Une déconnexion annulée à la confirmation n'y figure pas.

**Disque qui refuse le journal** (plein, quota, accès retiré) : la barre d'état affiche « ⚠ Intervention :
enregistrement interrompu » en rouge, et la fenêtre « Enregistrement interrompu à 14:10. » : plus rien n'est gardé
depuis cette heure-là, le titre, la note et la capture sont grisés dans l'onglet « Intervention ». Libérez de la place,
puis « Arrêter l'enregistrement » et « Enregistrer l'intervention » pour reprendre dans un nouvel enregistrement.

## 9. Partager une session avec un collègue

Montrer en direct une session (terminal SSH, Bureau à distance dans un onglet, VNC) à une autre personne qui a
ZillaTerm, et regarder les siennes : pour se faire aider, former quelqu'un ou travailler à deux pendant une
intervention. Plusieurs sessions peuvent être partagées en même temps, plusieurs personnes peuvent regarder la même, et
un même ZillaTerm peut partager et regarder plusieurs sessions à la fois.

**Activer** : Paramètres › Partage › « Permettre de partager mes sessions avec un autre ZillaTerm » (désactivé par
défaut ; regarder la session d'une autre personne fonctionne sans). Puis, par où les autres vous rejoignent :

- **Connexions directes à ce poste** : l'autre ZillaTerm se connecte à votre poste (port libre choisi au hasard, ou
  port fixe). La première fois, le pare-feu Windows peut demander d'autoriser ZillaTerm : autorisez-le sur les réseaux
  privés ou de domaine seulement.
- **Dossier relais** : un dossier d'un partage réseau accessible aux deux postes (`\\serveur\partage\dossier`), utilisé
  quand la connexion directe n'aboutit pas (pare-feu, VPN). La personne qui rejoint essaie d'abord la connexion
  directe, puis le dossier. Si l'invitation propose un dossier relais qui n'est pas celui de ses Paramètres, ZillaTerm
  le montre et demande avant de l'utiliser (« Passer par ce dossier relais ? ») : Windows s'y connecte avec son compte,
  il ne doit donc s'agir que du dossier relais de l'équipe.

**Partager une session**

1. Clic droit sur l'onglet de la session (ou dans le terminal) → « Partager la session… ».
2. Choisissez la durée (de 15 minutes à 4 heures) et, pour un terminal, « Permettre de donner la main ». Puis
   « Commencer le partage ».
3. Envoyez l'invitation (`zillaterm-share:1:…`, bouton « Copier l'invitation ») à la personne, par la messagerie de
   l'entreprise. L'invitation permet seulement de **demander** : rien n'est montré sans votre accord.
4. Quand la personne l'utilise, un bandeau au-dessus de la session dit « Paul Martin (CORP\pmartin, poste PC-PMARTIN)
   demande à regarder », et la fenêtre clignote dans la barre des tâches. « Répondre… » affiche un **code de
   vérification à 6 chiffres** : la personne voit le même chez elle. Comparez-le avec elle (téléphone, messagerie),
   cochez « J'ai vérifié le code avec cette personne », puis « Accepter ». Un code différent veut dire que quelqu'un
   d'autre s'interpose : refusez. Sans réponse, la demande expire au bout de 3 minutes.

<img src="captures/fr/share-request.png" alt="Demande d'une personne pour regarder une session partagée, avec le code de vérification" width="554">

Tant que la session est partagée, son bandeau dit qui regarde, qui a la main et jusqu'à quand, avec « Gérer… » et
« Arrêter le partage » ; la barre d'état compte les sessions partagées et regardées, et les demandes en attente.

**Donner la main** (terminal seulement, si le partage le permet) : la personne clique « Demander la main » ; dans le
bandeau, « Donner la main… » puis confirmez. Ce qu'elle tape part dans votre session **sous votre identité** (le PSMP
et le serveur l'enregistrent à votre nom) : restez devant la session. « Reprendre la main » remet tout le monde en
lecture seule. Une seule personne a la main à la fois. Bureau à distance et VNC restent toujours en lecture seule.

**Regarder la session d'une autre personne** : bouton « Partage » de la barre d'outils → « Rejoindre une session
partagée… », collez l'invitation (le texte autour, dans le message, n'est pas gênant), puis « Rejoindre ». Donnez à la
personne le code affiché et attendez son accord : la session s'ouvre dans un onglet. Le terminal garde la taille de
celui de la personne qui partage, agrandi ou réduit pour tenir dans l'onglet ; rien de ce que vous tapez n'est envoyé
tant que vous n'avez pas la main. Avec la main, un bandeau orange le rappelle ; « Rendre la main » revient à la lecture
seule. À la fin du partage, l'onglet dit pourquoi (arrêté, durée écoulée, poste verrouillé, connexion perdue…) et
garde la dernière image.

**Gérer** : bouton « Partage » de la barre d'outils (ou clic sur l'indicateur de la barre d'état) : vos sessions
partagées et leurs personnes (répondre, donner ou reprendre la main, exclure, réafficher l'invitation pour une autre
personne, arrêter), et les sessions que vous regardez (afficher, quitter).

**Le partage s'arrête** à la fin de sa durée, quand vous l'arrêtez, à la fermeture de la session ou de son onglet, au
**verrouillage de Windows**, à la déconnexion de CyberArk et à la fermeture de ZillaTerm. Une session Bureau à distance
n'est montrée que lorsque son onglet est affiché (ni en plein écran, ni fenêtre réduite) : sinon l'image reste figée
chez la personne qui regarde, qui en est avertie.

Pendant l'enregistrement d'une [intervention](#8-intervention--enregistrement-et-consignes-dastreinte), le journal note
le début et la fin de chaque partage, chaque demande (poste et adresse), chaque accord (avec le code vérifié), refus,
départ ou exclusion, la main donnée et reprise ; côté invité, la session regardée et la main reçue.

⚠ Tout ce qui s'affiche dans la session partagée est visible des personnes acceptées : n'affichez ni mot de passe ni
secret pendant le partage.

## Raccourcis

| Où | Action | Raccourci |
| --- | --- | --- |
| Hors terminal et onglet Fichiers | Recharger les comptes depuis le PVWA | `F5` |
| Hors terminal | Filtrer les comptes (dans « Mes serveurs » : rechercher un serveur ; dans « Fichiers » : filtrer le dossier) | `Ctrl+F` |
| Partout | Onglet de session suivant / précédent | `Ctrl+Tab` / `Ctrl+Maj+Tab` |
| Partout | Fermer l'onglet de session | `Ctrl+F4` ou `Ctrl+Maj+W` |
| Partout | Onglets « Disponibles », « Mes serveurs », « Fichiers », « Intervention » du panneau de gauche | `Ctrl+1`, `Ctrl+2`, `Ctrl+3`, `Ctrl+4` |
| Partout | Paramètres | `Ctrl+,` |
| Hors terminal | Connexion rapide (onglet Accueil) | `Ctrl+K` |
| Hors terminal | Passer du panneau de gauche à la session et retour | `F6` |
| Hors terminal | Replier / déplier le panneau de gauche | `Ctrl+B` (ou double-clic sur le séparateur) |
| Listes et arbres | Ouvrir la session | Double-clic ou `Entrée` |
| Listes, arbres, onglets | Menu du clic droit | Touche Menu ou `Maj+F10` |
| Recherche | Effacer le filtre | `Échap` ou ✕ au bout du champ |
| Accueil | Retirer une session récente de la liste | `Suppr` |
| Mes serveurs | Renommer / retirer ou supprimer | `F2` / `Suppr` |
| Mes serveurs | Choisir plusieurs serveurs (puis clic droit pour les ouvrir ensemble) | `Ctrl+clic`, `Maj+clic`, `Ctrl+Espace`, `Maj+↑` / `Maj+↓` ; `Échap` annule |
| Intervention | Modifier la note choisie dans l'historique | `F2`, puis `Entrée` ; `Échap` annule |
| Terminal | Copier | Sélection à la souris, ou `Ctrl+Maj+C` |
| Terminal | Coller | `Maj+Inser` ou `Ctrl+Maj+V` (clic droit avec l'option des Paramètres) |
| Terminal | Menu : copier, coller, tout sélectionner, rechercher, enregistrer, effacer l'historique, police, actions de l'onglet | Clic droit ou touche Menu (Maj+clic droit avec l'option de collage) |
| Terminal | Historique | Molette, barre de défilement, `Maj+Page préc.` / `Maj+Page suiv.` ; « ↓ Revenir à la fin » |
| Terminal | Rechercher (historique compris) | `Ctrl+Maj+F`, puis `Entrée` / `Maj+Entrée` |
| Terminal | Enregistrer le contenu dans un fichier | `Ctrl+Maj+S` |
| Terminal | Taille de police / taille par défaut | `Ctrl+molette` / `Ctrl+0` |
| Comparaison | Différence suivante / précédente | `F7` / `Maj+F7` |
| Onglet SSH ou Bureau à distance | Fermer | Croix de l'onglet ou clic molette |
| Onglet SSH ou Bureau à distance | Reconnecter, dupliquer (autre session sur le même compte ou la même entrée), détacher (SSH), fermer, fermer les autres onglets | Clic droit sur l'onglet |
| Onglet SSH | Détacher dans une fenêtre séparée (autre écran) | Glisser l'onglet hors de la fenêtre |
| Onglet SSH | Ajouter à la vue parallèle, ou l'en retirer | Clic droit sur l'onglet |
| Bureau à distance | Plein écran / retour | `Ctrl+Alt+Pause` |
| Fichiers | Ouvrir le dossier ou modifier le fichier / modifier / renommer / dossier parent / supprimer / actualiser | Double-clic ou `Entrée` / `F4` / `F2` / `Retour arrière` / `Suppr` / `F5` |
| Fichiers | Trier par une colonne, puis inverser | Clic sur son en-tête |
| Fichiers | Aller à l'élément dont le nom commence par ces lettres (la même lettre de nouveau : le suivant) | Taper le début du nom |
| Base KeePass | Se connecter / modifier / supprimer une entrée | Double-clic ou `Entrée` / `F2` / `Suppr` |

Dans un terminal, `F5`, `Ctrl+F`, `Ctrl+K`, `Ctrl+B` et `F6` sont envoyés au serveur (`F6` aux applications comme
mc) ; `Ctrl+Tab`, `Ctrl+F4`, `Ctrl+Maj+W` et `Ctrl+1/2/3/4` restent à ZillaTerm. Dans une session Bureau à distance, les
touches vont à la session distante.

**Clavier et accessibilité** : la barre d'outils s'atteint avec `Tab` (le focus est visible), chaque menu et chaque
fenêtre a ses touches d'accès (`Alt` + lettre soulignée, sans doublon, en français, anglais et italien), et le menu
d'un élément de « Mes serveurs » s'ouvre au même endroit par clic droit, `Maj+F10` ou la touche Menu. Les champs de
mot de passe (identification, base KeePass, coffre local) avertissent quand Verr. Maj est activé. Les lecteurs
d'écran annoncent le nom des éléments des listes et des arbres, des boutons à icône ou à symbole (✕, ↑, ↓), des
champs (chemin, numéro de ticket…), chaque bouton et chaque case par son libellé, les messages de la barre d'état
et les erreurs de connexion ; un mot de passe refusé est annoncé avec le numéro de l'essai (« Essai 2 sur 3 »), lu
aussi avec le champ, et un serveur qui ne répond plus ou une session terminée ne le sont qu'une fois. Sur la ligne
sélectionnée des arbres et des tableaux, les textes secondaires (compteurs, « correspond : … », ⚠) restent lisibles,
avec ou sans le focus. Les barres d'outils
de « Mes serveurs » et de l'onglet Fichiers sont un seul arrêt de `Tab` (les flèches passent d'un bouton à l'autre).
Dans les Paramètres, chaque case a sa touche d'accès, sans doublon dans la page affichée, et la pastille « Équipe »
est annoncée avec son infobulle. En contraste élevé, l'interface prend les couleurs système de Windows et suit leurs
changements.

**Thème sombre** : Paramètres › Général › Thème : « Comme Windows » (par défaut : le mode des applications
choisi dans Windows, Personnalisation → Couleurs, suivi même quand il change), « Clair » ou « Sombre ». Le changement
s'applique tout de suite, aux fenêtres ouvertes comme à la barre de titre ; le contraste élevé de Windows l'emporte
toujours. Les couleurs des terminaux SSH se règlent à part (page Terminal).

## Paramètres et fichier de configuration

<img src="captures/fr/settings.png" alt="Paramètres" width="480">

Bouton « Paramètres » de la barre d'outils → « Paramètres… » (ou `Ctrl+,`). La fenêtre, redimensionnable, a une page
par sujet (à gauche, `Ctrl+Tab` de l'une à l'autre) : Général, CyberArk, SSH, Bureau à distance, Terminal, Fichiers,
Mes serveurs, Intervention, Partage, Sécurité ; chacune tient sur un écran. Chaque réglage a sa ligne d'aide dessous ;
« ⚠ » y signale un vrai risque. « Enregistrer » applique les réglages ; une valeur incorrecte affiche la page du champ
concerné, curseur dans ce champ.

Tout s'applique à « Enregistrer », un environnement importé compris ; seules les actions du coffre local (page
Sécurité) s'appliquent tout de suite, et le disent. « En savoir plus sur cette page » (ou `F1`), en bas de la fenêtre,
ouvre la section de la page affichée dans ce guide en ligne (version installée, langue de l'interface).

**Pastille « Équipe »** : à droite d'un réglage, elle indique que le [fichier d'environnement](#environnement-partagé)
de l'équipe (à côté de l'exécutable ou fichier central) le définit, avec la valeur en vigueur ; son infobulle donne le
fichier. Le réglage reste modifiable ici, mais une nouvelle version du fichier pourra le changer (toujours après votre
accord). Un réglage que vous avez refusé à l'import, ou changé depuis, n'a pas de pastille.

<a id="settings-general"></a>

#### Page Général

| Paramètre | Rôle | Défaut |
| --- | --- | --- |
| Langue de l'interface | Français, English, Italiano ou langue du système ; appliquée après la déconnexion de CyberArk ou au prochain démarrage | langue de Windows (anglais si elle n'est pas traduite) |
| Thème | Comme Windows, Clair ou Sombre ; appliqué tout de suite (le contraste élevé de Windows l'emporte) | comme Windows |
| Confirmer avant de fermer une session connectée | SSH, Bureau à distance, VNC ; « Ne plus demander » dans la confirmation décoche ce réglage | oui |
| Rechercher une nouvelle version au démarrage | Une requête vers GitHub au plus une fois par jour ; lien dans la barre d'état si une version plus récente existe (la fenêtre « À propos » rappelle ce réglage) | non |
| Fichier central | Fichier d'environnement de l'équipe sur un partage réseau, relu à chaque démarrage ; ses changements sont montrés avant d'être appliqués (voir [Environnement partagé](#environnement-partagé)) | vide |
| Importer, exporter l'environnement | « Importer un environnement… » : ses changements sont montrés, puis repris dans les pages et appliqués à « Enregistrer » (« Annuler » les abandonne). « Exporter l'environnement… » : les valeurs affichées, ni mot de passe ni donnée personnelle (voir [Environnement partagé](#environnement-partagé)) | — |
| Journal de débogage | Déroulement des connexions dans un fichier, sans secret (voir [Sécurité](#sécurité)) ; « Afficher le fichier » l'ouvre dans l'Explorateur. Aussi dans le menu du bouton Paramètres | non |

<a id="settings-cyberark"></a>

#### Page CyberArk

| Paramètre | Rôle | Défaut |
| --- | --- | --- |
| Garder la session ouverte pendant que je travaille | Requête légère toutes les 4 minutes, tant que vous utilisez le poste ; suspendue quand Windows est verrouillé ou après 15 minutes sans clavier ni souris (la session CyberArk expire alors selon son délai d'inactivité) | oui |
| Afficher tout de suite la liste des comptes de la dernière connexion | Copie chiffrée (compte Windows) de la dernière liste complète, une par PVWA et utilisateur, sans mot de passe, supprimée après 30 jours : affichée aussitôt, puis remplacée par la liste relue au PVWA. Décochée : les copies sont supprimées | oui |
| Composant des comptes Windows | Composant PSM des comptes Windows (domaine ou locaux) sans composant mémorisé pour leur plateforme, par exemple `WIN-PSM` | vide = `PSM-RDP` |
| Composant par plateforme | Tableau Plateforme (ID du PVWA, par exemple `WinDomain`) / Composant PSM : « Ajouter un composant », « Enlever la ligne », cellules modifiables ; prioritaire sur le composant des comptes Windows. « Mémoriser ce composant pour la plateforme » (fenêtre de connexion) y ajoute une ligne | vide |

<a id="settings-ssh"></a>

#### Page SSH

| Paramètre | Rôle | Défaut |
| --- | --- | --- |
| Ouvrir les sessions SSH dans ZillaTerm | Terminal et onglet Fichiers intégrés ; décochée : Windows Terminal | oui |
| PSMP par défaut, port | Serveur PSM for SSH ; renseigné (ou un PSMP par domaine), les comptes Unix s'ouvrent en SSH par défaut (en fichiers seuls pour une plateforme « SFTP ») ; sans aucun PSMP, SSH et SFTP sont désactivés | vide, 22 |
| PSMP par domaine | Autres PSMP (adresse, port, domaine servi) ; chaque serveur passe par celui du domaine le plus proche du sien (voir [PSMP par domaine](#psmp-par-domaine)) ; « Quel PSMP pour le serveur » pour vérifier | aucun |
| Permettre à l'onglet Fichiers de suivre le dossier du terminal | Ajoute une commande à `PROMPT_COMMAND` du shell distant à l'ouverture de la session (voir [Suivi du dossier du terminal](#suivi-du-dossier-du-terminal)) | oui |
| Serveur X local | Serveur X de ce poste pour le transfert X11 (`:0` = port 6000) ; ce poste seulement. Le transfert X11 se coche dans les propriétés de chaque serveur de « Mes serveurs » | `:0` |

<a id="settings-rdp"></a>

#### Page Bureau à distance

| Paramètre | Rôle | Défaut |
| --- | --- | --- |
| Ouvrir les sessions PSM Windows dans un onglet de ZillaTerm | Session PSM (bureau ou application distante) dans un onglet, avec le client FreeRDP embarqué, à la taille de l'onglet (voir [Ouvrir une session PSM](#3-ouvrir-une-session-psm-bureau-à-distance)) ; décochée : Connexion Bureau à distance de Windows | non |
| Adapter la résolution à la taille de l'onglet | Sessions dans un onglet : la résolution suit la taille de l'onglet au lieu d'une image mise à l'échelle ; la connexion du PSM vers la cible peut garder sa taille d'origine | oui |
| Disques du poste partagés avec les sessions PSM Windows | **Aucun**, **Tous les disques** ou **Ces dossiers seulement** (voir [Disques du poste](#3-ouvrir-une-session-psm-bureau-à-distance)). Avec des disques ou des dossiers, ZillaTerm les demande au PVWA (case « Map local drives » de sa fenêtre de connexion, `AllowMappingLocalDrives`), et jamais plus n'est partagé que ce que le composant permet. ⚠ Les programmes de la session peuvent lire et écrire sur les disques ou dossiers partagés | Tous les disques |
| Dossiers (Ces dossiers seulement) | Nom vu dans la session (`\\tsclient\Nom` : lettres sans accent, chiffres, « . », « - », « _ ») et chemin sur ce poste, complet (`C:\…`, `\\serveur\partage\…`) ou commençant par une variable (`%USERPROFILE%\Partage`) ; « Ajouter un dossier… » propose le nom d'après le dossier choisi. Client de ZillaTerm dans un onglet seulement : Connexion Bureau à distance et le contrôle de Windows ne partagent alors aucun disque | aucun |
| Suspendre l'affichage des sessions que personne ne regarde | Sessions dans un onglet : onglet en arrière-plan, ZillaTerm réduit ou Windows verrouillé depuis 1 minute, ou ni clavier ni souris depuis 15 minutes, le serveur n'envoie plus l'image ; la session reste ouverte, l'image revient au retour sur l'onglet ou au premier mouvement de souris. Jamais pour l'onglet affiché pendant l'enregistrement d'une intervention ou un partage ; pendant un enregistrement, la pause d'un onglet en arrière-plan y est marquée (écran et frise du rapport) | oui |

<a id="settings-terminal"></a>

#### Page Terminal

| Paramètre | Rôle | Défaut |
| --- | --- | --- |
| Couleurs du terminal, taille de police | Palette (Campbell, One Half, Solarized…) et taille de police des terminaux SSH ; `Ctrl+molette` change la taille d'un terminal, `Ctrl+0` la rétablit | Campbell, 14 |
| Avertir avant de coller plusieurs lignes quand le shell les exécuterait une à une | Aperçu et confirmation avant le collage | oui |
| Le clic droit dans le terminal colle le presse-papiers | Maj+clic droit ouvre alors le menu ; ⚠ un clic droit par mégarde envoie le presse-papiers au shell | non |

<a id="settings-files"></a>

#### Page Fichiers

| Paramètre | Rôle | Défaut |
| --- | --- | --- |
| Protocole d'envoi | Protocole essayé d'abord (SFTP ou SCP) ; si le serveur le refuse, l'autre prend le relais | SFTP |
| Proposer une archive .tar.gz | Envoi en une seule archive proposé à partir de ce nombre de fichiers déposés d'un coup | oui, 200 |
| Afficher les fichiers cachés | Noms commençant par un point ; aussi la case « Cachés » de l'onglet Fichiers | non |
| Colonnes | Taille, Modifié, Droits, Propriétaire, Groupe (la colonne Nom reste toujours) ; aussi par un clic droit sur l'en-tête des colonnes de l'onglet Fichiers | toutes |
| Suivre dans une session indépendante | Le suivi d'un fichier (tail -f) ouvre sa propre connexion SFTP (une session PSMP de plus) | non |
| Couleurs par niveau | Suivi d'un fichier : erreurs (ERROR, FATAL, CRITICAL…) en rouge, avertissements (WARN, WARNING) en orange | oui |
| Surligner | Suivi d'un fichier : mots surlignés en jaune, séparés par des virgules | vide |
| Alerte si | Suivi d'un fichier : mots qui marquent la ligne et font clignoter le bouton de la barre des tâches, séparés par des virgules | vide |
| Notification Windows | Suivi d'un fichier : notification d'alerte quand la fenêtre n'est pas au premier plan (au plus une toutes les 30 secondes ; nombre de lignes et nom du fichier, jamais leur contenu) | oui |
| Éditeur de texte | Programme ouvert par « Modifier » dans l'onglet Fichiers | Bloc-notes |
| Outil de comparaison | Programme proposé dans la fenêtre de comparaison, avec ses arguments (`{0}` = fichier de gauche, `{1}` = de droite) | aucun |

<a id="settings-servers"></a>

#### Page Mes serveurs

| Paramètre | Rôle | Défaut |
| --- | --- | --- |
| Étiquettes des serveurs | Étiquettes données aux serveurs de « Mes serveurs » (nom, couleur) : « Ajouter une étiquette », « Supprimer l'étiquette », cellules modifiables ; renommer suit dans « Mes serveurs », supprimer retire l'étiquette des serveurs (voir [Étiquettes](#étiquettes-prod-qa-dev)) | PROD (rouge), QA (bleu), DEV (vert) |

<a id="settings-intervention"></a>

#### Page Intervention

| Paramètre | Rôle | Défaut |
| --- | --- | --- |
| Consignes d'astreinte | Bonnes pratiques et numéros d'urgence affichés en haut de l'onglet « Intervention » ; texte brut (voir [Intervention](#8-intervention--enregistrement-et-consignes-dastreinte)) | vide |
| Fichier des consignes | Fichier texte (partage réseau en général) relu à chaque ouverture, affiché à la place des consignes ci-dessus | vide |
| Rappels pendant l'intervention | Une ligne par rappel : minutes depuis le début de l'enregistrement, puis le texte (« 30 Escalader au N3 ») ; montrés dans l'onglet « Intervention » et la barre d'état, notés au journal | vide |

<a id="settings-sharing"></a>

#### Page Partage

| Paramètre | Rôle | Défaut |
| --- | --- | --- |
| Permettre de partager mes sessions avec un autre ZillaTerm | Partager une session depuis le menu de son onglet ; regarder celle d'une autre personne fonctionne sans (voir [Partager une session](#9-partager-une-session-avec-un-collègue)) | non |
| Connexions directes à ce poste, port | L'autre ZillaTerm se connecte à ce poste ; port 0 = un port libre au hasard, de 1024 à 65535 pour un port fixe (utile si le pare-feu doit l'autoriser) | oui, 0 |
| Dossier relais | Dossier d'un partage réseau accessible aux deux postes, utilisé quand la connexion directe n'aboutit pas ; échanges chiffrés, effacés à la fin du partage (un dossier sur un serveur réseau venu du fichier d'environnement demande de cocher « J'ai vérifié… ») | vide |

<a id="settings-security"></a>

#### Page Sécurité

| Paramètre | Rôle | Défaut |
| --- | --- | --- |
| Clés et certificats des serveurs | Tableau des empreintes vérifiées et acceptées (serveur, type, empreinte) : PSMP, SSH direct, certificats FTPS et Bureau à distance des entrées KeePass (une ligne par type de clé) ; « Filtrer les serveurs » réduit la liste. « Oublier les clés choisies » retire les lignes sélectionnées, oubliées à l'enregistrement (« Rétablir les clés retirées » les remet d'ici là) ; la clé sera redemandée à la prochaine connexion | — |
| Coffre local | Mots de passe maîtres KeePass mémorisés : « Créer le coffre… », « Déverrouiller… », « Changer le mot de passe… », « Supprimer maintenant… » ; ces actions s'appliquent tout de suite, sans « Enregistrer » | — |

Les réglages que peut diffuser le fichier d'environnement de l'équipe sont listés dans
[Environnement partagé](#environnement-partagé).

### Environnement partagé

Pour donner ZillaTerm à un collègue avec la configuration de l'équipe (adresse du PVWA, méthode de connexion, PSMP par
défaut et par domaine, composant des comptes Windows et composants par plateforme, listes partagées, clés des PSMP,
consignes d'astreinte (texte ou fichier), rappels pendant l'intervention, étiquettes des serveurs, dossiers partagés, partage de sessions,
quelques options), sans rien de personnel ni aucun mot de passe :

1. **Exporter** : Paramètres › Général › « Exporter l'environnement… » enregistre `ZillaTerm.env.json` avec les valeurs
   affichées.
2. **À côté de l'exécutable** : posez ce fichier à côté de `ZillaTerm.exe` (par exemple dans le même zip). Au
   démarrage, s'il est nouveau ou a changé, ZillaTerm le propose avant l'écran d'identification.
3. **Importer** : Paramètres › Général › « Importer un environnement… » (repris dans les pages, appliqué à
   « Enregistrer »), ou « Importer un environnement… » sur l'écran d'identification (appliqué aussitôt).
4. **Fichier central** : Paramètres › Général › « Fichier central » (un fichier sur un partage réseau, qu'il est
   aussi possible d'indiquer dans l'environnement lui-même). Il est relu à chaque démarrage : quand vous le modifiez,
   chacun voit les changements au démarrage suivant. Un partage injoignable (poste hors VPN) ne retarde pas le
   démarrage : au-delà de 5 secondes, le fichier est ignoré et relu au démarrage suivant.

Chaque fois, une fenêtre montre ce qui va changer (« ancienne valeur → nouvelle valeur ») et l'empreinte SHA-256 du
fichier ; « Ne pas appliquer » est le choix par défaut. Le PVWA et les PSMP reçoivent votre mot de passe CyberArk :
quand le fichier change leur adresse, ajoute une clé de serveur ou une liste partagée sur un serveur réseau (Windows s'y
authentifie à chaque démarrage ; le serveur est nommé), il faut cocher « J'ai vérifié… » avant d'appliquer. Une clé de
serveur déjà acceptée sur le poste n'est jamais remplacée par un fichier (elle est signalée). Seules les clés des PSMP
(ceux du poste ou du fichier) sont reprises : celle d'un autre serveur, d'accès d'urgence par exemple, est ignorée et se
vérifie à sa première connexion. Une clé reprise d'un fichier ne vaut que pour les connexions au PSMP : une connexion
directe au même serveur (KeePass) fait toujours vérifier sa clé. Dans Paramètres, ces clés apparaissent sous la forme
`psmp://hôte:port`. Un fichier écrit en « UTF-8 avec BOM » (PowerShell 5.1 : `Set-Content -Encoding UTF8`) est accepté,
comme une liste partagée. Un fichier invalide (adresse en http, nom de composant incorrect…) est refusé en entier. Un
fichier déjà proposé n'est reproposé que s'il a changé. Un réglage vide sur le poste qui exporte n'est pas exporté : il
n'efface rien chez celui qui importe. Les chemins (listes partagées, fichier central) sont complets : `C:\…` ou
`\\serveur\…`. Votre identifiant, « Mes serveurs » et vos sessions récentes ne sont jamais touchés ; les listes
partagées s'ajoutent sans retirer les vôtres. Les étiquettes du fichier remplacent celles du poste ; vos serveurs
gardent la leur (une étiquette absente du fichier s'affiche alors en gris). Les rappels du fichier
(`"InterventionReminders": [{ "Minutes": 30, "Text": "Escalader au N3" }]`) remplacent eux aussi ceux du poste.

Toutes les préférences sont enregistrées dans `%APPDATA%\ZillaTerm\settings.json` : langue, adresse du PVWA,
méthode et identifiant de connexion, paramètres ci-dessus, serveurs de « Mes serveurs », leurs dossiers et les fichiers qui
y ont été suivis (chemins), sessions récentes, emplacement des bases KeePass et de leurs fichiers clés, et des
listes partagées ouvertes, position et taille de la fenêtre, largeur et état du panneau de gauche. Ce fichier
ne contient **aucun mot de passe, jeton ni clé privée**. Pour repartir de zéro, fermez l'application et supprimez
`settings.json` et `settings.json.bak` (la sauvegarde seule serait reprise, avec un message) ; si l'ancien dossier
`%APPDATA%\CyberArkTerm` existe encore, supprimez-le ou renommez-le aussi, sinon ses réglages sont repris au
démarrage. Il est d'abord écrit dans un fichier temporaire puis mis en place, le précédent étant gardé en
`settings.json.bak` : si le fichier devient illisible, il est mis de côté (jamais écrasé), la sauvegarde est reprise
et un message le signale. ZillaTerm ne s'ouvre qu'une fois par session Windows : deux instances écraseraient
l'une l'autre leurs réglages. L'historique des transferts de l'onglet Fichiers est à côté, dans `transfers.json`
(noms et chemins des fichiers, sommes SHA-256, jamais leur contenu).

### Passage de CyberArkTerm à ZillaTerm

CyberArkTerm s'appelle désormais ZillaTerm. Au premier démarrage de `ZillaTerm.exe`, le dossier `%APPDATA%\CyberArkTerm`
(réglages, « Mes serveurs », coffre local, historique des transferts, journal de l'accès d'urgence) est copié dans
`%APPDATA%\ZillaTerm` ; l'ancien dossier est gardé : supprimez-le, avec `CyberArkTerm.exe`, une fois ZillaTerm adopté.
Un fichier `CyberArkTerm.env.json` posé à côté de l'exécutable est encore lu, les listes partagées restent lisibles par
les deux versions et les mots de passe maîtres du coffre local restent accessibles. L'ancien dossier temporaire
(`%TEMP%\CyberArkTerm`) est vidé au fil des démarrages (fichiers de plus d'un jour). Les deux versions ne s'ouvrent pas
en même temps. CyberArkTerm signale la première version de ZillaTerm mais ne peut pas la télécharger lui-même (dépôt
renommé) : téléchargez-la une fois depuis la page des versions.

## Sécurité

- **HTTPS obligatoire** vers le PVWA ; la validation des certificats n'est jamais désactivée. La révocation du
  certificat du PVWA (et de GitHub pour les mises à jour) est vérifiée en ligne : un certificat révoqué est refusé ;
  si la liste de révocation est injoignable (réseau fermé), la connexion passe après 5 secondes au plus, et le journal
  de débogage le note ; le résultat est gardé une heure. Une redirection du PVWA
  n'est jamais suivie (votre mot de passe partirait vers l'adresse indiquée) : elle est signalée avec cette adresse.
  L'adresse du PVWA ne peut contenir que le nom du serveur, un port et un chemin : un « nom@ » devant le serveur (qui
  ferait joindre un autre serveur que celui affiché), une espace, un « \ » ou une lettre hors ASCII sont refusés.
- **Noms de fichiers du serveur** : les caractères invisibles (inversion du sens d'écriture, espace sans largeur,
  caractères de contrôle) s'affichent « � » dans la liste, les confirmations, le choix d'un fichier de l'autre
  serveur, la comparaison, la file et l'historique des transferts, et le suivi (tail), et deviennent « _ » dans le nom du
  fichier téléchargé, pour qu'un nom ne puisse pas en imiter un autre (« .exe » affiché « .pdf »). Les fichiers venus
  des serveurs (téléchargements, copies ouvertes dans l'éditeur, glisser vers l'Explorateur) portent la marque des
  fichiers venus d'Internet (zone Internet ; le nom du serveur n'y est jamais noté, Windows pouvant l'envoyer à
  Microsoft) : SmartScreen contrôle un
  exécutable avant de le lancer, et les logiciels bureautiques ouvrent un document en mode protégé. Les fichiers
  spéciaux (périphérique comme `/dev/zero`, tube nommé, socket) ne sont ni ouverts ni téléchargés, et sont ignorés
  dans le téléchargement d'un dossier.
- **Aucun secret sur disque** : mot de passe CyberArk, jeton de session, clé MFA et mot de passe PSMP restent en
  mémoire, le temps de la session. Mots de passe PSMP gardés et clé MFA oubliés au verrouillage de Windows ; la clé
  MFA est effacée après chaque connexion et retirée du PVWA à la déconnexion (`Logoff`), faite à la fermeture.
- Session PVWA ouverte avec `concurrentSession` : votre session web PVWA éventuelle n'est pas fermée.
- **Ce que ZillaTerm garde sur le poste** (en plus des fichiers que vous enregistrez vous-même : téléchargements,
  rapports, exports) :
  - `%APPDATA%\ZillaTerm` : `settings.json` et sa sauvegarde `.bak` (réglages, « Mes serveurs », connexions récentes,
    empreintes des serveurs acceptés ; aucun mot de passe), `transfers.json` (historique des transferts : serveurs,
    chemins, tailles, sommes SHA-256), `coffre-local.dat` (mots de passe maîtres KeePass, chiffrés : voir plus bas) et
    `urgence.log` (journal de l'accès d'urgence) ;
  - `%LOCALAPPDATA%\ZillaTerm` : `Accounts` (copie de la liste des comptes, chiffrée pour votre compte Windows, sans
    mot de passe, supprimée après 30 jours ou quand l'option est décochée), `Interventions` (journaux d'intervention
    chiffrés, voir [Intervention](#8-intervention--enregistrement-et-consignes-dastreinte)), `debug.log` (seulement si
    le journal de débogage est activé), `FreeRDP` et `FreeRDP-config` (client Bureau à distance embarqué et sa
    configuration, sans identifiant ni certificat) ;
  - `%TEMP%\ZillaTerm` (ou `%LOCALAPPDATA%\ZillaTerm\Temp`), réservé à votre compte Windows : copies de travail
    (fichiers modifiés, comparaisons, archives d'envoi, fichiers glissés vers l'Explorateur, fichier .rdp le temps de
    l'ouverture d'une session PSM), effacées à la fermeture, ou au plus tard un jour après un arrêt brutal.
- **Partage de sessions** (désactivé par défaut) : chaque partage est lancé par vous, chaque personne acceptée par
  vous après comparaison d'un code à 6 chiffres ; rien n'écoute sur le réseau tant qu'aucune session n'est partagée.
  L'invitation contient un secret aléatoire de 256 bits, valable pour la durée du partage, et permet seulement de
  demander. Les échanges sont chiffrés de bout en bout : échange de clés ECDH P-256 authentifié par le secret de
  l'invitation, puis AES-256-GCM, une clé par sens, messages numérotés (un message altéré, rejoué ou déplacé coupe la
  connexion). Le code de vérification est tiré des clés de la connexion : quelqu'un qui s'interposerait, même avec
  l'invitation, obtiendrait un autre code. Après 5 tentatives avec une invitation fausse, le partage s'arrête. Le
  dossier relais ne voit passer que des messages chiffrés, effacés à la fin du partage ; celui d'une invitation, s'il
  n'est pas celui de vos Paramètres, n'est utilisé qu'après votre accord (Windows s'y connecte avec votre compte). Personne ne tape dans une
  session sans que vous lui ayez donné la main (terminal seulement), et vous la reprenez quand vous voulez ; le partage
  s'arrête au verrouillage de Windows. Le nom, le compte et le poste d'une personne qui demande sont déclaratifs :
  seul le code fait foi.
- **Fin de la session Windows** (déconnexion, arrêt) : ZillaTerm se ferme sans question ; ses sessions sont fermées
  (copies des fichiers modifiés effacées), les bases KeePass et le coffre local verrouillés, le presse-papiers du mot
  de passe vidé et la session PVWA fermée (2 secondes au plus).
- **Copie d'un mot de passe** : la réponse du PVWA est lue dans un tampon effacé ensuite et décodée sans passer par
  une chaîne ; le mot de passe est copié directement dans le presse-papiers Windows, marqué pour être exclu de
  l'historique (`Win+V`), de la synchronisation entre appareils et des outils de surveillance du presse-papiers,
  puis effacé après 20 s s'il y est encore (nouvel essai chaque seconde si une autre application garde le
  presse-papiers ouvert), ainsi qu'à la déconnexion de CyberArk, à la fermeture et au verrouillage de Windows. Il n'est jamais
  affiché ni écrit dans le journal de débogage. Une réponse qui n'est pas le mot de passe (page HTML de maintenance,
  redirection vers une page de connexion SSO, réponse vide) est refusée au lieu d'être copiée.
- **Import des sessions d'un autre logiciel** : seuls le serveur, le port, le protocole, l'utilisateur et le dossier
  sont lus, jamais un mot de passe ; fichiers XML lus sans DTD ni ressource externe, taille limitée. Chaque serveur
  importé s'ouvre via PSM ou le PSMP avec un compte du PVWA, jamais en direct ; un serveur sans compte n'est pas
  importé.
- **Ajout d'un compte** : le mot de passe saisi est lu dans le champ masqué sans passer par une chaîne, envoyé une
  seule fois au PVWA en HTTPS, puis effacé de la mémoire ; il n'est ni enregistré ni écrit dans le journal de
  débogage.
- **Dossier temporaire** : `%TEMP%\ZillaTerm`, réservé à votre compte Windows (droits limités à vous seul) ; s'il
  appartient à un autre compte (variable TEMP pointant vers un dossier partagé), `%LOCALAPPDATA%\ZillaTerm\Temp`
  le remplace. Les chemins ci-dessous sont relatifs à ce dossier.
- **Sessions PSM** : le fichier RDP du PVWA (jeton PSM à usage unique) est écrit dans le dossier temporaire pour
  `mstsc`, qui en vérifie la signature, puis supprimé après 60 s ou à la fermeture. Dans un onglet (option), il
  n'est jamais écrit sur le disque : ZillaTerm le lit en mémoire et le transmet au client FreeRDP sur son entrée
  standard (jeton absent de la ligne de commande et des journaux). Le certificat du PSM (ou sa clé) est lu et vérifié
  par ZillaTerm avant le lancement du client (approuvé par Windows, épinglé ou confirmé) ; le client n'en accepte alors
  aucun autre, ne montre aucune fenêtre de Windows et ne lit aucun identifiant enregistré de Windows.
- **Saisie simultanée** (vue parallèle) : désactivée à chaque ouverture de la vue, signalée par le bouton ambre
  « ACTIVE (n) », un bandeau et un cadre ambre qui nomment les sessions concernées ; les sessions exclues sont
  marquées « exclue », une session ajoutée n'y est pas incluse d'office, et un collage
  de plusieurs lignes vers plusieurs sessions demande confirmation. Chaque session reste une session PSMP distincte,
  enregistrée comme d'habitude.
- **Collage de plusieurs lignes** dans un terminal dont le shell les exécuterait une à une : aperçu et confirmation
  avant l'envoi (réglage activé par défaut).
- **Confirmations** : boutons au verbe explicite dans la langue de l'application, « Annuler » par défaut, serveur,
  compte ou safe nommé ; la fermeture d'une session connectée est confirmée (réglage activé par défaut).
- **Comparaison de fichiers** : contenus lus en mémoire et effacés à la fermeture de la fenêtre ; seules les copies
  données à un outil externe passent par le disque (dossier temporaire, `compare`), supprimées à la fermeture de la
  fenêtre et au lancement suivant.
- **Nouvelle version** : aucune requête vers Internet sans votre action ou l'option des Paramètres (désactivée par
  défaut) ; seules les adresses du dépôt du projet sont suivies, l'archive n'est gardée que si sa somme SHA-256 est
  celle de `SHA256SUMS.txt` de la même version (ce qui détecte un téléchargement incomplet ou abîmé, pas une version
  publiée par un tiers qui aurait pris la main sur le dépôt), et rien n'est installé ni lancé. L'archive gardée porte
  la marque des fichiers venus d'Internet (« Mark-of-the-Web ») : SmartScreen contrôle l'exécutable extrait. Depuis
  la 0.30.0, l'archive publiée a une attestation de provenance, qui prouve qu'elle a été compilée par le dépôt du
  projet : `gh attestation verify ZillaTerm-<version>-win-x64.zip --repo muller-camille/ZillaTerm`.
- **Clés d'hôte PSMP épinglées** au premier usage : l'empreinte est à comparer avant d'accepter (« Annuler la
  connexion » par défaut) ; une clé changée est signalée par un bandeau et ne remplace l'ancienne qu'après une case
  de confirmation (de même pour les serveurs joints en accès d'urgence et les certificats FTPS et Bureau à
  distance). Une clé est mémorisée par serveur et par type : une clé d'un type jamais vu pour ce serveur est montrée
  avec celles déjà acceptées et ne s'ajoute qu'après une case de vérification, jamais d'office. Les clés acceptées
  se consultent et s'oublient dans Paramètres › Sécurité.
- **Maintien de la session PVWA** : il évite l'expiration pendant que vous travaillez ; rien n'est envoyé tant que
  Windows est verrouillé ni après 15 minutes sans clavier ni souris, et l'option se désactive dans les Paramètres si
  votre politique l'exige.
- **Bases KeePass** :
  - mot de passe maître jamais enregistré, sauf dans le coffre local si vous le demandez : Argon2id (64 Mio, 3
    passes) puis AES-256-GCM, réglages de dérivation authentifiés, le tout protégé par DPAPI (compte Windows) ;
  - en mémoire, clé de la base et mots de passe des entrées restent masqués et ne sont révélés qu'au moment de la
    connexion ; bases verrouillées à la déconnexion de CyberArk, à la fermeture et au verrouillage de Windows ;
  - enregistrement sûr : relecture du fichier, modification appliquée à sa version du moment (les changements faits
    ailleurs sont gardés), vérification du résultat déchiffré, remplacement en une fois (copie `.bak` le temps du
    remplacement, supprimée une fois le fichier relu à l'identique) ; une entrée modifiée ailleurs entre-temps n'est
    pas écrasée ;
  - bureau à distance direct : certificat du serveur lu avant la lecture du mot de passe et épinglé au premier usage
    (un changement est signalé et bloqué tant qu'il n'est pas confirmé), serveur sans TLS refusé, cache d'images
    persistant désactivé. Avec FreeRDP, le mot de passe passe par son entrée standard (ni ligne de commande, ni
    fichier, ni gestionnaire d'identification) et la connexion n'accepte que le certificat épinglé. Limite, avec le
    contrôle de Windows seulement (FreeRDP absent) : il fait sa propre négociation TLS, sans qu'on puisse lui imposer
    l'empreinte épinglée, et pour un certificat que Windows n'approuve pas, il affiche encore son propre
    avertissement ;
  - VNC : le protocole ne chiffre ni l'écran, ni les frappes, ni le presse-papiers (bandeau permanent) ; le mot de
    passe n'est pas envoyé tel quel (défi-réponse du protocole), et seulement après votre accord : le serveur n'est
    pas authentifié et peut en retrouver les 8 premiers caractères ; presse-papiers échangé seulement sur un clic ;
    bips limités à un par seconde ;
    taille d'écran annoncée par le serveur bornée (8 192 pixels de côté) ;
  - FTP : TLS tenté d'abord, connexion en clair seulement après votre accord (bandeau permanent), jamais pour
    `ftpes://` et `ftps://` ; sous TLS, les transferts sont chiffrés aussi (`PROT P`) ; un serveur déjà vu avec TLS
    qui ne le propose plus est signalé comme une interception possible, et rien n'est envoyé sans confirmation ;
  - certificat FTPS : celui que Windows approuve est accepté ; sinon son empreinte SHA-256 est montrée et épinglée
    au premier accord (comme une clé d'hôte SSH), un changement est signalé ; refusé, la connexion s'arrête avant
    l'envoi de l'identifiant ;
  - noms de fichiers avec des caractères de contrôle refusés (pas d'injection de commande FTP) ;
  - journal `urgence.log` : date, compte Windows, poste, action, base, entrée, cible ; jamais de mot de passe.
    Chaque lecture du mot de passe d'une entrée y est notée, reconnexions et connexions SFTP / SCP de l'onglet
    Fichiers comprises ; si le journal ne peut pas être écrit, la connexion n'est pas ouverte.
- **Journal de débogage**, désactivé par défaut (menu du bouton Paramètres) :
  `%LOCALAPPDATA%\ZillaTerm\debug.log`, 5 Mo au plus plus une génération `.1`. Il note le déroulement des
  connexions PVWA, PSM, Bureau à distance et SSH : adresses et statuts des requêtes, réglages du fichier .rdp,
  événements et codes du contrôle Bureau à distance, version et algorithmes du serveur SSH, erreurs ; pour chaque
  protocole d'envoi refusé, l'étape (connexion, commande scp, annonce du fichier), la réponse du serveur et le
  protocole qui a pris le relais ; pendant l'enregistrement d'une intervention, un bilan par minute de l'écran des
  sessions Bureau à distance (captures, images gardées, poids, taille du journal). Il contient des noms de serveurs
  et de comptes, mais
  **jamais** de mot de passe, de jeton de session, de demande de session PSM (`PSM@…` masqué), de signature,
  d'en-tête ou de corps de requête, ni le contenu des sessions. La barre d'état le signale tant qu'il est actif.
  Relisez-le avant de le transmettre, et supprimez-le une fois le problème résolu.
- **Fichiers modifiés** : la copie locale ouverte dans l'éditeur est placée dans le dossier temporaire (`edit`) et
  supprimée à la fermeture de l'onglet SSH ; une alerte prévient si des modifications n'ont pas été renvoyées.
- **Pas d'injection de commande** : chemins SCP et dossiers de départ protégés entre apostrophes pour le shell
  distant ; arguments `ssh` / Windows Terminal validés et passés sans shell.
- Export CSV protégé contre l'injection de formules Excel.
- **Fichiers d'environnement** (`ZillaTerm.env.json`) : ni mot de passe ni donnée personnelle, seuls les champs
  connus sont lus. Un fichier n'est jamais appliqué sans votre accord : changements et empreinte SHA-256 affichés,
  case à cocher quand l'adresse du PVWA ou d'un PSMP change, qu'une clé de serveur ou une liste sur un serveur réseau
  est ajoutée. Il ne remplace jamais une clé de serveur déjà acceptée et n'apporte que des clés de PSMP ; adresse du PVWA en https obligatoire ; fichier de plus de 1 Mo refusé.
- **Fichiers de serveurs et listes partagées** : ni mot de passe ni jeton, seulement des noms de serveurs, de
  comptes et de safes, des ID de comptes et les réglages de connexion (le motif par défaut n'est jamais partagé).
  Ils ne donnent aucun accès : chacun se connecte avec ses droits CyberArk, et l'info-bulle montre le compte tel
  que le coffre CyberArk le décrit. Une machine cible venue d'une liste partagée et non autorisée pour le compte par
  CyberArk est confirmée avant la première connexion. L'auteur inscrit au journal (compte CyberArk et compte
  Windows) est déclaratif : l'audit du partage réseau fait foi. Un fichier de plus de 8 Mo est refusé.
- Les sessions PSM et PSMP ouvertes par ZillaTerm sont des sessions CyberArk standard : elles sont enregistrées
  et auditées par le PSM comme celles ouvertes depuis le PVWA.

Pour signaler une vulnérabilité, voir [SECURITY.md](../SECURITY.md) (signalement privé, pas d'issue publique).

## Fonctionnement technique

### API du PVWA utilisées

| Appel | Usage |
| --- | --- |
| `POST /PasswordVault/API/auth/{CyberArk\|LDAP\|RADIUS\|Windows}/Logon` | Ouverture de session |
| `GET /PasswordVault/API/Accounts?offset=…&limit=1000` | Liste paginée des comptes |
| `POST /PasswordVault/API/Accounts/{id}/PSMConnect` | Fichier RDP de la session PSM |
| `POST /PasswordVault/API/Accounts` | Création d'un compte dans un safe (« Ajouter un compte ») |
| `POST /PasswordVault/API/Accounts` (une fois par ligne) | Import de comptes depuis un CSV |
| `PATCH` / `DELETE /PasswordVault/API/Accounts/{id}` | Modification (seuls les champs changés) et suppression d'un compte |
| `POST /PasswordVault/API/Accounts/{id}/Verify`, `/Change`, `/Reconcile` | Opérations demandées au CPM |
| `POST /PasswordVault/API/Accounts/{id}/Password/Retrieve` | Copie du mot de passe (motif, ticket ; usage « copy » dans l'audit) |
| `POST` / `PUT` / `DELETE /PasswordVault/API/Safes/{safe}/Members[/{membre}]` | Ajout, droits et retrait d'un membre du safe |
| `GET /PasswordVault/API/Safes/{safe}/Members?offset=…&limit=1000` | Membres d'un safe et leurs droits (fenêtre « Membres du safe ») |
| `POST /PasswordVault/API/Users/Secret/SSHKeys/Cache` | Clé SSH temporaire « MFA caching » (si activée) |
| `GET /PasswordVault/API/Accounts?offset=0&limit=1` | Maintien de la session (toutes les 4 minutes) |
| `POST /PasswordVault/API/Auth/Logoff` | Fermeture de session |

### Listes partagées

Fichier JSON (`"format": "CyberArkTerm.SharedServers"`, version 1) : nom, PVWA d'origine, révision, dossiers,
serveurs (avec qui les a ajoutés et quand) et journal des modifications (les 1 000 dernières). Chaque modification
ouvre le fichier en exclusivité (les autres postes réessaient pendant 5 s), le relit, copie la révision en cours
dans `nom.versions\nom.r00012.20261006-101500.json` (révision et date de son enregistrement, 100 versions gardées),
applique la modification, augmente la révision, note qui, quand et quoi, puis réécrit le fichier (remis tel quel si
l'écriture échoue). L'affichage suit les changements du fichier (`FileSystemWatcher`, repris de lui-même une minute après une coupure du partage ou du VPN, puis la liste est relue) et se relit avec `F5`. L'export
de « Mes serveurs » a le même format avec `"format": "CyberArkTerm.Servers"`, sans révision ni journal.

### Bases KeePass

Lecture et écriture natives (sans KeePass installé) des formats **KDBX 3.1 et 4.x** : chiffrement AES-256 ou
ChaCha20, dérivation de clé AES-KDF (instructions AES du processeur) ou Argon2d / Argon2id, fichiers clés XML 1.0 /
2.0, 32 octets, 64 caractères hexadécimaux ou fichier quelconque. Le fichier réécrit garde la version, le
chiffrement et la dérivation de clé d'origine, avec de nouvelles graines à chaque enregistrement, celle de la
dérivation de clé comprise (comme KeePass : une clé dérivée capturée une fois ne déchiffre pas les versions
suivantes). Les bases de
test (`tests/ZillaTerm.Core.Tests/KeePass/Vaults`) viennent de KeePassXC et pykeepass, et les fichiers écrits par
ZillaTerm ont été vérifiés dans ces deux outils.

### Sessions VNC

Client intégré (protocole RFB 3.3, 3.7 et 3.8, RFC 6143 ; un serveur plus récent, comme RealVNC 4 ou 5, reçoit une
réponse en 3.8), sans logiciel à installer : authentification « aucune » ou « mot de passe VNC » (si le serveur
propose les deux : le mot de passe si l'entrée en a un, sinon aucune) (DES du protocole, implémenté dans ZillaTerm car le mode FIPS de Windows peut interdire
DES), encodages Raw, CopyRect et Hextile, changement de taille d'écran, pixels 32 bits. Le clavier est transmis en
« keysyms » X11 (les caractères AltGr sont envoyés comme caractères), la molette en boutons 4 et 5. Le mot de passe
VNC n'est envoyé qu'après votre accord (une fois par onglet) ; les bips du serveur sont limités à un par seconde.

### Sessions de fichiers FTP / FTPS

Bibliothèque FluentFTP (licence MIT). Mode passif : `PASV` en IPv4, la connexion de données allant toujours vers le
serveur lui-même (l'adresse annoncée dans la réponse est ignorée : un serveur ne peut pas faire viser une autre
machine), `EPSV` en IPv6 ; binaire, `PBSZ 0` et `PROT P` sous TLS ;
certificat vérifié par Windows, sinon épinglé (`ftps://serveur:port` parmi les clés de serveurs acceptées, dans
Paramètres › Sécurité) ; les serveurs qui ont chiffré une connexion sont notés (`FtpTlsServers` des réglages) pour
signaler un retrait de TLS. FTP n'a pas de somme de contrôle standard : chaque envoi est relu depuis le serveur et
comparé par SHA-256. Lecture partielle
(`REST`) pour la comparaison et le suivi en direct. Après un transfert interrompu, la connexion est rouverte et le
fichier incomplet supprimé. Un fichier remplacé est réécrit sur place : il garde ses droits. Liens symboliques : les
40 premiers d'un dossier sont résolus (un aller-retour chacun) ; au-delà, un lien s'affiche comme un fichier, et
l'ouvrir entre dans le dossier si c'en est un. Les noms sont échangés en UTF-8 (`OPTS UTF8 ON`), même si le serveur ne
l'annonce pas ; un nom que l'UTF-8 ne peut pas représenter est refusé plutôt que changé. En SFTP, les liens d'un
dossier sont suivis par paquets de 16 demandes, 400 au plus (au-delà, comme en FTP) ; un lien vers un fichier prend
la taille de ce fichier.

### Sessions Bureau à distance

Les sessions PSM s'ouvrent avec le fichier RDP renvoyé par `PSMConnect`. Par défaut, il est donné tel quel à la
Connexion Bureau à distance (`mstsc`) : elle en vérifie la signature et gère aussi bien le bureau que l'application
distante (RemoteApp). Le journal de débogage en note la structure (jeton, signature et arguments masqués).

Avec l'option « Sessions PSM Windows dans un onglet », le fichier est lu par ZillaTerm : adresse et port du PSM,
utilisateur, programme de démarrage (`alternate shell`) ou application distante (`remoteapplicationprogram`, nom et
arguments), sécurité, passerelle, répartition de charge, redirections (`drivestoredirect:s:*` seulement pour les
disques), effets. Le mode d'affichage du fichier (`screen mode id`, taille, écrans) est ignoré. Une session PSM est
imbriquée : dans sa propre session, le PSM ouvre la connexion vers le serveur cible à la même taille, et changer
ensuite la résolution de la session PSM ne la change pas pour la cible avec certaines configurations du PSM (bureau
tronqué, clics décalés). L'option « Adapter la résolution du bureau distant à la taille de l'onglet », activée par
défaut, change la résolution de la session PSM ; désactivée, ces sessions gardent une taille fixe, mise à l'échelle de
l'onglet. Les connexions directes, elles, suivent la taille de l'onglet.

**Client FreeRDP.** Les sessions PSM dans un onglet passent par le client Windows de FreeRDP (`wfreerdp`), compilé par
le workflow de release du fork `muller-camille/FreeRDP` (branche `ci`) et publié dans ses releases ;
`tools/freerdp-download.sh` prend la release figée et la vérifie (somme SHA-256 figée, attestation de provenance de
GitHub). FreeRDP 3.32.2 et OpenSSL 3.5.9 à des versions figées (commit vérifié, archive vérifiée par sa somme
SHA-256), liés statiquement (un seul exécutable, qui n'utilise que des DLL de Windows), avec des correctifs
génériques, sans rien de propre à ZillaTerm, chacun destiné à être proposé au projet FreeRDP (branche `patches-3.32.2`
du fork `muller-camille/FreeRDP`) : fichier de connexion lu sur l'entrée standard, ancienne « sécurité RDP » soumise à
`/cert:deny` et aux empreintes, applications distantes qui répondent au serveur et s'affichent dans la fenêtre parente,
clavier et molette, fenêtre qui répond entre deux tentatives de reconnexion, DPI de la fenêtre parente, recherche des
bibliothèques. ZillaTerm l'embarque, le copie au premier usage dans `%LOCALAPPDATA%\ZillaTerm\FreeRDP\<empreinte>`
(dossier réservé à l'utilisateur), en vérifie l'empreinte SHA-256 avant chaque lancement et le garde ouvert en lecture
seule pendant celui-ci.

Il le pilote uniquement avec des options standard de FreeRDP. Le fichier du PVWA, réécrit par ZillaTerm (PSM,
utilisateur, programme de démarrage ou application distante et ses arguments, qui portent le jeton), lui est transmis
sur son entrée standard (`-` comme fichier de connexion) : jeton PSM et mot de passe ne sont ni sur la ligne de
commande, ni sur le disque. Il contient un mot de passe vide : le client ne demande rien et ne lit jamais les
identifiants enregistrés de Windows (`TERMSRV/…`) ; `/from-stdin` l'empêche d'ouvrir une fenêtre d'identifiants de
Windows. `/cert:deny` suivi des empreintes acceptées par ZillaTerm (`fingerprint:sha256:…`) : il refuse tout autre
certificat ou clé, sans rien demander. Son dossier de configuration est un dossier de ZillaTerm, laissé vide.

**Identité du serveur, avant la connexion.** ZillaTerm lit lui-même le certificat du PSM avant de lancer le client
(demande de connexion X.224, puis négociation TLS arrêtée dès le certificat reçu : ni jeton ni utilisateur envoyés) et
le vérifie : chaîne jusqu'à une autorité approuvée par Windows, nom, usage, révocation quand elle est joignable ; sinon
empreinte épinglée (`rdp://serveur:port` parmi les clés de serveurs acceptées) ou confirmée par l'utilisateur. Un PSM
qui n'accepte que l'ancienne « sécurité RDP », sans TLS : ZillaTerm lit sa clé de la même façon (demande de connexion
dont la réponse contient la clé ; rien d'autre n'est envoyé) et l'épingle (`rdp://serveur:port`, type `RDP-RSA` ;
empreinte : SHA-256 de la clé en DER PKCS#1, celle que compare FreeRDP), chiffrement de 128 bits au moins ; la
question montre la taille de la clé et avertit en rouge sous 2 048 bits (une clé plus courte peut être cassée) ; un PSM sans
chiffrement est refusé, tout comme la clé d'un serveur qui a déjà présenté un certificat TLS (retrait de TLS :
interception possible). Plusieurs certificats et clés par adresse (PSM en répartition de charge : un par serveur,
chacun confirmé une fois) : le client les reçoit tous. Les questions sont posées avant le lancement du client, jamais
pendant la connexion ; une fois l'une d'elles répondue, la connexion part avec un nouveau jeton demandé au PVWA (le
jeton PSM ne sert qu'une fois et expire vite). Si le client refuse quand même le serveur (autre serveur derrière la même
adresse), ZillaTerm relit son identité et la soumet si elle est nouvelle (deux fois au plus par connexion). Après une
coupure réseau, FreeRDP tente de reprendre la session (une tentative toutes les 15 secondes, 20 au plus) ; pendant ce
temps, l'onglet affiche « Connexion perdue : reconnexion en cours… » avec la tentative en cours, la session est
masquée et ZillaTerm reste utilisable (les tentatives se font à côté de la fenêtre de la session) ; la session
réapparaît dès qu'elle est reprise. Si c'est un autre serveur qui répond à la même adresse, la
tentative est refusée (identité non vérifiée) et s'arrête aussitôt : la session est perdue, et ZillaTerm refait une
demande au PVWA (nouveau jeton), puis vérifie le serveur qui répond avant de s'y connecter (question s'il est inconnu ;
deux fois au plus avant une nouvelle ouverture de session). Affichage en pause : ZillaTerm masque la fenêtre du
client, qui envoie alors au serveur le message « Suppress Output » du protocole RDP (comme mstsc quand sa fenêtre est
réduite) ; montrée de nouveau, le client autorise l'affichage et le serveur renvoie l'image entière. Après une
reconnexion automatique, la pause est redemandée au serveur.

Durcissement : aucune bibliothèque chargée hors de son dossier et de System32 (les canaux virtuels sont compilés dans
l'exécutable), OpenSSL sans module, moteur ni fichier de configuration chargés à l'exécution, ses dossiers compilés
étant ceux de Windows réservés aux administrateurs ; variables de journal, de proxy et d'OpenSSL retirées de son
environnement. Le processus est dans un objet de travail fermé avec ZillaTerm : il ne lui survit pas. Sa fenêtre est
une enfant de l'onglet (option `/parent-window`) ; un clic dedans donne le clavier à l'onglet, d'où le crochet clavier
de FreeRDP envoie les touches à la session. Une application distante est affichée dans cette fenêtre (surface de la
session, comme un bureau) au lieu de fenêtres à part. Fermer l'onglet ferme cette fenêtre (fin de session comme avec
le bouton de fermeture du client). Fin de session et erreurs : le code de FreeRDP (`ERRINFO_…`, `ERRCONNECT_…`, code
de sortie du client) est expliqué dans l'onglet. Limite : le client Windows de FreeRDP est marqué « non maintenu » par
le projet FreeRDP (le cœur du protocole, lui, l'est) ; ZillaTerm en fige la version et la met à jour avec ses
correctifs. Les tests d'intégration (job `rdp` du workflow `build`) ouvrent avec lui un bureau et une application
distante sur le poste de CI : taille, clics, touches, certificat refusé, clé de l'ancienne sécurité RDP.

**Contrôle Bureau à distance de Windows.** Le bureau à distance direct des bases KeePass, et les sessions PSM de bureau
quand le client FreeRDP n'est pas embarqué (compilation sans lui), hébergent le contrôle ActiveX de Windows
(`mstscax.dll`, classe `MsRdpClient` la plus récente disponible). Une connexion directe est réglée ainsi :
authentification réseau (NLA), alerte si le serveur n'est pas reconnu, redirections désactivées sauf le
presse-papiers, cache d'images persistant désactivé. Avant chaque connexion, ZillaTerm lit lui-même le certificat TLS
du serveur (demande de connexion X.224, puis négociation TLS interrompue dès le certificat reçu : ni identifiant ni
mot de passe envoyés) pour l'épingler. Le contrôle ActiveX ne permet pas de lui imposer cette empreinte : pour une
session PSM sans FreeRDP, c'est Windows qui vérifie le certificat, comme avec `mstsc` (niveau d'authentification 2 au
moins). Les fermetures de session et les erreurs de connexion sont expliquées dans l'onglet avec le message et les
codes de Windows (raison, raison étendue).

**Un thread par connexion Bureau à distance.** Le contrôle, sa fenêtre et ses événements vivent sur un thread à part
(STA, avec sa boucle de messages) ; l'interface ne l'attend jamais. L'onglet contient une fenêtre du thread de
l'interface, dans laquelle ce thread place la fenêtre du contrôle. Avant de libérer le contrôle, il l'en retire :
une déconnexion ou une libération qui tarde ne fige plus l'application. Si le thread ne répond plus pendant 5 s, la
barre de l'onglet le signale, et le reste de l'application reste utilisable. Limite : Windows partage le clavier et
la souris entre une fenêtre et celles qu'elle contient, même d'un autre thread ; un contrôle bloqué pour de bon peut
encore retenir un clic dans sa zone ou un changement de focus. À la déconnexion ou à la fermeture, ZillaTerm attend au
plus 3 s la fin des sessions Bureau à distance ; une session dont le contrôle reste bloqué est alors retirée de la
fenêtre (son emplacement est mis de côté), et l'application se déconnecte quand même, ou se ferme directement.

### Partage de sessions

Invitation : `zillaterm-share:1:` suivi, en base64url, de l'identifiant du partage (128 bits), du secret (256 bits), de
la fin de validité, des adresses de ce poste avec le port d'écoute (seulement celles des cartes du domaine de
l'entreprise, VPN compris, quand le poste est dans un domaine et que leur suffixe DNS le dit ; sinon toutes), et du
dossier relais. Ouverture : chacun envoie une
clé publique ECDH P-256 éphémère authentifiée (HMAC-SHA256) par une clé tirée du secret ; les clés de session (une par
sens) et le code de vérification sont dérivés (HKDF-SHA256) du secret partagé ECDH, du secret de l'invitation et de
l'échange complet. Messages : AES-256-GCM, nonce = numéro du message dans son sens. Connexion directe : TCP, chaque
message précédé de sa longueur ; toutes les adresses de l'invitation sont essayées ensemble (4 secondes au plus). Le
port écoute sur toutes les cartes, mais une connexion arrivée par une adresse absente des invitations en cours est
refusée ; 4 ouvertures en attente au plus par adresse (64 en tout), pour qu'un seul poste ne bloque pas les autres.
Dossier relais : un sous-dossier `zts-<identifiant>` par partage, deux fichiers par personne (un par sens), lus toutes
les 200 ms ; les sous-dossiers abandonnés depuis plus de deux jours sont effacés.

Terminal : à l'accord, l'écran actuel (sans l'historique : contenu, couleurs, modes, curseur) puis le flux tel que le
terminal le reçoit ; un changement de taille renvoie l'écran entier. Écran (Bureau à distance, VNC) : capturé quatre
fois par seconde tant que quelqu'un regarde, en tuiles de 64 × 64 pixels (couleurs 16 bits), seules les tuiles
modifiées partent, compressées ; une image complète à chaque arrivée. La saisie de la personne qui a la main est
encodée chez celui qui partage, selon les modes de son terminal. Limites : 10 partages à la fois, 5 personnes par
partage, 3 minutes pour répondre, connexion coupée après 60 secondes sans nouvelles ; une personne en retard de plus
de 48 Mo reçoit l'écran entier au lieu du retard (les messages d'état, main reprise ou affichage en pause, sont
gardés). Chez la personne qui regarde, les tuiles d'écran gardées entre deux images complètes sont limitées (256 Mo) ;
celui qui partage envoie une image complète avant d'atteindre cette limite.

### Sessions PSMP

Chaque onglet SSH ouvre jusqu'à trois connexions au PSMP, avec le même identifiant
`<vous>@<compte>[#domaine]@<cible>` : le terminal, la connexion SFTP de l'onglet Fichiers, et une connexion SCP au
premier envoi en SCP (choisi dans les Paramètres, ou relais d'un envoi SFTP refusé). Chacune est une session PSMP, enregistrée par le PSM. Les envois au serveur
(frappe, taille du terminal) et la fermeture des connexions se font hors du thread de l'interface, dans l'ordre : un
serveur ou un PSMP qui ne lit plus ne fige pas l'application.

### Suivi du dossier du terminal

À l'ouverture d'une session SSH (si l'option est active), ZillaTerm attend que le shell du serveur cible affiche
son invite (jusqu'à 60 s : le PSMP met parfois plusieurs secondes à joindre la cible), puis lui envoie une commande
d'une ligne, précédée d'une espace pour ne pas entrer dans l'historique (bash, ou zsh avec `HIST_IGNORE_SPACE`).
Rien n'est envoyé si vous avez déjà commencé à taper ; la commande peut être renvoyée sans effet en double (case
« Suivre ») :

- définition de `PROMPT_COMMAND` (bash) ou `precmd` (zsh) qui émet la séquence standard **OSC 7** avec le dossier
  courant à chaque invite ;
- avec tcsh, l'alias `cwdcmd` (seulement s'il n'est pas déjà défini), qui émet la même séquence à chaque changement
  de dossier ;
- si un dossier de départ est configuré, un `cd` vers ce dossier ;
- un marqueur privé à la fin (`ESC ] 6973 ; BEL`) : à sa réception, ZillaTerm efface lui-même la commande tapée et
  son écho, depuis la ligne de l'invite, quel que soit le nombre de lignes que le serveur leur a données. Le marqueur
  est sans effet si ZillaTerm ne vient pas d'envoyer la commande. Pendant que ZillaTerm l'attend (3 secondes au plus),
  ce que vous tapez est retenu, puis envoyé juste après l'effacement : ce que vous tapez reste toujours affiché. L'enregistrement PSM montre quand même la commande,
  comme tout ce qui est tapé dans la session.

Le terminal intégré décode la séquence OSC 7 et l'onglet Fichiers se place dans le dossier indiqué.

La commande est lisible sans erreur par tous les shells : chaque partie n'est exécutée que par la famille de shell à
laquelle elle est destinée. Elle ne lit aucune variable qui pourrait ne pas exister : un profil qui active `set -u`
(`set -o nounset`) ne l'interrompt pas. Avec csh, ksh, sh ou fish, le suivi n'est pas installé et rien ne reste à
l'écran.

## Dépannage

| Symptôme | Cause probable et solution |
| --- | --- |
| « Impossible de joindre le poste qui partage » (Rejoindre une session partagée) | Le pare-feu ou le réseau (VPN) bloque la connexion directe : demandez à la personne qui partage d'autoriser ZillaTerm dans le pare-feu Windows (réseaux privés ou de domaine), ou de régler un dossier relais accessible aux deux postes (Paramètres › Partage). |
| « Partage impossible : ni connexion directe, ni dossier relais » | Le port choisi est déjà pris (port 0 pour un port au hasard), ou le dossier relais est injoignable : vérifiez Paramètres › Partage. |
| « Connexion TLS refusée : le certificat du PVWA n'est pas approuvé » | Le certificat (ou l'autorité qui l'a émis) n'est pas dans le magasin Windows du poste. |
| « Le PVWA doit être joint en HTTPS » | Saisissez l'adresse sans `http://` (ou avec `https://`). |
| « Le PVWA n'a pas de composant de connexion « PSM-RDP » pour ce compte » (`EPVWA093E Failed to get the relevant connection component`) | La plateforme du compte utilise un composant d'un autre nom (par exemple `WIN-PSM`) : celui que propose le bouton « Connect » du PVWA, ou le nom après `/c` dans une commande `psm /u … /a … /c …`. Saisissez-le dans « Composant » et cochez « Mémoriser ce composant pour la plateforme » : il est retenu une fois la connexion acceptée par le PVWA. |
| « Le PVWA refuse l'identifiant ou le mot de passe » (`ITATS004E Authentication failure…`) | Vérifiez l'identifiant, le mot de passe et Verr. Maj avant de réessayer : après plusieurs échecs, CyberArk suspend le compte. |
| « Votre compte CyberArk est suspendu ou désactivé » | Trop d'échecs d'identification, ou décision d'un administrateur : demandez à l'équipe CyberArk de le réactiver. |
| « Votre mot de passe CyberArk a expiré » | Changez-le sur la page web du PVWA, puis identifiez-vous dans ZillaTerm avec le nouveau. |
| « Le serveur SSH refuse l'authentification » | Via le PSMP, c'est votre mot de passe CyberArk : vérifiez-le (et Verr. Maj) avant de réessayer, plusieurs échecs suspendent le compte. |
| « Nom de serveur introuvable (DNS) », « Le serveur n'a pas répondu à temps », « Le serveur refuse la connexion sur ce port » | Vérifiez l'adresse et le port, votre accès au réseau de l'entreprise (VPN, pare-feu) ; le « Détail » qui suit donne le message de Windows. |
| Fenêtre « Session CyberArk expirée » | Délai d'inactivité du PVWA dépassé : saisissez votre mot de passe pour vous identifier de nouveau ; onglets, sessions et transferts restent ouverts. |
| « Mot de passe » → « Copier le mot de passe… » : « Le PVWA refuse : … « Récupérer les comptes » … » | Droit manquant sur le safe, ou motif / ticket exigé par la plateforme : saisissez-le. Avec une double validation, faites la demande dans le PVWA. |
| « Vérifier / Changer / Réconcilier » : « Le PVWA refuse : … « Lancer les opérations CPM » … » | Demandez ce droit sur le safe ; « Membres du safe » montre vos droits. |
| « Ajouter un compte » : « Le PVWA refuse : votre compte doit avoir le droit « Ajouter des comptes »… » | Demandez ce droit sur le safe (et « Modifier le contenu des comptes » pour fournir le mot de passe), ou créez le compte sans mot de passe. « Membres du safe » montre vos droits. |
| « Membres du safe » : « Votre compte ne peut pas voir les membres de ce safe » | Le PVWA exige le droit « View Safe Members » sur le safe : demandez-le à un gestionnaire du safe. |
| « Ce composant de connexion n'est pas configuré pour la plateforme du compte » (`Connection component … is not configured for platform …`) | Choisissez le bon composant dans « Options avancées… », cochez « Mémoriser » pour la plateforme. |
| « CyberArk exige un motif pour ce compte » (`You must specify a reason…`) | Saisissez un motif dans la fenêtre qui s'ouvre (ou un motif par défaut dans les propriétés du serveur, dans « Mes serveurs »). |
| « CyberArk exige un ticket valide pour ce compte », « Ce compte exige une demande approuvée (double validation) » | Saisissez le système et le numéro du ticket ; pour la double validation, faites la demande d'accès sur la page web du PVWA et attendez son approbation. |
| Le compte n'apparaît pas | Vous n'avez pas le droit « List accounts » sur son safe, ou la liste doit être rechargée (`F5`). |
| Le mot de passe PSMP est demandé à chaque onglet | MFA caching non activé sur le PVWA : comportement normal (une fois par onglet). En ouvrant plusieurs sessions ensemble, cochez « Utiliser aussi ce mot de passe… » pour ne le saisir qu'une fois. |
| L'onglet Fichiers indique « Connexion SFTP impossible » | SFTP n'est pas autorisé sur le PSMP ou pour ce compte : voir l'équipe CyberArk. |
| Un envoi indique « SFTP (SCP refusé) » ou « SCP (SFTP refusé) » | Le PSMP ou le serveur a refusé ce protocole pour ce fichier : l'autre a pris le relais et le fichier a été vérifié comme d'habitude. Le bilan donne la réponse du serveur. Un PSMP qui refuse SCP pour une plateforme (erreur `118E Selected component PSMP-SCP does not contain the target settings definitions…` dans ses journaux) n'a pas le composant de connexion PSMP-SCP : votre équipe CyberArk peut l'ajouter à la plateforme, sinon les envois passent en SFTP. Après ce refus, l'onglet ne retente plus SCP : les fichiers suivants partent directement en SFTP, sans rouvrir une session par le PSMP pour chacun. |
| Le navigateur ne suit pas les `cd` | Le shell distant n'est pas bash, zsh ou tcsh (ou tcsh a déjà son propre alias `cwdcmd`), l'option est désactivée dans les Paramètres, ou l'invite n'a pas été reconnue : recochez « Suivre le dossier du terminal » à l'invite du shell. |
| Alerte « La clé du PSMP a changé » | Ne continuez (case « J'ai confirmé ce changement avec l'équipe CyberArk », puis « Remplacer la clé et se connecter ») que si l'équipe CyberArk confirme un changement du serveur ; sinon, annulez et prévenez-la. |
| « Connexion annulée : la clé du serveur n'a pas été acceptée. » | La fenêtre de l'empreinte a été annulée ou fermée : reconnectez-vous au serveur et acceptez la clé après avoir comparé son empreinte. |
| Fenêtre « Nouvelle clé du PSMP, d'un autre type » | Le PSMP présente un type de clé (ed25519, RSA…) jamais vu sur ce poste : comparez l'empreinte avec celle publiée par l'équipe CyberArk avant de cocher la case et de l'accepter ; sinon, annulez et prévenez-la. |
| « Mot de passe maître ou fichier clé incorrect » | Vérifiez le mot de passe et le fichier clé ; une base protégée par YubiKey n'est pas prise en charge. |
| La base KeePass demande le mot de passe malgré « Mémoriser » | Coffre local verrouillé (« Plus tard » au déverrouillage) ou mot de passe maître changé ailleurs : saisissez-le, il est remémorisé. |
| « Le fichier du coffre local est endommagé ou a été créé par un autre compte Windows » | Le coffre local ne suit pas un changement de poste ou de compte : supprimez-le dans les Paramètres et recréez-le. |
| « L'entrée … a été modifiée ou supprimée dans la base KeePass entre-temps » | Quelqu'un a changé la même entrée ailleurs : la base est rechargée, refaites la modification. |
| Un compte Unix s'ouvre en PSM et pas en SSH | Adresse du PSMP non renseignée dans les Paramètres, ou compte non reconnu comme Unix : clic droit → « Se connecter en SSH (PSMP) ». |
| Un compte s'ouvre en fichiers seuls et pas dans un terminal | Le nom de sa plateforme contient « SFTP » : clic droit → « Se connecter en SSH (PSMP) », ou « Propriétés… » dans « Mes serveurs » pour changer le type de connexion. |
| « La liste partagée est en cours de modification par quelqu'un d'autre » | Un autre poste écrit la liste depuis plus de 5 secondes, ou garde le fichier ouvert : réessayez dans un instant. |
| « vous n'avez pas le droit de modifier ce fichier (droits du partage réseau) » | Le partage est en lecture seule pour vous : demandez le droit d'écriture à son responsable. La liste reste utilisable. |
| Une liste partagée affiche « (illisible) » | Partage injoignable ou fichier endommagé : l'info-bulle donne l'erreur. Si le fichier est endommagé, copiez la version la plus récente du dossier `nom.versions` à sa place. |
| Comprendre un échec de connexion | Paramètres → Journal de débogage, reproduisez le problème, puis Paramètres → « Afficher le fichier du journal ». |
| Une session PSM dans un onglet demande de confirmer le certificat du PSM | Windows n'approuve pas ce certificat (auto-signé, ou émis pour un autre nom) : comparez l'empreinte avec l'équipe CyberArk avant d'accepter ; elle est ensuite retenue. Un certificat émis par l'autorité de l'entreprise pour le nom du PSM supprime cette question. |
| Session PSM : « Nouveau certificat pour ce PSM » | L'adresse du PSM regroupe sans doute plusieurs serveurs (répartition de charge), chacun avec son certificat auto-signé, ou le certificat a été renouvelé : comparez l'empreinte avec l'administrateur CyberArk, puis « Lui faire aussi confiance » (autre serveur PSM) ou « Remplacer les certificats connus » (renouvellement). |
| Session PSM : « Identité de … impossible à vérifier » | ZillaTerm n'a pas pu lire le certificat du PSM avant la connexion (PSM injoignable, port filtré, réponse qui n'est pas du Bureau à distance) : la raison suit le message. Rien n'a été envoyé au PSM. |
| Une session PSM s'ouvre dans la Connexion Bureau à distance malgré l'option « dans un onglet » | Connexion par une passerelle Bureau à distance, ou client FreeRDP impossible à lancer sur le poste (stratégie du poste) : la barre d'état le dit. |
| Alt+Tab ou la touche Windows ne répondent plus | L'onglet d'une session PSM a le clavier : toutes les touches vont à la session. Cliquez ailleurs dans ZillaTerm (un autre onglet, la liste). |
| Session PSM dans un onglet : Ctrl+V d'un fichier ou d'un texte copié sur le poste ne fait rien | Le PVWA n'autorise peut-être pas le presse-papiers, ou le PSM n'autorise que le texte. Paramètres → Journal de débogage, reproduisez (copie, clic dans la session, collage), puis « Afficher le fichier du journal » : les lignes `freerdp` donnent les redirections demandées par le PVWA (`presse-papiers oui/non`), chaque changement du presse-papiers du poste (programme, formats : fichiers, texte ; jamais le contenu) et les échanges du presse-papiers avec le PSM (`cliprdr` : `CB_STREAM_FILECLIP_ENABLED` absent des drapeaux du serveur signifie pas de copie de fichiers). |
| Session PSM dans un onglet : un disque du poste ne répond pas, ou le journal de débogage se remplit de `drive_map_windows_err` | Paramètres → Journal de débogage, ouvrez la session, ouvrez le disque dans la session (`\\tsclient\…`), puis « Afficher le fichier du journal » : avec des disques partagés, les lignes `rdpdr` donnent chaque demande du PSM aux disques (`MajorFunction` : opération ; `deviceID` : disque) et sa réponse (`ioStatus`, code NTSTATUS : `0x00000000` réussi, `0xC0000001` échec) ; jamais le nom ni le contenu d'un fichier. Le journal devient bavard (deux lignes par demande) : désactivez-le une fois le problème reproduit. |
| Session PSM : « Vérifier la clé de ce PSM » | Le PSM n'accepte que l'ancienne « sécurité RDP », sans TLS : seule l'empreinte de sa clé l'identifie. Acceptez-la au premier usage (elle est retenue) et demandez à l'administrateur CyberArk d'activer TLS sur le PSM. « Clé faible » en rouge : la clé fait moins de 2 048 bits ; cette demande est alors urgente. |
| Session PSM : « Nouvelle clé pour ce PSM » | L'adresse du PSM regroupe sans doute plusieurs serveurs (répartition de charge), chacun avec sa clé : confirmez-la si l'administrateur CyberArk le confirme. Les clés déjà acceptées le restent. |
| Session SSH ou PSMP : « La connexion a été coupée par le serveur ou le réseau » ou « Session fermée par le serveur » après une veille, un verrouillage ou une pause | La connexion est perdue : le poste s'est mis en veille, le réseau ou le VPN a changé, un pare-feu l'a oubliée, ou le PSMP a fermé la session (inactivité). ZillaTerm le détecte en une minute au plus (demande de maintien toutes les 15 s, à laquelle le serveur doit répondre) : « Reconnecter » ouvre une nouvelle session ; l'onglet Fichiers propose de rouvrir la sienne. |
| Session SSH ou PSMP : « Le serveur ne répond pas depuis N s », terminal grisé | Ce que vous avez tapé est resté sans réponse, comme la demande que ZillaTerm envoie alors au serveur (au bout de 2 s). Tout revient dès que le serveur répond ; sinon la connexion est déclarée perdue au bout d'une minute environ. « Reconnecter » ouvre tout de suite une nouvelle session. |
| Session PSM : « Ce serveur ne propose plus TLS » | La connexion est peut-être interceptée : ne continuez qu'avec l'accord de l'administrateur CyberArk (voir le message). |
| Session PSM : « Le serveur Bureau à distance n'utilise aucun chiffrement… » | Le PSM (ou un équipement entre les deux) propose l'ancienne sécurité RDP sans chiffrement : rien ne protégerait le jeton. Alertez l'administrateur CyberArk. |
| Un onglet de bureau à distance direct (KeePass) affiche « Erreur du contrôle Bureau à distance » | Signalez le code affiché (si le contrôle Bureau à distance est absent du poste, la connexion passe par `mstsc`). |
| VNC : « Le serveur VNC ne propose aucune authentification prise en charge… » | Le serveur exige une authentification propre à son éditeur (compte Windows, chiffrement VeNCrypt…) : activez l'authentification « mot de passe VNC » sur le serveur. |
| VNC : « Pas de réponse VNC du serveur en 30 secondes » | Mauvais port (5900 + numéro d'écran) ou service autre que VNC à cette adresse. |
| FTP : « Le serveur FTP ne propose pas de chiffrement (TLS), exigé par cette entrée » | Le serveur n'accepte pas TLS : utilisez `ftp://` (connexion en clair après confirmation) ou SFTP s'il est disponible. |
| FTP : alerte « Ce serveur FTP ne propose plus de chiffrement » | Le serveur chiffrait ses connexions et ne le fait plus : ne continuez que si son administrateur confirme le retrait de TLS ; sinon la connexion est peut-être interceptée. |
| Bureau à distance direct : « Le serveur Bureau à distance n'accepte pas le chiffrement TLS… » | Le serveur n'accepte que l'ancienne « sécurité RDP », qui ne permet pas de vérifier son identité : demandez à son administrateur d'activer TLS ou NLA. |
| FTPS : la liste des fichiers ne s'affiche pas ou un transfert expire | Un pare-feu bloque les ports passifs du serveur, ou le serveur exige la reprise de session TLS sur les connexions de données (`522`, par exemple `require_ssl_reuse` de vsftpd) : voir l'administrateur du serveur. |
