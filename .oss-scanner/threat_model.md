# Threat model — ZillaTerm

## What this project does

ZillaTerm is a Windows desktop client (C#, .NET 10, WPF) for CyberArk Privileged Access Security. The user signs in
to a CyberArk PVWA (REST API over HTTPS) with CyberArk, LDAP, RADIUS or Windows authentication, browses the accounts
they may use, and opens sessions **only through CyberArk's proxies**: PSM (an `.rdp` file returned by the PVWA,
opened in Remote Desktop Connection) and PSMP (SSH and SFTP through SSH.NET, with the PVWA's "MFA caching" SSH key).

Around that: a built-in terminal emulator (xterm subset), a Files tab (SFTP/SCP through the PSMP, transfer queue with
SHA-256 checks, `tail -f` viewer, edit-in-editor, chmod), KeePass `.kdbx` 3.1/4 read/write with a local encrypted
vault for master passwords, "emergency access" direct SSH/RDP/VNC/FTP/FTPS/SFTP connections from KeePass entries
(when CyberArk is unavailable), import of saved sessions from other tools (PuTTY/KiTTY registry and `.reg`, WinSCP,
mRemoteNG, RDCMan, SecureCRT, OpenSSH config, `.rdp` and `.mxtsessions` files; never their passwords), CSV import of
accounts into CyberArk, shared server lists and team environment files on network shares, and an update check
against GitHub releases (download checked against the `SHA256SUMS.txt` of the same release: integrity of the
download only, not who published it).

## Where untrusted input enters (most exposed first)

1. **Remote servers** reached through the PSMP (and directly, for KeePass emergency access). Everything they send
   is untrusted:
   - terminal output: escape sequences parsed by `src/ZillaTerm.Core/Terminal/TerminalEmulator.cs`, including
     replies the emulator sends back (DA, DSR), OSC 7 (current folder) and the private OSC 6973 erase marker;
   - SFTP/SCP/FTP directory listings, file names, symlinks and file contents (`src/ZillaTerm.Core/Ssh/`,
     `src/ZillaTerm.Core/Ftp/`), used to build local paths for downloads and drag-and-drop to Explorer; the owner and
     group names of SFTP listings, parsed from the server's free-form `ls -l` style "longname"
     (`Ssh/SftpLongName.cs`) and shown in the Files tab;
   - the RFB stream of VNC servers (`src/ZillaTerm.Core/Vnc/RfbClient.cs`).
2. **The PVWA's JSON responses** (`src/ZillaTerm.Core/PvwaClient.cs`): account addresses, user names, platform and
   component names, remote machines and safe names end up in RDP/SSH parameters, PSMP login strings, file names and
   command lines.
3. **Files that other people can write**: shared server lists and environment files on network shares
   (`Sessions/SharedServerList.cs`, `EnvironmentProfile.cs`), KeePass databases (`KeePass/`), session files imported
   from other tools (`Migration/`), account CSV files (`AccountCsv.cs`), `.rdp` files.
4. **Commands ZillaTerm sends to remote shells** that embed remote or user-supplied values: the folder-tracking
   command (`Terminal/WorkingDirectory.cs`), `cd` to a start folder, archive extraction commands
   (`Ssh/TarGzPacker.cs`), `tail -f`, chmod.
5. **GitHub release metadata** for the update check (`UpdateChecker.cs`).

## What must hold (security goals)

- CyberArk secrets (password, RADIUS answers, PVWA session token, MFA caching SSH private key) and KeePass master
  passwords never reach disk, logs, settings, the clipboard history or any host other than the configured PVWA and
  PSMP. The debug log (off by default) must mask them.
- Nothing received from a remote server, the PVWA or a shared file can run code on the user's workstation, start a
  process with attacker-chosen arguments (Remote Desktop Connection, ssh, the text editor, the compare tool), or write
  outside the folder the user chose.
- CyberArk accounts are only ever reached through PSM or the PSMP, never directly, including for imported sessions.
- PSMP and direct SSH host keys are verified; a shared environment file can only add host keys after the user has
  seen and confirmed them.
- Transfers are integrity-checked (SHA-256) and a mismatch is never reported as success.
- The local vault (`KeePass/LocalSecretStore.cs`: AES-256-GCM, Argon2id, then DPAPI on Windows) and `.kdbx` handling are
  cryptographically sound: correct KDFs, authenticated formats checked before use, no downgrade.
- The intervention journal (`Interventions/InterventionJournal.cs`: terminal text, connections, transfers, captures)
  is only readable by the user's Windows account (DPAPI, block by block); a block removed, moved or changed is
  reported, and the exported HTML report cannot run script (CSP, every text encoded, images checked as PNG).
- The on-call duty reminder (settings text or `DutyReminderFile.cs`, a text file often on a network share) is shown as
  plain text only; invisible characters that could change what a phone number looks like are removed, and the path
  can never name a device or a named pipe.

## Components that matter most

- `src/ZillaTerm.Core/Terminal/` (emulator, folder-tracking injection), `src/ZillaTerm.Core/Ssh/` (paths, transfers,
  remote commands), `src/ZillaTerm.Core/KeePass/`, `src/ZillaTerm.Core/Migration/`, `PvwaClient.cs`,
  `EnvironmentProfile.cs`, `DutyReminderFile.cs`, `Sessions/`, `Vnc/`, `Ftp/`, `Interventions/`.
- `src/ZillaTerm.App/`: where external processes are started (`Services/SessionLauncher.cs` for Remote Desktop
  Connection and ssh, `Services/RemoteEditor.cs` for the text editor, `Views/CompareWindow.xaml.cs` for the compare
  tool), the clipboard, drag-and-drop to Explorer, and the session tabs. It compiles in this image but only runs on
  Windows.

## Less important / out of scope

- `tests/`, `docs/`, `tools/`.
- Bugs inside third-party packages (SSH.NET, FluentFTP, Argon2) unless ZillaTerm uses them unsafely. The two patches
  ZillaTerm carries on SSH.NET (keeping the SFTP "longname"; X11 forwarding with the fake cookie checked and replaced
  before anything reaches the local X server; built by the release workflow of the fork, fetched by
  `tools/sshnet-patched.sh`) are in scope.
- The CyberArk servers themselves (PVWA, PSM, PSMP) are trusted for authentication; a malicious PVWA is in scope
  only for what it can do to the workstation beyond what CyberArk already lets it do.
- Attacks that need the local Windows session to be already compromised (malware running as the user can read the
  process memory and DPAPI-protected data), or physical access to an unlocked workstation.

## How to exercise it

- `dotnet test tests/ZillaTerm.Core.Tests -c Debug --no-build` runs 800+ unit tests offline in this image.
- `src/ZillaTerm.Core` is plain .NET: write a small console project with a `ProjectReference` to
  `/src/src/ZillaTerm.Core/ZillaTerm.Core.csproj` (works offline here) and feed it bytes, for example
  `new TerminalEmulator(80, 24).Feed(...)`, the KeePass readers with a crafted `.kdbx`, or the session-file readers
  in `Migration/` with a crafted file.
- The best reproducer is a failing xunit test added to `tests/ZillaTerm.Core.Tests`.
- `tests/ZillaTerm.App.Tests` (WPF) only runs on Windows; it is built here but cannot be run.

## How we rate severity

- **Critical**: code execution on the user's workstation, or disclosure of CyberArk credentials, the PVWA token or
  the MFA caching key to a third party, triggered by a remote server, the PVWA or a file someone else can write.
- **High**: host-key verification bypass; writing or overwriting files outside the destination folder (path
  traversal in downloads, drag-and-drop or archive handling); injecting commands into the remote shell beyond what
  the user typed or chose; secrets written to disk or logs in clear; a CyberArk account connected directly instead of
  through PSM/PSMP; a KeePass or local-vault weakness that lets an attacker with the file recover secrets faster than
  the KDF allows.
- **Medium**: terminal sequences that spoof what the user sees or make the emulator send input to the shell; a
  transfer integrity check that can be bypassed without the user noticing; denial of service of the application from
  remote output or a crafted file (hang, crash, unbounded memory).
- **Low**: issues that need an already-compromised local account, or a malformed settings file the user wrote.

## How we would like reports

- The file and function, a minimal reproducer (ideally an xunit test), the impact in the terms above, and a patch.
  English or French.

## Anything to leave alone

- Folder tracking deliberately types a short command into the remote shell when a session opens (documented; the
  PSM recording shows it). ZillaTerm erases its echo itself when the OSC 6973 marker arrives, and only honours that
  marker for 3 seconds right after sending the command.
- The emulator answers DA and DSR queries (`ESC [ c`, `ESC [ > c`, `ESC [ 5 n`, `ESC [ 6 n`) like other terminals;
  report it only if a reply can carry attacker-chosen bytes.
