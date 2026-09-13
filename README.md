# TANSS Git-Connector

Ein Kommandozeilenwerkzeug, das jeden Git-Commit als Fernwartung **direkt in eure TANSS-Instanz**
bucht. Ein `post-commit`-Hook meldet den Commit, das Werkzeug baut daraus eine Fernwartung mit
Betreff, Zweig, Repository und Ticketbezug und schreibt sie in den Zeitstrahl des Technikers.

Kein Zwischendienst, kein Herstellerkonto, keine Daten außerhalb eures Hauses. Die einzige
Gegenstelle ist eure TANSS-Instanz.

Läuft auf **Linux, Windows und macOS**.

---

## Inhalt

- [Was das Werkzeug tut](#was-das-werkzeug-tut)
- [Was es nicht tut](#was-es-nicht-tut)
- [Voraussetzungen](#voraussetzungen)
- [Installation](#installation)
- [Einrichtung](#einrichtung)
- [Den Hook einrichten](#den-hook-einrichten)
- [Ticketbezug](#ticketbezug)
- [Konfiguration](#konfiguration)
- [Kommandos](#kommandos)
- [Betrieb](#betrieb)
- [Wenn TANSS nicht erreichbar ist](#wenn-tanss-nicht-erreichbar-ist)
- [Sicherheit](#sicherheit)
- [Datenschutz und Mitbestimmung](#datenschutz-und-mitbestimmung)
- [Häufige Fragen](#häufige-fragen)
- [Stand der Umsetzung](#stand-der-umsetzung)
- [Entwicklung](#entwicklung)
- [Die TANSS-Anbindung im Einzelnen](#die-tanss-anbindung-im-einzelnen)
- [Lizenz](#lizenz)

---

## Was das Werkzeug tut

- **Jeden Commit buchen.** Ein `post-commit`-Hook ruft `tanss-git hook` auf. Daraus entsteht
  eine Fernwartung auf eine externe Anbindung eurer Wahl, mit dem Commit-Betreff als
  Leistungsbeschreibung.
- **Den Ticketbezug herstellen.** Endet der Zweigname auf `#5000` oder trägt die Commit-Meldung
  eine Zeile `Ticket: 5000`, hängt die Fernwartung am Ticket — und die Nummer wird vorher gegen
  TANSS geprüft.
- **Zuverlässig übertragen.** Der Commit geht **zuerst** in eine lokale Warteschlange und erst
  danach ins Netz. Ein Netzausfall, ein zugeklapptes Notebook oder ein Abbruch kostet keine
  Arbeitszeit, sondern verschiebt sie.
- **Keine Dubletten erzeugen.** Der Commit-Hash ist die Vorgangskennung. Vor jeder Wiederholung
  mit ungeklärtem Ausgang wird in TANSS nachgesehen, ob die Fernwartung schon steht.
- **Den Commit nie scheitern lassen.** Der Hook endet immer mit 0. Wenn er läuft, ist der
  Commit bereits geschrieben.
- **Sich selbst am Leben halten.** Das Zugangstoken erneuert sich vor seinem Ablauf und wird
  gegengetestet, bevor es übernommen wird.

## Was es nicht tut

- **Kein Quelltext, keine Dateinamen, keine Änderungen.** Nach TANSS gehen Commit-Meldung,
  Kurzhash, Zweigname und Repositoryname — mehr nicht, und jedes dieser Felder lässt sich
  einzeln abschalten.
- **Kein Zwischendienst.** Es gibt genau eine Gegenstelle: eure TANSS-Instanz. Keine Telemetrie,
  keine Absturzberichte, keine Aktualisierungsabfrage bei einem Dritten.
- **Nichts heimlich.** Der Hook ist eine lesbare Datei in `.git/hooks/`, jede Buchung schreibt
  eine Zeile ins Protokoll, und `tanss-git status` zeigt jederzeit, was eingerichtet ist.
- **Keine erhöhten Rechte.** Alles läuft als angemeldeter Benutzer, alles liegt im
  Benutzerprofil.

---

## Voraussetzungen

| | |
|---|---|
| TANSS | 10.10.0 oder neuer, Modul **Fernwartung** lizenziert |
| Anbindung | mindestens eine externe Fernwartungs-Anbindung (Kennung ≥ 1000) in der TANSS-Administration |
| Mitarbeiterrecht | *„Darf API-Tokens für ext. Anbindungen erzeugen“* (Recht 480) |
| Arbeitsplatz | Linux, Windows 10/11 oder macOS; x64 oder arm64 |
| Git | 2.x, im Suchpfad erreichbar |
| .NET | keins — die veröffentlichte Fassung bringt ihre Laufzeit mit. Nur die [kleine Fassung](#die-kleine-fassung-wenn-net-ohnehin-da-ist) setzt die .NET-10-Laufzeit voraus |

### Die externe Fernwartungs-Anbindung

Gebucht wird auf eine Anbindung vom Typ ≥ 1000. Diese werden in der TANSS-Administration unter
*Externe Fernwartungs-Anbindungen verwalten* gepflegt.

**Legt dafür eine eigene Anbindung an**, etwa *Entwicklung* oder *Git*. Sie trennt die Commits
in jeder Auswertung von echten Fernwartungen — und sie lässt sich mit einer eigenen Farbe im
Zeitstrahl auf einen Blick erkennen. Welche Kennungen eure Instanz führt, zeigt
`tanss-git types`.

### Das Mitarbeiterrecht

Recht 480 braucht der Techniker **einmalig zur Einrichtung** und dauerhaft für die
Token-Erneuerung. Fehlt es, fragt die Einrichtung nach — ein Token ohne
Erneuerungsmöglichkeit stirbt Monate später kommentarlos, und genau diesen Ausfall soll niemand
suchen müssen.

---

## Installation

Es gibt noch keine fertigen Pakete; gebaut wird aus dem Quelltext. Nötig ist dafür einmalig das
[.NET-10-SDK](https://dotnet.microsoft.com/download) — **auf dem Rechner, der baut**, nicht auf
dem, der es benutzt: Das Ergebnis ist eine einzelne Datei, die ihre Laufzeit mitbringt.

```bash
git clone https://github.com/pronet-systems/tanss-git-connector.git
cd tanss-git-connector
```

### Linux

Auf einem ARM-Rechner — Raspberry Pi, Graviton-Instanz — statt `linux-x64` das Ziel
`linux-arm64` setzen. Deshalb steht es hier in einer Variablen: Es kommt an drei Stellen vor,
und wer nur die erste ändert, installiert am Ende die Datei der falschen Architektur.

```bash
rid=linux-x64      # auf ARM: linux-arm64

dotnet publish src/TanssGitConnector.Cli/TanssGitConnector.Cli.csproj \
  -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=true -p:PublishReadyToRun=true \
  -o "artifacts/$rid"

install -Dm755 "artifacts/$rid/tanss-git" ~/.local/bin/tanss-git

# Die Vorlage gehoert daneben: Laeuft ein Befehl ohne Konfiguration an, verweist
# das Werkzeug auf "config.example.json neben dem Programm".
install -Dm644 "artifacts/$rid/config.example.json" ~/.local/bin/config.example.json
```

Liegt `~/.local/bin` noch nicht im Suchpfad, gehört es hinein:

```bash
echo 'export PATH="$HOME/.local/bin:$PATH"' >> ~/.bashrc && . ~/.bashrc
```

### macOS

```bash
dotnet publish src/TanssGitConnector.Cli/TanssGitConnector.Cli.csproj \
  -c Release -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishReadyToRun=true \
  -o artifacts/osx-arm64

# Kein "install -D" hier: Das ist eine Erweiterung der GNU-coreutils, und macOS
# bringt das install aus BSD mit, das den Schalter nicht kennt.
mkdir -p ~/.local/bin
cp artifacts/osx-arm64/tanss-git ~/.local/bin/tanss-git
chmod 755 ~/.local/bin/tanss-git
cp artifacts/osx-arm64/config.example.json ~/.local/bin/

# macOS kennt ~/.local/bin nicht von Haus aus, und die Vorgabeshell ist zsh -
# eine ~/.bashrc wird dort nicht gelesen.
echo 'export PATH="$HOME/.local/bin:$PATH"' >> ~/.zshrc && exec zsh
```

Auf Intel-Macs `osx-x64`. Die Datei ist nicht signiert; macOS verlangt beim ersten Start eine
Bestätigung (*Systemeinstellungen → Datenschutz & Sicherheit → „Dennoch öffnen“*).

### Windows

```powershell
dotnet publish src\TanssGitConnector.Cli\TanssGitConnector.Cli.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishReadyToRun=true `
  -o artifacts\win-x64

$ziel = "$env:LOCALAPPDATA\Programs\TanssGitConnector"
New-Item -ItemType Directory -Force $ziel | Out-Null
Copy-Item artifacts\win-x64\tanss-git.exe $ziel -Force
Copy-Item artifacts\win-x64\config.example.json $ziel -Force

# Nur fuer den Aufruf von Hand noetig; der Hook merkt sich den vollen Pfad selbst.
[Environment]::SetEnvironmentVariable("PATH",
  [Environment]::GetEnvironmentVariable("PATH", "User") + ";$ziel", "User")
```

Danach eine neue Konsole öffnen, damit der Suchpfad greift. Auf ARM-Geräten — etwa einem
Surface mit Snapdragon — statt `win-x64` das Ziel `win-arm64` angeben, an allen drei Stellen.

### Prüfen

```bash
tanss-git --version
```

### Die kleine Fassung, wenn .NET ohnehin da ist

Die Befehle oben erzeugen eine einzelne Datei von rund 85 bis 95 MB, je nach Plattform — sie
bringt die gesamte .NET-Laufzeit mit und läuft auf einem Rechner ohne jede Installation. Wer die
.NET-10-**Laufzeit** ohnehin ausgerollt hat, lässt `-r`, `--self-contained` und
`PublishSingleFile` weg und bekommt knapp 1 MB:

```bash
dotnet publish src/TanssGitConnector.Cli/TanssGitConnector.Cli.csproj -c Release -o artifacts/portabel
```

**Das sind dann 17 Dateien, und sie gehören zusammen.** Anders als oben genügt es hier *nicht*,
`tanss-git` allein zu kopieren — das Programm sucht seine `tanss-git.dll` daneben und bricht
sonst mit einer englischen Meldung ab. Der ganze Ordner will installiert werden:

```bash
mkdir -p ~/.local/lib/tanss-git
cp -r artifacts/portabel/. ~/.local/lib/tanss-git/
ln -sf ~/.local/lib/tanss-git/tanss-git ~/.local/bin/tanss-git
```

Der Hook merkt sich den Pfad, unter dem er eingerichtet wurde; für ihn macht die Wahl keinen
Unterschied.

### Deinstallation

```bash
tanss-git disable --global     # Vorlage fuer neue Repositorys entfernen
tanss-git disable              # in jedem eingerichteten Repository
```

Danach die Programmdatei löschen und, wenn nichts mehr davon bleiben soll, auch die beiden
Verzeichnisse unter [Wo was liegt](#wo-was-liegt). **Vorher `tanss-git queue` aufrufen:** In der
Warteschlange kann ungebuchte Arbeitszeit liegen.

---

## Einrichtung

```
tanss-git setup
```

Der Assistent führt durch alle Schritte und **prüft jeden sofort gegen die echte
TANSS-Instanz**, statt Eingaben nur entgegenzunehmen:

| Schritt | Was geprüft wird |
|---|---|
| TANSS-Adresse | Schreibweise und die Endung `/backend` |
| Anmeldung | einmalig mit den eigenen Zugangsdaten, bei Bedarf mit zweitem Faktor |
| Berechtigung | Trockentest der Token-Erneuerung (Recht 480) |
| Token | wird geprägt, mit einem echten Aufruf gegengetestet und erst dann abgelegt |
| Mitarbeiter | die eigene Kennung wird gegen die Technikerliste geprüft |
| Anbindung | die in TANSS gepflegten Anbindungen werden angeboten |
| Dauer | wie viele Minuten ein Commit buchen soll |

**Zugangsdaten werden nach der Einrichtung verworfen.** Zurück bleiben das Arbeitstoken, die
eigene Mitarbeiter-ID und die Adresse.

Danach:

```
tanss-git doctor
```

---

## Den Hook einrichten

Zwei Wege, und sie schließen sich nicht aus.

### Für künftige Repositorys

```
tanss-git enable --global
```

Das legt eine Git-Vorlage an und trägt sie als `init.templatedir` ein. Git kopiert sie bei jedem
`git init` und `git clone` in das neue Repository.

**Bestehende Repositorys erreicht sie nicht.**

### Für ein bestehendes Repository

```
cd /pfad/zum/projekt
tanss-git enable
```

Alternativ genügt in einem bestehenden Repository auch `git init` — das ändert nichts am
Projekt und trägt nur die Vorlage nach.

### Wenn dort schon ein Hook liegt

Ein fremder `post-commit`-Hook wird **nicht** angetastet: Er kann ein Prüflauf, eine Signatur
oder eine Benachrichtigung sein. Zwei Möglichkeiten:

1. Den Aufruf in den vorhandenen Hook aufnehmen:
   ```sh
   tanss-git hook --repository "$PWD" || true
   ```
2. Oder ersetzen: `tanss-git enable --force` — der bisherige Stand wird daneben als
   `post-commit.vorher-<Zeitstempel>` aufbewahrt.

---

## Ticketbezug

Ohne Ticket ist eine gebuchte Fernwartung beim Kunden schwer zuzuordnen. Es gibt drei Wege, und
sie gelten in dieser Rangfolge:

| Weg | Beispiel | Gilt für |
|---|---|---|
| Von Hand | `tanss-git book HEAD --ticket 5000` | genau diesen Aufruf |
| Commit-Meldung | eine eigene Zeile `Ticket: 5000` | genau diesen Commit |
| Zweigname | `feature/rechnungslauf#5000` | alle Commits des Zweigs |

**Der Zweigname muss auf `#<Nummer>` enden.** Eine Raute mitten im Namen zählt nicht:
`fix/#2-spalten-layout` meint eine zweispaltige Darstellung und kein Ticket.

**In der Commit-Meldung zählt nur eine eigene Zeile.** `Behebt #42 endlich` wird nicht gelesen —
so schreiben GitHub, GitLab und Jira ihre Verweise, und das ist fast nie eine TANSS-Nummer. Eine
Fernwartung am falschen Ticket steht beim falschen Kunden in der Abrechnung, und darauf kommt
niemand von selbst.

**Die Nummer wird geprüft.** Gibt es das Ticket nachweislich nicht (TANSS meldet
`OBJECT_NOT_FOUND`), wird ohne Ticketbezug gebucht und der Grund protokolliert. Lässt sich die
Frage nicht klären — Netz, Zeitüberschreitung, 403 —, wird **mit** Ticketbezug gebucht: Eine
misslungene Prüfung ist kein Beleg gegen das Ticket.

---

## Konfiguration

Die Konfiguration ist eine versionierte JSON-Datei und von Hand editierbar. Vollständig
kommentiert liegt sie als [`config.example.json`](config.example.json) bei.

**Unbekannte Felder werden abgelehnt.** Wer `only_with_tickets` statt `only_with_ticket`
schreibt, hält seine Einschränkung für aktiv, während jeder Commit gebucht wird — ein Ladefehler
ist das kleinere Übel.

```jsonc
{
  "version": 1,
  "tanss": {
    "base_url": "https://tanss.kunde.de/backend",
    "employee_id": 42,
    "rotate_before_days": 60,
    "verify_tls": true,
    "timeout_seconds": 15
  },
  "commits": {
    "remote_support_type_id": 1007,
    "duration_mode": "fixed",
    "duration_minutes": 15,
    "minimum_minutes": 5,
    "maximum_minutes": 120,
    "include_body": true,
    "include_branch": true,
    "include_repository": true,
    "skip_merge_commits": true,
    "only_with_ticket": false
  },
  "tickets":  { "from_branch": true, "from_message": true, "verify": true },
  "hook":     { "send_immediately": true, "timeout_seconds": 10, "quiet": false },
  "proxy":    { "enabled": false, "address": "", "port": 8080, "user": "" },
  "logging":  { "level": "info", "retention_days": 30 }
}
```

### `tanss`

| Feld | Vorgabe | Bedeutung |
|---|---|---|
| `base_url` | — | Adresse der Instanz **einschließlich `/backend`**, ohne Schrägstrich am Ende |
| `employee_id` | — | eigene TANSS-Mitarbeiter-ID; sie trägt die gesamte Zuordnung |
| `rotate_before_days` | `60` | ab welcher Restlaufzeit das Token erneuert wird |
| `verify_tls` | `true` | Zertifikatsprüfung; `false` ist ein Notbehelf und wird im Doktor als Warnung gemeldet |
| `timeout_seconds` | `15` | Zeitgrenze je Aufruf |

`base_url` **muss** auf `/backend` enden. Zeigt sie stattdessen auf die Weboberfläche, antwortet
diese auf **jede** Anfrage mit HTTP 400 — auch ohne Token. Das ist der häufigste
Einrichtungsfehler überhaupt.

### `commits`

| Feld | Vorgabe | Bedeutung |
|---|---|---|
| `remote_support_type_id` | — | die externe Anbindung; muss ≥ 1000 sein und in TANSS existieren |
| `duration_mode` | `fixed` | `fixed` oder `since_last_commit` |
| `duration_minutes` | `15` | die feste Dauer — und der Rückfall, wenn sich nichts bemessen lässt |
| `minimum_minutes` | `5` | Untergrenze bei `since_last_commit` |
| `maximum_minutes` | `120` | **Obergrenze** bei `since_last_commit` |
| `include_body` | `true` | den Rumpf der Commit-Meldung mitschicken |
| `include_branch` | `true` | den Zweignamen mitschicken |
| `include_repository` | `true` | den Repositorynamen mitschicken |
| `skip_merge_commits` | `true` | Zusammenführungen übergehen |
| `only_with_ticket` | `false` | nur buchen, wenn eine Ticketnummer vorliegt |

**Zur Dauer.** `fixed` ist ehrlich: Es behauptet nichts über die Arbeitszeit, sondern setzt eine
vereinbarte Pauschale. `since_last_commit` ist näher an der Wirklichkeit, solange in einem Rutsch
gearbeitet wird — und daneben, sobald jemand eine Pause macht. Deshalb die Obergrenze: Ohne sie
buchte der erste Commit nach dem Wochenende zweiundsiebzig Stunden. **Beides bleibt eine
Schätzung.** Git weiß nicht, wann jemand angefangen hat zu arbeiten, nur wann er zuletzt
committet hat. Wer eine Messung braucht, benutzt die Timer in TANSS.

**`only_with_ticket` ist die schärfste Datenschutzeinstellung dieses Werkzeugs.** Eingeschaltet
verlässt nur das den Rechner, was ausdrücklich einem Ticket zugeordnet ist — das eigene Werkzeug
am Abend, das Übungsprojekt, das private Repository bleiben draußen. Ausgeschaltet ist die
Dokumentation vollständiger. Beides ist vertretbar; deshalb entscheidet es der Betrieb und nicht
dieses Werkzeug.

### `hook`

| Feld | Vorgabe | Bedeutung |
|---|---|---|
| `send_immediately` | `true` | nach dem Einreihen gleich senden |
| `timeout_seconds` | `10` | **so lange wartet der Techniker nach jedem Commit** |
| `quiet` | `false` | nichts ausgeben, wenn alles gutgegangen ist |

`send_immediately: false` ergibt einen Hook, der nur einreiht; `tanss-git queue --flush` bringt
die Commits dann später nach TANSS — der richtige Weg auf einem Rechner ohne ständige Verbindung
zur Instanz.

### `logging`

| Feld | Vorgabe | Bedeutung |
|---|---|---|
| `level` | `info` | `debug`, `info`, `warning`, `error` |
| `retention_days` | `30` | Aufbewahrung des Protokolls **und** der erledigten Warteschlangeneinträge |

Wartende und aufgegebene Einträge sind von der Frist **ausgenommen**, gleich wie alt sie werden:
Sie sind ungebuchte Arbeitszeit.

### Wo was liegt

| Was | Linux / macOS | Windows |
|---|---|---|
| Konfiguration | `~/.config/tanss-git-connector/config.json` | `%APPDATA%\ProNet Systems\TanssGitConnector\config.json` |
| Token | `~/.local/state/tanss-git-connector/credentials.dat` | `%LOCALAPPDATA%\ProNet Systems\TanssGitConnector\credentials.dat` |
| Warteschlange, Protokoll, Git-Vorlage | `~/.local/state/tanss-git-connector/` | `%LOCALAPPDATA%\ProNet Systems\TanssGitConnector\` |

Unter Unix gelten `XDG_CONFIG_HOME` und `XDG_STATE_HOME`, wenn sie gesetzt sind **und einen absoluten Pfad tragen** — ein relativer Wert wird nach der XDG-Festlegung übergangen. Die
Umgebungsvariable `TANSS_GIT_CONNECTOR_HOME` lenkt **beides** auf ein eigenes Verzeichnis um —
gedacht für Probeläufe, die die Einrichtung des Technikers nicht anfassen sollen.

Konfiguration und Laufzeitzustand sind bewusst getrennt: die eine will man sichern und
versionieren, den anderen nicht — das Token ist unter Windows ohnehin an Benutzer und Rechner
gebunden.

---

## Kommandos

| Befehl | Zweck |
|---|---|
| `tanss-git setup` | Einrichtungsassistent |
| `tanss-git doctor` | prüft Erreichbarkeit, Token, Rechte, Anbindung, Git, Hook, Warteschlange |
| `tanss-git status` | zeigt ohne Netzzugriff, was eingerichtet ist |
| `tanss-git enable [--global] [--force]` | Hook einrichten |
| `tanss-git disable [--global]` | Hook entfernen |
| `tanss-git types` | die externen Fernwartungs-Anbindungen der Instanz |
| `tanss-git book <Commit> [--ticket N] [--dry-run]` | einen Commit nachträglich buchen |
| `tanss-git hook [--ticket N] [--dry-run]` | der Aufruf aus dem Hook — endet immer mit 0 |
| `tanss-git queue [--flush]` | Warteschlange anzeigen; `--flush` sendet die fälligen Einträge |
| `tanss-git log [--lines N]` | das Änderungsprotokoll, jüngste Zeile zuerst |
| `tanss-git token status\|rotate` | Restlaufzeit anzeigen, Token erneuern |

Überall gültig: `--repository <Pfad>` (kurz `-C`), `--quiet`, `--help`, `--version`.

### Rückgabewerte

`doctor` ist der Überwachungsvertrag:

| Wert | Bedeutung |
|---|---|
| `0` | gesund |
| `1` | Warnung — läuft, verlangt aber Aufmerksamkeit |
| `2` | gestört — in diesem Zustand kommt Arbeitszeit nicht in TANSS an |
| `64` | Aufruffehler — unbekannter Befehl oder fehlendes Argument |

Damit lässt sich das Werkzeug in eine bestehende Überwachung einhängen, ohne Ausgaben zu parsen.

**`hook` ist davon ausgenommen und endet immer mit 0.** Wenn er läuft, ist der Commit bereits
geschrieben; ein Fehlschlag beim Buchen darf nicht wie ein misslungener Commit aussehen.

### Erst sehen, dann buchen

```
tanss-git book HEAD --dry-run
```

Zeigt vollständig, was gebucht würde — Zeitraum, Ticket, Anbindung und den ganzen Kommentar —,
ohne etwas einzureihen und ohne etwas zu senden.

---

## Betrieb

Nach jedem Commit erscheint eine Zeile:

```
TANSS · 6bf09a3 „Rechnungslauf korrigiert“ · 15 Minuten · Ticket 5000 · gebucht
```

Und wenn etwas nicht ging:

```
TANSS · 6bf09a3 „Rechnungslauf korrigiert“ · 15 Minuten · Ticket 5000 · eingereiht, aber nicht gebucht (Grund: tanss-git queue)
```

Der Commit ist in beiden Fällen geschrieben, und in beiden Fällen ist die Arbeitszeit erfasst —
im zweiten liegt sie in der Warteschlange und geht beim nächsten Commit oder bei
`tanss-git queue --flush` mit.

---

## Wenn TANSS nicht erreichbar ist

Das ist der Fall, für den die Warteschlange gebaut ist.

```
Commit geschrieben  → Hook laeuft
                    → Commit in die lokale Warteschlange, ZUERST, vor jedem Netzkontakt
TANSS erreichbar?   → nein: Eintrag bleibt liegen, Rueckstau waechst exponentiell,
                            Deckel eine Stunde
                    → ja:  senden, bei Erfolg abschliessen
```

Es gibt **kein Verlustfenster**: Der Hook sendet nie, bevor er eingereiht hat. Bricht der
Vorgang mitten im Senden ab, bleibt der Eintrag als *wartend* stehen — mit dem Kennzeichen
**Ausgang unbekannt**, weil niemand die Antwort gesehen hat.

**Und dann wird nicht geraten.** Vor jeder Wiederholung eines solchen Eintrags fragt das Werkzeug
per Textfilter auf den Commit-Hash nach, ob die Fernwartung schon in TANSS steht, und sendet nur
bei Abwesenheit. Das ist keine Vorsicht, sondern nötig:

> **TANSS erkennt Dubletten nicht.** Ein zweiter Aufruf mit derselben Kennung erzeugt einen
> zweiten Datensatz. Und eine Dublette ist nur über einen direkten Datenbankzugriff wieder zu
> entfernen — die Löschroute der API ist ohne ein typgebundenes Token unerreichbar.

Scheitert die Existenzprüfung selbst, heißt das **unbekannt**, nicht *nicht vorhanden*. Der
Eintrag wird zurückgestellt statt gesendet.

Nach zehn misslungenen Versuchen gilt ein Eintrag als aufgegeben. **Gelöscht wird er nicht** —
er trägt ungebuchte Arbeitszeit. `tanss-git doctor` meldet ihn, `tanss-git queue --flush` nimmt
ihn wieder auf.

---

## Sicherheit

**Das Token ist ein Ausweis.** Es erlaubt, im Namen des Mitarbeiters zu handeln. Wie gut es auf
der Platte geschützt ist, hängt von der Plattform ab, und `tanss-git doctor` sagt es
ausdrücklich:

| Plattform | Schutz |
|---|---|
| Windows | DPAPI, an das Windows-Konto gebunden, mit zusätzlicher Entropie |
| Linux, macOS | Dateirechte `0600` — nur der Eigentümer darf lesen. **Verschlüsselt ist die Datei nicht.** |

Unter Unix ist das ehrlich gesagt weniger: Wer die Datei in die Hand bekommt — als `root`, über
eine Sicherung, über eine ausgebaute Platte —, hat das Token. Eine Verschlüsselung mit einem
Schlüssel, der daneben liegt, wäre Theater und kein Schutz. Die richtige Antwort ist ein
verschlüsseltes Dateisystem und eine kurze Tokenlaufzeit.

**TANSS 10.10.0 kennt keinen Widerruf.** Ein einmal ausgestelltes Token bleibt bis zu seinem
Ablauf gültig. Deshalb ist die Laufzeit hier auf **180 Tage** gesetzt statt auf die möglichen
365. Geht ein Arbeitsplatz verloren:

1. Dem Mitarbeiter das Recht 480 entziehen. Das stoppt die Erneuerung, nicht das vorhandene
   Token.
2. Im Ernstfall die instanzweiten JWT-Schlüssel in TANSS erneuern. Das entwertet **alle** Tokens
   einschließlich der Web-Sitzungen.

**Schreibende Aufrufe werden nie automatisch wiederholt.** Eine Zeitüberschreitung heißt nicht,
dass der Server nichts getan hat.

**Geheimnisse werden niemals protokolliert.** JWT-Muster und `Bearer`-Werte werden beim Schreiben
geschwärzt; das gilt immer und lässt sich nicht abschalten.

---

## Datenschutz und Mitbestimmung

Das Werkzeug überträgt Zeiträume mit Personenbezug: Wer wann wie lange an welchem Projekt
gearbeitet hat. In Deutschland ist das eine **mitbestimmungspflichtige technische Einrichtung
nach § 87 Abs. 1 Nr. 6 BetrVG**. Vor der Einführung gehört der Betriebsrat eingebunden.

**Commit-Meldungen sind Freitext und enthalten regelmäßig personenbezogene Daten** — Kundennamen,
Ticketbetreffe, Namen von Ansprechpartnern. Sie landen im Kommentar der Fernwartung und damit in
der Dokumentation beim Kunden. Wer das nicht will, schaltet `include_body` ab; dann geht nur die
Betreffzeile mit.

Was das Werkzeug dagegen vorsieht:

- **Nichts läuft heimlich.** Der Hook ist eine lesbare Textdatei im Repository des Technikers,
  die er selbst einrichtet und jederzeit mit einem Befehl wieder entfernt.
- **Jedes übertragene Feld ist einzeln abschaltbar** — Rumpf, Zweig, Repository.
- **`only_with_ticket`** hält alles zurück, was nicht ausdrücklich Kundenarbeit ist.
- **Kein Quelltext verlässt den Rechner.** Weder Dateinamen noch Änderungen werden gelesen.
- **Das Protokoll bleibt lokal** und wird nach `logging.retention_days` aufgeräumt.

---

## Häufige Fragen

### Nach dem Commit passiert nichts.

Drei Ursachen, in dieser Reihenfolge zu prüfen:

1. In diesem Repository ist kein Hook eingerichtet. `tanss-git status` sagt es.
2. Dort liegt ein fremder `post-commit`-Hook; er wird nicht angetastet.
3. Die Vorlage (`--global`) wirkt nur auf Repositorys, die **danach** angelegt oder geklont
   wurden.

### Ich habe `--amend` benutzt — wird doppelt gebucht?

Ja. `git commit --amend` erzeugt einen **neuen** Hash, und der ist eine neue Vorgangskennung.
Der ursprüngliche Commit ist dann bereits gebucht. Dasselbe gilt für `rebase` und
`cherry-pick`. Eine Dublette in TANSS ist nur per Datenbankzugriff zu entfernen — wer viel
nachbessert, schaltet `send_immediately` ab und räumt vor dem `queue --flush` auf.

### Ein Commit ist nicht gebucht worden.

`tanss-git queue` zeigt, ob er liegt und woran es lag. `tanss-git log` zeigt auch die
übergangenen Commits mit Grund — in TANSS ist bei denen nie etwas angekommen, das lokale
Protokoll ist also der einzige Beleg.

### Wie reiche ich einen Commit nach?

```
tanss-git book <Commit-Hash>
```

Steht er schon in der Warteschlange oder ist er gebucht, wird nichts angehängt — der Hash ist
der Schlüssel.

### Zählt ein Merge als Arbeitszeit?

In der Voreinstellung nicht: Seine Arbeitszeit steckt in den Commits, die er zusammenführt, und
die sind meist schon gebucht. Zu ändern über `commits.skip_merge_commits`.

### Muss ich das Token regelmäßig erneuern?

Nein. Das Werkzeug erneuert es selbstständig, standardmäßig 60 Tage vor Ablauf, und prüft das
neue Token mit einem echten Aufruf, bevor es das alte ersetzt. Schlägt die Erneuerung fehl,
bleibt das bisherige aktiv — es ist ja noch gültig. Voraussetzung ist, dass der Mitarbeiter das
Recht 480 behält.

### Kann ich das Werkzeug neben einem anderen betreiben, das ebenfalls Commits bucht?

Nein. Beide schrieben dieselben Fernwartungen, und Dubletten sind in TANSS nur per
Datenbankzugriff wieder loszuwerden.

### Verlässt irgendetwas den Rechner außer den Fernwartungen?

Nein. Es gibt genau eine Gegenstelle: eure TANSS-Instanz.

---

## Stand der Umsetzung

| Baustein | Stand |
|---|---|
| TANSS-Anbindung (Client, Token, Fernwartungen, Ticketprüfung) | fertig, getestet |
| Commit lesen, Ticketnummer bestimmen, Fernwartung bauen | fertig, getestet |
| Warteschlange, Existenzprüfung, Rückstau, Protokoll | fertig, getestet |
| Hook einrichten (Repository und Vorlage), fremde Hooks erkennen | fertig, getestet |
| Kommandozeile (`setup`, `doctor`, `status`, `enable`, `disable`, `book`, `hook`, `queue`, `token`, `types`, `log`) | fertig |
| Prüfung gegen eine echte TANSS-Instanz | **erste Buchung angelegt und zurückgelesen** (siehe unten) |
| Einrichtungsassistent gegen eine echte Instanz erprobt | offen |
| Probebetrieb über mehrere Arbeitstage | offen |
| Fertige Pakete | offen — der Veröffentlichungslauf steht (`.github/workflows/release.yml`), es ist nur noch keine Marke gesetzt |
| Signierte Binärdateien | offen |

### Was gegen eine Produktivinstanz gemessen ist

Am 13.09.2026 gegen eine Instanz der Fassung 10.10.0:

- `GET /api/tanss.x/v1/remoteSupports/systems` liefert die externen Anbindungen wie erwartet.
- `POST /api/tanss.x/v1/remoteSupports` legt die Fernwartung an und weist den
  Mitarbeiter in `meta.linkedEntities.employees` aus — die Attribution ist damit **bestätigt**
  und nicht nur angenommen.
- `PUT /api/v1/remoteSupports` mit dem vollen Commit-Hash als Textfilter findet **genau diesen
  einen** Datensatz wieder. Das ist die Existenzprüfung, an der die Dublettenvermeidung hängt.
- Zurückgelesen: die eingestellte Anbindung und der eingestellte Mitarbeiter, `ticketId 0`, `companyId 0`,
  `deviceName "tanss-git-connector"`, Zeitraum 15 Minuten, Kommentar 1051 Zeichen.
  `userId`/`userName` gehen als leere Zeichenketten hinaus und kommen leer zurück — ohne
  Nebenwirkung.
- Ein zweiter `book`-Aufruf mit demselben Hash sendet nichts.

**Noch nicht gemessen:** der Ticketbezug (`GET /api/v1/tickets/{id}`) gegen eine echte Instanz,
die Tokenerneuerung und der Einrichtungsassistent.

**Vor einem Produktiveinsatz** stehen aus: ein Probebetrieb über mehrere Arbeitstage und die
Einbindung der Mitbestimmung.

> **`doctor` ist nicht rein lesend.** Er prüft das Recht auf Tokenerneuerung, indem er ein
> Token mit 60 Sekunden Laufzeit prägen lässt — TANSS führt das in seinem Tokenprotokoll, und
> das lässt sich nicht zurücknehmen. Wer gegen eine Produktivinstanz nur nachsehen will, nimmt
> `status`, `types` und `queue`.

---

## Entwicklung

```bash
dotnet build TanssGitConnector.slnx -warnaserror   # 0 Warnungen sind Pflicht
dotnet test  TanssGitConnector.slnx
```

Zielframework ist **.NET 10**, plattformneutral (`net10.0`). Es gibt keine
Windows-Abhängigkeit: DPAPI liegt hinter einer Plattformweiche, alles andere ist portabel.

### Aufbau

```
src/TanssGitConnector.Api        HTTP, Token, Repositories — kennt weder Git noch Dateipfade
src/TanssGitConnector.Git        git aufrufen, Commit lesen, Fernwartung bauen, Hook schreiben
src/TanssGitConnector.Storage    Konfiguration, Tokenspeicher, Warteschlange, Protokoll
src/TanssGitConnector.Cli        Kommandozeile
tests/                           je Modul ein Testprojekt
```

Die Schichtung ist strikt: `Api → Git → Storage → Cli`, keine Rückwärtsabhängigkeit. Alles außer
dem Netzzugriff ist ohne TANSS-Instanz und ohne Repository testbar.

### Konventionen

- Deutsche Prosa in den Dokumentationskommentaren und in allen Benutzertexten, englische
  Bezeichner im Code.
- Kommentare erklären **warum**, nicht was.
- **Erst Status prüfen, dann Rumpf.** Ein leerer Rumpf ist nur bei Erfolg eine leere Antwort —
  stünde die Leerprüfung vorher, wäre eine leere 403 ein leerer Erfolg.
- **Erst einreihen, dann senden.** Nie umgekehrt.
- **Vor jeder Wiederholung steht die Existenzprüfung** — und scheitert sie selbst, heißt das
  *unbekannt* und nicht *nicht vorhanden*: Dann wird zurückgestellt, nicht gesendet. Siehe
  [Wenn TANSS nicht erreichbar ist](#wenn-tanss-nicht-erreichbar-ist) und
  `Cli/Booking/CommitUploader.cs`.
- Zeiten ausschließlich über `TanssTime`. Es gibt bewusst keinen Millisekunden-Umrechner.

---

## Die TANSS-Anbindung im Einzelnen

Dieser Abschnitt richtet sich an Mitentwickler. Wer an der Anbindung etwas ändert, braucht eine
Messung gegen eine echte Instanz, keine Vermutung.

### Transport

| | |
|---|---|
| Kopfzeile | `apiToken: Bearer <jwt>` — **nicht** `Authorization` |
| Basisadresse | `https://host/backend`, ohne Schrägstrich am Ende |
| Erfolg | `{ "meta": {…}, "content": … }` |
| Fehler | `{ "error": { "text", "localizedText", "type", "traceId" } }` |
| Zeiten | **Unix-Sekunden**, überall |

### Zwei Präfixe, zwei Regeln

| Präfix | Regel |
|---|---|
| `/api/v1/**` | `loggedInUserId` ist **zwingend**. Ohne ihn antwortet TANSS mit 403 |
| `/api/tanss.x/v1/**` | `loggedInUserId` wird ignoriert und deshalb nicht gesendet |

`TanssRoutes.NeedsLoggedInUserId` entscheidet es; der Client setzt den Parameter selbsttätig,
der Aufrufer nie von Hand.

### Die benutzten Routen

| Zweck | Route |
|---|---|
| Fernwartung anlegen | `POST /api/tanss.x/v1/remoteSupports` |
| Fernwartungen lesen (Existenzprüfung) | `PUT /api/v1/remoteSupports` |
| Anbindungen auflisten | `GET /api/tanss.x/v1/remoteSupports/systems` |
| Techniker | `GET /api/tanss.x/v1/technicians` |
| Ticket prüfen | `GET /api/v1/tickets/{id}` |
| Anmeldung | `POST /api/v1/login` |
| Token prägen | `GET /api/v1/jwts/tanss_app` |

Zwei Eigenheiten, die Zeit kosten, wenn man sie nicht kennt:

- **`PUT /api/v1/remoteSupports` ist der Leseweg**, trotz des Verbs. Eine GET-Route dafür gibt
  es nicht.
- **`GET /api/v1/jwts/tanss_app` ist trotz des Verbs kein Lesevorgang.** Jeder Aufruf stellt ein
  Token aus. Er wird deshalb nie wiederholt.

### Der Rumpf einer Fernwartung

| Feld | Einheit | Pflicht | Anmerkung |
|---|---|---|---|
| `typeId` | — | ja | muss ≥ 1000 sein und in TANSS existieren |
| `employeeId` | — | ja | trägt die gesamte Zuordnung |
| `startTime`, `endTime` | **Sekunden** | ja | `endTime` 0 bedeutet: läuft noch |
| `remoteMaintenanceId` | — | ja | hier der Commit-Hash; Grundlage der Existenzprüfung |
| `comment` | — | optional | Betreff, Rumpf, Herkunftszeile |
| `ticketId` | — | optional | erzeugt den Ticketbezug |
| `deviceName` | — | optional | hier der Repositoryname |
| `id`, `fee`, `typeName` | — | **nie senden** | |

`deviceId` und `companyId` werden bewusst **nicht** gesetzt: TANSS übersetzt eine Gerätekennung
in eine Firma, und ein Commit gehört zu keinem Gerät des Kunden. Der Kundenbezug entsteht über
das Ticket.

---

## Lizenz

MIT — siehe [LICENSE](LICENSE). Copyright (c) 2026 ProNet Systems GmbH.

**Herkunft der TANSS-Anbindung.** Der Baustein `src/TanssGitConnector.Api` — HTTP-Zugang,
Umschlag, Token, Schwärzung — stammt überwiegend wörtlich aus dem Schwesterprojekt
[TANSS Log-Watcher](https://github.com/pronet-systems/tanss-log-watcher) desselben Hauses, das
unter derselben Lizenz und demselben Copyright steht. Das ist keine fremde Übernahme, aber es
soll dastehen: Wer den Ursprung selbst entdeckt, liest es sonst anders.

TANSS ist ein Produkt der HUCK IT GmbH, Roßdorf (Amtsgericht Darmstadt, HRB 95700). Dieses
Projekt ist ein unabhängiges Werkzeug, steht in keiner Verbindung zur HUCK IT GmbH und wird von
ihr weder unterstützt noch geprüft. Marken gehören ihren jeweiligen Inhabern; die Nennung dient
allein dazu, zu sagen, wofür dieses Werkzeug gemacht ist.
