# Jellyfin Plugin: Nextcloud Memories

Spiegelt Fotos, Alben und Videos aus der Nextcloud-App [Memories](https://memories.gallery/) in eine
Jellyfin-Bibliothek. Jellyfin und Nextcloud müssen sich **kein** Dateisystem teilen — das Plugin
spricht ausschließlich über HTTP mit Nextcloud.

> **Status:** v1.0, getestet gegen Jellyfin 10.11.11. Vor dem ersten Sync ein Backup der
> Jellyfin-Konfiguration anlegen.

---

## Wie es funktioniert

Jellyfins Channels-API kann keine Fotos darstellen (`ChannelMediaType.Photo` wird intern nie zu einem
`Photo`-Item aufgelöst, jedes Foto landet als `Video` in der Datenbank). Deshalb geht dieses Plugin
einen anderen Weg:

1. Ein **Scheduled Task** holt über die Memories-API die Zeitachse und die Alben.
2. Für jedes Foto wird die von Memories gerenderte **JPEG-Vorschau** in ein lokales Cache-Verzeichnis
   heruntergeladen (Standard: 2048 px Kantenlänge). Der Dateiname enthält Aufnahmedatum und
   Nextcloud-`fileid`, die Datei-`mtime` wird auf das Aufnahmedatum gesetzt.
3. Videos werden standardmäßig als **`.strm`-Datei** abgelegt, die auf einen signierten
   Streaming-Proxy im Plugin zeigt. Nextcloud-Zugangsdaten landen dadurch nie in der Jellyfin-Datenbank.
4. Das Cache-Verzeichnis wird als reguläre **„Home Videos & Photos"-Bibliothek** in Jellyfin
   registriert. Damit funktionieren Thumbnails, Slideshow, Favoriten und alle Clients ohne Sonderfälle.
5. Ein Metadaten-Provider ergänzt Aufnahmedatum und — optional — Tags, Personen und Orte.

```
<Cache-Verzeichnis>/
├── Zeitachse/
│   └── 2024/
│       └── 2024-06/
│           ├── 2024-06-01_143022_1048576.jpg
│           └── 2024-06-03_090511_1048701.strm
└── Alben/
    └── Sommerurlaub 2024/
        └── 2024-06-01_143022_1048576.jpg   ← Symlink auf die Zeitachsen-Datei
```

---

## Voraussetzungen

| | |
|---|---|
| Jellyfin | 10.11.0 oder neuer (getestet mit 10.11.11) |
| Nextcloud | mit installierter und eingerichteter App „Memories" |
| Speicherplatz | ca. 300–600 KB pro Foto bei 2048 px — 100.000 Fotos ≈ 30–60 GB |
| Netzwerk | Jellyfin muss die Nextcloud-URL per HTTP(S) erreichen |

---

## Installation

### Variante A — Release-ZIP (empfohlen)

1. Aktuelles `nextcloud-memories.zip` von der
   [Releases-Seite](https://github.com/leoguiders/jellyfin-plugin-nextcloud-memories/releases) laden.
2. Jellyfin stoppen.
3. ZIP entpacken und den Inhalt nach
   `<jellyfin-config>/plugins/Nextcloud Memories/` kopieren. Typische Pfade:

   | Setup | Pfad |
   |---|---|
   | Linux (Paket) | `/var/lib/jellyfin/plugins/Nextcloud Memories/` |
   | Docker (linuxserver) | `/config/plugins/Nextcloud Memories/` im Container |
   | Docker (offiziell) | `/config/plugins/Nextcloud Memories/` im Container |
   | Windows | `%ProgramData%\Jellyfin\Server\plugins\Nextcloud Memories\` |

4. Rechte prüfen — der Ordner muss dem Jellyfin-Benutzer gehören:

   ```bash
   chown -R jellyfin:jellyfin "/var/lib/jellyfin/plugins/Nextcloud Memories"
   ```

5. Jellyfin starten. Unter **Dashboard → Plugins** sollte „Nextcloud Memories" erscheinen.

### Variante B — selbst bauen

```bash
git clone https://github.com/leoguiders/jellyfin-plugin-nextcloud-memories.git
cd jellyfin-plugin-nextcloud-memories
dotnet publish Jellyfin.Plugin.NextcloudMemories/Jellyfin.Plugin.NextcloudMemories.csproj \
  -c Release -o publish
```

Anschließend `publish/Jellyfin.Plugin.NextcloudMemories.dll` in den oben genannten Plugin-Ordner
kopieren und Jellyfin neu starten. Benötigt das **.NET 9 SDK**.

---

## Nextcloud vorbereiten

Ein **App-Passwort** anlegen, nicht das normale Login-Passwort verwenden:

**Nextcloud → Einstellungen → Sicherheit → Geräte & Sitzungen → „Neues App-Passwort erstellen"**

Vorteile: funktioniert trotz aktivierter Zwei-Faktor-Authentisierung und lässt sich einzeln
widerrufen, ohne andere Geräte auszusperren.

---

## Konfiguration

**Dashboard → Plugins → Nextcloud Memories**

### Verbindung

| Feld | Beschreibung |
|---|---|
| Nextcloud-URL | z. B. `https://cloud.example.de` — ohne `/apps/memories` |
| Benutzername | Nextcloud-Benutzername |
| App-Passwort | siehe oben |

Nach dem Speichern **„Verbindung testen"** klicken. Die Meldung nennt die Anzahl gefundener Tage und
Dateien.

### Bibliothek

| Feld | Standard | Beschreibung |
|---|---|---|
| Cache-Verzeichnis | `<plugin-data>/library` | Muss vom Jellyfin-Prozess beschreibbar sein |
| Name der Bibliothek | `Nextcloud Fotos` | |
| Bibliothek automatisch anlegen | an | Legt beim ersten Sync eine „Home Videos & Photos"-Bibliothek an |
| Nach Sync scannen | an | Startet nach jedem Sync einen Bibliotheksscan |

### Inhalte

| Feld | Standard | Beschreibung |
|---|---|---|
| Zeitachse spiegeln | an | Ordnerbaum Jahr/Monat |
| Alben spiegeln | an | Ein Ordner je Memories-Album |
| Nur ab / bis Datum | leer | Begrenzt den Zeitraum, z. B. `2020-01-01` |
| Album-Auswahl | alle | „Alben laden" klicken, dann gezielt auswählen |

### Dateien

| Feld | Standard | Beschreibung |
|---|---|---|
| Vorschaugröße | 2048 | Kantenlänge in Pixel |
| Originale laden | aus | Experten-Option, siehe „Bekannte Grenzen" |
| Videos | `.strm` | oder Original herunterladen / ignorieren |
| Albumeinträge | Symlink | oder Kopie, falls das Dateisystem keine Symlinks kann |
| Basis-URL dieses Servers | `http://127.0.0.1:8096` | Wird in `.strm`-Dateien geschrieben |

### Erweitert

| Feld | Standard | Beschreibung |
|---|---|---|
| Parallele Downloads | 4 | |
| Sync-Intervall | 12 h | 0 deaktiviert den automatischen Trigger |
| HTTP-Timeout | 120 s | |
| Album-Filterparameter | `albums` | siehe Troubleshooting |
| Detailmetadaten | aus | Tags, Personen, Orte — ein Extra-Request pro Foto |

Danach **„Jetzt synchronisieren"**. Der Fortschritt ist unter
**Dashboard → Geplante Aufgaben → „Nextcloud Memories synchronisieren"** sichtbar.

---

## Docker

Das Cache-Verzeichnis muss ein persistentes Volume sein, sonst ist nach jedem Container-Neustart
alles weg:

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin:10.11.11
    volumes:
      - ./config:/config
      - ./cache:/cache
      - ./memories-mirror:/media/memories   # <- als Cache-Verzeichnis eintragen
```

Im Plugin dann `/media/memories` als Cache-Verzeichnis setzen. Symlinks funktionieren innerhalb
desselben Volumes problemlos; bei exotischen Dateisystemen auf `Kopie` umstellen.

Läuft Nextcloud im selben Docker-Netz, kann als Nextcloud-URL der interne Servicename verwendet
werden (z. B. `http://nextcloud`) — das spart den Umweg über den Reverse Proxy.

---

## Endpunkte des Plugins

| Methode | Pfad | Zweck |
|---|---|---|
| `POST` | `/NextcloudMemories/TestConnection` | Verbindungstest |
| `GET` | `/NextcloudMemories/Albums` | Albumliste |
| `GET` | `/NextcloudMemories/Describe` | Rohantwort von `GET /apps/memories/api/describe` |
| `POST` | `/NextcloudMemories/Sync` | Sync starten |
| `GET` | `/NextcloudMemories/Status` | Status |
| `GET` | `/NextcloudMemories/Stream/{fileId}?token=…` | Video-Proxy (anonym, HMAC-signiert) |

Alle außer `Stream` erfordern Administratorrechte.

---

## Troubleshooting

**Alben bleiben leer oder enthalten die komplette Bibliothek**

Der Query-Parameter zum Filtern der Zeitachse nach Album ist in Memories nicht versioniert und hat
sich zwischen Versionen geändert. Das Plugin erkennt den Fall (Album liefert deutlich mehr Dateien
als erwartet), überspringt das Album und schreibt eine Warnung ins Log. Vorgehen:

1. `GET /NextcloudMemories/Describe` aufrufen (oder direkt
   `https://<nextcloud>/apps/memories/api/describe`).
2. Den passenden Parameternamen heraussuchen.
3. Unter **Erweitert → Album-Filterparameter** eintragen.

**Plugin-Einstellungen lassen sich nicht speichern**

Bekannter Jellyfin-10.11-Fehler hinter Reverse Proxys mit gesetzter Base-URL. Abhilfe: Dashboard
direkt über die interne Adresse aufrufen (`http://<server>:8096`) oder die Base-URL temporär leeren.

**Fotos ohne Vorschaubild**

Meist HEIC oder RAW bei aktivierter Option „Originale laden". Die Option ausschalten — dann liefert
Memories fertige JPEGs, die Jellyfin in jedem Fall darstellen kann.

**Videos springen beim Vorspulen nicht sauber**

`.strm`-Wiedergabe mit Range-Requests ist in Jellyfin nicht vollständig implementiert
([jellyfin#13974](https://github.com/jellyfin/jellyfin/issues/13974)). Der Proxy reicht `Range`
korrekt durch, das Verhalten hängt aber vom Client ab. Zuverlässigste Variante:
**Videos → Originaldateien herunterladen**.

**Jellyfin startet nicht mehr**

Ab 10.11 verweigert Jellyfin den Start bei weniger als 2 GB freiem Speicher im Datenverzeichnis.
Cache-Verzeichnis auf ein anderes Laufwerk legen oder Vorschaugröße/Zeitraum reduzieren.

**Der Scan dauert ewig**

Jellyfin skaliert bei sechsstelligen Foto-Zahlen schlecht. Zeitraum begrenzen, nur ausgewählte Alben
spiegeln oder die Zeitachse deaktivieren.

**Logs**

`Dashboard → Protokolle`, alle Meldungen des Plugins tragen den Kontext
`Jellyfin.Plugin.NextcloudMemories`.

---

## Bekannte Grenzen

- **Doppelte Items.** Ein Foto, das in Zeitachse *und* Album liegt, erzeugt in Jellyfin zwei
  `Photo`-Items. Das ist bei einer dateibasierten Bibliothek nicht vermeidbar. Wer das nicht will,
  deaktiviert einen der beiden Zweige.
- **Ein Nextcloud-Konto.** Das Plugin läuft mit genau einem Nextcloud-Benutzer. Alle Jellyfin-Nutzer
  mit Zugriff auf die Bibliothek sehen dessen komplette Fotosammlung.
- **Vorschauen enthalten kein EXIF.** Das Aufnahmedatum wird deshalb über `mtime` *und* den
  Metadaten-Provider gesetzt. Kameradaten gehen verloren, wenn nicht „Originale laden" aktiv ist.
- **Löschungen** in Nextcloud werden erst beim nächsten Sync sichtbar.
- **Gesichter und Orte** landen als Tags am Item, nicht als eigene Navigation. Jellyfin hat keine
  Foto-spezifische Personen- oder Kartenansicht.
- **Memories-API ist unversioniert.** Ein Update der Nextcloud-App kann Feldnamen ändern.

---

## Alternative ohne Plugin

Wer einen WebDAV-Mount auf dem Jellyfin-Host einrichten kann (`rclone mount` oder `davfs2` auf
`/remote.php/dav/files/<user>/Photos`), braucht dieses Plugin nicht: Jellyfin liest die Dateien dann
direkt, ohne Cache und ohne doppelten Speicherverbrauch. Der Preis ist ein zusätzlicher Dienst
außerhalb von Jellyfin und spürbar langsamere Scans.

---

## Entwicklung

```bash
dotnet build Jellyfin.Plugin.NextcloudMemories/Jellyfin.Plugin.NextcloudMemories.csproj -c Debug
```

| Datei | Inhalt |
|---|---|
| `Plugin.cs` | Plugin-Einstiegspunkt, Konfigurationsseite |
| `PluginServiceRegistrator.cs` | DI-Registrierung |
| `Api/MemoriesApiClient.cs` | HTTP-Client für die Memories-API |
| `Api/MemoriesController.cs` | Admin-Endpunkte der Konfigurationsseite |
| `Sync/SyncService.cs` | Soll-Ist-Abgleich, Download, Verknüpfung, Cleanup |
| `Sync/LibraryIndex.cs` | Persistenter Zustand (`fileid` ↔ Pfad ↔ etag) |
| `Streaming/` | Signierter Video-Proxy |
| `Providers/` | Metadaten-Anreicherung |
| `Tasks/MemoriesSyncTask.cs` | Geplante Aufgabe |

Pull Requests willkommen. Bitte keine direkten Datenbankzugriffe ergänzen — ab Jellyfin 10.11 ist
Raw-SQL nicht mehr erlaubt, und die Plugin-Datenbank-API gilt bis 10.12 als experimentell.

---

## Lizenz

GPL-3.0-only, passend zu Jellyfin. Nextcloud Memories steht unter AGPL-3.0 und wird ausschließlich
über HTTP angesprochen.
