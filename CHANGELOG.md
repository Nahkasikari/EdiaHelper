# Changelog

Version numbers: last number = fixes, middle = new features, first = big changes.

## 1.1.0 - (in testing)

- **Trace level** on the EDIABAS folders tab: a dropdown for `IfhTrace` 0-3 with a short description of each level. It sets INPA's and ISTA's EDIABAS.INI at once and applies to both the K+DCAN cable (BMW's EDIABAS) and the MHD (EdiabasLib). Level 3 asks for confirmation. "Open trace folders" opens the trace folders and shows their size. A level left on is set back to 0 whenever EdiaHelper starts. Check connection warns while a trace level is on.
- EdiaHelper's EdiabasLib.config files no longer set `IfhTrace` / `ApiTrace`, so EDIABAS.INI is the one place for the trace level (EdiabasLib reads EDIABAS.INI first and its config file second, which used to override it with 0).
- Traces keep every EDIABAS start: `AppendTrace = 1` in EdiaHelper's EdiabasLib configs (ISTA starts EDIABAS several times, and each start used to replace the file), and `TraceBuffering = 0` whenever a level above 0 is set (buffered lines were lost when ISTA ended EDIABAS abruptly). Found in the first in-car test of 1.1.0.
- "Delete trace files" button (asks first; files still in use are skipped).
- `EdiaHelper.exe /trace:<0-3>` sets the trace level from the command line.

## 1.0.1 - 2026-11-21

- **ISTA works right after installing EdiaHelper, also with a fresh ISTA installation:** switching now also sets `Interface` in ISTA's own EDIABAS.INI (STD:OBD for the MHD K-line / D-CAN and the K+DCAN cable, ENET for the MHD's ENET mode). A fresh ISTA has `REMOTE` there, so ISTA's "EDIABAS standard settings" connection did not work until it was changed by hand. Check connection checks ISTA's interface too.
- The installer says clearly when it was started from the source code (GitHub's "Code → Download ZIP") instead of the installable package.
- Full VINs are no longer written to the log (the NCS backup "module not found" line keeps only the last 7 characters).

The ISTA problem was found on a second laptop with a freshly installed ISTA; 1.0.0 had been tested only on a computer whose ISTA was already set to STD:OBD.

## 1.0.0 - 2026-11-20

First public version.

- **Adapter switching** for INPA, NCS Expert and ISTA: MHD WiFi K-line / D-CAN, MHD WiFi ENET, K+DCAN cable. From the window or the tray menu.
- **MHD relay**: lets ISTA identify D-CAN cars through the MHD (forwards only well-formed BMW-FAST telegrams on MHD CAN networks). Connects only on MHD networks.
- **Tray icon** with the connection state (grey / K / D / E / C).
- **Automatic switching** when an MHD WiFi or the K+DCAN cable is connected (optional; only when exactly one adapter is connected, never while INPA / NCS Expert / ISTA are open).
- **K+DCAN cable setup**: finds the cable's COM port, writes it into obd.ini (INPA and ISTA), sets the latency timer to 1 ms.
- **Check connection**: checks folders, DLLs, runtimes, settings, WiFi, adapter / cable and says in plain words what is wrong.
- **Last seen**: when each adapter was last connected and which car was read then (from ISTA's session logs and the NCS Expert backups).
- **NCS Expert coding backups** (optional): every FSW_PSW.TRC / NETTODAT.TRC / FA.TRC NCS Expert writes is saved with the module name and time; the module is recognised from the trace itself. Whole-car files are named "All modules".
- **Check for updates** (About window): asks GitHub for the newest release only when pressed, and tells what is new and how to update. Never downloads or installs anything by itself.
- **BMW's original DLLs kept per EDIABAS folder** (`EdiaHelper-original`), so INPA and ISTA can have different EDIABAS versions; a newer original (e.g. after an ISTA update) is saved before it is switched away.
- **Installer and uninstaller** with backups of every changed EDIABAS file; installs to `C:\Program Files\EdiaHelper`. Gives your account rights only on the files it switches, and Uninstall takes them back. Uninstall puts BMW's own DLLs back (never an EdiabasLib one) plus the settings files from before the first installation. Takes over an older "Adapter Switch" installation and its settings.
- Includes EdiabasLib 8.0.0.407 (GPLv3, unmodified), checked by its SHA-256 fingerprint.
- Command line for support: `/check <file>`, `/lastseen <choice> <file>`, `/identify <trace> <file>`, `/screenshot <prefix>`.

Tested: see "Tested with" in README.md.
