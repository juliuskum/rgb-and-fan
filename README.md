# Licht & Lüfter

Windows-11-Tray-App für Beleuchtung (über den OpenRGB-SDK-Server) und Lüftersteuerung (über LibreHardwareMonitorLib/PawnIO) auf diesem PC:
MSI MAG Z790 TOMAHAWK WIFI (MS-7D91), 2× Corsair Vengeance RGB DDR5, 7× be quiet! Light Wings (Hub an JRAINBOW3), be quiet! Light Loop (Kühlkopf an JRAINBOW2), RTX 5070 Ti.

## Installieren, aktualisieren, starten

- **Installieren und aktualisieren:** `powershell -ExecutionPolicy Bypass -File tools\install.ps1` (fragt nach Adminrechten). Baut die App (lädt bei Bedarf das .NET-10-SDK nach `.tools\dotnet`), beendet eine laufende Instanz sauber, installiert nach `C:\Program Files\LichtLuefter`, legt Startmenü-Verknüpfung und Eintrag unter „Apps & Features“ an und startet die App im Tray. Update: `git pull`, dann dasselbe Skript. Einstellungen bleiben erhalten.
- **Deinstallieren:** über „Apps & Features“ (ruft `LichtLuefter.exe --uninstall` auf; Einstellungen auf Wunsch behalten).
- **Starten:** Startmenü „Licht & Lüfter“. Mit `--tray` startet sie ohne Fenster.
- Autostart: in der App unter *Einstellungen → Beim Anmelden im Tray starten* (Aufgabe „LichtLuefter“ in der Aufgabenplanung).
- Notfall: `LichtLuefter.exe --restore-bios` stellt alle Lüfterkanäle sofort auf die BIOS-Kurven zurück. Läuft die App, wird sie dabei auf das Profil „BIOS-Kurve“ umgeschaltet (sonst würde sie die Kanäle sofort wieder übernehmen). Reagiert sie nicht, wird direkt zurückgestellt und die Zustandsdatei für den Watchdog behalten. `--quiet` unterdrückt die Meldung. `--exit` beendet eine laufende Instanz sauber.

## Komfort

- **Licht automatisch aus**, wenn der PC gesperrt ist, der Bildschirm ausgeht oder im Ruhezustand (einzeln schaltbar, Einstellungen → „Licht automatisch ausschalten“). Vorübergehend, die eigene Einstellung bleibt gespeichert.
- **Warnungen:** kritische CPU-/GPU-Temperatur, Lüfter-/Pumpenstörung, Laufwerk über seiner Warngrenze → Licht pulsiert rot und Windows-Benachrichtigung. Endet nach 30 s ohne Bedingung.
- **Im Vollbild nicht stören:** im Spiel oder bei Präsentationen nur Warnungen.
- **Tray-Symbol** mit CPU- oder GPU-Temperatur.

## Beleuchtung

| Gerät | Was die App sendet |
|---|---|
| Mainboard (Light-Wings-Hub, Light-Loop-Kühlkopf) | nur Hardware-Modi auf das gesamte Gerät: Static, Breathing, Rainbow wave. **Nie Direct.** Farbwechsel, Feuer (als Glut) und Temperatur laufen im Modus Static, die Farbe führt die App 1–2×/s nach. Die Hardware-Effekte „Color shift“ (bleibt stehen) und „Fire“ (nur Zufallsfarben) funktionieren auf diesem Board nicht. |
| RAM | nur Direct, Effekte in Software (25 Bilder/s). Die Hardware-Modi der Module speichern bei jedem Wechsel in den Flash (`AutomaticSave`); das wird vermieden. |

**Board-Eigenheiten (gemessen):** ~0,2 s nach einem Wechsel auf „Rainbow wave“ oder „Fire“ meldet sich der Mystic-Light-Controller für ~0,5 s vom USB ab, OpenRGB registriert ihn neu und schickt „Geräteliste geändert“ an alle Programme. Befehle in diesem Fenster gehen verloren. Die App sendet deshalb nicht blind neu (das gab eine Endlosschleife), sondern liest den Board-Zustand nach einer Ruhephase zurück und wiederholt nur bei Abweichung (höchstens 3×).

Profile per Befehl/Verknüpfung umschalten (laufende App): `--light-effect rainbow`, `--light-profile "Name"`, `--fan-profile quiet`.

Kein „Apply all devices“, kein `SaveMode`, keine Profil- oder Einstellungsbefehle an OpenRGB. OpenRGB wird bei Bedarf mit `--server --startminimized` gestartet. Vor jedem Start, den die App auslöst, und bei jedem App-Start wird `%APPDATA%\OpenRGB` gesichert, sofern sich etwas geändert hat (`%LOCALAPPDATA%\LichtLuefter\backups`, die letzten 10).

## Lüfter

Gemessen am 18.11.2026 (Selbsttest im Prüfwerkzeug `tools\Probe`):

| Kanal | Anschluss | Rolle |
|---|---|---|
| 0 | CPU_FAN1 | Lüfter (≈ 975 U/min BIOS, 1540 bei 100 %) |
| 1 | PUMP_FAN1 | Light-Loop-Pumpe (≈ 2850 U/min, ab ≈ 79 % am Anschlag) |
| 7 | SYS_FAN6 | Lüfter (≈ 2100 U/min BIOS, 1500 bei 40 %, 2440 bei 100 %) |
| 2–6 | SYS_FAN1–5 | keine Drehzahl, werden nie angefasst |

Drehzahlmessung (`tools\Probe fansweep`, 18.11.2026):

| Anschluss | läuft stabil bis | steht bei | läuft wieder an ab | Minimum der App |
|---|---|---|---|---|
| CPU_FAN1 | 15 % (311 U/min) | 10 % | 20 % | **20 %** (433 U/min) |
| SYS_FAN6 | 30 % (1196 U/min) | 25 % (schlagartig) | 30 % | **35 %** (1357 U/min) |
| PUMP_FAN1 | 70–100 % ≈ 2850–2870 U/min (Sättigung) | – | – | **70 %** |
| nicht gemessen | | | | 40 % |

**Regelung:** Lüfter- und Pumpenkurven folgen dem geglätteten **Kerndurchschnitt** (nicht „CPU Package“). Package ist der jeweils heißeste Kern und springt schon im Leerlauf um 20 °C. In einer Simulation auf gemessenen Temperaturverläufen ließ das die Lüfter beim Spielen 16× pro Minute hoch- und runterlaufen. Mit Kerndurchschnitt, Rampe +4 %/s bzw. −1,5 %/s und 3 % Totband: keine Richtungswechsel, gleiche mittlere Drehzahl. Die Notfallgrenzen bleiben am Package.

**Sicherheitsgrenzen** (Konstanten in `src/LichtLuefter.Core/Settings/AppSettings.cs`, nicht per Einstellung änderbar): Pumpe ≥ 70 %, Lüfter ≥ gemessenes Minimum je Anschluss (siehe Tabelle), ab 90 °C CPU / 86 °C GPU alles 100 %, Sensorausfall > 5 s, stehende Pumpe (< 1000 U/min) oder stehender Lüfter, nicht bestätigter Befehl: zurück auf BIOS. Beenden, Abmelden, Ruhezustand: zurück auf BIOS. Absturz oder Hänger (15 s ohne Lebenszeichen): ein eigener Watchdog-Prozess stellt aus `%LOCALAPPDATA%\LichtLuefter\fan-state.json` zurück.

**Wichtig, LHM-Fehler:** `IControl.SetDefault()` aus LibreHardwareMonitorLib 0.9.6 gibt beim NCT6687D die Kanäle *nicht* ans BIOS zurück. Es schreibt das Modusbit (0xA00) außerhalb der Konfigurationsphase, die Kanäle bleiben fest auf dem alten Wert. Darum nutzt die App einen eigenen, rückgelesenen Restore (`SuperIoAccess.RestoreBios`). LHM ist auf 0.9.6 gepinnt, weil intern per Reflection zugegriffen wird. Bei einem Update erst `tools\Probe fantest` laufen lassen.

## Aufbau

- `src/LichtLuefter.Core`: OpenRGB-SDK-Client (Protokoll 4), Lichtdienst, Sensoren, Lüfterregelung, Watchdog
- `src/LichtLuefter`: WinForms-Hülle mit WebView2-Oberfläche (`ui/`), Tray, Autostart
- `tools/Probe`: Diagnose (`sdk`, `readback`, `sensors`, `regs`, `fantest`)
- .NET 10 SDK einmalig lokal installieren (nicht im Repo, keine Systeminstallation): `dotnet-install.ps1` von https://dot.net/v1/dotnet-install.ps1 laden und `-Channel 10.0 -InstallDir .tools\dotnet -NoPath` ausführen
- Bauen: `.tools\dotnet\dotnet.exe publish src\LichtLuefter -c Release --self-contained true -o dist\LichtLuefter`

Einstellungen: `%LOCALAPPDATA%\LichtLuefter\settings.json`. Protokolle: `%LOCALAPPDATA%\LichtLuefter\logs`.
