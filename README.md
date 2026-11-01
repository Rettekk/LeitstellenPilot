# LeitstellenPilot

Standalone-Client für Leitstellenspiel.de mit grafischer Oberfläche und Headless-Betrieb. Windows und Linux verwenden dieselbe Botlogik.

## Funktionen

- Einsätze alarmieren, Nachforderungen bearbeiten und Fahrzeuge umverteilen.
- Anfahrtsgrenzen und gleichzeitig bearbeitete Einsätze einstellen.
- Personal nach Fahrzeugbedarf und vorhandener Ausbildung verteilen.
- Patienten- und Gefangenentransporte einschließlich Krankenhausauswahl bearbeiten.
- Aktionsabstände, Arbeitsrhythmus und Nachtruhe konfigurieren.
- Tagesbonus, fertige Aufgaben, Beförderungen und Sammelobjekte optional abholen.
- Wachen optional mit Credit-Reserve und Tagesbudget ausbauen.

## Starten

Die fertigen Pakete enthalten die .NET-Laufzeit. Für den Betrieb ist keine separate .NET-Installation erforderlich. Verfügbar sind Windows x64 und Linux x64.

### Windows

`LeitstellenPilot-windows-x64.zip` entpacken, `LeitstellenPilot.exe` starten und mit dem Spielkonto anmelden.

### Linux mit Oberfläche

`LeitstellenPilot-linux-x64.tar.gz` entpacken und im entpackten Verzeichnis starten:

```sh
chmod +x LeitstellenPilot
./LeitstellenPilot --gui
```

Für die Oberfläche werden X11- und Fontbibliotheken benötigt. Unter Ubuntu/Debian sind dies insbesondere `libx11-6`, `libice6`, `libsm6` und `libfontconfig1`.

### Linux ohne Oberfläche

```sh
./LeitstellenPilot --headless --config ./profil/config.json --init-config
```

Danach `LoginIdentifier` in der Konfiguration setzen und das Passwort für die erste Anmeldung über `LEITSTELLENPILOT_PASSWORD` bereitstellen. Bei aktivem `AutoLogin` werden die Zugangsdaten lokal geschützt gespeichert.

```sh
./LeitstellenPilot --headless --config ./profil/config.json --login
./LeitstellenPilot --headless --config ./profil/config.json --start
```

Konfigurationspfade, Passwortspeicherung und die systemd-Vorlage sind unter [Betrieb und Konfiguration](docs/Standalone.md) beschrieben.

## Daten und Verbindungen

Das Programm verbindet sich mit Leitstellenspiel.de, dem MissionChief-Routingdienst und öffentlichen LSS-Manager-Referenzdaten. Spielcookies und Zugangsdaten werden nicht an LSS-Manager übertragen.

Es gibt keine Betreiberregistrierung, Lizenzabfrage, HWID-Erfassung, Telemetrie oder automatische Programmupdates. Neue Versionen werden manuell installiert. Das kompakte Protokoll bleibt für den aktuellen Programmstart im Speicher; es werden keine Diagnose- oder Protokolldateien geschrieben.

## Entwicklung

Benötigt wird das **.NET 10 SDK**. Die Solution heißt `LeitstellenPilot.sln`.

```sh
dotnet restore LeitstellenPilot.sln
dotnet build LeitstellenPilot.sln
```

Die Anwendung liegt unter `LeitstellenPilot/`, Linux-Vorlagen unter `deploy/linux/`, die Anleitung unter `docs/` und Buildskripte unter `tools/`.

Pakete mit PowerShell 7 bauen:

```powershell
./tools/Publish-Standalone.ps1
./tools/Publish-Linux.ps1
```

Die fertigen Pakete liegen unter `publish/`. GitHub Actions prüft, ob sich die Anwendung unter Windows und Linux bauen lässt.

## Lizenz und Datenquellen

Der eigene Client-Code steht unter der [MIT-Lizenz](LICENSE). Fremde Bibliotheken und übernommene Katalogdaten behalten ihre jeweiligen Bedingungen und sind davon ausgenommen. Insbesondere sind die LSS-Manager-Daten nicht pauschal MIT-lizenziert. Quellen und bekannte Bedingungen stehen in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md); mitgelieferte Lizenztexte in [THIRD_PARTY_LICENSES.txt](THIRD_PARTY_LICENSES.txt).

Das Projekt ist unabhängig vom Spielanbieter. Die Nutzung unterliegt weiterhin den Regeln des Spiels.
