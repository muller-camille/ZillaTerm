<img src="docs/icone.png" alt="" width="72" align="right">

# ZillaTerm

[Français](README.fr.md) · [English](README.md) · **Italiano**

[![build](https://github.com/muller-camille/ZillaTerm/actions/workflows/build.yml/badge.svg)](https://github.com/muller-camille/ZillaTerm/actions/workflows/build.yml)
[![release](https://github.com/muller-camille/ZillaTerm/actions/workflows/release.yml/badge.svg)](https://github.com/muller-camille/ZillaTerm/releases/latest)

**Client Windows multi-sessione per CyberArk.** ZillaTerm accede al tuo PVWA, elenca gli account a cui hai accesso
e apre le sessioni con un doppio clic: desktop remoto tramite **PSM**, oppure un terminale SSH tramite **PSM for SSH
(PSMP)** con un **browser dei file** integrato per inviare file al server.

> Prima **CyberArkTerm**: le impostazioni vengono riprese automaticamente al primo avvio
> ([dettagli](docs/guide.it.md#passaggio-da-cyberarkterm-a-zillaterm)).

![Sessione SSH tramite il PSMP, con la scheda File che segue la cartella del terminale](docs/captures/it/main-window.png)

**[Scarica](https://github.com/muller-camille/ZillaTerm/releases/latest)** ·
**[Guida all'uso](docs/guide.it.md)** · [Note di versione](https://github.com/muller-camille/ZillaTerm/releases) ·
[Politica di sicurezza](SECURITY.md)

> Le schermate provengono da un ambiente dimostrativo (dati fittizi).

## Indice

- [Funzionalità](#funzionalità)
- [Installazione](#installazione)
- [Primi passi](#primi-passi)
- [Sicurezza](#sicurezza)
- [Sviluppo](#sviluppo)
- [Limiti e sviluppi futuri](#limiti-e-sviluppi-futuri)
- [Licenza](#licenza)

## Funzionalità

| | |
| --- | --- |
| **Accesso CyberArk** | Autenticazione CyberArk, LDAP, RADIUS (challenge / OTP compresi) o Windows (sessione corrente). |
| **Disponibili** | Tutti gli account visibili nel vault CyberArk, raggruppati per safe, piattaforma o tipo di destinazione, con ricerca istantanea; password (CPM, copia), membri di un safe, aggiunta, modifica e importazione di account. |
| **I miei server** | I tuoi server di lavoro, organizzati in cartelle e sottocartelle, ognuno con le proprie impostazioni e un'**etichetta** di ambiente (PROD, QA, DEV…) il cui colore contrassegna la scheda e la sessione; esportazione, importazione ed **elenchi condivisi** su una condivisione di rete (ognuno aggiunge o rimuove, cronologia delle modifiche e delle versioni). |
| **Passaggio da un altro programma** | Le sessioni di PuTTY, KiTTY, WinSCP, mRemoteNG, RDCMan, SecureCRT, OpenSSH, di file `.rdp` o `.mxtsessions` riportate in «I miei server» con le stesse cartelle, ognuna associata al suo account nel PVWA (sessioni già tramite PSM o il PSMP decodificate) e aperta tramite PSM o il PSMP, mai direttamente; risultato per server esportabile in CSV, nessuna password letta. |
| **Ambiente condiviso** | La configurazione del team (PVWA, PSMP per dominio, componenti PSM, elenchi condivisi, chiavi dei PSMP) in un file `ZillaTerm.env.json`: accanto all'eseguibile, importato o centrale su una condivisione; ogni modifica viene mostrata e confermata, niente di personale né alcuna password. |
| **Sessioni PSM** | Desktop remoto tramite il PSM (come il pulsante «Connect» del PVWA), in Connessione Desktop remoto di Windows o, come opzione, in una scheda di ZillaTerm con il client FreeRDP integrato (desktop Windows o applicazione remota alla dimensione della scheda, certificato del PSM verificato da ZillaTerm, senza l'avviso dei file .rdp): componente, macchina di destinazione (chiesta per un account di dominio, e mantenuta in «I miei server» se lo desideri), motivo, ticket. |
| **Sessioni SSH (PSMP)** | Terminale integrato in una scheda (compatibile xterm: colori, vim, less, top…), MFA, menu del clic destro, ricerca, finestre separate, «Riconnetti» alla fine della sessione, un PSMP per dominio dei server. Solo file (SFTP, senza terminale) per le piattaforme «SFTP» o su richiesta. |
| **Scheda File** | Browser SFTP del server: invio (SFTP, o SCP, con l'altro che subentra se il server rifiuta) e download con il trascinamento, verifica SHA-256 di ogni file, coda (risultati conservati) e cronologia dei trasferimenti, ordinamento per colonna, rinomina, modifica nel tuo editor di testo, permessi, monitoraggio in tempo reale (`tail -f`), confronto, invio a più server. |
| **Vista parallela** | Fino a 8 sessioni SSH affiancate (una cartella di «I miei server» si apre con un clic), digitazione simultanea opzionale. |
| **Accesso di emergenza (KeePass)** | Senza CyberArk: database KeePass (.kdbx) in «I miei server», connessioni SSH, desktop remoto e VNC dirette, file in SFTP, FTP o FTPS nella scheda File, registro locale. |
| **Tastiera e accessibilità** | Scorciatoie per le schede, il pannello laterale e la connessione rapida, conferme con pulsanti espliciti, utilità per la lettura dello schermo, contrasto elevato. |
| **Lingue** | Italiano, francese e inglese: lingua di Windows per impostazione predefinita, modificabile in qualsiasi momento. |

<table>
<tr>
<td width="50%"><img src="docs/captures/it/available.png" alt="Scheda Disponibili"><br><sub>«Disponibili»: tutti gli account del vault CyberArk, ricerca istantanea</sub></td>
<td width="50%"><img src="docs/captures/it/my-servers.png" alt="Scheda «I miei server»"><br><sub>«I miei server»: i tuoi server in cartelle, database KeePass in cima</sub></td>
</tr>
<tr>
<td><img src="docs/captures/it/terminal-menu.png" alt="Menu del clic destro nel terminale"><br><sub>Clic destro nel terminale: copia, incolla, cerca, azioni della scheda</sub></td>
<td><img src="docs/captures/it/keepass-vault.png" alt="Accesso di emergenza con KeePass"><br><sub>Accesso di emergenza: SSH diretto da un database KeePass</sub></td>
</tr>
</table>

## Installazione

### Scaricare l'eseguibile

1. Apri l'[ultima versione](https://github.com/muller-camille/ZillaTerm/releases/latest) nelle
   *Releases* del repository.
2. Scarica **`ZillaTerm-<versione>-win-x64.zip`** ed estrailo (l'impronta SHA256 è in `SHA256SUMS.txt`).
3. Avvia `ZillaTerm.exe`: un solo file, nessun runtime da installare, nessun diritto di amministratore
   richiesto.

Versioni di sviluppo: l'eseguibile di ogni compilazione è disponibile anche come artefatto
`ZillaTerm-win-x64` nella scheda [Actions](https://github.com/muller-camille/ZillaTerm/actions/workflows/build.yml).

L'eseguibile non è firmato: al primo avvio Windows SmartScreen può mostrare un avviso
(«Ulteriori informazioni» → «Esegui comunque»).

### Aggiornare

Pulsante Impostazioni → **«Informazioni su ZillaTerm…»**: versione, link del progetto, cartella delle impostazioni e
«Cerca ora». Se esiste una versione più recente, «Scarica e verifica» salva l'archivio nella cartella Download e lo
confronta con `SHA256SUMS.txt` della stessa versione (conservato solo se identico). Nulla viene installato
automaticamente: chiudi ZillaTerm e sostituisci l'eseguibile; le impostazioni vengono conservate. L'opzione «Cerca
una nuova versione all'avvio» (Impostazioni › Generale, disattivata per impostazione predefinita) fa questa ricerca al
massimo una volta al giorno e mostra un link nella barra di stato.

**Da CyberArkTerm**: scarica ZillaTerm una volta dalla pagina delle versioni (CyberArkTerm lo segnala ma non può
scaricarlo da solo); le impostazioni vengono riprese al primo avvio, poi puoi eliminare `CyberArkTerm.exe`.

### Requisiti

**Postazione di lavoro**

- Windows 10 o 11 (x64).
- Il client Desktop remoto di Windows (presente di default): Connessione Desktop remoto (`mstsc`) per le sessioni
  PSM, il suo controllo integrato per il desktop remoto diretto dei database KeePass. Le sessioni PSM in una scheda
  (opzione) usano il client FreeRDP incluso in ZillaTerm.
- Facoltativo: Windows Terminal e il «Client OpenSSH» di Windows, solo se scegli di aprire l'SSH fuori da
  ZillaTerm.

**Lato CyberArk**

- PVWA **v10 o superiore** (API REST `/PasswordVault/API/...`), raggiungibile in **HTTPS** con un
  certificato considerato attendibile dalla postazione.
- Permesso **List accounts** sui safe interessati: l'applicazione mostra solo ciò che l'API ti consente di
  vedere.
- PSM configurato sulle piattaforme da usare (componenti `PSM-RDP`, `PSM-SSH`…).
- Per l'SSH: un **PSM for SSH (PSMP)**, con SFTP consentito per la scheda File (e SCP se scegli l'invio in SCP).
- Facoltativo: **MFA caching** attivato sul PVWA, per non reinserire password e MFA sul PSMP.

## Primi passi

1. **Accedere**: indirizzo del PVWA (basta `pvwa.miodominio.local`), metodo di autenticazione, utente e password.
   Indirizzo, metodo e utente vengono memorizzati; la password mai. Per riprendere la configurazione del team:
   «Importa un ambiente…» nella stessa finestra, oppure un file `ZillaTerm.env.json` accanto all'eseguibile.
2. **Trovare un account** nella scheda «Disponibili»: la ricerca riguarda tutti i campi (`prd sql`). Clic destro su un
   account per la sua password (verifica, cambia, riconcilia, copia), i membri del suo safe, o per aggiungere,
   modificare e importare account.
3. **Connettersi** con un doppio clic: un account Windows apre una sessione PSM in Connessione Desktop remoto (un
   account di dominio chiede prima il server); un account Unix apre un terminale SSH in una scheda, tramite il PSMP
   del suo dominio impostato nelle **Impostazioni**. Clic destro nel
   terminale per copiare, incollare, cercare e le azioni della scheda.
4. **Scheda File** (accanto a una sessione SSH): sfoglia il server, trascina i file da Esplora file per inviarli, verso
   Esplora file per scaricarli. Ogni file viene verificato (SHA-256); il pulsante **Trasferimenti** della barra degli
   strumenti conserva ogni trasferimento e i suoi checksum. Un clic sull'intestazione di una colonna ordina l'elenco.
5. **I miei server**: organizza i server di lavoro in cartelle, ognuno con le sue impostazioni di connessione (PSM o
   SSH, componente, macchina di destinazione, cartella iniziale); apri un'intera cartella nella **vista parallela**.
   Le tue sessioni PuTTY, WinSCP, mRemoteNG… si importano con un clic, ognuna con il suo account nel PVWA.
6. **Accesso di emergenza**: quando CyberArk non è disponibile, «Accesso di emergenza (KeePass)» nella finestra di
   accesso apre i tuoi database KeePass e si connette direttamente in SSH, desktop remoto o VNC, o ai file in SFTP, FTP
   o FTPS (senza registrazione del PSM, annotato in un registro su questo computer).

La **[guida all'uso](docs/guide.it.md)** descrive ogni scheda in dettaglio, le
[scorciatoie](docs/guide.it.md#scorciatoie), le
[impostazioni e il file di configurazione](docs/guide.it.md#impostazioni-e-file-di-configurazione), il
[funzionamento tecnico](docs/guide.it.md#funzionamento-tecnico) e la
[risoluzione dei problemi](docs/guide.it.md#risoluzione-dei-problemi).

## Sicurezza

- **HTTPS obbligatorio** verso il PVWA; la validazione del certificato non viene mai disattivata.
- **Nessun segreto su disco**: password CyberArk, token di sessione, chiave MFA e password PSMP restano in memoria; la
  sessione PVWA viene chiusa all'uscita. Il file di configurazione non contiene password, token né chiavi private.
- **Sessioni CyberArk standard**: le sessioni PSM e PSMP aperte da ZillaTerm sono registrate e verificate dal PSM
  come quelle aperte dal PVWA.
- **Password copiate**: direttamente negli appunti di Windows, escluse dalla loro cronologia e sincronizzazione,
  cancellate dopo 20 s; mai mostrate né registrate.
- **Chiavi host del PSMP** memorizzate alla prima connessione, con un avviso se cambiano (lo stesso per i server SSH e
  i certificati FTPS delle voci KeePass).
- **Protocolli non cifrati** (VNC, FTP senza TLS) segnalati da un banner permanente; FTP passa in chiaro solo dopo il
  tuo consenso.
- **Database KeePass**: la password principale non viene mai salvata, tranne nel vault locale se lo chiedi (Argon2id,
  AES-256-GCM, protetto dal tuo account Windows); ogni apertura e connessione viene annotata in un registro locale.
- **Importazione da un altro programma**: vengono letti solo il server, la porta, il protocollo, l'utente e la
  cartella, mai una password; ogni server importato passa da PSM o dal PSMP con un account del PVWA.
- **Nessuna richiesta verso Internet** senza una tua azione o l'opzione di aggiornamento (disattivata per
  impostazione predefinita); un aggiornamento scaricato viene conservato solo se il suo SHA-256 corrisponde a
  `SHA256SUMS.txt`, e nulla viene installato automaticamente.
- **Registro di debug** disattivato per impostazione predefinita; non contiene mai password, token né il contenuto
  delle sessioni.

Tutti i dettagli: [guida all'uso → Sicurezza](docs/guide.it.md#sicurezza). Per segnalare una vulnerabilità, vedi
[SECURITY.md](SECURITY.md) (segnalazione privata, nessuna segnalazione pubblica).

## Sviluppo

### Struttura

| Progetto | Ruolo |
| --- | --- |
| `src/ZillaTerm.Core` | Logica senza interfaccia, multipiattaforma: client dell'API PVWA, classificazione degli account, emulatore di terminale xterm, connessioni PSMP e browser SFTP/SCP (SSH.NET), browser FTP/FTPS (FluentFTP), client VNC, cartelle di «I miei server», database KeePass (KDBX), vault locale, preferenze. |
| `src/ZillaTerm.App` | Applicazione WPF: finestre, schede, controllo terminale, controllo Desktop remoto (schede RDP), avvio di `mstsc`, icona (`Assets`). |
| `tests/ZillaTerm.Core.Tests` | Test xUnit di Core (falso PVWA HTTP, terminale, PSMP, cartelle, traduzioni…). |
| `tests/ZillaTerm.App.Tests` | Test Windows dell'applicazione (vero controllo Desktop remoto, DPAPI). |

Dipendenze esterne: [SSH.NET](https://github.com/sshnet/SSH.NET), [FluentFTP](https://github.com/robinrodricks/FluentFTP)
e [Konscious.Security.Cryptography](https://github.com/kmaragon/Konscious.Security.Cryptography) (Argon2), tutte
con licenza MIT. SSH.NET è la versione 2026.0.0 più due correzioni, una che conserva i nomi del proprietario e del
gruppo negli elenchi SFTP e una che aggiunge l'inoltro X11 delle shell, compilata da
[un fork](https://github.com/muller-camille/SSH.NET) a un commit fisso da `tools/sshnet-patched.sh`, in attesa di una
versione di SSH.NET che le includa.

### Traduzioni

I testi dell'interfaccia si trovano in `src/ZillaTerm.Core/Localization/CoreStrings*.resx` e
`src/ZillaTerm.App/Localization/Strings*.resx`: inglese nel file neutro, poi `.fr` e `.it`.
Le classi `*.Designer.cs` sono generate da Visual Studio (`PublicResXFileCodeGenerator`); un test verifica che
ogni lingua abbia tutte le chiavi, gli stessi parametri `{0}` e gli stessi tasti di scelta `_`.
Per aggiungere una lingua: copia i file `.resx` con il nuovo codice (`.de.resx`…), traducili, poi aggiungi il
codice a `UiLanguage.Supported`.

### Compilare e testare

Con l'[SDK .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) e Git (Git Bash su Windows); il primo comando
compila il pacchetto SSH.NET con la correzione in `local-packages` (basta una volta):

```powershell
bash tools/sshnet-patched.sh
dotnet test ZillaTerm.sln
dotnet run --project src/ZillaTerm.App
```

Il progetto si compila anche su Linux o macOS (`EnableWindowsTargeting`); l'applicazione funziona solo su
Windows. I test di `tests/ZillaTerm.App.Tests` (tra cui un test del vero controllo Desktop remoto) si
eseguono solo su Windows; altrove, esegui `dotnet test tests/ZillaTerm.Core.Tests`.

### Pubblicare l'eseguibile

```powershell
# Autonomo (~65 MB): niente da installare sulla postazione
dotnet publish src/ZillaTerm.App -c Release -r win-x64 -p:SelfContained=true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish

# Leggero: richiede il «.NET Desktop Runtime 10» sulla postazione
dotnet publish src/ZillaTerm.App -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -o publish
```

La CI ([`.github/workflows/build.yml`](.github/workflows/build.yml)) esegue i test e pubblica l'eseguibile
autonomo come artefatto `ZillaTerm-win-x64` per ogni pull request e ogni push su `main`.

### Pubblicare una versione

Da GitHub: **Actions → release → Run workflow** su `main`, indicando il numero `X.Y.Z`. Il workflow
[`release.yml`](.github/workflows/release.yml) esegue i test e compila l'eseguibile con quel numero di versione (con
un token in sola lettura), poi un secondo job, che non compila nulla, crea il tag se non esiste e pubblica la
*Release* GitHub con lo zip e `SHA256SUMS.txt`. Le note di versione vengono lette da `docs/releases/vX.Y.Z.md` se il
file esiste. «Verificare senza pubblicare» esegue tutta la catena senza pubblicare nulla, da qualsiasi branch.

## Limiti e sviluppi futuri

**Limiti attuali**

- **Privilege Cloud** (accesso tramite CyberArk Identity) e **SAML** non sono supportati.
- L'API Accounts non indica quali componenti PSM offre una piattaforma: il componente viene dedotto dalla piattaforma,
  poi si può impostare per piattaforma nelle Impostazioni.
- Database KeePass: cifratura Twofish e chiavi YubiKey non supportate; nessuna creazione di database (crealo con
  KeePass o KeePassXC); allegati conservati ma non mostrati.
- Il monitoraggio della cartella del terminale richiede bash, zsh o tcsh sul server.
- VNC: solo password VNC o nessuna autenticazione (né autenticazione propria di un produttore, né cifratura). FTPS:
  un server che esige la ripresa della sessione TLS sulle connessioni dati può rifiutare i trasferimenti.
- PSM Gateway (HTML5), doppio controllo (dual control) e accesso esclusivo non sono supportati.

**Sviluppi futuri**

- Eseguibile firmato e programma di installazione MSI.
- Più PVWA (profili di connessione), Privilege Cloud.
- Altre idee, tenute per dopo: vedi [IDEAS.md](IDEAS.md).

## Licenza

[MIT](LICENSE) © 2026 muller-camille
