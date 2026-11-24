EDIAHELPER - MHD WiFi adapter for INPA, NCS Expert and ISTA
===========================================================

What it does
------------
- Lets INPA, NCS Expert and ISTA use the MHD universal WiFi adapter on K-line, D-CAN and ENET cars.
- Lets you switch between the MHD adapter and a K+DCAN cable with one dropdown.
- Shows a small tray icon (bottom right of the screen):
    grey dongle        = no adapter connected
    red with K / D / E = MHD connected (K-line / D-CAN / ENET)
    red with C         = K+DCAN cable found

Before installing
-----------------
1. INPA / EDIABAS (and ISTA if you use it) must already be installed and working with a cable.
2. Close INPA, NCS Expert, Tool32 and ISTA.
   (EdiabasLib, the free open-source library that talks to the MHD, is included in this package.)

Installing
----------
1. Double-click Install.cmd and allow the administrator prompt.
2. The installer finds the EDIABAS and ISTA folders, backs up the files it changes, installs
   EdiaHelper to C:\Program Files\EdiaHelper and starts it. Read what it prints, then
   press Enter.
3. If Windows shows "Windows protected your PC" when EdiaHelper starts the first time,
   click "More info" -> "Run anyway". It is a self-made program without a publisher signature.

Using it
--------
- Connect the laptop's WiFi to the adapter ("MHD KLINE ...", "MHD CAN ..." or "MHD ENET ...").
  The adapter picks its mode by itself depending on the car.
- Open "EdiaHelper" on the Desktop (or click the tray icon), choose the adapter, press Apply.
  Close INPA / NCS Expert / ISTA before switching. You can also switch from the tray icon's menu.
    MHD on a K-line or D-CAN car  -> "MHD WiFi - K-line / D-CAN"
    MHD on an ENET car            -> "MHD WiFi - ENET"
    K+DCAN cable                  -> "K+DCAN cable"
- Automatic switching (on by default, checkbox in the window or "Switch automatically" in the
  tray menu): when the WiFi connects to an MHD adapter, or the K+DCAN cable is plugged in,
  EdiaHelper switches by itself and shows a short notice. It only switches when exactly one
  adapter is connected: with both the MHD WiFi and the cable, choose by hand. If INPA / NCS Expert /
  ISTA is open, it waits until they are closed. A manual choice is kept until the next adapter is
  connected.
- In ISTA choose the connection in ISTA's own connection manager as usual:
    K-line / D-CAN (MHD or cable)  -> the EDIABAS standard settings option
    ENET (MHD or cable)            -> ENET
- EdiaHelper starts automatically with Windows. Closing its window keeps it running in the tray;
  use the tray menu -> Exit to stop it.

If something does not work
--------------------------
- Press "Check connection" in the EdiaHelper window (or in the tray menu). It checks every
  setting and the connection and tells in plain words what is wrong. "Copy" copies the result.
- Check that the WiFi is connected to the adapter (it does not reconnect automatically if Windows
  is set to connect manually).
- Tray icon menu -> "Open log" shows what the relay did.
- The INPA and ISTA folders can be changed on the "EDIABAS folders" tab.
- Trace (logging), on the "EDIABAS folders" tab: set the trace level, repeat the one action that
  fails, then set it back to 0. "Open trace folders" shows the files.
    0 = off (normal use)
    1 = messages to and from the car - enough for most troubleshooting
    2 = level 1 + error details
    3 = every script step - gigabytes and very slow; only for a few seconds
  The level takes effect the next time INPA / NCS Expert / ISTA starts, and is set back to 0
  whenever EdiaHelper starts.

NCS Expert coding backups
-------------------------
Every time NCS Expert reads a module, a copy of FSW_PSW.TRC (and NETTODAT.TRC, FA.TRC) is saved to
C:\NCSEXPER\TRC_BACKUP\<date> <VIN or chassis>\, named after the module and the time, for example
"CIC.C1A 17.58.47.TRC". Nothing is overwritten or deleted. Settings and "Open backups" are on the
"NCS backups" tab. If NCS Expert is not installed, nothing happens.

Updates
-------
Open EdiaHelper -> About -> "Check for updates". It asks GitHub for the newest version (only when
you press the button) and tells you if there is a newer one and how to update: download the new zip
from https://github.com/Nahkasikari/EdiaHelper/releases, unzip it, close INPA / NCS Expert / ISTA
and run its Install.cmd. Your settings and NCS backups are kept.

License
-------
EdiaHelper is free software under the GNU General Public License version 3 (LICENSE.txt) and
comes without any warranty. It includes EdiabasLib (also GPLv3), see THIRD-PARTY-NOTICES.txt and
the About button in the program.

Uninstalling
------------
Double-click Uninstall.cmd (in C:\Program Files\EdiaHelper or in this package). It puts BMW's own
EDIABAS DLLs and the settings files from before the first installation back, takes back the file
permissions the installer gave and removes EdiaHelper. Your NCS backups and settings are kept.
