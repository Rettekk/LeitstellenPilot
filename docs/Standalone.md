# Betrieb und Konfiguration

## Windows und Linux

Die Windows-EXE und das Linux-Programm sind jeweils eigenständig. Für grafische Linux-Systeme werden die üblichen X11-/Fontbibliotheken benötigt; auf Ubuntu/Debian insbesondere `libx11-6`, `libice6`, `libsm6` und `libfontconfig1`. Ein Server ohne Desktop verwendet `--headless`.

Der Programmstart führt direkt zur Spielanmeldung. Es wird kein Betreiberkonto und keine Gerätefreigabe benötigt. Der Spielbenutzername erscheint nach der Anmeldung als Begrüßung. Updates werden manuell installiert, nachdem das Programm beziehungsweise der Dienst beendet wurde.

## Konfigurationsdateien

- Windows: `%APPDATA%/LSS/config.json` (vorhandene Profilpfade werden weiterverwendet).
- Linux: `${XDG_CONFIG_HOME:-~/.config}/LSS/config.json`; ein bereits vorhandenes altes Profil unter `leitstellenpilot` wird weiterhin erkannt.
- Explizit: `--config /pfad/zur/config.json`.

Passwörter und Spielsitzungen werden unter Windows mit dem Benutzerkonto geschützt. Unter Linux verwendet das Programm einen lokalen Schlüssel in `.secrets/storage.key`; Verzeichnis und Dateien erhalten private Berechtigungen. Diesen Ordner bei einer Profilmigration zusammen mit dem Profil sichern und nicht veröffentlichen.

Alte Betreiberregistrierungen, Gerätekennungen, Telemetriezustände, gemeinsame Zeitprofile und gespeicherte Pflichtupdates werden beim Laden ignoriert und bei der nächsten Konfigurationsspeicherung entfernt. Die Spielanmeldung, Zeitspannen, Personalplanung und Wachenbudgets bleiben erhalten.

## Headless

```sh
./LeitstellenPilot --headless --config ./profil/config.json --init-config
```

Danach `LoginIdentifier` in der Datei setzen. Das Passwort kann für die erste Anmeldung mit der Umgebungsvariable `LEITSTELLENPILOT_PASSWORD` bereitgestellt werden. Bei aktivem `AutoLogin` wird es lokal geschützt gespeichert. Nicht als Klartext in ein öffentliches Startskript schreiben.

```sh
./LeitstellenPilot --headless --config ./profil/config.json --login
./LeitstellenPilot --headless --config ./profil/config.json --start
```

`--login` prüft und speichert nur die Spielanmeldung. `--start` startet den Bot. Weitere Optionen: `--help` und `--check-environment`. `--register`, `--check-updates` und `--update` existieren nicht mehr. Als Dienstvorlage dient `deploy/linux/leitstellenpilot.service`; diese ist auf ein eigenes Dienstkonto und private Profilrechte ausgelegt.

## Verbindungen

- `www.leitstellenspiel.de`: Spielanmeldung, Bestand, Einsätze und Spielaktionen.
- `osrm.missionchief.com`: Fahrzeiten zwischen Spielkoordinaten.
- `api.lss-manager.de`: öffentliche Fahrzeug- und Bezeichnungsdaten, ohne Spielanmeldung.

Keine Telemetrie, automatische Fehlerübertragung, zentrale Zeitprofile, Betreiberfreigabe oder Updateprüfung.

## Einstellungen

Aktionen und Personalaktionen verwenden bei neuen Profilen 3–11 Sekunden, Statusabfragen 10–20 Sekunden. Eigene vorhandene Zeitspannen bleiben erhalten. Beim Wachenausbau bedeutet Tagesbudget `0` kein Tageslimit; die Credit-Reserve bleibt wirksam. Als Ziel sind eine feste Stufe oder die individuell angebotene Maximalstufe wählbar. Bereits fertige Wachen werden ohne Abruf einer nicht mehr verfügbaren Ausbauansicht übersprungen.

Das Protokoll bleibt ausschließlich während des aktuellen Programmstarts im Speicher. Es werden keine Diagnose- oder Protokolldateien geschrieben. Alte Proxy- und Ausbauhilfe-Einstellungen werden beim nächsten Speichern entfernt.
