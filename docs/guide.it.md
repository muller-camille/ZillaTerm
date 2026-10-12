# Guida all'uso di ZillaTerm

[Français](guide.fr.md) · [English](guide.md) · **Italiano** · [← Torna al README](../README.it.md)

## Indice

- [1. Accedere al vault CyberArk](#1-accedere-al-vault-cyberark)
- [2. Trovare un account: scheda «Disponibili»](#2-trovare-un-account-scheda-disponibili)
- [3. Aprire una sessione PSM (desktop remoto)](#3-aprire-una-sessione-psm-desktop-remoto)
- [4. Aprire una sessione SSH tramite il PSMP](#4-aprire-una-sessione-ssh-tramite-il-psmp)
- [5. Sfogliare e inviare file: scheda «File»](#5-sfogliare-e-inviare-file-scheda-file)
- [6. Organizzare i server: scheda «I miei server»](#6-organizzare-i-server-scheda-i-miei-server)
- [7. Accesso di emergenza fuori da CyberArk: database KeePass](#7-accesso-di-emergenza-fuori-da-cyberark-database-keepass)
- [8. Intervento: registrazione e promemoria di reperibilità](#8-intervento-registrazione-e-promemoria-di-reperibilità)
- [Scorciatoie](#scorciatoie)
- [Impostazioni e file di configurazione](#impostazioni-e-file-di-configurazione)
- [Sicurezza](#sicurezza)
- [Funzionamento tecnico](#funzionamento-tecnico)
- [Risoluzione dei problemi](#risoluzione-dei-problemi)

## 1. Accedere al vault CyberArk

<img src="captures/it/sign-in.png" alt="Finestra di accesso" width="440">

Inserisci l'indirizzo del PVWA (`pvwa.miodominio.local` è sufficiente: `https://` e `/PasswordVault` vengono
aggiunti), scegli il metodo di autenticazione, poi nome utente e password. Se il server RADIUS pone una domanda
(codice OTP), la finestra la mostra e attende la tua risposta. Durante la digitazione di una password, l'avviso
«Bloc Maiusc è attivo.» compare se il tasto è attivo (lo stesso per i database KeePass e il vault locale).

Indirizzo, metodo e nome utente vengono memorizzati; **la password mai**.

L'elenco in basso a sinistra cambia la lingua dell'interfaccia (Français, English, Italiano); la finestra si riapre
subito nella lingua scelta, conservando l'indirizzo e il nome utente inseriti.

### Finestra principale

- **Pannello laterale**: schede «Disponibili», «I miei server» e «File» (`Ctrl+1`, `Ctrl+2`, `Ctrl+3`); la scheda
  «File» compare solo quando è aperta una sessione SSH o di file. Trascina il separatore per cambiarne la larghezza; `Ctrl+B` o un doppio clic sul separatore lo chiude (la striscia delle
  schede resta: un clic su una scheda lo riapre). `F6` passa dal pannello alla sessione. Posizione e dimensione
  della finestra, larghezza del pannello e il suo stato chiuso vengono memorizzati.
- **Schede di sessione**: un pallino indica lo stato della sessione con il colore e con la forma: anello arancione
  durante la connessione, pallino verde una volta connessa, anello grigio quando la sessione è terminata, pallino
  rosso in caso di errore (il nome è allora attenuato). La descrizione comandi riporta il nome completo, lo stato e
  la modalità: «Tramite il PSMP …: sessione gestita da CyberArk» o «Accesso diretto di emergenza (KeePass): fuori
  da CyberArk, annotato in urgence.log». Un nome troppo lungo viene troncato; un nome già aperto viene numerato
  («srv01 (2)»). Quando le schede non ci stanno più, la striscia scorre (rotellina, la scheda scelta resta visibile)
  e «⌄» le elenca tutte con il loro stato. `Ctrl+Tab` / `Ctrl+Maiusc+Tab`: scheda successiva / precedente;
  `Ctrl+F4` o `Ctrl+Maiusc+W`: chiudi la scheda.
- **Chiudere una sessione connessa** (SSH, desktop remoto, VNC) chiede conferma, con la casella «Non chiedere più
  alla chiusura di una sessione» (impostazione «Conferma prima di chiudere una sessione connessa», Impostazioni ›
  Terminale). Con «Disconnetti da CyberArk» (che chiude tutte le schede) e all'uscita, una sola finestra riepiloga
  ciò che verrà chiuso: sessioni, trasferimenti in corso, file modificati non rimandati.
- **Conferme**: i pulsanti dicono l'azione («Elimina l'account», «Sostituisci la chiave e connetti»…), «Annulla» è il
  pulsante predefinito, e il server, l'account o il safe interessato viene nominato. I valori da confrontare
  (impronte) sono mostrati con un carattere a spaziatura fissa, con «Copia». Alcune azioni irreversibili (eliminare
  un account, accettare una chiave di server cambiata) richiedono anche di selezionare una casella.
- **Pulsanti disattivati**: la loro descrizione comandi dice cosa manca (selezione, PSMP, sessione SSH per
  «Parallelo»…). In accesso di emergenza, i pulsanti propri di CyberArk sono nascosti.
- **Barra di stato**: un messaggio ordinario si cancella dopo 10 secondi; un errore resta visibile fino al messaggio
  successivo. Il numero di account compare solo con la scheda «Disponibili».
- **Messaggi di errore**: i rifiuti comuni del PVWA (password rifiutata, account sospeso, password scaduta, motivo o
  ticket richiesto, doppio controllo, diritti mancanti, sessione scaduta) e gli errori comuni di connessione
  (autenticazione SSH rifiutata, nome del server non trovato, server che non risponde o rifiuta la connessione,
  connessione interrotta) sono spiegati e dicono cosa fare; segue il testo originale («Messaggio del PVWA: …» o
  «Dettaglio: …»), da trasmettere così com'è al team CyberArk o al supporto.
- **Sessione CyberArk scaduta** (timeout di inattività del PVWA): una finestra chiede la password (e la risposta
  RADIUS se serve) per accedere di nuovo, con lo stesso indirizzo, lo stesso utente e lo stesso metodo. Schede,
  sessioni aperte e trasferimenti restano come sono; l'azione che ha incontrato la scadenza viene ripetuta
  (caricamento degli account, connessione). «Più tardi» permette di lavorare senza CyberArk: F5, o la prossima azione
  CyberArk, lo propone di nuovo. Se la sessione scade mentre ZillaTerm è in secondo piano, la finestra compare quando
  ci torni.

## 2. Trovare un account: scheda «Disponibili»

![Scheda «Disponibili» filtrata su più server](captures/it/available.png)

- La casella di ricerca («Filtra gli account…», `Ctrl+F`) filtra su tutti i campi (server, utente, safe,
  piattaforma, dominio…), anche con più parole (`prd sql`). Quando la parola cercata non si vede nella riga, il
  motivo compare in grigio a destra dell'account («corrisponde: macchina srv01», «corrisponde: safe PROD»…).
  La ✕ in fondo alla casella (come in ogni casella di ricerca o di filtro) o `Esc` la svuota.
- Più account mostrati con lo stesso nome (`utente@indirizzo`) in una cartella si distinguono con un testo grigio a
  destra: le loro macchine autorizzate («→ srv01, srv02 (+2)», «→ qualsiasi macchina»), altrimenti la
  piattaforma, il safe o il nome in CyberArk. Le utilità per la lettura dello schermo lo leggono con il nome
  dell'account.
- Al posto di un elenco vuoto, la scheda dice cosa succede: caricamento degli account, caricamento non riuscito con
  il suo messaggio e «Riprova», nessun account disponibile per il tuo utente CyberArk, o nessun account
  corrispondente al filtro, con «Cancella il filtro».
- «Raggruppa per» ordina gli account per safe, piattaforma o tipo di destinazione.
- Clic destro su un account → «Esporta gli account visualizzati (CSV)…» salva in CSV gli account mostrati
  (filtrati dalla ricerca).
- **Membri di un safe**: clic destro su un account (o su un safe quando gli account sono raggruppati per safe, o su
  un server di «I miei server») → «Membri del safe». La finestra elenca gli utenti e i gruppi del safe con i loro
  diritti (elencare, usare, recuperare, aggiungere account, aggiornare, eliminare, gestire i membri…), indica chi
  può **aggiungere account** e mostra tutti i diritti del membro selezionato. Durante la lettura mostra
  «Caricamento dei membri…»; in caso di errore, il messaggio compare al centro con «Riprova». Il PVWA fornisce questo elenco solo a
  un account con il diritto «View Safe Members» sul safe. `Ctrl+A` e poi `Ctrl+C` copia la tabella. Con il diritto
  «Gestire i membri del safe», i pulsanti «Aggiungi un membro…», «Modifica i diritti…» (o doppio clic) e «Rimuovi…»
  gestiscono i membri: nome, tipo (utente o gruppo), directory («Vault» o il dominio LDAP), eventuale data di fine e
  i 22 diritti, raggruppati come nel PVWA. L'elenco «Profilo» seleziona in un colpo i diritti di un uso comune (sola
  lettura, utente degli account, gestore degli account, completo); i diritti sensibili sono segnalati e concederli
  chiede conferma; «Modifiche: +n / −n» riassume ciò che cambia. La rimozione di un membro viene confermata
  («Rimuovi il membro»).
- **Aggiungere un account**: clic destro su un account (o su un safe quando gli account sono raggruppati per safe) →
  «Aggiungi un account al safe…». Safe, piattaforma, indirizzo e utente sono obbligatori; dominio di accesso, nome
  dell'account, password, macchine consentite e gestione da parte del CPM sono facoltativi. L'account cliccato fa da
  modello (safe, piattaforma, dominio) e il cursore viene posto sul primo campo obbligatorio vuoto. L'account viene
  creato con i diritti della tua sessione: serve il diritto
  «Aggiungere account» sul safe e, in genere, «Aggiornare il contenuto degli account» per fornire la password.
  L'elenco viene poi ricaricato e il nuovo account selezionato.
- **Importare account (CSV)**: clic destro su un account o un safe → «Importa account (CSV)…». Una finestra chiede
  il file («Salva un modello…» fornisce un esempio), il safe e la piattaforma predefiniti, poi mostra un'anteprima
  di tutte le righe del file (righe con errore in rosso, casella «Solo errori») e i safe interessati; nulla viene
  inviato prima del pulsante «Crea N account». Colonne
  obbligatorie: indirizzo e utente (più safe e piattaforma, altrimenti i valori predefiniti); facoltative: nome,
  dominio, password, macchine consentite, gestione CPM (sì/no), motivo. Separatore `;`, `,` o tabulazione, nomi
  delle colonne in italiano, francese o inglese; un file prodotto da «Esporta gli account visualizzati» si può reimportare. Una seconda
  finestra crea poi gli account riga per riga e mostra lo stato di ciascuna (creato, rifiutato con il messaggio del
  PVWA, non importato, non inviato; «Interrompi» disponibile). Chiuderla durante l'importazione chiede conferma:
  «Continua» (predefinito) o «Interrompi l'importazione» (l'account in corso viene completato, gli account già creati
  restano nel vault CyberArk). Alla fine propone di salvare il risultato in CSV
  (senza le password). Le password del file non vengono mai mostrate; elimina il file dopo l'importazione.
- **Modificare / eliminare un account**: clic destro → «Modifica l'account…» (piattaforma, indirizzo, utente,
  dominio, nome, macchine consentite, gestione da parte del CPM; vengono inviati solo i campi modificati) oppure
  «Elimina l'account…»: la conferma ricorda che l'account viene eliminato per tutti gli utenti e che la sua password
  non sarà più recuperabile; occorre selezionare «Ho capito che la password attuale non sarà più recuperabile».
  Diritti «Aggiornare le proprietà degli account» ed «Eliminare account».
- **Stato della password (CPM)**: il tooltip di un account indica se è gestito dal CPM e la data dell'ultimo cambio,
  dell'ultima verifica e dell'ultima riconciliazione; un **⚠** segnala un account la cui ultima operazione del CPM
  non è riuscita.
- **Clic destro → «Password»** (account di «Disponibili» e server di «I miei server»):
  - «Verifica (CPM)», «Cambia (CPM)…», «Riconcilia (CPM)…» chiedono l'operazione al CPM (conferma per cambiare e
    riconciliare, con i pulsanti «Cambia la password» e «Riconcilia»; diritto «Avviare le operazioni CPM»). Il CPM la
    esegue poi: `F5` per vedere il nuovo stato.
  - «Copia la password…»: la finestra nomina l'account e il suo safe, e indica che il recupero viene registrato
    nell'audit CyberArk e che la password resta 20 secondi negli appunti. Motivo e ticket sono facoltativi, a meno
    che la piattaforma non li richieda; «Recupera e copia» copia la password **senza mostrarla**, poi la barra di
    stato conta alla rovescia i secondi prima della sua cancellazione (diritto «Recuperare gli account»).
- Nella scheda Home, la **connessione rapida** (`Ctrl+K`; il cursore vi si trova all'avvio) trova un server mentre
  digiti: Invio per connetterti. Dice quando nessun account corrisponde e mostra solo i primi 50 risultati («Primi 50
  account su N: precisa la ricerca.»). Le **sessioni recenti** sono datate («Oggi 09:28», «Ieri 18:02»); clic
  destro → «Rimuovi dall'elenco» (o `Canc`) ne toglie una. Restano in grigio finché gli account non sono caricati
  dal PVWA («in attesa degli account del PVWA…»); sono quelle del PVWA a cui sei connesso: su un altro PVWA lo
  stesso ID di account indica un altro account.

## 3. Aprire una sessione PSM (desktop remoto)

<img src="captures/it/psm-connect.png" alt="Opzioni avanzate di connessione: macchina di destinazione, motivo, ticket" width="520">

Fai doppio clic sull'account (oppure Invio, oppure il pulsante «Connetti»); un account Unix si apre in SSH tramite
il PSMP quando il suo indirizzo è impostato, o in soli file per una piattaforma «SFTP» (vedi 4.), e «Opzioni
avanzate…» permette allora di scegliere il PSM. ZillaTerm richiede la connessione al PVWA e apre la sessione in **Connessione Desktop remoto** di Windows
(`mstsc`), esattamente come il pulsante «Connect» del PVWA: il file RDP del PVWA le viene passato così com'è. Un
componente che apre un'applicazione remota (RemoteApp) apre le sue finestre sul desktop del computer.

**Sessioni PSM in una scheda** (opzione «Apri le sessioni PSM Windows in una scheda di ZillaTerm», Impostazioni ›
CyberArk, disattivata per impostazione predefinita): ZillaTerm legge da sé il file del PVWA e apre la sessione in una
scheda con il client Desktop remoto FreeRDP che integra: nessun file `.rdp`, nessun avviso di Windows sul suo autore,
nessuna finestra separata.

- **Apertura**: la scheda si apre subito, come per una sessione PSMP; una barra di avanzamento vi resta mentre
  ZillaTerm chiede la sessione al PVWA e poi si connette al PSM, fino alla comparsa della sessione. Se il PVWA rifiuta
  la richiesta (motivo richiesto, componente sconosciuto…), la scheda si chiude e la finestra di connessione lo dice.
- **Desktop o applicazione remota**: un desktop Windows come un'applicazione remota (RemoteApp:
  `||PSMInitSession`, componenti PSM-RDP, PSM-SSH, WinSCP…) si mostrano nella scheda. Le finestre dell'applicazione
  vi sono collocate come su un desktop; nessuna si apre su quello del computer.
- **Dimensione**: la sessione prende la dimensione della scheda alla connessione e la mantiene. Se la finestra cambia
  dimensione, l'immagine viene ridimensionata: il PSM ha aperto la sua connessione al server a quella dimensione. La
  sessione non passa mai a schermo intero, anche se il file del PVWA lo chiede. Con l'opzione sperimentale «Adatta la
  risoluzione del desktop remoto alla dimensione della scheda» (Impostazioni, pagina CyberArk), la risoluzione della
  sessione PSM segue la scheda a ogni ridimensionamento, senza adattare l'immagine; a seconda della configurazione del
  PSM, la connessione al server di destinazione può mantenere la dimensione originale: disattiva allora l'opzione.
- **Tastiera**: quando la scheda ha la tastiera (clic nella sessione), tutti i tasti vanno alla sessione, scorciatoie
  di Windows comprese (Alt+Tab, tasto Windows). Fai clic altrove in ZillaTerm per riaverle.
- **Certificato del PSM**: letto da ZillaTerm durante la barra di avanzamento, prima di qualsiasi invio al PSM (né
  token, né utente), e verificato con le autorità approvate da Windows (nome del PSM, date, revoca quando è
  raggiungibile). Approvato: niente da confermare. Altrimenti (certificato autofirmato, emesso per un altro nome),
  ZillaTerm ne mostra l'impronta e chiede di confermare una volta, poi lo memorizza, come una chiave SSH. L'indirizzo di
  un PSM con bilanciamento del carico può nascondere più server, ciascuno con il proprio certificato: un altro
  certificato viene segnalato, poi accettato come altro server PSM (i certificati noti restano accettati) o al posto dei
  certificati noti (rinnovo), dopo conferma. Dopo una domanda, la connessione parte con un nuovo token richiesto al
  PVWA. Mai una connessione senza verifica, mai una domanda durante la connessione, mai una domanda di Windows. Un
  certificato del PSM emesso dall'autorità dell'azienda, da chiedere al team CyberArk, evita le conferme.
- **Reindirizzamenti** (appunti, unità…): quelli richiesti dal PVWA, mai di più. Nessuna immagine della sessione
  viene conservata sul disco.
- **Riconnetti**, «Duplica la scheda»: nuova richiesta al PVWA (il token di una sessione PSM vale una sola volta);
  motivo e ticket restano quelli della prima connessione.
- La scheda mostra l'etichetta del server (PROD, QA…) e la sua cornice colorata; «Cattura» aggiunge un'immagine al
  registro di un intervento in corso.
- Una connessione tramite un gateway Desktop remoto si apre sempre in Connessione Desktop remoto; anche la sessione,
  se il client FreeRDP non può essere avviato sul computer (criteri del computer, per esempio): la barra di stato lo
  dice.

- **Componente PSM**: dedotto dalla piattaforma (`PSM-RDP` per Windows, `PSM-SSH` per Unix e rete,
  `PSM-SQLServerMgmtStudio`, `PSM-SQLPlus`…). Seleziona «Memorizza questo componente» per conservarlo per tutta la
  piattaforma. Il tuo PVWA può chiamare i suoi componenti in un altro modo (ad esempio `WIN-PSM`): inserisci il nome
  proposto dal suo pulsante «Connect»; l'elenco propone poi i componenti già usati, quello della piattaforma per
  primo. Per tutti i tuoi account Windows, imposta una volta il componente in **Impostazioni › CyberArk** («Account
  Windows», ad esempio `WIN-PSM`); un componente memorizzato per una piattaforma resta prioritario. I componenti per
  piattaforma si vedono e si modificano nello stesso punto («Componente per piattaforma»).
- **Account di dominio**: un account registrato per il suo dominio non ha un server. Viene riconosciuto dalla
  piattaforma di dominio, dalla limitazione alle sue macchine autorizzate o dall'indirizzo: il dominio di accesso, un
  dominio sotto cui si trovano altri server (`corp.local` quando un account punta a `srv01.corp.local`), o il dominio
  del PVWA o della postazione. Un account locale il cui dominio di accesso è il nome del server (`srv01.corp.local`,
  `SRV01`) resta un account di quel server quando un altro account della cassaforte lo punta (account locale, account
  Unix) o quando la sua piattaforma è una piattaforma di server o di postazioni («Server», «Srv», «Desktop»,
  «Workstation»). Un account che ha solo un elenco di macchine, senza esservi limitato, si apre sul proprio
  indirizzo; la macchina di destinazione resta facoltativa. Mai una sessione verso il dominio stesso: il server viene sempre chiesto, «Opzioni avanzate…» comprese. La finestra «Scegli il server» chiede su quale aprire la sessione:
  l'elenco propone i server già usati con questo account (sessioni recenti, «I miei server»), poi le sue macchine
  autorizzate; un account limitato alle sue macchine rifiuta le altre. «Mantieni questo server in «I miei server»»,
  con la cartella desiderata, lo aggiunge dopo una connessione riuscita, con il nome `account@server` (la scelta
  viene ricordata per la volta successiva; la casella scompare se il server è già presente). «Opzioni avanzate…»
  apre la finestra completa con questo server.
- **Motivo e ticket**: se il PVWA rifiuta la richiesta (motivo o ticket richiesto, componente non configurato…), la
  finestra dice cosa fare, seguito dal messaggio originale del PVWA, e puoi correggere e riprovare.
- Il pulsante «Opzioni avanzate…» della barra degli strumenti (o clic destro → «Opzioni avanzate…») apre questa
  finestra, «Opzioni avanzate di connessione», su richiesta. I due campi del ticket (sistema, numero) hanno un nome
  per le utilità per la lettura dello schermo; la colonna delle etichette si allarga con il loro testo. Il cursore viene posto sul primo campo utilizzabile; in SSH o in soli file, i campi che
  servono solo al PSM sono disattivati e la loro descrizione comandi lo dice; senza componente, la finestra chiede di
  sceglierne uno.

## 4. Aprire una sessione SSH tramite il PSMP

Imposta una volta il PSMP in **Impostazioni › CyberArk** (vedi [PSMP per dominio](#psmp-per-dominio) se ne hai
diversi). Il doppio clic (o Invio) sceglie allora in base al nome della piattaforma dell'account:

| Nome della piattaforma | Apertura predefinita |
| --- | --- |
| contiene «SFTP» (`UnixSFTP`, `SFTP-Partner`…) | **solo file** in SFTP tramite il PSMP (vedi sotto) |
| contiene «SSH» (`UnixSSH`, `CiscoSSH`…), o un'altra piattaforma Unix | **SSH** tramite il PSMP |
| altro | **PSM** (desktop remoto) |

Il clic destro propone sempre le tre (l'apertura predefinita è in grassetto): «Connetti (PSM)», «Connetti in SSH
(PSMP)» (o il pulsante «SSH») e «Apri i file (SFTP, PSMP)».

**Solo file**: una sola sessione PSMP SFTP, senza terminale. Una scheda ne mostra lo stato, il server e un pulsante
«Mostra i file», che apre la scheda «File» del pannello di sinistra e la allarga se serve perché tutte le colonne ci
stiano. I file sono lì, con le stesse funzioni (trasferimenti verificati, coda, editor, confronto, monitoraggio in
tempo reale, permessi), tranne ciò che richiede un terminale (monitoraggio della cartella del terminale, estrazione di
un archivio `.tar.gz`). Utile per depositare o recuperare file, o quando la piattaforma consente PSMP-SFTP ma non la
shell. Come ogni sessione PSMP, è registrata e verificata da CyberArk.

La sessione si apre **in una scheda di ZillaTerm**, subito: una barra di avanzamento compare mentre ZillaTerm chiede
la chiave MFA al PVWA e si connette al PSMP (le domande su chiave del server, password o codice arrivano nel
frattempo). L'identificativo è quello PSMP standard `<tu>@<account di destinazione>[#dominio]@<server di
destinazione>`. I nomi utente che contengono spazi (`Mario Rossi`, `Admin
Locale`) sono accettati. Il pannello laterale passa alla scheda «File» del server (se è ridotto, resta ridotto).

### Applicazioni grafiche (X11)

Le applicazioni grafiche avviate in una sessione SSH (`xclock`, programmi di installazione Oracle, `xterm`…) possono
aprirsi sul vostro computer, come con `ssh -X`:

1. Installate un **server X** sul computer (VcXsrv, X410, Xming…) e avviatelo consentendo le **connessioni TCP
   locali**: il display `:0` è in ascolto sulla porta 6000. Il server X si imposta in **Impostazioni › Terminale ›
   Server X locale** (`:0` per impostazione predefinita; solo questo computer, perché il protocollo X non è cifrato).
2. Selezionate **«Inoltro X11 (applicazioni grafiche)»** per il server: clic destro → «Proprietà…» in «I miei
   server», oppure in «Opzioni avanzate…» per una sola connessione. Solo sessioni SSH; non selezionato per impostazione
   predefinita.
3. Aprite la sessione: una riga grigia indica se l'inoltro è attivo, se è stato rifiutato (dal server, il cui
   `sshd_config` deve contenere `X11Forwarding yes`, o dal PSMP) o se nessun server X è in ascolto sul computer.

ZillaTerm fornisce al server un **cookie fittizio**: ogni connessione X11 deve presentarlo, e viene rimosso prima che
qualsiasi cosa raggiunga il server X del computer; una connessione senza questo cookie viene chiusa. ⚠ Solo per server
attendibili: un server compromesso potrebbe vedere le altre finestre X del computer e inviarvi dei tasti. La scelta è
personale: non viene né esportata né inserita in un elenco condiviso.

### PSMP per dominio

Con un PSMP per dominio, dichiarali in **Impostazioni › CyberArk**: un **PSMP predefinito** e l'elenco **PSMP per
dominio** («Aggiungi un PSMP», indirizzo, porta; il dominio servito è quello dell'indirizzo, modificabile). Ogni
server passa dal PSMP del dominio **più vicino al suo**:

| PSMP configurati | Server | PSMP usato |
| --- | --- | --- |
| `psmp.xxx.corp.com`, `psmp.zzz.corp.com` | `srv01.xxx.corp.com` | `psmp.xxx.corp.com` |
| idem | `srv02.zzz.corp.com` | `psmp.zzz.corp.com` |
| solo `psmp.xxx.corp.com` | `srv03.zzz.xxx.corp.com` | `psmp.xxx.corp.com` (nessun PSMP per `zzz.xxx.corp.com`) |
| idem | `srv04.altro.org`, un indirizzo IP | il PSMP predefinito; senza, l'unico PSMP dell'elenco se ce n'è uno solo |

Senza PSMP per un server (diversi PSMP, nessuno predefinito, dominio non coperto), la connessione viene rifiutata e
la barra di stato lo dice. «Quale PSMP per il server» dà la risposta prima di salvare; due PSMP per lo stesso dominio
vengono rifiutati. Solo i PSMP dell'elenco ricevono la tua password CyberArk, mai un indirizzo dedotto dal nome di un
server; la chiave di ogni PSMP viene verificata alla sua prima connessione. Il tooltip della scheda indica il PSMP
usato.

<img src="captures/it/psmp-authentication.png" alt="Domanda di autenticazione posta dal PSMP" width="49%"> <img src="captures/it/terminal-menu.png" alt="Menu del clic destro nel terminale SSH" width="49%">

- **Autenticazione**: se il PVWA fornisce una chiave «MFA caching», non viene posta alcuna domanda. Altrimenti
  le domande del PSMP (password, codice MFA) compaiono in una finestra che nomina la sessione interessata, con un
  aiuto secondo la domanda: probabilmente la password del tuo account CyberArk (riutilizzata per le connessioni
  SFTP e SCP della stessa scheda, mai salvata), o il codice MFA (richiesto a ogni connessione). Una risposta
  rifiutata viene segnalata in rosso sopra il campo con il numero del tentativo («Tentativo 2 di 3»); dopo tre rifiuti
  la connessione si ferma per non bloccare il tuo account. «Bloc Maiusc è attivo.» compare durante la digitazione.
  Quando più sessioni si aprono insieme (cartella, selezione, vista parallela), la finestra propone «Usa questa
  password anche per le altre sessioni in apertura»: se spuntata, viene chiesta una sola volta (mai per un codice
  MFA) e conservata solo in memoria fino alla loro connessione. Le password conservate per le schede vengono
  dimenticate al blocco di Windows.
- **Chiave del PSMP**: alla prima connessione, una finestra ne mostra l'impronta SHA-256 con un carattere a
  spaziatura fissa, con «Copia»: confrontala con quella pubblicata dal team CyberArk prima di «Considera attendibile
  e connetti» («Annulla la connessione» è il pulsante predefinito). L'impronta viene poi memorizzata sul computer.
  Se la chiave cambia, una fascia rossa avverte di una possibile intercettazione, vengono mostrate l'impronta
  memorizzata e quella nuova, e «Sostituisci la chiave e connetti» è possibile solo dopo aver selezionato «Ho
  confermato la modifica con il team CyberArk». Una chiave rifiutata ferma la connessione («Connessione annullata:
  la chiave del server non è stata accettata.»). Un PSMP può avere chiavi di più tipi (RSA, ed25519…): ognuna è
  memorizzata a parte, e ZillaTerm chiede per primo il tipo già noto. Una chiave di un tipo mai visto per questo PSMP
  non è né accettata d'ufficio né presa per una modifica: la finestra «Nuova chiave del PSMP, di un altro tipo»
  mostra le impronte già accettate e quella nuova, e «Accetta questa chiave e connetti» è possibile solo dopo aver
  selezionato «Ho verificato questa impronta con il team CyberArk».
- **Terminale**: la selezione copia, la rotellina o la barra di scorrimento a destra percorre la cronologia; dopo
  essere risaliti, «↓ Torna alla fine» (o digitare) riporta alla fine. AltGr funziona sulle tastiere
  internazionali. Chiudi la scheda con la croce, con un clic centrale o con `Ctrl+F4` (conferma se la sessione è
  connessa).
- **Fine della sessione**: una fascia in cima al terminale ne indica il motivo, con «Riconnetti»; le ultime righe
  restano leggibili, selezionabili e copiabili.
- **Clic destro nel terminale** (o tasto Menu della tastiera): copia, incolla, seleziona tutto, cerca, salva il
  contenuto, cancella la cronologia (solo su questo computer, nulla viene inviato al server), dimensione del
  carattere, e le azioni della scheda (riconnetti, duplica, stacca, vista parallela, aggiungi a «I miei server»,
  chiudi). Per incollare con un semplice clic destro, spunta «Il clic destro nel terminale incolla gli appunti» nelle
  Impostazioni; Maiusc+clic destro apre allora il menu.
- **Incollare più righe**: quando la shell eseguirebbe le righe una alla volta (senza incolla protetto, «bracketed
  paste»), una finestra mostra le righe e chiede conferma («Incolla», «Annulla» predefinito), con la casella «Non
  avvisare più prima di incollare più righe» (impostazione «Avvisa prima di incollare più righe quando la shell le
  eseguirebbe una alla volta», Impostazioni › Terminale).
- **Aspetto**: tavolozza di colori e dimensione del carattere nelle Impostazioni (Campbell, One Half, Solarized,
  scuri o chiari); `Ctrl+rotellina` ingrandisce o riduce un terminale, `Ctrl+0` torna alla dimensione predefinita.
- **Cercare** nel terminale, cronologia compresa: `Ctrl+Maiusc+F` (o clic destro nel terminale o sulla scheda). Le
  occorrenze sono evidenziate; `Invio` risale verso le più vecchie, `Maiusc+Invio` riscende, `Esc` chiude.
- **Salvare il contenuto** del terminale (cronologia e schermo) in un file di testo: `Ctrl+Maiusc+S` (o clic destro
  nel terminale o sulla scheda). Solo su tua richiesta: il file può contenere informazioni sensibili.
- **Staccare una scheda** (altro schermo): trascina la scheda SSH fuori dalla finestra, o clic destro → «Stacca in
  una nuova finestra». Il terminale passa in una finestra separata e la sessione continua. La scheda mantiene il suo
  posto («Mostra la finestra», «Riporta nella scheda») e la scheda File lavora su questa sessione quando è
  selezionata. Chiudere la finestra separata riporta il terminale nella sua scheda senza chiudere la sessione. Le
  schede Desktop remoto non si staccano (usa «Schermo intero»); una sessione PSM aperta in Connessione Desktop
  remoto di Windows è già una finestra a parte.
- **Vista parallela** (fino a 8 sessioni sullo schermo): pulsante «Parallelo» della barra degli strumenti
  (disattivato finché non è aperta alcuna sessione SSH), o clic
  destro su una scheda SSH → «Aggiungi alla vista parallela». Seleziona le sessioni SSH aperte da mostrare insieme
  (8 al massimo): vengono disposte a griglia nella scheda «Parallelo», affiancate fino a 3, poi su due righe. Ogni
  sessione ha il suo titolo e il suo stato (anello durante la connessione, pallino pieno poi); «⤢» (o doppio clic
  sul titolo) la ingrandisce da sola, «✕» la rimanda nella sua scheda. La sessione in cui digiti ha una cornice più
  spessa e il segno «⌨ Digitazione qui». La scheda File segue la sessione in cui lavori. «Chiudi la vista»
  restituisce ogni terminale alla sua scheda senza chiudere le sessioni. Le sessioni Desktop remoto non possono
  esservi inserite.
  - **Digitazione simultanea**: pulsante «Digitazione simultanea» della vista. Ciò che digiti in una sessione
    selezionata («Riceve la digitazione») viene inviato anche alle altre sessioni selezionate e connesse: lo stesso
    comando su più server. È **disattivata a ogni apertura della vista**; quando è attiva, il pulsante diventa ambra
    con «ATTIVA (n)», una fascia ambra indica il numero e il nome delle sessioni che ricevono la digitazione, e una
    cornice ambra le circonda; le sessioni non selezionate sono attenuate e segnate «esclusa». Una
    sessione aggiunta mentre è attiva non è selezionata; ciò che viene digitato in una sessione non selezionata va
    solo a lei. Ogni tasto viene codificato dalla sessione che lo riceve (le frecce funzionano in una shell come in
    vim). La rotellina non viene copiata, e incollare più righe in più sessioni chiede conferma.
  - **Da «I miei server»**: clic destro su una cartella → «Apri nella vista parallela» connette i suoi server SSH
    (sottocartelle comprese) e li mette direttamente nella vista; oppure scegli dei server con `Ctrl+clic`
    (`Maiusc+clic` per una serie), poi clic destro → «Apri i N server nella vista parallela». Oltre i posti liberi
    (8 al massimo), una finestra chiede quali aprire; i server Windows (PSM) vengono esclusi. Ogni connessione resta
    una sessione PSMP distinta, con le sue domande abituali.
  - **Finestra separata**: pulsante «Finestra separata» della vista, clic destro sulla scheda «Parallelo», o
    trascina la scheda fuori dalla finestra. Chiuderla riporta la vista nella sua scheda senza chiudere le sessioni.
  - **Inviare file** alle sessioni della vista: pulsante «Invia file…» (vedi la scheda File).

## 5. Sfogliare e inviare file: scheda «File»

La scheda **File** del pannello laterale (`Ctrl+3`) segue la scheda SSH attiva e passa in primo piano all'apertura di
una sessione SSH; compare solo quando è aperta una sessione SSH o di file. Serve anche alle sessioni di soli file, che la mostrano alla loro apertura: account CyberArk
in SFTP tramite il PSMP ([sezione 4](#4-aprire-una-sessione-ssh-tramite-il-psmp))
e voci KeePass SFTP, FTP, FTPS ([sezione 7](#7-accesso-di-emergenza-fuori-da-cyberark-database-keepass)).

![Scheda File ordinata per data, accanto al terminale](captures/it/main-window.png)

- **Contrassegno della scheda**: sulla scheda «File», un contrassegno indica il numero di trasferimenti in corso o
  in attesa, oppure «!» per un trasferimento non riuscito o diverso che non hai ancora visto (mostrare la scheda lo
  segna come visto). Anche il pulsante di ZillaTerm nella barra delle applicazioni di Windows mostra l'attività o
  l'errore. Senza sessioni aperte la scheda è nascosta: il «!» passa sul pulsante «Trasferimenti» della barra
  degli strumenti, e aprire la cronologia dei trasferimenti lo segna come visto.
- **Barra del percorso**: percorso corrente, modificabile (digita un percorso e premi Invio). Doppio clic su una
  cartella per entrarvi, `..` per risalire, pulsanti «cartella superiore» (icona diversa da quella di «Invia») e
  «cartella home».
- **Colonne e ordinamento**: clic sull'intestazione di una colonna (Nome, Dimensione, Modificato, Permessi,
  Proprietario, Gruppo); un secondo clic inverte l'ordine (lo indica una freccia). Dimensione e data partono dai più
  grandi e dai più recenti. Le cartelle restano in cima; l'ordinamento è mantenuto da una cartella e da una sessione
  all'altra. La colonna Nome prende la larghezza lasciata dalle altre; quando il pannello è stretto, le colonne Gruppo,
  Proprietario e poi Permessi vengono nascoste invece di essere tagliate (ricompaiono allargando il pannello): lo
  segnala «+n» in fondo all'intestazione, con i loro nomi nella descrizione comandi.
- **Scegliere le colonne**: clic destro sull'intestazione dell'elenco (o clic su «+n»): una casella per colonna
  (Dimensione, Modificato, Permessi, Proprietario, Gruppo), mantenuta da una volta all'altra; una colonna selezionata
  senza spazio è indicata con «manca spazio». «Allarga il pannello per mostrarle tutte» dà al pannello la larghezza
  che manca (la sessione mantiene almeno 360 pixel). Deselezionare Dimensione o Modificato fa posto a Permessi e
  Proprietario in un pannello stretto.
- **Proprietario e Gruppo**: passando il mouse compare `proprietario:gruppo` (come per `chown`). Sono i nomi che il
  server invia con l'elenco dei file, come quelli di `ls -l`. In SFTP, se la riga che li contiene non ha la forma
  abituale (un nome con uno spazio…), ZillaTerm mostra invece i numeri (UID e GID, come `ls -n`); in FTP, le colonne
  restano vuote se il server non fornisce i nomi.
- **Pulsanti della barra**: Scarica, Modifica, Rinomina, Permessi ed Elimina sono disattivati, come nel menu, finché
  la selezione non è adatta; la loro descrizione comandi dice cosa scegliere («Seleziona un solo file (non una
  cartella).»…).
- **Inviare file**: trascinali da Esplora file sull'elenco (o il pulsante «Invia»). Invio in **SFTP** per
  impostazione predefinita (SCP a scelta nelle Impostazioni), cartelle comprese. Rilasciati sulla riga di una
  cartella, vanno in quella cartella: la riga viene evidenziata e la barra di stato indica la destinazione
  («Rilascia in server:/percorso»). Se degli elementi esistono già sul server (file nascosti compresi, anche se non
  mostrati), una conferma nomina il server e gli elementi, e propone «Sostituisci», «Salta gli esistenti» o
  «Annulla» (predefinito); un file sostituito viene riscritto sul posto e resta incompleto se l'invio viene
  annullato o non riesce. Se il server rifiuta quel protocollo per un file prima di riceverlo (regola del PSMP, SFTP in sola
  lettura…), l'altro subentra subito, senza domande né attese: la barra di stato e il riepilogo lo indicano con la
  risposta del server, e così la cronologia dei trasferimenti («SCP (SFTP rifiutato)»). In SCP, dopo un rifiuto all'annuncio di un
  file, i file grandi almeno altrettanto partono direttamente in SFTP fino alla chiusura della scheda.
  In una cartella inviata (o messa in un archivio .tar.gz), i collegamenti simbolici e le giunzioni verso cartelle non
  vengono seguiti, come al download: una giunzione può creare un ciclo o portare a una cartella vietata («Musica» dei
  vecchi profili Windows). Sono segnalati nel riepilogo («↷ n collegamento/i a una cartella non seguito/i») e in
  «Dettagli», con la loro destinazione; un collegamento a un file viene inviato con il contenuto di quel file.
- **Scaricare**: pulsante «Scarica» o clic destro. Un file chiede dove salvarlo; più file vanno in una cartella
  scelta, con una sola domanda («Sostituisci») per quelli già presenti. Un file locale viene sostituito solo a download completato: un
  download interrotto o annullato lo lascia com'era. Il file in corso arriva in una copia temporanea della stessa
  cartella (`nome.xxxxxxxx.part`, o `zillaterm.xxxxxxxx.part` per un nome di più di 241 caratteri: i nomi arrivano a
  255 caratteri, il limite di Windows). «Scarica» prende solo file: per una cartella, la barra di stato
  ricorda di trascinarla in Esplora file o sul desktop.
- **Scaricare trascinando**: trascina file o cartelle dall'elenco verso Esplora file o il desktop. Nulla viene
  scaricato durante il trascinamento: al rilascio, una finestra mostra l'avanzamento (Annulla lo interrompe), poi
  Esplora file copia i file dove li hai rilasciati. I nomi Unix vengono resi validi per Windows (`\`, `:`, `..`,
  `CON`… sostituiti), senza mai scrivere fuori dalla cartella di rilascio; la cartella temporanea del download viene
  poi eliminata.
- **Coda dei trasferimenti**: invii e download vengono eseguiti uno alla volta, nell'ordine delle richieste; ciò che
  chiedi durante un trasferimento si aggiunge alla coda invece di essere ignorato. Un invio va nella cartella
  mostrata al momento del rilascio (o nella cartella su cui i file sono stati rilasciati), e la conferma di
  sovrascrittura tiene conto anche degli invii ancora in attesa. Il pannello «Trasferimenti», sopra la barra di
  stato, mostra ogni elemento con il suo server (in attesa, avanzamento e file n/N, verifica, risultato): ✕ rimuove
  un elemento in attesa, «Annulla» interrompe quello in corso, «Annulla tutto» annulla tutto ciò che resta. I
  risultati restano visibili a trasferimenti finiti: «✓ completato · SHA-256 verificato (n/n)», «⚠ completato · non
  verificato: n su N», o l'errore in rosso; «Dettagli» su una riga terminata mostra la somma SHA-256 di ogni file, e
  «Cancella i completati» toglie dall'elenco i trasferimenti completati, non riusciti o annullati (la cronologia dei
  trasferimenti li conserva). Un
  trasferimento interrotto elimina il file in corso, incompleto (sul server per un invio, sul computer per un
  download); i file già trasferiti restano. Attenzione: se l'invio sostituiva un file esistente e aveva iniziato a
  scriverlo, il vecchio contenuto è perso; interrotto prima di qualsiasi contenuto, il file del server resta com'era. In SCP, l'interruzione riguarda solo quel trasferimento: gli elementi successivi proseguono
  sulla stessa connessione; un trasferimento che non avanza più (server che non legge più) si ferma 2 s dopo
  «Annulla» e i successivi partono su una nuova connessione. Un file inviato via SCP prende sul server la data dell'invio (come `scp` senza `-p`, e come in
  SFTP). Un errore viene mostrato in rosso nella coda e la coda prosegue; alla fine, un unico riepilogo nella barra
  di stato della scheda (al massimo tre righe, testo completo nella descrizione comandi). Navigazione,
  eliminazione, permessi, editor e trascinamento verso Esplora file passano tra due file. Chiudere la scheda con
  trasferimenti in corso chiede conferma («Annulla i trasferimenti e chiudi» o «Continua i trasferimenti»); alla
  disconnessione da CyberArk e all'uscita, compaiono nel riepilogo.
- **Molti file insieme: archivio .tar.gz**: da 200 file rilasciati (soglia nelle Impostazioni, opzione «Proporre un
  archivio .tar.gz»), ZillaTerm propone di inviarli in un unico archivio: un solo file da trasferire e verificare
  invece di migliaia, molto più veloce tramite il PSMP. L'archivio viene creato sul computer (nella coda,
  annullabile), inviato e verificato (SHA-256), poi eliminato dal computer; ogni elemento rilasciato è alla radice
  dell'archivio (permessi 0644 e 0755). Nulla viene eseguito sul server: un riquadro arancione compare in fondo alla
  scheda File con il comando di estrazione, per esempio `cd '/opt/app' && /usr/bin/gzip -dc './deploy.tar.gz' | tar
  xf - && rm -f './deploy.tar.gz'` (l'archivio viene eliminato dal server dopo l'estrazione). «Copia il comando»,
  oppure «Scrivi nel terminale», che lo digita al prompt della sessione senza eseguirlo: controllalo, poi premi
  Invio. Il riquadro resta visibile (per quella sessione) finché non lo chiudi. «Invia i file uno per uno» mantiene
  l'invio abituale; «Non proporre più» disattiva l'opzione.
  - **Tutti gli Unix** (Red Hat da 5 a 9, HP-UX 11.11 e 11.31, Solaris, AIX…): l'archivio è nel formato tar POSIX
    standard (ustar), letto da tutti i tar, e il comando usa `gzip` e `tar xf` separatamente, senza opzioni proprie
    di GNU tar. Si digita in qualsiasi shell (sh, ksh, bash, zsh, csh, tcsh).
  - **gzip** viene cercato sul server (tramite SFTP) dove ogni sistema lo installa: `/bin`, `/usr/bin`,
    `/usr/contrib/bin` (HP-UX), `/usr/local/bin`, `/opt/freeware/bin` (AIX), `/usr/sfw/bin` e `/opt/csw/bin`
    (Solaris). Non trovato: l'archivio viene inviato senza compressione (`.tar`), estratto dal solo `tar`.
  - Un nome oltre 100 caratteri (cartelle escluse) o un file oltre 8 GB non rientra in questo formato: i file
    vengono allora inviati uno per uno, con un messaggio.
- **Inviare a più server**: pulsante (freccia verso tre server) o clic destro → «Invia a più server…». Scegli i file
  o le cartelle, la cartella di destinazione (`~` = la cartella personale dell'account su ogni server, ad es.
  `~/deploy`) e le sessioni SSH destinatarie. ZillaTerm verifica prima su ogni server che la cartella esista e
  cosa verrebbe sostituito (una sola domanda per tutti), poi mette in coda un invio per server: stesso protocollo,
  stessa verifica SHA-256 su ogni server, un solo riepilogo alla fine.
- **Confrontare**: clic destro su un file → «Confronta con…»: lo stesso percorso (o un altro) su un server con una
  sessione SSH aperta, o un file di questo computer; con due file selezionati, «Confronta i 2 file». «Sfoglia…»
  accanto al percorso apre un esploratore dell'altro server, sulla stessa cartella con il file preselezionato (o
  sulla cartella superiore più vicina che esiste): doppio clic su una cartella per entrarvi, Backspace per risalire,
  si può anche digitare un percorso; il file scelto sostituisce il percorso. La scheda File di quel server resta
  sulla sua cartella. Lo stesso file sullo stesso server viene rifiutato; senza altri server aperti, viene proposto
  un file di questo computer. I file vengono
  letti **in memoria** (50 MB al massimo ciascuno), senza copia sul computer. La finestra mostra le righe
  affiancate: rimosse in rosso a sinistra, aggiunte in verde a destra. `F7` / `Maiusc+F7`: differenza successiva /
  precedente; «Ignora gli spazi»; «Solo le differenze»; «Salva il diff…» nel formato `diff -u`. Un file binario (o
  oltre 10 MB) viene confrontato per dimensione e checksum SHA-256. Con uno strumento di confronto scelto nelle
  Impostazioni (WinMerge, VS Code…), «Apri in …» gli passa due copie temporanee, eliminate alla chiusura della
  finestra.
- **Cronologia dei trasferimenti**: pulsante «Trasferimenti» della barra degli strumenti (frecce su e giù con un
  orologio, a sinistra di «Impostazioni»), disponibile anche senza sessione. Elenca gli ultimi 200 invii e download
  (trascinamento compreso): data, direzione, server, elemento, destinazione, numero di file, protocollo («SCP (SFTP
  rifiutato)» quando l'altro protocollo è subentrato), risultato, scritto come nella coda; errori e file diversi
  sono in rosso, e il testo di una colonna troppo stretta compare nella descrizione comandi. Filtro
  «Invii» / «Download» / «Errori e non verificati» (trasferimenti non riusciti, file diversi o non riletti), scelto da
  solo quando un errore segnalato da «!» non è ancora stato visto; «Checksum…» (o doppio clic) mostra per ogni file la dimensione, i checksum SHA-256 e il
  risultato, e li copia nel formato di `sha256sum -c` per riverificare sul server; «Apri la cartella» per un
  download; «Cancella la cronologia», separato dagli altri pulsanti, chiede conferma (i file stessi non vengono
  toccati).
- **Verifica dei trasferimenti (SHA-256)**: ogni file inviato o scaricato viene verificato. All'invio (SCP o SFTP),
  il file locale viene sottoposto a hash, poi il file arrivato sul server viene riletto via SFTP e sottoposto a
  hash. Al download, i dati ricevuti dal server vengono sottoposti a hash, poi il file scritto sul computer viene
  riletto. La barra di stato conferma «✓ identico su entrambi i lati»; i checksum di ogni file sono in «Dettagli»
  nella coda e nella cronologia dei trasferimenti (pulsante «Trasferimenti»). Se un file è diverso, l'errore viene mostrato e il
  dettaglio si apre da solo; un download per trascinamento fallisce invece di consegnare una copia errata. Un file
  che non può essere riletto (permessi) è segnalato «non verificato». La rilettura di un invio raddoppia il volume
  scambiato con il server.
- **Eliminare**: selezione poi Canc (o clic destro → «Elimina (rm)»), con una conferma che nomina il server e
  ricorda che sul server non c'è un cestino. Le cartelle devono essere vuote.
  Un collegamento simbolico viene eliminato esso stesso, mai il file o la cartella a cui punta.
- **Rinominare**: `F2`, clic destro → «Rinomina…» o il pulsante della barra. Un file non viene mai sovrascritto: un
  nome già usato viene rifiutato prima di qualsiasi invio al server (in SFTP come in FTP). «/», «.», «..» e i
  caratteri di controllo (a capo, tabulazione…) sono rifiutati, come per «Nuova cartella». Un collegamento simbolico
  viene rinominato esso stesso, mai il file o la cartella a cui punta.
- **Modificare un file**: **doppio clic** sul file (o `Invio`, `F4`, clic destro → «Modifica», il pulsante matita).
  Il file si apre nell'editor di testo scelto nelle Impostazioni (Blocco note per impostazione predefinita). Con il
  doppio clic, un archivio, un'immagine, un eseguibile o un documento d'ufficio viene scaricato invece di essere
  aperto, come ogni file i cui primi byte sono binari. A ogni salvataggio,
  ZillaTerm propone di rinviarlo al server («Rimanda» o «Non ora»): invio in SFTP, permessi del file conservati.
  Se il file è cambiato sul server dopo l'apertura, un avviso lo dice e chiede conferma («Sostituisci con la mia
  versione») prima di sovrascriverlo.
- **Seguire un file (tail -f)**: clic destro su uno o più file → «Segui (tail -f)». Una finestra mostra la fine del
  file, poi ogni nuova riga appena viene scritta, come `tail -f`, leggendo il file via SFTP ogni secondo: nessun
  comando viene eseguito sul server. Un file troncato o sostituito da una rotazione viene riletto dall'inizio;
  vengono conservate le ultime 10.000 righe.
  - **Colori e avvisi**: errori (ERROR, FATAL, CRITICAL…) in rosso, avvisi (WARN) in arancione; parole a scelta
    evidenziate in giallo («Evidenzia», separate da virgole). «Avviso se» (ad es. `ERROR, OutOfMemory, Connection
    refused`): ogni nuova riga che contiene una di queste parole è segnata, il contatore «⚠ n avvisi» aumenta (un
    clic va alla successiva) e il pulsante della finestra lampeggia nella barra delle applicazioni; può essere
    mostrata una notifica di Windows, al massimo una ogni 30 s, con il numero di righe e il nome del file soltanto,
    **mai il contenuto delle righe** (può apparire sulla schermata di blocco). Queste impostazioni sono conservate
    per le finestre successive.
  - **Vista combinata**: più file selezionati si aprono in un'unica finestra, e «Aggiungi a una finestra di
    monitoraggio» vi aggiunge un file di un'altra scheda, quindi di un altro server. Le righe si alternano
    nell'ordine di arrivo, con prefisso e colore del file (`[root@srv01 app.log]`); in basso, ogni file ha il suo
    stato e un pulsante per smettere di seguirlo.
  - **Filtro e ricerca**: filtro (come `grep`), esclusione (come `grep -v`), righe di contesto (come `grep -C 3`),
    in testo semplice o con espressioni regolari. Un campo di filtro o di esclusione compilato passa su sfondo ambra,
    con ✕ per svuotarlo, e la barra di stato indica «Filtro: n / N righe visualizzate». `Ctrl+F` cerca nelle righe
    senza filtrarle (Invio / `F3`: successivo, `Maiusc+F3`: precedente). Scorrere verso l'alto smette di seguire la
    fine.
  - **Interruzioni**: se la connessione si perde, un segno lo indica; quando la scheda SSH si riconnette (o con
    «Riconnetti»), il monitoraggio riprende da dove si era fermato, con le righe scritte nel frattempo. Chiudere la
    scheda smette di seguire i suoi file; la finestra conserva le righe ricevute.
  - **File memorizzati**: su un server della scheda «I miei server», i file seguiti sono memorizzati (gli ultimi
    12); alla connessione successiva, il pulsante di monitoraggio della scheda File li propone: «Seguili tutti in
    una finestra» con un clic, o uno solo.
  - **Tenere traccia**: «Salva…» scrive le righe visualizzate in un file di questo computer; «Registra in continuo…»
    scrive le righe già ricevute e poi ogni nuova riga appena arriva, finché la casella è selezionata; «Segno»
    inserisce una riga `—— 14:32:05 ——` per ritrovare un momento (prima di un intervento, ad esempio).
  - **Connessione**: per impostazione predefinita, il monitoraggio usa la connessione SFTP della scheda File e passa
    tra due file di un trasferimento. L'opzione «Segui i file (tail -f) in una sessione indipendente» delle
    Impostazioni gli dà una connessione propria, una per finestra e per server: non attende più i trasferimenti, ma
    è una sessione PSMP in più (registrata separatamente, e può essere richiesta una convalida MFA). Viene chiusa
    con la finestra. Una connessione persa non viene mai riaperta in ciclo.
- **Permessi**: clic destro → «Permessi…» (o il pulsante lucchetto). Caselle lettura / scrittura / esecuzione per
  proprietario, gruppo e altri, bit speciali (setuid, setgid, sticky) e valore ottale (`644`, `1777`…), per uno o
  più elementi. Quando gli elementi scelti non hanno tutti gli stessi permessi, una casella lasciata nello stato
  intermedio non cambia quel permesso su ogni elemento: vengono applicati solo i permessi modificati. Per una
  cartella, «Applica anche al contenuto» propaga i permessi a sottocartelle e file; per
  impostazione predefinita, l'esecuzione (x) viene data solo alle cartelle e ai file già eseguibili. Il pulsante
  diventa allora «Applica ricorsivamente…» e una conferma ricorda cosa succederà; durante la propagazione,
  «Interrompi» nella scheda File la ferma (gli elementi già trattati mantengono i nuovi permessi). I link
  simbolici non vengono seguiti e il proprietario non viene modificato. In FTP, un nome con un carattere di controllo
  (aggiungerebbe un comando per il server) viene lasciato com'è e segnalato come errore; gli altri elementi vengono
  trattati.
- **Operazione in corso**: durante un'eliminazione, una ridenominazione o una modifica dei permessi su un server, gli
  altri server restano utilizzabili; sullo stesso server, Elimina, `F2` e Permessi vengono rifiutati fino alla fine,
  e la barra di stato lo dice.
- **Filtrare**: il campo sotto il percorso mostra solo gli elementi della cartella il cui nome contiene il testo
  (`nginx`), o corrisponde a una maschera con `*` e `?` (`*.log`, `app?.conf`; più maschere separate da `;`:
  `*.log;*.gz`), senza distinguere maiuscole e minuscole. `..` resta per risalire; la barra di stato dice quanti
  elementi sono visualizzati. Si svuota quando cambi cartella; `Ctrl+F` nella scheda File, `Esc` o ✕ lo svuota,
  `Invio` o `↓` passa al primo elemento.
- Inoltre: nuova cartella, download, copia del percorso, visualizzazione dei file nascosti.
- **Segui la cartella del terminale**: se la casella è selezionata, ogni `cd` nel terminale sposta il browser nella
  stessa cartella (vedi [Funzionamento tecnico](#funzionamento-tecnico)). Dopo `sudo -i` o `su`, riseleziona la
  casella al prompt della shell per riattivare il monitoraggio nella nuova shell.

<img src="captures/it/transfer-history.png" alt="Cronologia dei trasferimenti con la verifica SHA-256 di ogni file" width="820">

## 6. Organizzare i server: scheda «I miei server»

![I miei server organizzati in cartelle](captures/it/my-servers.png)

- **Aggiungere** un account: clic destro in «Disponibili» → «Aggiungi a «I miei server»» e poi la cartella desiderata,
  oppure trascina l'account sulla scheda «I miei server», oppure il pulsante «Aggiungi» della barra degli strumenti.
  Per un account di dominio viene chiesto il server (facoltativo: senza server, verrà chiesto a ogni connessione).
  Viene poi chiesta la sua [etichetta](#etichette-prod-qa-dev), con una proposta in base al nome.
- **Aggiungere una sessione aperta**: clic destro sulla scheda della sessione (o nel suo terminale) → «Aggiungi a
  «I miei server»» e poi la cartella desiderata. Il server mantiene il tipo di connessione e la macchina di destinazione;
  la voce è disattivata se è già presente.
- **Aggiungere una connessione recente**: clic destro nelle sessioni recenti della home → «Aggiungi a «I miei server»»
  e poi la cartella desiderata. Il server mantiene il tipo di connessione (PSM, SSH o solo file), il componente PSM e la
  macchina di destinazione usati.
- **Cartelle**: clic destro → nuova cartella o sottocartella, rinomina, elimina; trascina server e cartelle per
  spostarli. Eliminare una cartella conta ed elimina tutti i suoi server di questo PVWA, anche quelli nascosti dalla
  ricerca; quelli di un altro PVWA restano. Rimuovere un server o eliminare una cartella chiede conferma (gli
  account restano in «Disponibili»). I pulsanti «Proprietà / rinomina» e «Rimuovi il server o elimina la cartella»,
  in cima alla scheda, sono disattivati finché non è selezionato nulla.
- **Cercare**: campo in cima alla scheda (o `Ctrl+F` nella scheda). Filtra i server per nome, server, utente,
  cartella, componente, macchina di destinazione, e le voci dei database KeePass sbloccati; le cartelle dei
  risultati vengono espanse. `Invio` o `↓` seleziona il primo risultato, `Esc` o la ✕ in fondo al campo cancella.
  Con migliaia di server, l'elenco viene filtrato appena smetti di scrivere.
- **Più server alla volta**: `Ctrl+clic` aggiunge o toglie un server (o tutti quelli di una cartella), `Maiusc+clic`
  sceglie una serie di server; `Esc` o un clic semplice annulla. Clic destro su uno di essi → «Apri i N server nella
  vista parallela» o «Connettiti ai N server» (una scheda ciascuno). Clic destro su una cartella → «Apri nella vista
  parallela» o «Connettiti ai N server».
- **Etichetta** (PROD, QA, DEV…): clic destro → «Etichetta ▸», vedi [più sotto](#etichette-prod-qa-dev).
- **Configurazione propria di ogni server** (clic destro → «Proprietà…»):

| Impostazione | Effetto |
| --- | --- |
| Nome, cartella | Visualizzazione e posizione nell'albero. |
| Etichetta | Ambiente del server (PROD, QA, DEV…): il suo colore contrassegna l'albero, la scheda e il contorno della sessione. |
| PSM, SSH tramite PSMP o solo file (SFTP tramite PSMP) | Tipo di connessione aperto con il doppio clic (all'inizio, in base alla piattaforma). |
| Componente PSM | Componente da usare (vuoto: dedotto dalla piattaforma). |
| Macchina di destinazione | Server su cui aprire la sessione per un account di dominio. |
| Motivo predefinito | Motivo di accesso inviato automaticamente al PVWA. |
| Cartella SFTP iniziale | Il terminale **e** il browser dei file si aprono direttamente in questa cartella. |
| Inoltro X11 | Le applicazioni grafiche del server si aprono sul server X del computer (solo SSH, vedi [Applicazioni grafiche (X11)](#applicazioni-grafiche-x11)); scelta personale, mai esportata né condivisa. |

<img src="captures/it/server-properties.png" alt="Proprietà di un server in «I miei server»" width="540">

Senza PSMP nelle Impostazioni, i tipi SSH e solo file sono disattivati, come altrove; un valore errato
viene segnalato nella finestra. Un server il cui account non è trovato in CyberArk (eliminato o diritti revocati)
porta la dicitura «⚠ non trovato in CyberArk», letta anche dalle utilità per la lettura dello schermo; nulla viene
segnalato mentre gli account si caricano.

### Etichette (PROD, QA, DEV…)

Ogni server di «I miei server» può avere un'etichetta di ambiente, con un colore: PROD in rosso, QA in blu, DEV
in verde all'inizio. Ricorda a colpo d'occhio dove si lavora:

- un'etichetta colorata davanti al nome nell'albero «I miei server» e negli elenchi condivisi;
- un'etichetta colorata sulla scheda della sessione (e `[PROD]` all'inizio del titolo di una finestra staccata);
- una cornice del suo colore attorno al terminale SSH o alla sessione di file (il nome resta sulla scheda, senza
  togliere spazio al terminale); un'etichetta colorata nell'intestazione di ogni riquadro della vista parallela e in
  quella della scheda File: si vede su quale ambiente si deposita un file.

Una sessione aperta da «Disponibili» o dalle sessioni recenti prende l'etichetta del server di «I miei server» che ha
lo stesso account (e la stessa macchina di destinazione). L'etichetta si applica all'apertura: dopo averla cambiata,
riaprire la sessione per vedere il nuovo colore.

- **All'aggiunta** di un server in «I miei server», viene chiesta l'etichetta. Un'etichetta viene proposta in base al
  nome del server, al suo indirizzo, al suo safe e poi alle sue cartelle («prd», «prod», «production», «produzione» →
  PROD; «rec», «recette», «qual», «uat», «preprod», «staging», «test», «collaudo» → QA; «dev», «sviluppo» → DEV;
  un'etichetta aggiunta nelle Impostazioni si riconosce dal suo nome). Non viene proposto nulla quando il testo nomina
  due ambienti. Verificatela: è solo una proposta. «Nessuna etichetta» è possibile; «Annulla» non aggiunge il server.
- **Cambiare**: clic destro su un server o una selezione (`Ctrl+clic`) → «Etichetta ▸», oppure su una cartella →
  «Etichetta i server della cartella ▸», poi l'etichetta, o «Nessuna». «Proponi in base al nome» etichetta in un colpo
  solo i server che non ne hanno ancora una (quelli senza proposta restano senza etichetta). Anche in «Proprietà…».
- **Elenco delle etichette**: Impostazioni › Generale › «Etichette dei server» (nome di 16 caratteri al massimo,
  colore). Rinominare un'etichetta la rinomina sui server; eliminarla la toglie dai server.
- L'etichetta segue il server nell'esportazione di «I miei server» e negli elenchi condivisi; un'etichetta assente
  dalle proprie Impostazioni appare in grigio. L'elenco delle etichette (nomi e colori) si diffonde al team con il
  [file di ambiente](#ambiente-condiviso).

### Esportare, importare, condividere

Due menu in alto nella scheda, «Importa ▾» e «Condividi ▾», a sinistra dei pulsanti «Nuova cartella», «Proprietà /
rinomina» e «Rimuovi il server o elimina la cartella» (`Tab` raggiunge questi pulsanti, le frecce passano dall'uno
all'altro):

- **Condividi › Esporta «I miei server» in un file…** salva «I miei server» in un file `.json`: cartelle (anche vuote), nome, account CyberArk (ID), etichetta, tipo di
  connessione, componente, macchina di destinazione, motivo predefinito, cartella SFTP iniziale. Nessuna password né
  file seguito. Utile per cambiare computer o passare il proprio elenco.
- **Importa › File ZillaTerm (esportazione di «I miei server»)…** legge un file esportato (o un elenco condiviso) e
  riassume prima di aggiungere: server aggiunti, server
  già presenti (stesso account, tipo, componente, macchina di destinazione e cartella: ignorati), cartelle create,
  server aperti su una macchina di destinazione (da verificare: la macchina viene dal file). Niente viene rimosso o
  modificato in «I miei server». Un file creato per un altro PVWA viene rifiutato: i suoi ID di account vi indicano
  altri account.
- **Importa › Sessioni di un altro programma…**: vedi [sotto](#riprendere-le-sessioni-di-un-altro-programma).
- **Importa › Aggiungi un database KeePass…**: vedi [7.](#7-accesso-di-emergenza-fuori-da-cyberark-database-keepass)
- **Elenchi condivisi**: un elenco di server in un file su una condivisione di rete, che tutto il team apre e
  completa.
  - «Condividi › Crea un elenco condiviso…»: scegli la posizione (condivisione di rete) e il nome mostrato a tutti;
    «Importa › Apri un elenco condiviso…»: aggiungi un elenco creato da un collega. Gli elenchi aperti compaiono in cima alla scheda
    (dopo i database KeePass), con le loro cartelle; anche la ricerca li filtra.
  - **Aggiungere**: clic destro su un server o una cartella di «I miei server» → «Condividi in un elenco» (la
    cartella viene mantenuta), o trascina un server, una cartella o un account di «Disponibili» sull'elenco o su una
    sua cartella (conferma). Il motivo predefinito resta personale: non viene mai condiviso.
  - **Rimuovere**: clic destro → «Rimuovi dall'elenco condiviso…» (o `Canc`), dopo conferma.
  - **Usare**: doppio clic per connettersi; clic destro per le opzioni avanzate, la password, i membri del safe o
    «Copia in «I miei server»». Ognuno si connette con i propri diritti CyberArk: un account che non vedi nel vault
    CyberArk porta la dicitura «⚠ non trovato in CyberArk». La descrizione comandi mostra l'account come lo descrive CyberArk, la macchina di destinazione,
    chi ha aggiunto il server e quando.
  - **Macchina di destinazione**: un server condiviso che apre un account di dominio su una macchina non presente tra
    le macchine consentite dell'account in CyberArk chiede conferma alla prima connessione (chiunque abbia diritto di
    scrittura sulla condivisione può modificare l'elenco). «Copia in «I miei server»» elenca questi server e chiede
    conferma prima di copiarli.
  - **Elenco di un altro PVWA**: un elenco creato per un altro vault CyberArk viene mostrato, ma i suoi server non si aprono e
    non si copiano, e non vi si può aggiungere nulla: accedi a quel PVWA per usarlo.
  - **Cronologia**: clic destro → «Cronologia delle modifiche…». La scheda «Modifiche» elenca chi ha aggiunto,
    rimosso o ripristinato cosa, e quando; la scheda «Versioni» conserva una copia dell'elenco a ogni revisione (le
    ultime 100, nella cartella `nome.versions` accanto al file). In questa scheda, «Ripristina questa versione…»
    riporta l'elenco in quello stato per tutti, dopo conferma; il ripristino viene a sua volta registrato, quindi può
    essere annullato.
  - Le modifiche di ognuno si sommano: il file viene riletto e modificato in esclusiva (un computer che scrive nello
    stesso momento attende il proprio turno), e l'elenco mostrato si aggiorna quando un collega lo modifica (`F5` lo
    rilegge anche). «Chiudi l'elenco» lo toglie dalla tua scheda senza toccare il file.
  - Diritti: quelli della condivisione di rete. In sola lettura, l'elenco resta utilizzabile ma non modificabile.

### Riprendere le sessioni di un altro programma

Per passare a ZillaTerm senza ridigitare i server, e smettere di connettersi a loro direttamente: menu «Importa ▾» ›
«Sessioni di un altro programma…» in alto nella scheda, o menu **Impostazioni** › «Importa le sessioni di un altro
programma…». Gli account del PVWA devono essere caricati.

<img src="captures/it/session-import.png" alt="Importazione delle sessioni di un altro programma: anteprima prima dell'importazione" width="820">

1. **Origine**: scegli il programma, poi «Leggi…».

   | Origine | Letta da |
   | --- | --- |
   | PuTTY, KiTTY, WinSCP | il tuo registro di Windows, direttamente |
   | Esportazione del registro (`.reg`) | le sessioni PuTTY e KiTTY e i siti WinSCP che contiene (esportata da un altro computer, per esempio) |
   | KiTTY portable | la cartella di KiTTY (sottocartella `Sessions`, un file per sessione) |
   | WinSCP | il file `WinSCP.ini` (versione portable) |
   | File `.mxtsessions` o `.ini` | le sue sezioni di sessioni, con le loro cartelle |
   | mRemoteNG | `confCons.xml`; un file interamente cifrato viene rifiutato: esporta le connessioni senza quella cifratura |
   | Remote Desktop Connection Manager | il file `.rdg` (gruppi, credenziali ereditate o profili di credenziali) |
   | SecureCRT | la cartella di configurazione (`Config\Sessions`) o l'esportazione XML delle impostazioni |
   | OpenSSH | il file `config` (`%USERPROFILE%\.ssh\config`): ogni `Host` senza caratteri jolly, impostazioni prese come fa `ssh` |
   | File Desktop remoto | una cartella di file `.rdp` e le sue sottocartelle |

2. **Abbinamento**: ogni sessione viene associata a un account del PVWA.
   - Account del server stesso: stesso nome, o nome breve e nome completo (`srv01` e `srv01.corp.local`); con lo stesso
     utente se indicato. Nessuna risoluzione DNS: contano solo i nomi.
   - Di un tipo adatto: Windows per il Desktop remoto, Unix o rete per SSH; un altro tipo solo se non ce ne sono; mai
     un account di database.
   - Altrimenti, un account di dominio dello stesso utente (`CORP\admin`, `admin@corp.local`) autorizzato su quel
     server: il server diventa la sua macchina di destinazione. `CORP\admin` non indica mai l'account locale `admin`
     del server.
   - Una sessione che passava già dal PSMP (`vault@destinazione@server@psmp`) o da PSM (programma di avvio
     `psm /u account /a server /c componente` di un file `.rdp`) viene decodificata: contano l'account e il server di
     destinazione, e il componente PSM viene mantenuto.
   - Più account possibili: viene proposto il più probabile («da verificare»), ma la sessione non è selezionata;
     scegliere un account nell'elenco della colonna «Account PVWA» la seleziona (oppure selezionala per tenere quello
     proposto). Il pulsante «Importa (n)» indica quante sessioni verranno aggiunte.
3. **Connessione salvata**, mai diretta: Desktop remoto tramite PSM; SSH e file (SFTP, SCP) tramite il PSMP se ce n'è
   uno per quel server (solo file per una piattaforma «SFTP»), altrimenti tramite PSM (`PSM-WinSCP` per i file);
   Telnet tramite `PSM-Telnet`.
4. **Cartella in «I miei server»**: le cartelle dell'altro programma vengono ricreate sotto questa cartella («Importati»
   per impostazione predefinita; vuoto: nella radice), e ogni server mantiene il suo nome.
   **Etichetta**: la colonna «Etichetta» propone quella di ogni sessione in base al nome, al server, al safe
   dell'account associato e poi alle cartelle nell'altro programma («Prod/Linux» → PROD, «Recette» → QA), come
   [all'aggiunta di un server](#etichette-prod-qa-dev); vuota quando non si riconosce nulla. Cambiarla se necessario
   prima di importare; compare anche nel risultato esportato.
5. **Importa** aggiunge le sessioni selezionate e pronte. La tabella mostra poi il risultato di ogni server:
   «Importato», o «Non importato» con il motivo (nessun account nel PVWA, tipo di connessione non supportato come VNC,
   FTP o porta seriale, deselezionato). Un server già presente nella stessa cartella con lo stesso account non viene
   aggiunto due volte. «Solo i problemi» filtra la tabella.
6. **Esporta il risultato…** salva questa tabella in CSV (separatore delle impostazioni internazionali di Windows): è
   l'elenco dei server senza account, da far aggiungere in CyberArk.
7. **File degli account mancanti…** salva gli account da creare per i server rimasti senza account, pronti per
   «Importa account (CSV)» ([sezione 2](#2-trovare-un-account-scheda-disponibili)): una riga per server e utente per
   un account locale (piattaforma `WinServerLocal` per il desktop remoto, `UnixSSH` per SSH e SFTP), una riga per
   utente di dominio (piattaforma `WinDomain`, i server nelle macchine autorizzate). Completa il safe, verifica le
   piattaforme (nomi del tuo Vault), poi importalo. Nessuna password nel file.

Nessuna password viene letta, né nel registro né nei file: solo il server, la porta, il protocollo, l'utente e la
cartella. Le password conservate dall'altro programma restano dove sono: a migrazione terminata, eliminale da quello
strumento per non aggirare più CyberArk.

## 7. Accesso di emergenza fuori da CyberArk: database KeePass

Quando CyberArk non è disponibile, ZillaTerm apre i tuoi database KeePass (`.kdbx`) e si connette **direttamente**
ai server, in SSH, in desktop remoto o in VNC, o ai soli loro file (SFTP, FTP, FTPS), con gli account che
contengono.

> Queste connessioni **non passano dal PSM**: nessuna registrazione, nessuna regola CyberArk. Ogni apertura di
> database, connessione e modifica è annotata nel registro locale `%APPDATA%\ZillaTerm\urgence.log`.

![Accesso di emergenza: database KeePass sbloccato in «I miei server»](captures/it/keepass-vault.png)

- **Senza CyberArk**: nella schermata di accesso, «Accesso di emergenza (KeePass)» apre la finestra principale senza
  PVWA (sono mostrati solo i database KeePass; la scheda «Disponibili» e i pulsanti propri di CyberArk sono
  nascosti; «Esci dall'accesso di emergenza» blocca i database e torna alla schermata di accesso). Con CyberArk, i database compaiono anche in cima a «I miei server». La descrizione comandi di una
  scheda di sessione aperta da un database lo ricorda: «Accesso diretto di emergenza (KeePass): fuori da CyberArk,
  annotato in urgence.log».
- **Aggiungere un database**: menu «Importa ▾» › «Aggiungi un database KeePass…» della scheda «I miei server»
  (pulsante cassaforte in accesso di emergenza), o clic destro nella scheda → «Aggiungi un database KeePass…». La finestra «Aggiungi un database KeePass» ricorda in una fascia che queste connessioni sono
  fuori da CyberArk; «Sfoglia…» sceglie il file `.kdbx`, poi il nome e un eventuale file chiave.
- **Sbloccare**: doppio clic sul database. Password principale e/o file chiave (tutti i formati di KeePass).
  «Memorizza la password principale nel vault locale» evita di ridigitarla (vedi sotto).
- **Connettersi**: doppio clic su una voce. Il protocollo viene dal suo indirizzo (`ssh://server:22`,
  `rdp://server`, `vnc://server`, `sftp://`, `ftp://`, `ftpes://`, `ftps://`, o `server:3389`), da un campo
  «Protocol» / «Port» o da un'etichetta (`ssh`, `rdp`, `vnc`, `sftp`, `ftp`, `ftpes`, `ftps`); altrimenti
  ZillaTerm chiede il protocollo. La password della voce è usata direttamente; non è mai mostrata né scritta su
  disco.
  - **SSH**: schede terminale + File. Alla prima connessione, l'impronta della chiave del server va confrontata con
    quella fornita dal suo amministratore, nella stessa finestra usata per il PSMP (vedi
    [sezione 4](#4-aprire-una-sessione-ssh-tramite-il-psmp)).
  - **Desktop remoto**: la scheda ne segue la dimensione (risoluzione del desktop remoto) e propone «Schermo intero»
    (`Ctrl+Alt+Pausa` per tornare), «Disconnetti» e «Riconnetti». Prima di ogni connessione, ZillaTerm legge il
    certificato del server. Se Windows non lo approva (autofirmato…), il soggetto, l'emittente, le date e l'impronta
    SHA-256 sono mostrati alla prima connessione, poi è memorizzato per quel server; se in seguito cambia, vengono
    mostrate l'impronta memorizzata e quella nuova, e occorre selezionare «Ho confermato la modifica con
    l'amministratore del server». La password è letta e passata al controllo Desktop remoto solo dopo questa
    verifica; un server che non accetta la cifratura TLS è rifiutato. Nessuna immagine della sessione resta sul disco
    del computer.
  - **VNC** (`vnc://server`, porta 5900; `vnc://server:1` indica lo schermo 1, porta 5901): desktop in una scheda,
    adattato alla finestra o a dimensione reale («Adatta»), pulsanti «Ctrl+Alt+Canc» (dopo conferma: a seconda della
    macchina, apre la schermata di sicurezza o riavvia alcune console di macchine virtuali), «Invia gli appunti» e
    «Copia il testo remoto»: gli appunti sono scambiati solo tramite questi pulsanti. Autenticazione con password VNC (8
    caratteri al massimo, limite del protocollo) o senza autenticazione. **VNC non cifra nulla**: un banner lo
    ricorda; riservalo a una rete fidata. Prima del primo invio della password VNC, ZillaTerm chiede il tuo consenso
    («Invia la password e connetti», una sola volta per scheda, riconnessioni comprese): VNC non verifica l'identità
    del server, e un falso server o chi intercetta la connessione può recuperare i primi 8 caratteri della password.
    I segnali acustici del server sono limitati a uno al secondo.
  - **File** (`sftp://`, `ftp://`, `ftpes://` per FTP con TLS esplicito, `ftps://` per TLS implicito, porta 990):
    una scheda di stato, senza terminale, e i file nella scheda «File» con le stesse funzioni (trasferimenti
    verificati con SHA-256, coda, cronologia, editor, confronto, monitoraggio in tempo reale, permessi se il server
    accetta `SITE CHMOD`). Clic destro → «Apri i file (SFTP, FTP)» fa lo stesso per una voce SSH, in SFTP. Con
    `ftp://`, la cifratura TLS è tentata per prima; se il server non la propone, ZillaTerm chiede prima di
    connettersi in chiaro («Connetti senza cifratura», una volta per sessione) e un banner lo ricorda. `ftpes://` e
    `ftps://` non passano mai in chiaro. Un certificato FTPS che Windows non approva (autofirmato…) è mostrato con il
    soggetto, l'emittente, le date di validità e l'impronta SHA-256 (con «Copia»), poi memorizzato per quel server
    se lo accetti; se in seguito cambia, vengono mostrate l'impronta memorizzata e quella nuova, e occorre
    selezionare «Ho confermato la modifica con l'amministratore del server». Un server che ha già cifrato una
    connessione viene memorizzato: se poi si presenta senza TLS (cosa che un intermediario può ottenere), non è più la
    semplice domanda ma l'avviso «Questo server FTP non propone più la cifratura», che lascia proseguire solo dopo
    aver selezionato «Ho confermato con l'amministratore del server la rimozione della cifratura»; altrimenti la
    connessione si ferma senza inviare la password.
- **Modificare il database**: clic destro → «Nuova voce…», «Modifica…» (`F2`), «Elimina» (`Canc`, nel cestino
  del database, dopo conferma). L'indirizzo del server è obbligatorio: una voce senza indirizzo (né nel campo
  Indirizzo, né nei suoi campi personalizzati) non viene salvata. Il resto del database (allegati, campi,
  impostazioni) è conservato; la versione precedente di una voce va nella sua cronologia, come in KeePass. Durante
  il salvataggio, la versione precedente del file è conservata in `database.kdbx.bak`, poi eliminata appena il nuovo
  file viene riletto identico: nessuna vecchia copia, che si aprirebbe ancora con una vecchia password principale,
  resta accanto al database (condiviso o no).
- **Bloccare**: clic destro → «Blocca». I database si bloccano anche alla disconnessione da CyberArk, alla chiusura e al
  **blocco di Windows**.

**Vault locale**: le password principali che scegli di memorizzare sono conservate in
`%APPDATA%\ZillaTerm\coffre-local.dat`, cifrato con una tua password (almeno 8 caratteri) e legato al tuo account
Windows. Questa password viene chiesta quando sblocchi un database KeePass la cui password è memorizzata; «Più tardi»
(proposto solo in quel momento) permette di digitare invece la password del database. Se il vault locale non è
aperto, il database si apre comunque, e la barra di stato segnala che la sua password principale non è stata
memorizzata. Gestione nelle **Impostazioni**, pagina Sicurezza: «Crea…», «Sblocca…», «Cambia password…», «Elimina
ora…»; queste azioni si applicano subito, senza «Salva». Si blocca alla disconnessione da CyberArk (così «Accesso di emergenza»
non riapre mai i database memorizzati senza password), alla chiusura e al blocco di Windows.

## 8. Intervento: registrazione e promemoria di reperibilità

Il pulsante **«Intervento»** della barra degli strumenti apre una finestra utile a qualsiasi intervento, in
reperibilità o no:

- **Il promemoria di reperibilità**: buone pratiche, numeri di emergenza, contatti… Proviene:
  - dal testo di **Impostazioni › Generale › Promemoria di reperibilità**, diffuso a tutto il team con il
    [file di ambiente](#ambiente-condiviso);
  - oppure da un **file di testo** indicato in **Impostazioni › Generale › File del promemoria** (di solito su una
    condivisione di rete; anch'esso diffondibile con il file di ambiente). Viene riletto a ogni apertura della
    finestra: i responsabili lo aggiornano senza ridistribuire l'ambiente. Il suo percorso appare sotto il titolo; se
    non è leggibile (assente, accesso negato, condivisione che non risponde entro 5 secondi), viene mostrato il testo
    delle Impostazioni, con il motivo. UTF-8 o UTF-16 (il Blocco note va bene); vengono letti solo i primi 64 KB.

  Viene mostrato come testo semplice (selezionabile per copiare un numero): nessun link né formattazione viene
  interpretato, e i caratteri invisibili di un file (che potrebbero invertire l'ordine delle cifre di un numero)
  vengono rimossi.
- **«Registra l'intervento»**: da quel momento ZillaTerm conserva tutto ciò che accade, fino a «Interrompi la
  registrazione» o alla chiusura di ZillaTerm. La registrazione continua dopo la disconnessione da CyberArk e in
  accesso di emergenza. Un pallino rosso sul pulsante e «● Intervento registrato» nella barra di stato lo ricordano.
  Vengono conservati:
  - il **testo dei terminali SSH** (comandi digitati e il loro risultato, riga per riga, con l'ora; non la schermata
    di vim, less o top, né il comando che ZillaTerm digita da sé per seguire la cartella);
  - le **connessioni**: apertura, errore e chiusura delle sessioni SSH, file, desktop remoto e VNC, e le sessioni
    PSM con componente, macchina di destinazione, motivo e ticket;
  - le **azioni**: password copiata (mai la password), richieste al CPM, account aggiunti, modificati, eliminati o
    importati, finestre X11, passaggio all'accesso di emergenza, disconnessione;
  - i **trasferimenti di file**, con lo SHA-256 di ogni file;
  - le vostre **note** (campo «Nota per il registro»: numero di incidente, decisione presa…);
  - le **catture**: «Cattura un desktop remoto…» acquisisce l'immagine di una finestra Connessione Desktop remoto
    (sessioni PSM; una finestra ridotta a icona non può essere catturata), e il pulsante «Cattura» delle schede
    desktop remoto di ZillaTerm acquisisce il desktop visualizzato.
- **«Interventi registrati»**: l'elenco delle registrazioni, dalla più recente. All'interruzione l'intervento resta
  selezionato: **«Esporta il rapporto…»** (o doppio clic) ne fa una pagina HTML: riepilogo, cronologia, blocchi di
  testo dei terminali e catture, da allegare al resoconto dell'intervento. **«Elimina…»** cancella una registrazione
  terminata.

Il registro è **cifrato solo per il vostro account Windows** (DPAPI) in `%LOCALAPPDATA%\ZillaTerm\Interventions`, blocco
per blocco durante la registrazione (un arresto improvviso perde solo gli ultimi due secondi, e il rapporto lo
segnala). Un blocco rimosso, spostato o modificato viene segnalato. ⚠ Le password digitate senza eco non ci sono, ma
**tutto ciò che mostra un terminale sì** (il contenuto di un file di configurazione, per esempio); il rapporto
esportato **non è cifrato**: conservatelo in un luogo sicuro. Il testo dei terminali è limitato a 64 milioni di
caratteri per intervento (gli altri eventi continuano a essere conservati oltre).

## Scorciatoie

| Dove | Azione | Scorciatoia |
| --- | --- | --- |
| Ovunque (tranne la scheda File) | Ricaricare gli account dal PVWA | `F5` |
| Ovunque | Filtrare gli account (in «I miei server»: cercare un server; in «File»: filtrare la cartella) | `Ctrl+F` |
| Ovunque | Scheda di sessione successiva / precedente | `Ctrl+Tab` / `Ctrl+Maiusc+Tab` |
| Ovunque | Chiudere la scheda di sessione | `Ctrl+F4` o `Ctrl+Maiusc+W` |
| Ovunque | Schede «Disponibili», «I miei server», «File» del pannello laterale | `Ctrl+1`, `Ctrl+2`, `Ctrl+3` |
| Ovunque | Impostazioni | `Ctrl+,` |
| Fuori dal terminale | Connessione rapida (scheda Home) | `Ctrl+K` |
| Fuori dal terminale | Passare dal pannello laterale alla sessione e ritorno | `F6` |
| Fuori dal terminale | Chiudere / riaprire il pannello laterale | `Ctrl+B` (o doppio clic sul separatore) |
| Elenchi e alberi | Aprire la sessione | Doppio clic o `Invio` |
| Elenchi, alberi, schede | Menu del clic destro | Tasto Menu o `Maiusc+F10` |
| Ricerca | Cancellare il filtro | `Esc` o ✕ in fondo al campo |
| Home | Togliere una sessione recente dall'elenco | `Canc` |
| I miei server | Rinominare / rimuovere o eliminare | `F2` / `Canc` |
| I miei server | Scegliere più server (poi clic destro per aprirli insieme) | `Ctrl+clic`, `Maiusc+clic`; `Esc` annulla |
| Terminale | Copiare | Selezione con il mouse, o `Ctrl+Maiusc+C` |
| Terminale | Incollare | `Maiusc+Ins` o `Ctrl+Maiusc+V` (clic destro con l'opzione delle Impostazioni) |
| Terminale | Menu: copia, incolla, seleziona tutto, cerca, salva, cancella la cronologia, carattere, azioni della scheda | Clic destro o tasto Menu (Maiusc+clic destro con l'opzione di incolla) |
| Terminale | Cronologia | Rotellina, barra di scorrimento, `Maiusc+Pag su` / `Maiusc+Pag giù`; «↓ Torna alla fine» |
| Terminale | Cercare (cronologia compresa) | `Ctrl+Maiusc+F`, poi `Invio` / `Maiusc+Invio` |
| Terminale | Salvare il contenuto in un file | `Ctrl+Maiusc+S` |
| Terminale | Dimensione del carattere / predefinita | `Ctrl+rotellina` / `Ctrl+0` |
| Confronto | Differenza successiva / precedente | `F7` / `Maiusc+F7` |
| Scheda SSH o Desktop remoto | Chiudere | Croce della scheda o clic centrale |
| Scheda SSH o Desktop remoto | Riconnettere, duplicare (altra sessione sullo stesso account o sulla stessa voce), staccare (SSH), chiudere, chiudere le altre schede | Clic destro sulla scheda |
| Scheda SSH | Staccare in una finestra separata (altro schermo) | Trascinare la scheda fuori dalla finestra |
| Scheda SSH | Aggiungere alla vista parallela, o toglierla | Clic destro sulla scheda |
| Desktop remoto | Schermo intero / ritorno | `Ctrl+Alt+Pausa` |
| File | Aprire la cartella o modificare il file / modificare / rinominare / cartella superiore / eliminare / aggiornare | Doppio clic o `Invio` / `F4` / `F2` / `Backspace` / `Canc` / `F5` |
| File | Ordinare per una colonna, poi invertire | Clic sulla sua intestazione |
| Database KeePass | Connettere / modificare / eliminare una voce | Doppio clic o `Invio` / `F2` / `Canc` |

In un terminale, `Ctrl+K`, `Ctrl+B` e `F6` vengono inviati al server (`F6` alle applicazioni come mc); `Ctrl+Tab`,
`Ctrl+F4`, `Ctrl+Maiusc+W` e `Ctrl+1/2/3` restano a ZillaTerm.

**Tastiera e accessibilità**: la barra degli strumenti si raggiunge con `Tab` (il focus è visibile), ogni menu e
ogni finestra ha i suoi tasti di scelta (`Alt` + lettera sottolineata, senza doppioni, in italiano, francese e
inglese), e il menu di un elemento di «I miei server» si apre nello stesso punto con il clic destro, `Maiusc+F10` o
il tasto Menu. I campi password (accesso, database KeePass, vault locale) avvisano quando Bloc Maiusc è attivo. Le
utilità per la lettura dello schermo annunciano il nome degli elementi di elenchi e alberi, dei pulsanti con icona
o simbolo (✕, ↑, ↓), dei campi (percorso, numero del ticket…), i messaggi della barra di stato e gli errori di
connessione. Le barre degli strumenti di «I miei server» e della scheda File sono un solo passo di `Tab` (le frecce
passano da un pulsante all'altro). Nelle Impostazioni, ogni casella ha il suo tasto di scelta, senza doppioni nella
pagina mostrata. In contrasto elevato, l'interfaccia usa i colori
di sistema di Windows e ne segue i cambiamenti.

**Tema scuro**: Impostazioni → Generale → Aspetto → Tema: «Come Windows» (predefinito: la modalità delle app scelta in
Windows, Personalizzazione → Colori, seguita anche quando cambia), «Chiaro» o «Scuro». La modifica si applica subito,
alle finestre aperte e alla barra del titolo; il contrasto elevato di Windows ha sempre la precedenza. I colori dei
terminali SSH si impostano a parte (pagina Terminale).

## Impostazioni e file di configurazione

<img src="captures/it/settings.png" alt="Impostazioni" width="480">

Pulsante Impostazioni della barra degli strumenti → «Impostazioni…» (o `Ctrl+,`). La finestra, ridimensionabile, è
organizzata in pagine: Generale, CyberArk, Terminale, File, Sicurezza. «Salva» applica le impostazioni; un valore
errato mostra la pagina del campo interessato, con il cursore nel campo. Le opzioni con un effetto collaterale lo
dicono sotto la loro casella («⚠ Effetto: …»).

| Pagina | Impostazione | Ruolo | Predefinito |
| --- | --- | --- | --- |
| Generale | Tema | Come Windows, Chiaro o Scuro; applicato subito (il contrasto elevato di Windows ha la precedenza) | come Windows |
| Generale | Promemoria di reperibilità | Buone pratiche e numeri di emergenza mostrati nella finestra «Intervento»; testo semplice, diffuso dal file di ambiente (vedi [Intervento](#8-intervento-registrazione-e-promemoria-di-reperibilità)) | vuoto |
| Generale | File del promemoria | File di testo (di solito su una condivisione di rete) riletto a ogni apertura della finestra «Intervento», mostrato al posto del promemoria qui sopra; diffuso dal file di ambiente | vuoto |
| Generale | Etichette dei server | Etichette assegnate ai server di «I miei server» (nome, colore): «Aggiungi un'etichetta», «Elimina l'etichetta», celle modificabili; la ridenominazione si applica in «I miei server», l'eliminazione toglie l'etichetta dai server; diffuse dal file di ambiente (vedi [Etichette](#etichette-prod-qa-dev)) | PROD (rosso), QA (blu), DEV (verde) |
| Generale | Lingua dell'interfaccia | Français, English, Italiano o lingua del sistema; applicata dopo la disconnessione da CyberArk o al prossimo avvio | lingua di Windows (inglese se non è tradotta) |
| Generale | File centrale | File di ambiente del team su una condivisione di rete, riletto a ogni avvio; le sue modifiche vengono mostrate prima di essere applicate (vedi [Ambiente condiviso](#ambiente-condiviso)) | vuoto |
| Generale | Cercare una nuova versione all'avvio | Una richiesta a GitHub al massimo una volta al giorno; un link nella barra di stato se esiste una versione più recente (la finestra «Informazioni» ricorda questa impostazione) | no |
| CyberArk | Mantenere aperta la sessione CyberArk | Richiesta leggera ogni 4 minuti mentre usi il computer; sospesa quando Windows è bloccato o dopo 15 minuti senza tastiera né mouse (la sessione CyberArk scade allora secondo il suo timeout di inattività) | sì |
| CyberArk | PSMP predefinito, porta | Server PSM for SSH; se impostato (o un PSMP per dominio), gli account Unix si aprono in SSH per impostazione predefinita (in soli file per una piattaforma «SFTP»); senza alcun PSMP, SSH e SFTP sono disattivati | vuoto, 22 |
| CyberArk | PSMP per dominio | Altri PSMP (indirizzo, porta, dominio servito); ogni server passa da quello del dominio più vicino al suo (vedi [PSMP per dominio](#psmp-per-dominio)); «Quale PSMP per il server» per verificare | nessuno |
| CyberArk | Componente degli account Windows | Componente PSM degli account Windows (di dominio o locali) senza componente memorizzato per la loro piattaforma, ad esempio `WIN-PSM` | vuoto = `PSM-RDP` |
| CyberArk | Sessioni PSM Windows in una scheda | Sessione PSM (desktop o applicazione remota) in una scheda di ZillaTerm, con il client FreeRDP integrato, alla dimensione della scheda (vedi [Aprire una sessione PSM](#3-aprire-una-sessione-psm-desktop-remoto)); diffusa dal file d'ambiente | no |
| CyberArk | Adatta la risoluzione del desktop remoto alla dimensione della scheda (sperimentale) | Sessioni PSM in una scheda: la risoluzione segue la dimensione della scheda invece di un'immagine adattata; la connessione dal PSM alla destinazione può mantenere la dimensione originale. Per le nuove sessioni | no |
| CyberArk | Componente per piattaforma | Tabella Piattaforma (ID del PVWA, ad esempio `WinDomain`) / Componente PSM: «Aggiungi un componente», «Rimuovi la riga», celle modificabili; ha la precedenza sul componente degli account Windows. «Memorizza questo componente per la piattaforma» (finestra di connessione) vi aggiunge una riga | vuoto |
| Terminale | SSH in ZillaTerm | Terminale e scheda File integrati; altrimenti Windows Terminal | sì |
| Terminale | Colori del terminale, dimensione carattere | Tavolozza (Campbell, One Half, Solarized…) e dimensione del carattere dei terminali SSH | Campbell, 14 |
| Terminale | Avvisa prima di incollare più righe | Anteprima e conferma quando la shell eseguirebbe le righe una alla volta | sì |
| Terminale | Conferma prima di chiudere una sessione connessa | SSH, desktop remoto, VNC; «Non chiedere più» nella conferma deseleziona questa impostazione | sì |
| Terminale | Il clic destro nel terminale incolla gli appunti | Maiusc+clic destro apre allora il menu; ⚠ un clic destro involontario invia gli appunti alla shell | no |
| Terminale | Segui la cartella del terminale | Consente di attivare il monitoraggio della cartella nella shell; ⚠ un comando viene aggiunto a `PROMPT_COMMAND` | sì |
| Terminale | Server X locale | Server X di questo computer per l'inoltro X11 (`:0` = porta 6000); solo questo computer | `:0` |
| File | Invio dei file | Protocollo provato per primo (SFTP o SCP); se il server lo rifiuta, subentra l'altro | SFTP |
| File | Proporre un archivio .tar.gz | Invio in un unico archivio proposto a partire da questo numero di file rilasciati insieme | sì, 200 |
| File | Monitoraggio in una sessione indipendente | Seguire un file (tail -f) apre una propria connessione SFTP (una sessione PSMP in più) | no |
| File | Editor di testo | Programma aperto da «Modifica» nella scheda File | Blocco note |
| File | Strumento di confronto | Programma proposto nella finestra di confronto, con i suoi argomenti (`{0}` = file di sinistra, `{1}` = di destra) | nessuno |
| Sicurezza | Vault locale | Password principali KeePass memorizzate: «Crea…», «Sblocca…», «Cambia password…», «Elimina ora…»; queste azioni si applicano subito, senza «Salva» | — |
| Sicurezza | Chiavi dei server accettate | Tabella delle impronte verificate e accettate (server, tipo, impronta): PSMP, SSH diretto, certificati FTPS e Desktop remoto delle voci KeePass (una riga per tipo di chiave). «Dimentica le chiavi scelte» rimuove le righe selezionate al salvataggio; la chiave verrà richiesta di nuovo alla prossima connessione | — |
| Menu del pulsante Impostazioni | Registro di debug | Svolgimento delle connessioni in un file, senza segreti (vedi [Sicurezza](#sicurezza)); «Mostra il file del registro» lo apre in Esplora risorse | no |

### Ambiente condiviso

Per dare ZillaTerm a un collega con la configurazione del team (indirizzo del PVWA, metodo di accesso, PSMP
predefinito e per dominio, componente degli account Windows e componenti per piattaforma, elenchi condivisi, chiavi
dei PSMP, promemoria di reperibilità (testo o file), etichette dei server, alcune opzioni), senza niente di personale né alcuna password:

1. **Esportare**: pulsante «Impostazioni» → «Esporta l'ambiente…» salva `ZillaTerm.env.json`.
2. **Accanto all'eseguibile**: metti questo file accanto a `ZillaTerm.exe` (ad esempio nello stesso zip).
   All'avvio, se è nuovo o è cambiato, ZillaTerm lo propone prima della schermata di accesso.
3. **Importare**: pulsante «Impostazioni» → «Importa un ambiente…», oppure «Importa un ambiente…» nella schermata di
   accesso.
4. **File centrale**: Impostazioni › Generale › «File centrale» (un file su una condivisione di rete, che si può anche
   indicare nell'ambiente stesso). Viene riletto a ogni avvio: quando lo modifichi, ognuno vede le modifiche
   all'avvio successivo. Una condivisione irraggiungibile (postazione fuori VPN) non rallenta l'avvio: oltre 5
   secondi il file viene ignorato e riletto all'avvio successivo.

Ogni volta una finestra mostra cosa cambierà («valore precedente → nuovo valore») e l'impronta SHA-256 del file;
«Non applicare» è la scelta predefinita. Il PVWA e i PSMP ricevono la tua password CyberArk: quando il file cambia
il loro indirizzo, aggiunge una chiave di server o un elenco condiviso su un server di rete (Windows vi si autentica a
ogni avvio; il server viene indicato), bisogna spuntare «Ho verificato…» prima di applicare. Una chiave di server già
accettata sul computer non viene mai sostituita da un file (viene segnalata). Vengono riprese solo le chiavi dei PSMP
(quelli del computer o del file): quella di un altro server, ad esempio di accesso di emergenza, viene ignorata e si
verifica alla sua prima connessione. Un file non valido
(indirizzo in http, nome di componente errato…) viene rifiutato per intero. Un file già proposto viene riproposto solo
se è cambiato. Un'impostazione vuota sul computer che esporta non viene esportata: non cancella nulla su quello che
importa. I percorsi (elenchi condivisi, file centrale) sono completi: `C:\…` o `\\server\…`.
Il tuo nome utente, «I miei server» e le tue sessioni recenti non vengono mai toccati; gli elenchi
condivisi si aggiungono senza togliere i tuoi. Le etichette del file sostituiscono quelle del computer; i server
mantengono la loro (un'etichetta assente dal file appare allora in grigio).

Tutte le preferenze sono salvate in `%APPDATA%\ZillaTerm\settings.json`: lingua, indirizzo del PVWA, metodo e
nome utente di accesso, impostazioni qui sopra, «I miei server», le loro cartelle e i file seguiti su di essi
(percorsi), sessioni recenti, posizione dei database KeePass e dei loro file chiave, e degli elenchi condivisi
aperti, posizione e dimensione della finestra, larghezza e stato del pannello laterale. Questo file **non contiene
password, token né chiavi private**. Per ripartire da zero, chiudi l'applicazione ed eliminalo. Viene scritto
prima in un file temporaneo e poi messo al suo posto, mantenendo il precedente come `settings.json.bak`: se il file
diventa illeggibile, viene messo da parte (mai sovrascritto), si riprende il backup e un messaggio lo segnala.
ZillaTerm si apre una sola volta per sessione di Windows: due istanze si sovrascriverebbero a vicenda le
impostazioni. La cronologia dei
trasferimenti della scheda File è accanto, in `transfers.json` (nomi e percorsi dei file, checksum SHA-256, mai il
loro contenuto).

### Passaggio da CyberArkTerm a ZillaTerm

CyberArkTerm ora si chiama ZillaTerm. Al primo avvio di `ZillaTerm.exe`, la cartella `%APPDATA%\CyberArkTerm`
(impostazioni, «I miei server», vault locale, cronologia dei trasferimenti, registro dell'accesso di emergenza) viene
copiata in `%APPDATA%\ZillaTerm`; la vecchia cartella viene conservata: eliminala, insieme a `CyberArkTerm.exe`, una
volta passato a ZillaTerm. Un file `CyberArkTerm.env.json` accanto all'eseguibile viene ancora letto, gli elenchi
condivisi restano leggibili dalle due versioni e le password principali del vault locale restano accessibili. La
vecchia cartella temporanea (`%TEMP%\CyberArkTerm`) viene svuotata nei successivi avvii (file più vecchi di un giorno).
Le due versioni non possono essere aperte contemporaneamente. CyberArkTerm segnala la prima versione di ZillaTerm ma non
può scaricarla da solo (repository rinominato): scaricala una volta dalla pagina delle versioni.

## Sicurezza

- **HTTPS obbligatorio** verso il PVWA; la convalida dei certificati non viene mai disattivata. Un reindirizzamento
  del PVWA non viene mai seguito (la tua password partirebbe verso l'indirizzo indicato): viene segnalato con
  quell'indirizzo. L'indirizzo del PVWA può contenere solo il nome del server, una porta e un percorso: un «nome@»
  davanti al server (che farebbe raggiungere un server diverso da quello mostrato), uno spazio, una «\» o una lettera
  non ASCII vengono rifiutati.
- **Nomi dei file del server**: i caratteri invisibili (inversione della direzione di scrittura, spazio a larghezza
  zero, caratteri di controllo) compaiono come «�» nell'elenco e nelle conferme, e diventano «_» nel nome del file
  scaricato, perché un nome non possa imitarne un altro («.exe» mostrato come «.pdf»). I file speciali (un
  dispositivo come `/dev/zero`, una named pipe, un socket) non vengono né aperti né scaricati, e vengono ignorati nel
  download di una cartella.
- **Nessun segreto su disco**: password CyberArk, token di sessione, chiave MFA e password PSMP restano in memoria
  per la durata della sessione. Password PSMP conservate e chiave MFA dimenticate al blocco di Windows; la chiave MFA
  viene cancellata dopo ogni connessione e rimossa dal PVWA alla disconnessione (`Logoff`), fatta alla chiusura.
- Sessione PVWA aperta con `concurrentSession`: l'eventuale sessione web del PVWA non viene chiusa.
- **Copia di una password**: la risposta del PVWA viene letta in un buffer cancellato subito dopo e decodificata
  senza passare da una stringa; la password va direttamente negli appunti di Windows, contrassegnata per essere
  esclusa dalla cronologia (`Win+V`), dalla sincronizzazione tra dispositivi e dagli strumenti di monitoraggio degli
  appunti, poi cancellata dopo 20 s se è ancora presente (nuovo tentativo ogni secondo se un'altra applicazione
  tiene aperti gli appunti), oltre che alla disconnessione da CyberArk, alla chiusura e al blocco di Windows. Non viene mai
  mostrata né scritta nel registro di debug. Una risposta che non è la password (pagina HTML di manutenzione,
  reindirizzamento verso una pagina di accesso SSO, risposta vuota) viene rifiutata invece di essere copiata.
- **Importazione delle sessioni di un altro programma**: vengono letti solo il server, la porta, il protocollo,
  l'utente e la cartella, mai una password; file XML letti senza DTD né risorse esterne, dimensione limitata. Ogni
  server importato si apre tramite PSM o il PSMP con un account del PVWA, mai direttamente; un server senza account
  non viene importato.
- **Aggiunta di un account**: la password viene letta dal campo mascherato senza passare da una stringa, inviata una
  sola volta al PVWA in HTTPS, poi cancellata dalla memoria; non viene né salvata né scritta nel registro di debug.
- **Cartella temporanea**: `%TEMP%\ZillaTerm`, riservata al vostro account Windows (permessi limitati a voi
  soli); se appartiene a un altro account (variabile TEMP che punta a una cartella condivisa), viene usata
  `%LOCALAPPDATA%\ZillaTerm\Temp`. I percorsi qui sotto sono relativi a questa cartella.
- **Sessioni PSM**: il file RDP del PVWA (token PSM monouso) viene scritto nella cartella temporanea per `mstsc`, che
  ne verifica la firma, poi eliminato dopo 60 s o alla chiusura. In una scheda (opzione), non viene mai scritto sul
  disco: ZillaTerm lo legge in memoria e lo trasmette al client FreeRDP sul suo standard input (token assente dalla
  riga di comando e dai registri). Il certificato del PSM (o la sua chiave) viene letto e verificato da ZillaTerm prima
  dell'avvio del client (approvato da Windows, fissato o confermato); il client non ne accetta poi nessun altro, non
  mostra alcuna finestra di Windows e non legge alcuna credenziale salvata di Windows.
- **Digitazione simultanea** (vista parallela): disattivata a ogni apertura della vista, segnalata dal pulsante
  ambra «ATTIVA (n)», da una fascia e da una cornice ambra che nominano le sessioni interessate; le sessioni escluse
  sono segnate «esclusa», una sessione aggiunta non vi è inclusa d'ufficio, e
  incollare più righe in più sessioni chiede conferma. Ogni sessione resta una sessione PSMP distinta, registrata
  come di consueto.
- **Incollare più righe** in un terminale la cui shell le eseguirebbe una alla volta: anteprima e conferma prima
  dell'invio (impostazione attiva per impostazione predefinita).
- **Conferme**: pulsanti con un verbo esplicito nella lingua dell'applicazione, «Annulla» predefinito, server,
  account o safe nominato; la chiusura di una sessione connessa viene confermata (impostazione attiva per
  impostazione predefinita).
- **Confronto di file**: contenuti letti in memoria e cancellati alla chiusura della finestra; solo le copie date a
  uno strumento esterno passano dal disco (cartella temporanea, `compare`), eliminate alla chiusura della finestra e
  all'avvio successivo.
- **Nuova versione**: nessuna richiesta verso Internet senza una tua azione o l'opzione delle Impostazioni
  (disattivata per impostazione predefinita); vengono seguiti solo gli indirizzi del repository del progetto,
  l'archivio viene conservato solo se il suo checksum SHA-256 è quello di `SHA256SUMS.txt` della stessa versione (il
  che rileva un download incompleto o danneggiato, non una versione pubblicata da qualcuno che avesse preso il
  controllo del repository), e nulla viene installato né avviato.
- **Chiavi host del PSMP fissate** al primo utilizzo: l'impronta va confrontata prima di accettare («Annulla la
  connessione» predefinito); una chiave cambiata è segnalata da una fascia e sostituisce la vecchia solo dopo aver
  selezionato una casella di conferma (lo stesso per i server raggiunti in accesso di emergenza e per i certificati
  FTPS e Desktop remoto). Una chiave è memorizzata per server e per tipo: una chiave di un tipo mai visto per quel
  server è mostrata con quelle già accettate e si aggiunge solo dopo una casella di verifica, mai d'ufficio. Le
  chiavi accettate si consultano e si dimenticano in Impostazioni › Sicurezza.
- **Mantenimento della sessione PVWA**: evita la scadenza mentre lavori; non viene inviato nulla mentre Windows è
  bloccato né dopo 15 minuti senza tastiera né mouse, e l'opzione si disattiva nelle Impostazioni se la tua politica
  lo richiede.
- **Database KeePass**:
  - la password principale non è mai salvata, tranne nel vault locale se lo chiedi: Argon2id (64 MiB, 3 passate) poi
    AES-256-GCM, parametri di derivazione autenticati, il tutto protetto da DPAPI (account Windows);
  - in memoria, la chiave del database e le password delle voci restano mascherate e sono rivelate solo al momento
    della connessione; database bloccati alla disconnessione da CyberArk, alla chiusura e al blocco di Windows;
  - salvataggio sicuro: il file viene riletto, la modifica è applicata alla sua versione attuale (le modifiche fatte
    altrove sono conservate), il risultato decifrato è verificato e il file è sostituito in un solo passo (una copia
    `.bak` durante la sostituzione, eliminata una volta riletto il file identico); una voce modificata altrove nel
    frattempo non viene sovrascritta;
  - desktop remoto diretto: la password è passata solo al controllo Desktop remoto (nessun file, nessun gestore
    credenziali), con autenticazione a livello di rete (NLA) e avviso se il server non è riconosciuto; certificato
    del server letto prima dell'invio della password e fissato al primo utilizzo (una modifica è segnalata e bloccata
    finché non è confermata), server senza TLS rifiutato, cache persistente delle immagini disattivata. Limite: il
    controllo esegue poi la propria negoziazione TLS, senza che gli si possa imporre l'impronta fissata; per un
    certificato che Windows non approva, mostra ancora il proprio avviso;
  - VNC: il protocollo non cifra né lo schermo, né i tasti, né gli appunti (banner permanente); la password non è
    inviata così com'è (sfida-risposta del protocollo), e solo dopo il tuo consenso: il server non è autenticato e
    può recuperarne i primi 8 caratteri; segnali acustici limitati a uno al secondo; gli appunti sono scambiati solo
    con un clic; la dimensione dello schermo annunciata dal server è limitata (8.192 pixel per lato);
  - FTP: TLS tentato per primo, connessione in chiaro solo dopo il tuo consenso (banner permanente), mai per
    `ftpes://` e `ftps://`; con TLS, anche i trasferimenti sono cifrati (`PROT P`); un server già visto con TLS
    che non lo propone più è segnalato come possibile intercettazione, e nulla viene inviato senza conferma;
  - certificato FTPS: quello che Windows approva è accettato; altrimenti la sua impronta SHA-256 è mostrata e
    fissata al primo consenso (come una chiave host SSH), un cambiamento è segnalato; rifiutato, la connessione si
    ferma prima dell'invio del nome utente;
  - nomi di file con caratteri di controllo rifiutati (nessuna iniezione di comandi FTP);
  - `urgence.log`: data, account Windows, computer, azione, database, voce, destinazione; mai una password. Ogni
    lettura della password di una voce vi è annotata, riconnessioni e connessioni SFTP / SCP della scheda File
    comprese; se il registro non può essere scritto, la connessione non viene aperta.
- **Registro di debug**, disattivato per impostazione predefinita (menu del pulsante Impostazioni):
  `%LOCALAPPDATA%\ZillaTerm\debug.log`, al massimo 5 MB più una generazione `.1`. Registra lo svolgimento delle
  connessioni PVWA, PSM, desktop remoto e SSH: indirizzi e stati delle richieste, impostazioni del file .rdp, eventi
  e codici del controllo Desktop remoto, versione e algoritmi del server SSH, errori; per ogni protocollo di invio
  rifiutato, il passo (connessione, comando scp, annuncio del file), la risposta del server e il protocollo
  subentrato. Contiene nomi di server e di account, ma **mai** password, token di
  sessione, richiesta di sessione PSM (`PSM@…` mascherata), firma, intestazione o corpo delle richieste, né il
  contenuto delle sessioni. La barra di stato lo segnala finché è attivo. Rileggilo prima di trasmetterlo, ed
  eliminalo una volta risolto il problema.
- **File modificati**: la copia locale aperta nell'editor si trova nella cartella temporanea (`edit`) e viene eliminata
  alla chiusura della scheda SSH; un avviso segnala le modifiche non rinviate.
- **Nessuna iniezione di comandi**: percorsi SCP e cartelle iniziali protetti tra apici per la shell remota;
  argomenti `ssh` / Windows Terminal convalidati e passati senza shell.
- Esportazione CSV protetta contro l'iniezione di formule Excel.
- **File di ambiente** (`ZillaTerm.env.json`): nessuna password né dato personale, vengono letti solo i campi noti.
  Un file non viene mai applicato senza il tuo consenso: modifiche e impronta SHA-256 mostrate, una casella da
  spuntare quando cambia l'indirizzo del PVWA o di un PSMP, o viene aggiunta una chiave di server o un elenco su un
  server di rete. Non sostituisce mai una chiave di server già accettata e porta solo chiavi di PSMP; indirizzo del PVWA in https obbligatorio; un file oltre 1 MB viene rifiutato.
- **File di server ed elenchi condivisi**: nessuna password né token, solo nomi di server, account e safe, ID degli
  account e impostazioni di connessione (il motivo predefinito non viene mai condiviso). Non danno alcun accesso:
  ognuno si connette con i propri diritti CyberArk, e la descrizione comandi mostra l'account come lo descrive il
  vault CyberArk. Una macchina di destinazione proveniente da un elenco condiviso e non consentita per l'account da CyberArk
  viene confermata prima della prima connessione. L'autore annotato nel registro (account CyberArk e account
  Windows) è dichiarativo: fa fede l'audit della condivisione di rete. Un file di oltre 8 MB viene rifiutato.
- Le sessioni PSM e PSMP aperte da ZillaTerm sono sessioni CyberArk standard: vengono registrate e verificate dal
  PSM come quelle aperte dal PVWA.

Per segnalare una vulnerabilità, vedi [SECURITY.md](../SECURITY.md) (segnalazione privata, nessuna issue pubblica).

## Funzionamento tecnico

### API del PVWA utilizzate

| Chiamata | Uso |
| --- | --- |
| `POST /PasswordVault/API/auth/{CyberArk\|LDAP\|RADIUS\|Windows}/Logon` | Apertura della sessione |
| `GET /PasswordVault/API/Accounts?offset=…&limit=1000` | Elenco paginato degli account |
| `POST /PasswordVault/API/Accounts/{id}/PSMConnect` | File RDP della sessione PSM |
| `POST /PasswordVault/API/Accounts` | Creazione di un account in un safe («Aggiungi un account») |
| `POST /PasswordVault/API/Accounts` (una volta per riga) | Importazione di account da un CSV |
| `PATCH` / `DELETE /PasswordVault/API/Accounts/{id}` | Modifica (solo i campi cambiati) ed eliminazione di un account |
| `POST /PasswordVault/API/Accounts/{id}/Verify`, `/Change`, `/Reconcile` | Operazioni richieste al CPM |
| `POST /PasswordVault/API/Accounts/{id}/Password/Retrieve` | Copia della password (motivo, ticket; uso «copy» nell'audit) |
| `POST` / `PUT` / `DELETE /PasswordVault/API/Safes/{safe}/Members[/{membro}]` | Aggiunta, diritti e rimozione di un membro del safe |
| `GET /PasswordVault/API/Safes/{safe}/Members?offset=…&limit=1000` | Membri di un safe e i loro diritti (finestra «Membri del safe») |
| `POST /PasswordVault/API/Users/Secret/SSHKeys/Cache` | Chiave SSH temporanea «MFA caching» (se attivata) |
| `GET /PasswordVault/API/Accounts?offset=0&limit=1` | Mantenimento della sessione (ogni 4 minuti) |
| `POST /PasswordVault/API/Auth/Logoff` | Chiusura della sessione |

### Elenchi condivisi

File JSON (`"format": "CyberArkTerm.SharedServers"`, versione 1): nome, PVWA di origine, revisione, cartelle,
server (con chi li ha aggiunti e quando) e registro delle modifiche (le ultime 1.000). Ogni modifica apre il file
in esclusiva (gli altri computer riprovano per 5 s), lo rilegge, copia la revisione corrente in
`nome.versions\nome.r00012.20261006-101500.json` (revisione e data del suo salvataggio, 100 versioni conservate),
applica la modifica, aumenta la revisione, annota chi, quando e cosa, poi riscrive il file (rimesso com'era se la
scrittura fallisce). La visualizzazione segue le modifiche del file (`FileSystemWatcher`, ripreso da solo un minuto dopo un'interruzione della condivisione o della VPN, poi l'elenco viene riletto) e lo rilegge con `F5`.
L'esportazione di «I miei server» ha lo stesso formato con `"format": "CyberArkTerm.Servers"`, senza revisione né
registro.

### Database KeePass

Lettura e scrittura native (senza KeePass installato) dei formati **KDBX 3.1 e 4.x**: cifratura AES-256 o ChaCha20,
derivazione della chiave AES-KDF (istruzioni AES del processore) o Argon2d / Argon2id, file chiave XML 1.0 / 2.0, 32
byte, 64 caratteri esadecimali o file qualsiasi. Il file riscritto mantiene la versione, la cifratura e la
derivazione della chiave originali, con nuovi semi a ogni salvataggio, compreso quello della derivazione della chiave
(come KeePass: una chiave derivata catturata una volta non decifra le versioni successive). I database di test
(`tests/ZillaTerm.Core.Tests/KeePass/Vaults`) provengono da KeePassXC e pykeepass, e i file scritti da
ZillaTerm sono stati verificati in entrambi gli strumenti.

### Sessioni VNC

Client integrato (protocollo RFB 3.3, 3.7 e 3.8, RFC 6143; a un server più recente, come RealVNC 4 o 5, si risponde
in 3.8), nulla da installare: autenticazione «nessuna» o «password VNC» (se il server le propone entrambe: la password
se la voce ne ha una, altrimenti nessuna) (DES del protocollo, implementato in ZillaTerm perché la modalità FIPS di Windows può vietare DES),
codifiche Raw, CopyRect e Hextile, cambio di dimensione dello schermo, pixel a 32 bit. La tastiera è inviata come
«keysym» X11 (i caratteri AltGr sono inviati come caratteri), la rotellina come pulsanti 4 e 5. La password VNC è
inviata solo dopo il tuo consenso (una volta per scheda); i segnali acustici del server sono limitati a uno al secondo.

### Sessioni di file FTP / FTPS

Libreria FluentFTP (licenza MIT). Modalità passiva: `PASV` in IPv4, con la connessione dati sempre verso il server
stesso (l'indirizzo indicato nella risposta viene ignorato: un server non può farla puntare verso un'altra macchina),
`EPSV` in IPv6; binaria, `PBSZ 0` e `PROT P` con TLS;
certificato verificato da Windows, altrimenti fissato (`ftps://server:porta` tra le chiavi dei server accettate,
in Impostazioni › Sicurezza); i server che hanno cifrato una connessione sono annotati (`FtpTlsServers` delle
impostazioni) per segnalare una rimozione di TLS. FTP non ha una somma di controllo standard: ogni invio è riletto
dal server e confrontato con
SHA-256. Lettura parziale (`REST`) per il confronto e il monitoraggio in tempo reale. Dopo un trasferimento
interrotto, la connessione è riaperta e il file incompleto eliminato. Un file sostituito viene riscritto sul posto:
conserva i suoi permessi. Collegamenti simbolici: i primi 40 di una cartella vengono risolti (un'andata e ritorno
ciascuno); oltre, un collegamento appare come un file, e aprirlo entra nella cartella se è una cartella.

### Sessioni Desktop remoto

Le sessioni PSM si aprono con il file RDP restituito da `PSMConnect`. Per impostazione predefinita viene passato così
com'è a Connessione Desktop remoto (`mstsc`): ne verifica la firma e gestisce sia il desktop sia l'applicazione
remota (RemoteApp). Il registro di debug ne annota la struttura (token, firma e argomenti mascherati).

Con l'opzione «Sessioni PSM Windows in una scheda», il file viene letto da ZillaTerm: indirizzo e porta del PSM,
utente, programma di avvio (`alternate shell`) o applicazione remota (`remoteapplicationprogram`, nome e argomenti),
sicurezza, gateway, bilanciamento del carico, reindirizzamenti (`drivestoredirect:s:*` solo per le unità), effetti.
La modalità di visualizzazione del file (`screen mode id`, dimensione, schermi) viene ignorata. Una sessione PSM è
annidata: nella propria sessione, il PSM apre la connessione al server di destinazione alla stessa dimensione, e
cambiare poi la risoluzione della sessione PSM non la cambia per la destinazione (desktop tagliato, clic spostati).
Da qui la dimensione fissa e il ridimensionamento dell'immagine di queste sessioni per impostazione predefinita
(l'opzione sperimentale «Adatta la risoluzione del desktop remoto alla dimensione della scheda» cambia la risoluzione
della sessione PSM, da provare con il proprio PSM); le connessioni dirette seguono la dimensione della scheda.

**Client FreeRDP.** Le sessioni PSM in una scheda passano dal client Windows di FreeRDP (`wfreerdp`), compilato dal
workflow di release del fork `muller-camille/FreeRDP` (ramo `ci`) e pubblicato nelle sue release;
`tools/freerdp-download.sh` prende la release fissata e la verifica (somma SHA-256 fissa, attestazione di provenienza
di GitHub). FreeRDP 3.32.1 e OpenSSL 3.5.9 a versioni fisse (commit verificato, archivio verificato dalla sua somma
SHA-256), collegati staticamente (un solo eseguibile, che usa solo DLL di Windows), con correzioni
generiche, senza nulla di specifico a ZillaTerm, ciascuna destinata a essere proposta al progetto FreeRDP (ramo
`patches-3.32.1` del fork `muller-camille/FreeRDP`): file di connessione letto dallo standard input, vecchia
«sicurezza RDP» soggetta a `/cert:deny` e alle impronte, applicazioni remote che rispondono al server e vengono
mostrate nella finestra padre, tastiera e rotellina, finestra che risponde tra due tentativi di riconnessione, DPI
della finestra padre, ricerca delle librerie. ZillaTerm lo integra, lo copia al primo utilizzo in
`%LOCALAPPDATA%\ZillaTerm\FreeRDP\<impronta>` (cartella riservata all'utente), ne verifica l'impronta SHA-256 prima
di ogni avvio e lo tiene aperto in sola lettura durante l'esecuzione.

Lo pilota solo con opzioni standard di FreeRDP. Il file del PVWA, riscritto da ZillaTerm (PSM, utente, programma di
avvio o applicazione remota e i suoi argomenti, che portano il token), gli viene trasmesso sul suo standard input (`-`
come file di connessione): token PSM e password non sono né sulla riga di comando, né sul disco. Contiene una password
vuota: il client non chiede nulla e non legge mai le credenziali salvate di Windows (`TERMSRV/…`); `/from-stdin` gli
impedisce di aprire una finestra di credenziali di Windows. `/cert:deny` seguito dalle impronte accettate da ZillaTerm
(`fingerprint:sha256:…`): rifiuta qualsiasi altro certificato o chiave, senza chiedere nulla. La sua cartella di
configurazione è una cartella di ZillaTerm, lasciata vuota.

**Identità del server, prima della connessione.** ZillaTerm legge da sé il certificato del PSM prima di avviare il
client (richiesta di connessione X.224, poi negoziazione TLS interrotta appena ricevuto il certificato: né token né
utente inviati) e lo verifica: catena fino a un'autorità approvata da Windows, nome, utilizzo, revoca quando è
raggiungibile; altrimenti impronta fissata (`rdp://server:porta` tra le chiavi dei server accettate) o confermata
dall'utente. Un PSM che accetta solo la vecchia «sicurezza RDP», senza TLS: ZillaTerm legge la sua chiave allo stesso
modo (richiesta di connessione la cui risposta contiene la chiave; nient'altro viene inviato) e la fissa
(`rdp://server:porta`, tipo `RDP-RSA`; impronta: SHA-256 della chiave in DER PKCS#1, quella che confronta FreeRDP),
cifratura di almeno 128 bit; un PSM senza cifratura viene rifiutato, così come la chiave di un server che ha già
presentato un certificato TLS (TLS rimosso: possibile intercettazione). Più certificati e chiavi per indirizzo (PSM con
bilanciamento del carico: uno per server, ciascuno confermato una volta): il client li riceve tutti. Le domande
vengono poste prima dell'avvio del client, mai durante la connessione; una volta data una risposta, la connessione
parte con un nuovo token richiesto al PVWA (il token PSM vale una sola volta e scade presto). Se il client rifiuta
comunque il server (altro server dietro lo stesso indirizzo), ZillaTerm ne rilegge l'identità e la sottopone se è
nuova (al massimo due volte per connessione).

Rafforzamento: nessuna libreria caricata fuori dalla sua cartella e da System32 (i canali virtuali sono compilati
nell'eseguibile), OpenSSL senza moduli, motori né file di configurazione caricati all'esecuzione, essendo le sue
cartelle compilate quelle di Windows riservate agli amministratori; variabili di log, di proxy e di OpenSSL rimosse
dal suo ambiente. Il processo è in un oggetto processo (job object) chiuso insieme a ZillaTerm: non gli sopravvive. La
sua finestra è figlia della scheda (opzione `/parent-window`); un clic al suo interno dà la tastiera alla scheda, da cui
l'hook di tastiera di FreeRDP invia i tasti alla sessione. Un'applicazione remota viene mostrata in questa finestra
(superficie della sessione, come un desktop) invece che in finestre separate. Chiudere la scheda chiude questa finestra
(fine della sessione come con il pulsante di chiusura del client). Fine della sessione ed errori: il codice di FreeRDP
(`ERRINFO_…`, `ERRCONNECT_…`, codice di uscita del client) è spiegato nella scheda. Limite: il client Windows di
FreeRDP è contrassegnato come «non mantenuto» dal progetto FreeRDP (il nucleo del protocollo, invece, lo è); ZillaTerm
ne fissa la versione e lo aggiorna con le sue correzioni. I test di integrazione (job `rdp` del workflow `build`)
aprono con esso un desktop e un'applicazione remota sul computer di CI: dimensione, clic, tasti, certificato
rifiutato, chiave della vecchia sicurezza RDP.

**Controllo Desktop remoto di Windows.** Il desktop remoto diretto dei database KeePass, e le sessioni PSM di desktop
quando il client FreeRDP non è integrato (compilazione senza di esso), ospitano il controllo ActiveX di Windows
(`mstscax.dll`, la classe `MsRdpClient` più recente disponibile). Una connessione diretta è impostata così:
autenticazione a livello di rete (NLA), avviso se il server non è riconosciuto, reindirizzamenti disattivati tranne
gli appunti, cache persistente delle immagini disattivata. Prima di ogni connessione, ZillaTerm legge da sé il
certificato TLS del server (richiesta di connessione X.224, poi negoziazione TLS interrotta appena ricevuto il
certificato: né nome utente né password inviati) per fissarlo. Il controllo ActiveX non permette di imporgli questa
impronta: per una sessione PSM senza FreeRDP, è Windows a verificare il certificato, come con `mstsc` (almeno il
livello di autenticazione 2). Le chiusure di sessione e gli errori di connessione sono spiegati nella scheda con il
messaggio e i codici di Windows (motivo, motivo esteso).

**Un thread per connessione desktop remoto.** Il controllo, la sua finestra e i suoi eventi vivono su un thread a
parte (STA, con il proprio ciclo di messaggi); l'interfaccia non lo attende mai. La scheda contiene una finestra del
thread dell'interfaccia, in cui quel thread colloca la finestra del controllo. Prima di rilasciare il controllo, ve
la toglie: una disconnessione o un rilascio lento non blocca più l'applicazione. Se il thread non risponde per 5 s,
la barra della scheda lo segnala, e il resto dell'applicazione resta utilizzabile. Limite: Windows condivide
tastiera e mouse tra una finestra e quelle che contiene, anche di un altro thread; un controllo bloccato
definitivamente può ancora trattenere un clic nella sua area o un cambio di focus. Alla disconnessione o alla
chiusura, ZillaTerm attende al massimo 3 s la fine delle sessioni desktop remoto; una sessione il cui controllo resta
bloccato viene allora tolta dalla finestra (il suo spazio è messo da parte), e l'applicazione si disconnette comunque,
o si chiude direttamente.

### Sessioni PSMP

Ogni scheda SSH apre fino a tre connessioni al PSMP, con lo stesso identificativo
`<tu>@<account>[#dominio]@<destinazione>`: il terminale, la connessione SFTP della scheda File e una connessione SCP
al primo invio in SCP (scelto nelle Impostazioni, o subentrato a un invio SFTP rifiutato). Ognuna è una sessione PSMP, registrata dal PSM. Gli invii al server (digitazione, dimensione
del terminale) e la chiusura delle connessioni avvengono fuori dal thread dell'interfaccia, in ordine: un server o
un PSMP che non legge più non blocca l'applicazione.

### Monitoraggio della cartella del terminale

All'apertura di una sessione SSH (se l'opzione è attiva), ZillaTerm attende che la shell del server di
destinazione mostri il prompt (fino a 60 s: il PSMP a volte impiega diversi secondi a raggiungere la destinazione),
poi le invia un comando di una riga, preceduto da uno spazio per non finire nella cronologia (bash, o zsh con
`HIST_IGNORE_SPACE`). Non viene inviato nulla se hai già iniziato a digitare; il comando può essere reinviato senza
effetti doppi (casella «Segui»):

- definizione di `PROMPT_COMMAND` (bash) o `precmd` (zsh) che emette la sequenza standard **OSC 7** con la cartella
  corrente a ogni prompt;
- con tcsh, l'alias `cwdcmd` (solo se non è già definito), che emette la stessa sequenza a ogni cambio di cartella;
- se è configurata una cartella iniziale, un `cd` verso quella cartella;
- un marcatore privato alla fine (`ESC ] 6973 ; BEL`): quando arriva, ZillaTerm cancella da sé il comando digitato e
  la sua eco, dalla riga del prompt in giù, qualunque sia il numero di righe che il server ha usato. Il marcatore non
  ha effetto se ZillaTerm non ha appena inviato il comando. Mentre ZillaTerm lo attende (al massimo 3 secondi), ciò che
  digiti viene trattenuto, poi inviato subito dopo la cancellazione: ciò che digiti resta sempre visibile. La registrazione PSM mostra comunque il comando, come
  tutto ciò che viene digitato nella sessione.

Il terminale integrato decodifica la sequenza OSC 7 e la scheda File si posiziona nella cartella indicata.

Tutte le shell leggono il comando senza errori: ogni parte viene eseguita solo dalla famiglia di shell a cui è
destinata. Non legge nessuna variabile che potrebbe non esistere: un profilo che attiva `set -u` (`set -o nounset`)
non lo interrompe. Con csh, ksh, sh o fish il monitoraggio non viene installato e sullo schermo non resta nulla.

## Risoluzione dei problemi

| Sintomo | Causa probabile e soluzione |
| --- | --- |
| «Il PVWA non ha un componente di connessione «PSM-RDP» per questo account» (`EPVWA093E Failed to get the relevant connection component`) | La piattaforma dell'account usa un componente con un altro nome (ad esempio `WIN-PSM`): quello proposto dal pulsante «Connect» del PVWA, o il nome dopo `/c` in un comando `psm /u … /a … /c …`. Inseriscilo in «Componente»; «Memorizza questo componente per la piattaforma» è selezionata per le connessioni successive. |
| «Connessione TLS rifiutata: il certificato del PVWA non è considerato attendibile» | Il certificato (o l'autorità che lo ha emesso) non è nell'archivio Windows della postazione. |
| «Il PVWA deve essere raggiunto in HTTPS» | Inserisci l'indirizzo senza `http://` (o con `https://`). |
| «Il PVWA rifiuta il nome utente o la password» (`ITATS004E Authentication failure…`) | Verifica il nome utente, la password e il Bloc Maiusc prima di riprovare: dopo vari tentativi falliti, CyberArk sospende l'account. |
| «Il tuo account CyberArk è sospeso o disattivato» | Troppi accessi non riusciti, o decisione di un amministratore: chiedi al team CyberArk di riattivarlo. |
| «La tua password CyberArk è scaduta» | Cambiala nella pagina web del PVWA, poi accedi a ZillaTerm con quella nuova. |
| «Il server SSH rifiuta l'autenticazione» | Tramite il PSMP è la tua password CyberArk: verificala (e il Bloc Maiusc) prima di riprovare, vari tentativi falliti sospendono l'account. |
| «Nome del server non trovato (DNS)», «Il server non ha risposto in tempo», «Il server rifiuta la connessione su questa porta» | Verifica l'indirizzo e la porta, e l'accesso alla rete aziendale (VPN, firewall); il «Dettaglio» che segue riporta il messaggio di Windows. |
| Finestra «Sessione CyberArk scaduta» | Timeout di inattività del PVWA superato: inserisci la password per accedere di nuovo; schede, sessioni e trasferimenti restano aperti. |
| «Password» → «Copia la password…»: «Il PVWA rifiuta: … «Recuperare gli account» …» | Diritto mancante sul safe, o motivo / ticket richiesto dalla piattaforma: inseriscilo. Con la doppia convalida, fai la richiesta nel PVWA. |
| «Verifica / Cambia / Riconcilia»: «Il PVWA rifiuta: … «Avviare le operazioni CPM» …» | Chiedi questo diritto sul safe; «Membri del safe» mostra i tuoi diritti. |
| «Aggiungi un account»: «Il PVWA rifiuta: il tuo account deve avere il diritto «Aggiungere account»…» | Chiedi questo diritto sul safe (e «Aggiornare il contenuto degli account» per fornire la password), oppure crea l'account senza password. «Membri del safe» mostra i tuoi diritti. |
| «Membri del safe»: «Il tuo account non può vedere i membri di questo safe» | Il PVWA richiede il diritto «View Safe Members» sul safe: chiedilo a un gestore del safe. |
| «Questo componente di connessione non è configurato per la piattaforma dell'account» (`Connection component … is not configured for platform …`) | Scegli il componente corretto in «Opzioni avanzate…», seleziona «Memorizza» per la piattaforma. |
| «CyberArk richiede un motivo per questo account» (`You must specify a reason…`) | Inserisci un motivo nella finestra che si apre (o un motivo predefinito nelle proprietà del server, in «I miei server»). |
| «CyberArk richiede un ticket valido per questo account», «Questo account richiede una richiesta approvata (doppio controllo)» | Inserisci il sistema e il numero del ticket; per il doppio controllo, richiedi l'accesso nella pagina web del PVWA e attendi l'approvazione. |
| L'account non compare | Non hai il permesso «List accounts» sul suo safe, oppure l'elenco va ricaricato (`F5`). |
| La password PSMP viene chiesta per ogni scheda | MFA caching non attivato sul PVWA: comportamento normale (una volta per scheda). Aprendo più sessioni insieme, spunta «Usa questa password anche…» per digitarla una sola volta. |
| La scheda File indica «Connessione SFTP impossibile» | SFTP non è consentito sul PSMP o per questo account: rivolgiti al team CyberArk. |
| Un invio indica «SFTP (SCP rifiutato)» o «SCP (SFTP rifiutato)» | Il PSMP o il server ha rifiutato quel protocollo per questo file: l'altro è subentrato e il file è stato verificato come al solito. Il riepilogo riporta la risposta del server. Un PSMP che rifiuta SCP per una piattaforma (errore `118E Selected component PSMP-SCP does not contain the target settings definitions…` nei suoi log) non ha il componente di connessione PSMP-SCP: il tuo team CyberArk può aggiungerlo alla piattaforma, altrimenti gli invii passano in SFTP. |
| Il browser non segue i `cd` | La shell remota non è bash, zsh o tcsh (o tcsh ha già un proprio alias `cwdcmd`), l'opzione è disattivata nelle Impostazioni, oppure il prompt non è stato riconosciuto: riseleziona «Segui la cartella del terminale» al prompt della shell. |
| Avviso «La chiave del PSMP è cambiata» | Prosegui (casella «Ho confermato la modifica con il team CyberArk», poi «Sostituisci la chiave e connetti») solo se il team CyberArk conferma una modifica del server; altrimenti annulla e avvisalo. |
| «Connessione annullata: la chiave del server non è stata accettata.» | La finestra dell'impronta è stata annullata o chiusa: riconnettiti e accetta la chiave dopo averne confrontato l'impronta. |
| Finestra «Nuova chiave del PSMP, di un altro tipo» | Il PSMP presenta un tipo di chiave (ed25519, RSA…) mai visto su questo computer: confronta l'impronta con quella pubblicata dal team CyberArk prima di selezionare la casella e accettarla; altrimenti annulla e avvisalo. |
| «Password principale o file chiave errati.» | Controlla la password e il file chiave; un database protetto da YubiKey non è supportato. |
| Il database KeePass chiede la password nonostante «Memorizza» | Vault locale bloccato («Più tardi» allo sblocco) o password principale cambiata altrove: digitala, viene memorizzata di nuovo. |
| «Il file del vault locale è danneggiato o è stato creato da un altro account Windows.» | Il vault locale non segue un cambio di computer o di account: eliminalo nelle Impostazioni e ricrealo. |
| «La voce … è stata modificata o eliminata nel database KeePass nel frattempo» | Qualcuno ha cambiato la stessa voce altrove: il database viene ricaricato, rifai la modifica. |
| Un account Unix si apre con il PSM e non in SSH | Indirizzo del PSMP non impostato nelle Impostazioni, oppure account non riconosciuto come Unix: clic destro → «Connetti in SSH (PSMP)». |
| Un account si apre in soli file e non in un terminale | Il nome della sua piattaforma contiene «SFTP»: clic destro → «Connetti in SSH (PSMP)», o «Proprietà…» in «I miei server» per cambiare il tipo di connessione. |
| «L'elenco condiviso è in corso di modifica da parte di qualcun altro» | Un altro computer scrive l'elenco da più di 5 secondi, o tiene il file aperto: riprova tra un momento. |
| «non hai il diritto di modificare questo file (diritti della condivisione di rete)» | La condivisione è in sola lettura per te: chiedi il diritto di scrittura al suo responsabile. L'elenco resta utilizzabile. |
| Un elenco condiviso mostra «(illeggibile)» | Condivisione irraggiungibile o file danneggiato: la descrizione comandi riporta l'errore. Se il file è danneggiato, copia al suo posto la versione più recente della cartella `nome.versions`. |
| Capire un errore di connessione | Impostazioni → Registro di debug, riproduci il problema, poi Impostazioni → «Mostra il file del registro». |
| Una sessione PSM in una scheda chiede di confermare il certificato del PSM | Windows non approva questo certificato (autofirmato, o emesso per un altro nome): confronta l'impronta con il team CyberArk prima di accettare; viene poi memorizzata. Un certificato emesso dall'autorità dell'azienda per il nome del PSM elimina questa domanda. |
| Sessione PSM: «Nuovo certificato per questo PSM» | L'indirizzo del PSM raggruppa probabilmente più server (bilanciamento del carico), ciascuno con il proprio certificato autofirmato, oppure il certificato è stato rinnovato: confronta l'impronta con l'amministratore CyberArk, poi «Fidati anche di questo» (altro server PSM) o «Sostituisci i certificati noti» (rinnovo). |
| Sessione PSM: «Impossibile verificare l'identità di …» | ZillaTerm non è riuscito a leggere il certificato del PSM prima della connessione (PSM irraggiungibile, porta filtrata, risposta che non è Desktop remoto): il motivo segue il messaggio. Nulla è stato inviato al PSM. |
| Una sessione PSM si apre in Connessione Desktop remoto nonostante l'opzione «in una scheda» | Connessione tramite un gateway Desktop remoto, oppure client FreeRDP impossibile da avviare sul computer (criteri del computer): la barra di stato lo dice. |
| Alt+Tab o il tasto Windows non rispondono più | La scheda di una sessione PSM ha la tastiera: tutti i tasti vanno alla sessione. Fai clic altrove in ZillaTerm (un'altra scheda, l'elenco). |
| Sessione PSM in una scheda: Ctrl+V di un file o di un testo copiato sul computer non fa nulla | Il PVWA forse non consente gli appunti, o il PSM consente solo il testo. Impostazioni → Registro di debug, riproduci (copia, clic nella sessione, incolla), poi «Mostra il file del registro»: le righe `freerdp` riportano i reindirizzamenti richiesti dal PVWA (`presse-papiers oui/non`), ogni modifica degli appunti del computer (programma, formati: file, testo; mai il contenuto) e gli scambi degli appunti con il PSM (`cliprdr`: `CB_STREAM_FILECLIP_ENABLED` assente dai flag del server significa nessuna copia di file). |
| Sessione PSM: «Verifica la chiave di questo PSM» | Il PSM accetta solo la vecchia «sicurezza RDP», senza TLS: solo l'impronta della sua chiave lo identifica. Accettala al primo utilizzo (viene memorizzata) e chiedi all'amministratore CyberArk di attivare TLS sul PSM. |
| Sessione PSM: «Nuova chiave per questo PSM» | L'indirizzo del PSM raggruppa probabilmente più server (bilanciamento del carico), ciascuno con la propria chiave: fidati se l'amministratore CyberArk lo conferma. Le chiavi già accettate restano tali. |
| Sessione PSM: «Questo server non offre più TLS» | La connessione potrebbe essere intercettata: continua solo con l'accordo dell'amministratore CyberArk (vedi il messaggio). |
| Sessione PSM: «Il server Desktop remoto non usa alcuna cifratura…» | Il PSM (o un apparato intermedio) propone la vecchia sicurezza RDP senza cifratura: nulla proteggerebbe il token. Avvisa l'amministratore CyberArk. |
| Una scheda di desktop remoto diretto (KeePass) mostra «Errore del controllo Desktop remoto» | Segnala il codice mostrato (se il controllo Desktop remoto manca dal computer, la connessione passa da `mstsc`). |
| VNC: «Il server VNC non propone alcuna autenticazione supportata da ZillaTerm…» | Il server richiede un'autenticazione propria del suo produttore (account Windows, cifratura VeNCrypt…): attiva l'autenticazione «password VNC» sul server. |
| VNC: «Nessuna risposta VNC dal server entro 30 secondi» | Porta sbagliata (5900 + numero dello schermo) o servizio diverso da VNC a questo indirizzo. |
| FTP: «Il server FTP non propone la cifratura (TLS), richiesta da questa voce» | Il server non accetta TLS: usa `ftp://` (connessione in chiaro dopo conferma) o SFTP se disponibile. |
| FTP: avviso «Questo server FTP non propone più la cifratura» | Il server cifrava le connessioni e non lo fa più: prosegui solo se il suo amministratore conferma la rimozione di TLS; altrimenti la connessione potrebbe essere intercettata. |
| Desktop remoto diretto: «Il server Desktop remoto non accetta la cifratura TLS…» | Il server accetta solo la vecchia «sicurezza RDP», che non permette di verificarne l'identità: chiedi al suo amministratore di attivare TLS o NLA. |
| FTPS: l'elenco dei file non appare o un trasferimento scade | Un firewall blocca le porte passive del server, o il server esige la ripresa della sessione TLS sulle connessioni dati (`522`, per esempio `require_ssl_reuse` di vsftpd): rivolgiti all'amministratore del server. |
