<img src="docs/icone.png" alt="" width="72" align="right">

# ZillaTerm

[Français](README.fr.md) · **English** · [Italiano](README.it.md)

[![build](https://github.com/muller-camille/ZillaTerm/actions/workflows/build.yml/badge.svg)](https://github.com/muller-camille/ZillaTerm/actions/workflows/build.yml)
[![release](https://github.com/muller-camille/ZillaTerm/actions/workflows/release.yml/badge.svg)](https://github.com/muller-camille/ZillaTerm/releases/latest)

**Multi-session Windows client for CyberArk.** ZillaTerm signs in to your PVWA, lists the accounts you can access
and opens your sessions with a double-click: remote desktop through **PSM**, or an SSH terminal through **PSM for SSH
(PSMP)** with a built-in **file browser** to upload files to the server.

> Formerly **CyberArkTerm**: your settings are carried over automatically on first start
> ([details](docs/guide.md#moving-from-cyberarkterm-to-zillaterm)).

![SSH session through the PSMP, with the Files tab following the terminal folder](docs/captures/en/main-window.png)

**[Download](https://github.com/muller-camille/ZillaTerm/releases/latest)** ·
**[User guide](docs/guide.md)** · [Release notes](https://github.com/muller-camille/ZillaTerm/releases) ·
[Security policy](SECURITY.md)

> Screenshots come from a demo environment (fictitious data).

## Contents

- [Features](#features)
- [Installation](#installation)
- [Getting started](#getting-started)
- [Security](#security)
- [Development](#development)
- [Limitations and ideas](#limitations-and-ideas)
- [License](#license)

## Features

| | |
| --- | --- |
| **CyberArk sign-in** | CyberArk, LDAP, RADIUS (challenge / OTP included) or Windows (current session) authentication. |
| **Available** | Every account visible in the CyberArk Vault, grouped by safe, platform or target type, with instant search; password actions (CPM, copy), safe members, adding, editing and importing accounts. |
| **My servers** | Your working servers, organized in folders and subfolders, each with its own settings and an environment **tag** (PROD, QA, DEV…) whose color marks the tab and the session; export, import and **shared lists** on a network share (everyone adds or removes, history of changes and versions). |
| **Moving from another tool** | The sessions of PuTTY, KiTTY, WinSCP, mRemoteNG, RDCMan, SecureCRT, OpenSSH, `.rdp` or `.mxtsessions` files brought into "My servers" with the same folders, each matched with its PVWA account (sessions already through PSM or the PSMP are decoded) and opened through PSM or the PSMP, never directly; per-server result exportable as CSV, no password read. |
| **Shared environment** | The team configuration (PVWA, PSMPs by domain, PSM components, shared lists, PSMP keys) in a `ZillaTerm.env.json` file: next to the executable, imported, or central on a share; every change is shown and confirmed, nothing personal and no password. |
| **PSM sessions** | Remote desktop through the PSM (like the PVWA "Connect" button), in Windows Remote Desktop Connection or, as an option, in a ZillaTerm tab (Windows desktop at the size of the tab, without the .rdp file warning): component, target machine (asked for a domain account, and kept in "My servers" if you wish), reason, ticket. |
| **SSH sessions (PSMP)** | Built-in terminal in a tab (xterm compatible: colours, vim, less, top…), MFA, right-click menu, search, separate windows, "Reconnect" at the end of a session, one PSMP per server domain. Files only (SFTP, without a terminal) for "SFTP" platforms or on request. |
| **Files tab** | SFTP browser of the server: drag-and-drop upload (SFTP, or SCP, the other one taking over if the server refuses) and download, SHA-256 check of every file, transfer queue (results kept) and history, sortable columns, renaming, editing in your text editor, permissions, live following (`tail -f`), comparison, sending to several servers. |
| **Parallel view** | Up to 8 SSH sessions side by side (a "My servers" folder opens in one click), optional simultaneous typing. |
| **Emergency access (KeePass)** | Without CyberArk: KeePass databases (.kdbx) in "My servers", direct SSH, remote desktop and VNC connections, files over SFTP, FTP or FTPS in the Files tab, local log. |
| **Keyboard and accessibility** | Shortcuts for the tabs, the side panel and quick connect, confirmations with explicit buttons, screen readers, high contrast. |
| **Languages** | English, French and Italian: Windows language by default, can be changed at any time. |

<table>
<tr>
<td width="50%"><img src="docs/captures/en/available.png" alt="Available tab"><br><sub>"Available": every account of the CyberArk Vault, instant search</sub></td>
<td width="50%"><img src="docs/captures/en/my-servers.png" alt="My servers tab"><br><sub>"My servers": your servers in folders, KeePass databases on top</sub></td>
</tr>
<tr>
<td><img src="docs/captures/en/terminal-menu.png" alt="Right-click menu of the terminal"><br><sub>Right-click in the terminal: copy, paste, search, tab actions</sub></td>
<td><img src="docs/captures/en/keepass-vault.png" alt="Emergency access with KeePass"><br><sub>Emergency access: direct SSH from a KeePass database</sub></td>
</tr>
</table>

## Installation

### Download the executable

1. Open the [latest release](https://github.com/muller-camille/ZillaTerm/releases/latest) in the
   repository's *Releases*.
2. Download **`ZillaTerm-<version>-win-x64.zip`** and unzip it (the SHA256 hash is in
   `SHA256SUMS.txt`).
3. Run `ZillaTerm.exe`: a single file, no runtime to install, no administrator rights needed.

Development builds: the executable of every build is also available as the `ZillaTerm-win-x64`
artifact in the [Actions](https://github.com/muller-camille/ZillaTerm/actions/workflows/build.yml) tab.

The executable is not signed: on first launch, Windows SmartScreen may show a warning
("More info" → "Run anyway").

### Update

Settings button → **"About ZillaTerm…"**: version, project links, settings folder, and "Check now". When a newer
version exists, "Download and check" saves the archive to the Downloads folder, then compares it with `SHA256SUMS.txt`
of the same version (kept only when identical). Nothing is installed automatically: close ZillaTerm and replace
the executable; your settings are kept. The option "Look for a new version at startup" (Settings › General, off by
default) does this check at most once a day and shows a link in the status bar.

**From CyberArkTerm**: download ZillaTerm once from the releases page (CyberArkTerm reports it but cannot download it
itself); your settings are carried over on first start, then you can delete `CyberArkTerm.exe`.

### Requirements

**Workstation**

- Windows 10 or 11 (x64).
- The Windows Remote Desktop client (installed by default): Remote Desktop Connection (`mstsc`) for PSM sessions,
  its built-in control for PSM sessions in a tab (option) and direct remote desktop from KeePass databases.
- Optional: Windows Terminal and the Windows "OpenSSH Client", only if you choose to open SSH outside
  ZillaTerm.

**CyberArk side**

- PVWA **v10 or later** (REST API `/PasswordVault/API/...`), reachable over **HTTPS** with a certificate
  trusted by the workstation.
- **List accounts** permission on the relevant safes: the application only shows what the API lets you see.
- PSM configured on the platforms you use (`PSM-RDP`, `PSM-SSH`… components).
- For SSH: a **PSM for SSH (PSMP)**, with SFTP allowed for the Files tab (and SCP if you choose SCP uploads).
- Optional: **MFA caching** enabled on the PVWA, so you do not type your password and MFA again at the PSMP.

## Getting started

1. **Sign in**: PVWA address (`pvwa.mydomain.local` is enough), authentication method, user name and password. The
   address, method and user name are remembered; the password never is. To get your team's configuration: "Import an
   environment…" on the same screen, or a `ZillaTerm.env.json` file next to the executable.
2. **Find an account** in the "Available" tab: the search box filters on every field (`prd sql`). Right-click an
   account for its password (verify, change, reconcile, copy), the members of its safe, or to add, edit and import
   accounts.
3. **Connect** with a double-click: a Windows account opens a PSM session in Remote Desktop Connection (a domain
   account first asks which server); a Unix account opens an SSH terminal in a tab, through the PSMP of its domain set
   in **Settings**. Right-click in the terminal for copy, paste,
   search and the tab actions.
4. **Files tab** (next to an SSH session): browse the server, drag files from Explorer to upload them, drag them to
   Explorer to download them. Each file is checked (SHA-256); the **Transfers** button of the toolbar keeps every
   transfer and its checksums. Click a column header to sort.
5. **My servers**: keep your working servers in folders, each with its connection settings (PSM or SSH, component,
   target machine, start folder); open a whole folder in the **parallel view**. Your PuTTY, WinSCP, mRemoteNG…
   sessions import in one click, each with its PVWA account.
6. **Emergency access**: when CyberArk is unavailable, "Emergency access (KeePass)" on the sign-in window opens your
   KeePass databases and connects directly over SSH, remote desktop or VNC, or to the files over SFTP, FTP or FTPS (not
   recorded by the PSM, logged on this computer).

The **[user guide](docs/guide.md)** describes every tab in detail, the
[shortcuts](docs/guide.md#shortcuts), the [settings and configuration file](docs/guide.md#settings-and-configuration-file),
[how it works](docs/guide.md#how-it-works) and [troubleshooting](docs/guide.md#troubleshooting).

## Security

- **HTTPS required** to the PVWA; certificate validation is never disabled.
- **No secret on disk**: CyberArk password, session token, MFA key and PSMP password stay in memory; the PVWA session
  is closed on exit. The configuration file holds no password, token or private key.
- **Standard CyberArk sessions**: PSM and PSMP sessions opened by ZillaTerm are recorded and audited by the PSM like
  the ones opened from the PVWA.
- **Copied passwords** go straight to the Windows clipboard, kept out of its history and synchronization, and are
  cleared after 20 s; they are never shown nor logged.
- **PSMP host keys** are pinned on first use, with a warning if they change (likewise for SSH servers and FTPS
  certificates of KeePass entries).
- **Unencrypted protocols** (VNC, FTP without TLS) are flagged by a permanent banner; FTP only goes to clear text
  after your agreement.
- **KeePass databases**: the master password is never saved, except in the local vault if you ask for it (Argon2id,
  AES-256-GCM, protected by your Windows account); every opening and connection is written to a local log.
- **Importing from another tool**: only the server, port, protocol, user and folder are read, never a password; each
  imported server goes through PSM or the PSMP with a PVWA account.
- **No request to the Internet** without your action or the update option (off by default); a downloaded update is
  kept only if its SHA-256 matches `SHA256SUMS.txt`, and nothing is installed automatically.
- **Debug log** off by default; it never contains a password, token or session content.

All the details: [user guide → Security](docs/guide.md#security). To report a vulnerability, see
[SECURITY.md](SECURITY.md) (private reporting, no public issue).

## Development

### Structure

| Project | Role |
| --- | --- |
| `src/ZillaTerm.Core` | Cross-platform logic without UI: PVWA API client, account classification, xterm terminal emulator, PSMP connections and SFTP/SCP browser (SSH.NET), FTP/FTPS browser (FluentFTP), VNC client, "My servers" folders, KeePass databases (KDBX), local vault, preferences. |
| `src/ZillaTerm.App` | WPF application: windows, tabs, terminal control, Remote Desktop control (RDP tabs), `mstsc` launch, icon (`Assets`). |
| `tests/ZillaTerm.Core.Tests` | xUnit tests of Core (fake PVWA over HTTP, terminal, PSMP, folders, translations…). |
| `tests/ZillaTerm.App.Tests` | Windows tests of the application (real Remote Desktop control, DPAPI). |

External dependencies: [SSH.NET](https://github.com/sshnet/SSH.NET), [FluentFTP](https://github.com/robinrodricks/FluentFTP)
and [Konscious.Security.Cryptography](https://github.com/kmaragon/Konscious.Security.Cryptography) (Argon2), all
under the MIT license. SSH.NET is version 2026.0.0 plus two patches, one that keeps the owner and group names of SFTP
listings and one that adds X11 forwarding for shells, built from [a fork](https://github.com/muller-camille/SSH.NET)
at a pinned commit by `tools/sshnet-patched.sh`, until an SSH.NET release includes them.

### Translations

Interface texts live in `src/ZillaTerm.Core/Localization/CoreStrings*.resx` and
`src/ZillaTerm.App/Localization/Strings*.resx`: English in the neutral file, then `.fr` and `.it`.
The `*.Designer.cs` classes are generated by Visual Studio (`PublicResXFileCodeGenerator`); a test checks that
every language has all the keys, the same `{0}` parameters and the same `_` access keys.
To add a language: copy the `.resx` files with the new code (`.de.resx`…), translate them, then add the code
to `UiLanguage.Supported`.

### Build and test

With the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Git (Git Bash on Windows); the first command
builds the patched SSH.NET package into `local-packages` (needed once):

```powershell
bash tools/sshnet-patched.sh
dotnet test ZillaTerm.sln
dotnet run --project src/ZillaTerm.App
```

The project also builds on Linux or macOS (`EnableWindowsTargeting`); the application only runs on Windows.
The tests in `tests/ZillaTerm.App.Tests` (including a test of the real Remote Desktop control) only run on
Windows; elsewhere, run `dotnet test tests/ZillaTerm.Core.Tests`.

### Publish the executable

```powershell
# Self-contained (~65 MB): nothing to install on the workstation
dotnet publish src/ZillaTerm.App -c Release -r win-x64 -p:SelfContained=true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish

# Lightweight: requires the ".NET Desktop Runtime 10" on the workstation
dotnet publish src/ZillaTerm.App -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -o publish
```

CI ([`.github/workflows/build.yml`](.github/workflows/build.yml)) runs the tests and publishes the
self-contained executable as the `ZillaTerm-win-x64` artifact for every pull request and every push to
`main`.

### Publish a release

From GitHub: **Actions → release → Run workflow** on `main`, with the `X.Y.Z` number. The
[`release.yml`](.github/workflows/release.yml) workflow runs the tests and builds the executable with that version
number (with a read-only token), then a second job, which builds nothing, creates the tag if it does not exist and
publishes the GitHub *Release* with the zip and `SHA256SUMS.txt`. Release notes are read from
`docs/releases/vX.Y.Z.md` when that file exists. "Check without publishing" runs the whole chain without publishing
anything, from any branch.

## Limitations and ideas

**Current limitations**

- **Privilege Cloud** (sign-in through CyberArk Identity) and **SAML** are not supported.
- The Accounts API does not say which PSM components a platform offers: the component is deduced from the platform,
  then can be set per platform in Settings.
- KeePass databases: Twofish encryption and YubiKey keys are not supported; no database creation (create it with
  KeePass or KeePassXC); attachments are kept but not shown.
- Following the terminal folder requires bash, zsh or tcsh on the server.
- VNC: VNC password or no authentication only (no vendor-specific authentication or encryption). FTPS: a server
  that requires TLS session reuse on data connections may refuse transfers.
- PSM Gateway (HTML5), dual control and exclusive access are not supported.

**Ideas**

- Signed executable and MSI installer.
- Several PVWAs (connection profiles), Privilege Cloud.
- Other ideas, kept for later: see [IDEAS.md](IDEAS.md).

## License

[MIT](LICENSE) © 2026 muller-camille
