# EdiaHelper

A small Windows tray helper for BMW's EDIABAS tools (INPA, NCS Expert, ISTA).

Use the **MHD universal WiFi adapter** with BMW's INPA, NCS Expert and ISTA on K-line, D-CAN and ENET cars, and switch between the MHD and a K+DCAN cable from one small Windows program.

Optional: **NCS Expert coding backups** - every module NCS Expert reads is saved automatically, named after the module and the time, so earlier codings are never lost.

## Download

**[Download EdiaHelper-1.1.0.zip](https://github.com/Nahkasikari/EdiaHelper/releases/download/v1.1.0/EdiaHelper-1.1.0.zip)** (from the [Releases page](https://github.com/Nahkasikari/EdiaHelper/releases/latest), under *Assets*). Unzip it, close INPA / NCS Expert / ISTA and run `Install.cmd`.

> **Do not use the green "Code → Download ZIP" button or "Source code (zip)".** They contain only the source code, not the ready-to-install program.

Requirements: Windows 10 or 11, INPA / EDIABAS 7.3 already installed and working with a K+DCAN cable. ISTA (4.39) is optional. See [Install](#install) below.

## Background

The MHD adapter can be used with EDIABAS through [EdiabasLib](https://github.com/uholeschak/ediabaslib)'s `Api32.dll` (`ObdComPort = DEEPOBDWIFI`). With an old EdiabasLib build (7.3.0.500), K-line and ENET worked but D-CAN gave `IFH-0018`. What we found:

| Problem | Cause | Fix |
|---|---|---|
| ENET fails with `IFH-0056` through EdiabasLib 7.3 | old EdiabasLib ENET support | EdiabasLib 8.0 (or BMW's original DLL) |
| EdiabasLib 8.0 fails with `IFH-0018` on the MHD | the MHD does not answer EdiabasLib's adapter checks (write-escape test, adapter info) | `DEEPOBDWIFI:...:RAW` skips them |
| ISTA identification on D-CAN breaks with `IFH-0003` | ISTA falls back to KWP2000\* telegrams (`B8 ...`) for missing ECUs. In CAN mode the MHD reads `B8` as a 56-byte length and swallows the following requests, across reconnects | a local relay that only forwards well-formed BMW-FAST telegrams while the WiFi is an `MHD CAN`/`MHD DCAN` network |

Result: K-line, D-CAN and ENET cars all work wirelessly through the MHD in INPA and ISTA.

## Tested with

| Car | Connection | Adapter | INPA | ISTA |
|---|---|---|---|---|
| E61 | K-line | MHD WiFi | yes | yes |
| E82, E90 | D-CAN | MHD WiFi | yes | yes |
| F11 | ENET | MHD WiFi | | yes |
| F15 | ENET | MHD WiFi | yes | yes |
| E61 | K-line | K+DCAN cable | yes | yes |

Two different MHD adapters. Windows 10 (x64), EDIABAS 7.3 (BMW Standard Tools 2.12), EdiabasLib 8.0.0.407. NCS Expert: E61 with the K+DCAN cable; coding backups: CIC (E60/E61 data).

**ISTA: only tested with ISTA 4.39 (4.39.20), with EDIABAS 7.3 in ISTA's own EDIABAS folder.** Newer ISTA versions come with a newer EDIABAS (7.7 and later, 64-bit) and have not been tested; they may need changes.

## What the program does

`EdiaHelper.exe` runs in the tray and

- **switches the adapter** for INPA / NCS Expert / ISTA: copies the right `Api32.dll` / `Api64.dll` into the EDIABAS folders and sets `Interface` in `EDIABAS.INI`, both INPA's and ISTA's own (ISTA's "EDIABAS standard settings" connection uses it; a fresh ISTA installation has `REMOTE`)

  | Choice | DLLs | Interface |
  |---|---|---|
  | MHD WiFi - K-line / D-CAN | EdiabasLib 8.0 | `STD:OBD` |
  | MHD WiFi - ENET | EdiabasLib 8.0 | `ENET` |
  | K+DCAN cable | BMW original | `STD:OBD` (`obd.ini`) |

- **runs the MHD relay** on `127.0.0.1:2323` (EdiabasLib: `ObdComPort = DEEPOBDWIFI:127.0.0.1:2323:RAW`). The adapter is the WiFi gateway (normally `192.168.4.1`), port 23. The filter is only active on `MHD CAN` / `MHD DCAN` networks; the unit ID at the end of the network name does not matter.
- **shows the state** in the tray: grey = nothing connected, red with K / D / E / C = connected.
- **checks the connection** ("Check connection" button or tray menu): goes through folders, DLLs, settings, WiFi, adapter / cable and says what is wrong in plain words. `EdiaHelper.exe /check <file>` writes the same report to a file.
- **sets the trace level** (EDIABAS folders tab) for troubleshooting: `IfhTrace` 0-3 in INPA's and ISTA's EDIABAS.INI at once, for the cable (BMW's EDIABAS) and the MHD (EdiabasLib) alike. 1 = messages to and from the car, 2 = + error details, 3 = + every script step (gigabytes; only for a few seconds). "Open trace folders" shows where the files are. The level is set back to 0 whenever EdiaHelper starts, so a forgotten trace cannot fill the disk. `EdiaHelper.exe /trace:<0-3>` does the same from the command line.
- **remembers where the adapter was last seen** ("Last seen" button): when each MHD adapter / the cable was last connected, and which car was read then (from ISTA's session logs and the NCS Expert backups).
- **switches automatically** (optional, on by default) when the WiFi connects to an MHD network or the K+DCAN cable is plugged in. It reacts to changes only, waits while INPA / NCS Expert / ISTA are open, and never shows an administrator prompt by itself.
- **backs up NCS Expert coding** (optional, on by default): NCS Expert overwrites `WORK\FSW_PSW.TRC` with every module it reads. EdiaHelper is told by Windows when the file changes and keeps a copy of every write in `TRC_BACKUP\<date> <VIN or chassis>\<module> <time>.TRC`, plus `<module> NETTODAT <time>.TRC` (raw coding bytes) and `FA <time>.TRC`. Nothing is overwritten or deleted; nothing happens if NCS Expert is not installed. No load while nothing changes.

## Included and not included

- **EdiabasLib** (GPLv3, by Ulrich Holeschak) - the package includes its unmodified `Api32.dll` and `Api64.dll` (version 8.0.0.407, release [binaries_20260607](https://github.com/uholeschak/ediabaslib/releases/tag/binaries_20260607)), its license, and `THIRD-PARTY-NOTICES.txt` with the source link. `make-package` takes them from `Binaries-20260607.zip` and checks their SHA-256 fingerprints; the installer checks them again and uses only the files in the package.
- **BMW's EDIABAS DLLs** are *not* included - they come with INPA / ISTA. The installer finds the originals in the existing EDIABAS folders and keeps a copy for the cable choice.

## Build

Needs only Windows (the .NET Framework C# compiler that comes with it):

```
build.cmd
```

Output: `build\EdiaHelper.exe`. `make-package.cmd` builds and puts an installable folder on the Desktop (needs `Binaries-20260607.zip` in Downloads or in `third-party\EdiabasLib`).

## Install

See `install\README.txt`. In short: close INPA/ISTA, run `Install.cmd` from the package. The program goes to `C:\Program Files\EdiaHelper` (only administrators can change it); settings, log and NCS backup state are per user in `%LOCALAPPDATA%\EdiaHelper`. Everything that is changed in the EDIABAS folders is backed up to `EdiaHelper-backup-<date>` folders; `Uninstall.cmd` restores them.

## Updates

- **About → Check for updates** asks GitHub for the newest release, only when you press it. If there is a newer version, it shows what is new and how to update; it never downloads or installs anything by itself.
- To update: download the new `EdiaHelper-<version>.zip` from the [Releases page](https://github.com/Nahkasikari/EdiaHelper/releases), unzip it, close INPA / NCS Expert / ISTA and run its `Install.cmd`. Settings, NCS backups and "last seen" records are kept.
- With a GitHub account you can also get an email for every new version: **Watch → Custom → Releases** at the top of this page.

## Security

- No internet access of its own: the relay listens on `127.0.0.1` only, and the program connects only to the MHD adapter (the WiFi gateway of an `MHD ...` network). The one exception is **Check for updates**, and only when you press it: it asks GitHub for the newest version number and sends nothing about the computer or the user. Nothing is uploaded.
- Administrator rights are asked only when you press something. The administrator part accepts only its own jobs (an FTDI cable that Windows lists, a valid IP address and port), works only on the EDIABAS folders the installer set up, and never writes through links (junctions / symbolic links).
- The installer gives your user account the right to change only the files EdiaHelper switches (Api32/Api64.dll, EdiabasLib.config, EDIABAS.INI, RPLUS.INI, obd.ini and its own `EdiaHelper-original` folder), not whole folders. Uninstall takes these rights back.
- Each EDIABAS folder keeps its own copy of BMW's original DLLs (`EdiaHelper-original`), so INPA and ISTA can have different EDIABAS versions. Uninstall puts BMW's own DLLs back, never an EdiabasLib DLL.
- Windows tools are started by their full path.
- Automatic switching only happens when exactly one adapter (MHD WiFi or K+DCAN cable) is connected.
- The relay on `127.0.0.1:2323` accepts connections from any program on the same computer (like EDIABAS itself does with a cable). Only use EdiaHelper on a computer you trust.
- Logs, "last seen" records and NCS backup folder names contain parts of VINs and WiFi names. They stay on your computer; remove those details before you post a log anywhere.

## License

Copyright (C) 2026 Nahkasikari. Free software under the GNU General Public License version 3 (`LICENSE`), without any warranty. Not affiliated with BMW or MHD.

## Files

| Path | |
|---|---|
| `src\EdiaHelper.cs` | tray, window, switching, status, About |
| `src\Relay.cs` | MHD relay and log |
| `src\Check.cs` | Check connection |
| `src\NcsBackup.cs` | NCS Expert coding backups, module recognition |
| `src\LastSeen.cs` | "Last seen": adapter sightings and the car read at that time |
| `src\IconArt.cs`, `src\MakeIcon.cs` | program and tray icons |
| `config\EdiabasLib-*.config` | EdiabasLib settings for INPA and ISTA |
| `install\` | installer, uninstaller, user instructions |
| `third-party\EdiabasLib\` | EdiabasLib license |
| `LICENSE`, `THIRD-PARTY-NOTICES.txt` | licenses |
