# Security Policy

ZillaTerm handles CyberArk credentials and opens privileged sessions, so security reports are welcome and
taken seriously.

## Supported Versions

Only the latest release receives security fixes. Please update to it before reporting.

| Version | Supported          |
| ------- | ------------------ |
| 0.38.x  | :white_check_mark: |
| < 0.38  | :x:                |

## Reporting a Vulnerability

**Please do not open a public issue.** Report it privately through GitHub:
[Security → Report a vulnerability](https://github.com/muller-camille/ZillaTerm/security/advisories/new).

Include, if possible:

- the ZillaTerm version and Windows version;
- the steps to reproduce, or a proof of concept;
- the impact you see (what an attacker could read, change or execute).

The report is acknowledged and investigated. If it is confirmed, a fix is released in a new version and a
GitHub security advisory is published, crediting you unless you prefer otherwise. If it is declined, you get
an explanation.

## Scope

In scope: the ZillaTerm code in this repository (PVWA client, PSM / PSMP connections, SSH terminal,
SFTP / SCP and FTP / FTPS file transfers, embedded remote desktop and VNC tabs, KeePass vault reading and writing, the
local encrypted vault, the emergency access log, the intervention journal and its exported report, the on-call duty
reminder file, session sharing (invitation, encrypted channel, direct connections and relay folder), the encrypted copy
of the account list, the team environment file, settings storage, release executables).

Out of scope, to be reported to their maintainers:

- vulnerabilities in CyberArk products (PVWA, PSM, PSMP) → CyberArk;
- vulnerabilities in [SSH.NET](https://github.com/sshnet/SSH.NET), [FluentFTP](https://github.com/robinrodricks/FluentFTP),
  [Konscious.Security.Cryptography](https://github.com/kmaragon/Konscious.Security.Cryptography) (Argon2) or .NET → their projects
  (except in the two patches that ZillaTerm carries on SSH.NET, SFTP longname and X11 forwarding, built by the release
  workflow of the fork and fetched by `tools/sshnet-patched.sh`: report them here);
- vulnerabilities in [FreeRDP](https://github.com/FreeRDP/FreeRDP) → its project (except in the generic patches that
  ZillaTerm carries on FreeRDP 3.32.1, built by the release workflow of the fork and fetched by
  `tools/freerdp-download.sh`: report them here);
- vulnerabilities in the Windows Remote Desktop client or in KeePass / KeePassXC → Microsoft or their projects.

The security measures built into the application are summed up in the [README](README.md#security) and described in
detail in the [user guide](docs/guide.md#security).
