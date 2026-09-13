# Änderungsprotokoll

Alle nennenswerten Änderungen an diesem Projekt stehen hier. Das Format folgt
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/), die Fassungsnummern folgen
[Semantic Versioning](https://semver.org/lang/de/).

Die Fassungsnummer selbst steht an genau einer Stelle: im Element `Version` in
`Directory.Build.props`.

---

## [Unveröffentlicht]

### Behoben — zwei Wege, auf denen eine Dublette entstehen konnte

Ein Prüflauf über den Schreibpfad hat vor der ersten Produktivbuchung zwei Fälle gefunden, in
denen der Ausgang eines Sendeversuchs fälschlich als **geklärt** galt. Beide hätten dazu
geführt, dass die Wiederholung ohne Existenzprüfung hinausgeht — und TANSS dedupliziert nicht.

- **Eine 5xx heißt nicht „nichts angelegt“.** Bisher galt jede Antwort außer der
  Unerreichbarkeit als geklärt. Ein Server, der auf halbem Weg stolpert, kann die Fernwartung
  aber vorher geschrieben haben. Der Ausgang hängt jetzt am Status: 4xx ist geklärt, 5xx und
  **jeder Fehler ohne Status** sind es nicht.
- **Ein Fehler nach einer erfolgreichen Antwort ist der stärkste Hinweis auf einen angelegten
  Datensatz** — „quittiert, aber keinen Datensatz genannt“ und „der Rumpf ist kein JSON“
  entstehen erst nach einer 2xx. Sie gelten jetzt als ungeklärt.
- **Ein Abbruch mitten im Senden hinterlässt einen Vermerk.** Strg+C sagt nichts darüber, ob die
  Anfrage angekommen ist; bisher blieb der Eintrag als scheinbar geklärter Erstversuch stehen.
  Der Abbruch wird weiterhin durchgereicht.
- Die Zeitgrenze des Hakens zählt einen Versuch nicht mehr doppelt.

Acht neue Tests decken die Fälle ab (178 insgesamt).

### Berichtigt — eine Anmerkung, die den Doktor verharmlost hat

An `CanRotateAsync` stand, TANSS protokolliere den Rechte-Trockentest nicht und gebe ein
unbrauchbares Token zurück. Beides ist falsch und widersprach der nachgemessenen Anmerkung
sechzig Zeilen darüber: Es entsteht ein echtes Token, und die Instanz führt es in ihrem
Tokenprotokoll. **`tanss-git doctor` ist damit nicht rein lesend** — das steht jetzt im
Quelltext und im README.

### Gemessen — die erste Buchung in einer Produktivinstanz

Gegen eine Instanz der Fassung 10.10.0 angelegt und zurückgelesen; die Einzelheiten stehen im
[README unter „Was gegen eine Produktivinstanz gemessen ist“](README.md#was-gegen-eine-produktivinstanz-gemessen-ist).
Neu belegt: die Attribution wird serverseitig bestätigt, die Existenzprüfung findet den
Datensatz über den Commit-Hash wieder, und `userId`/`userName` dürfen leer hinausgehen.

---

## [0.1.0] — 2026-09-13

Erste Fassung. Sie bucht Git-Commits als Fernwartungen unmittelbar in eine TANSS-Instanz,
ohne einen Dienst dazwischen.

### Hinzugefügt

- **Der `post-commit`-Haken.** `tanss-git enable` schreibt ihn in das aktuelle Repository,
  `tanss-git enable --global` legt eine Git-Vorlage an, die Git bei jedem `git init` und
  `git clone` mitkopiert. Ein fremder Haken bleibt unangetastet; `--force` ersetzt ihn und
  bewahrt den bisherigen Stand daneben auf.
- **Die Buchung.** Aus einem Commit entsteht eine Fernwartung auf eine externe Anbindung
  (Kennung ≥ 1000) mit Betreff, Rumpf, Kurzhash, Repository und Zweig im Kommentar. Jedes dieser
  Felder lässt sich einzeln abschalten. Gebucht wird **rückwärts vom Commit**: Gearbeitet wurde
  vorher, nicht nachher.
- **Zwei Arten, die Dauer zu bestimmen** — eine feste Pauschale je Commit oder die Zeit seit dem
  letzten Commit, begrenzt nach oben und unten. Die Obergrenze ist kein Feinschliff: Ohne sie
  buchte der erste Commit nach dem Wochenende zweiundsiebzig Stunden.
- **Der Ticketbezug** aus dem Zweignamen (`feature/xy#5000`), aus einer eigenen Zeile
  `Ticket: 5000` in der Commit-Meldung oder von Hand über `--ticket`. Die Nummer wird vor dem
  Buchen gegen TANSS geprüft; nur ein belegtes „gibt es nicht“ kostet den Ticketbezug.
- **Die Warteschlange.** Der Commit geht zuerst in eine lokale Datei und erst danach ins Netz.
  Ein Netzausfall verschiebt die Buchung, statt sie zu kosten. Rückstau wächst exponentiell mit
  Deckel bei einer Stunde; nach zehn Versuchen gilt ein Eintrag als aufgegeben — gelöscht wird
  er nicht.
- **Die Existenzprüfung vor jeder Wiederholung.** TANSS dedupliziert nicht; ein Eintrag mit
  ungeklärtem Ausgang wird erst gesucht und nur bei Abwesenheit gesendet. Scheitert die Prüfung
  selbst, wird zurückgestellt statt gesendet.
- **Der Einrichtungsassistent** `tanss-git setup`: Anmeldung, Rechte-Trockentest, Token prägen
  und gegenprüfen, Mitarbeiter bestätigen, Anbindung auswählen.
- **Der Doktor** `tanss-git doctor` mit den Rückgabewerten 0/1/2 als Überwachungsvertrag, dazu
  `status`, `queue`, `log`, `types`, `token status|rotate` und `book --dry-run`.
- **Token-Erneuerung** vor dem Ablauf, mit Gegenprobe: Das neue Token ersetzt das alte erst,
  wenn ein echter Aufruf damit gelungen ist. Laufzeit 180 Tage statt der möglichen 365 — TANSS
  10.10.0 kennt keinen Widerruf.
- **Plattformgerechter Schutz des Tokens:** DPAPI unter Windows, Dateirechte `0600` unter Unix.
  Welcher gerade gilt, sagt der Doktor ausdrücklich — eine Verschlüsselung mit einem Schlüssel,
  der daneben liegt, wäre Theater.
- **Ein Änderungsprotokoll** mit einer Zeile JSON je Vorgang, einschließlich der übergangenen
  Commits samt Grund. In TANSS kommt bei denen nie etwas an; das lokale Protokoll ist der
  einzige Beleg.

### Zugesichert

- **Der Haken endet immer mit 0.** Wenn er läuft, ist der Commit bereits geschrieben.
- **Es verlässt kein Quelltext den Rechner** — keine Dateinamen, keine Änderungen.
- **Es gibt genau eine Gegenstelle:** die TANSS-Instanz des Kunden. Keine Telemetrie, keine
  Absturzberichte, keine Aktualisierungsabfrage bei Dritten.
