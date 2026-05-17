# OpenSim DataS3 MinIO Integration

- Versuchsreihe 1 - MinIO mit den Batch/Shell Skripten herunterladen Verzeichnis /bin/minio
- Versuchsreihe 2 - HybridBlobObjectStore

⚠️ Early status for experimentation. ⚡

## 1. Beispiel: AssetService.DataS3.Migration.ini einbinden

Damit OpenSim/Robust deine MinIO-Konfiguration übernimmt, musst du die Datei in der Hauptkonfiguration (z. B. Robust.HG.ini oder Robust.ini) einbinden.

**Beispiel für absoluten Pfad:**

```ini
[Includes]
Include-Abs = "D:/OpenSim-Data/opensim/OpenSim/DataS3/AssetService.DataS3.Migration.ini"
```

**Beispiel für relativen Pfad (wenn Datei in config-include):**

```ini
[Includes]
Include = "config-include/AssetService.DataS3.Migration.ini"
```

**Wichtig:**

- Entferne das `.example` im Dateinamen, sonst wird die Datei nicht automatisch geladen.
- Pfad und Name müssen exakt stimmen.

---

## 2. Beispiel-Inhalt für AssetService.DataS3.Migration.ini

```ini
[AssetService]
LocalServiceModule = "OpenSim.Services.AssetServiceS3.dll:AssetServiceS3"

[AssetStorage]
ObjectStore = MinIO
; Standardvorgabe: Robust startet den Go-MinIO-Server automatisch mit.
MinioAutoStart = true
MinioBinaryPath = /opt/robust/bin/minio/linux-x64/minio
MinioDataPath = /opt/robust/bin/minio/data
MinioPort = 10080
MinioEndpoint = http://127.0.0.1:10080
MinioBucket = assets
MinioAccessKey = minioadmin
MinioSecretKey = minioadmin
MinioRegion = us-east-1
MinioAutoCreateBucket = true
FallbackReadEnabled = true
ReadThroughMigrationEnabled = true
```

---

## 3. Typische Fehlerquellen

- **Datei nicht eingebunden:** Die Datei muss per Include in Robust.HG.ini/Robust.ini stehen.
- **Falscher Pfad/Name:** Prüfe auf Tippfehler und Groß-/Kleinschreibung.
- **.example-Endung:** Nur Dateien ohne `.example` werden geladen.
- **Robust-Neustart:** Nach Änderungen Robust/Opensim neu starten.

---

## 4. Sicherheitshinweis MinIO

Die Standardvorgabe im Konfigurationsbeispiel ist `MinioAutoStart = true`.
Damit startet Robust/OpenSim den Go-MinIO-Server beim Start automatisch.

Wenn `MinioAutoStart = true` gesetzt ist, startet OpenSim den `minio server` selbst.
Starte MinIO in diesem Fall **nicht zusätzlich manuell**, sonst läuft der Port bereits und der von OpenSim gestartete Prozess schlägt fehl.

Setze stattdessen die MinIO-Umgebungsvariablen in der Umgebung, in der du Robust/OpenSim startest. Der von OpenSim gestartete MinIO-Prozess übernimmt diese Variablen.

Beispiel unter Linux:

```sh
export MINIO_ROOT_USER=minioadmin
export MINIO_ROOT_PASSWORD=minioadmin
export MINIO_VOLUMES=/opt/robust/bin/minio/data
export MINIO_ADDRESS=:10080
export MINIO_CONSOLE_ADDRESS=:10081
export MINIO_REGION=us-east-1
```

Danach startest du nur Robust/OpenSim.

Beispiel unter Windows CMD:

```cmd
set MINIO_ROOT_USER=minioadmin
set MINIO_ROOT_PASSWORD=minioadmin
set MINIO_VOLUMES=D:\OpenSim-Data\opensim\bin\minio\data
set MINIO_ADDRESS=:10080
set MINIO_CONSOLE_ADDRESS=:10081
set MINIO_REGION=us-east-1
```

Beispiel unter Windows PowerShell:

```powershell
$env:MINIO_ROOT_USER="minioadmin"
$env:MINIO_ROOT_PASSWORD="minioadmin"
$env:MINIO_VOLUMES="D:\OpenSim-Data\opensim\bin\minio\data"
$env:MINIO_ADDRESS=":10080"
$env:MINIO_CONSOLE_ADDRESS=":10081"
$env:MINIO_REGION="us-east-1"
```

Falls du MinIO bewusst als eigenen externen Dienst startest, setze in der INI `MinioAutoStart = false`. Dann darf OpenSim den Server nicht noch einmal starten.

---

**Tipp:**

- Prüfe nach dem Start das Log auf Zeilen wie `MinioEndpoint`, um zu sehen, ob die Werte übernommen wurden.
- Bei Problemen: Pfade, Dateinamen und Includes kontrollieren.

---

# HybridBlobObjectStore

HybridBlob speichert Blob-Daten im Dateisystem und Metadaten in einer SQL-Datenbank.

## Unterstuetzte Datenbanken

- SQLite
- MySQL
- MariaDB
- PostgreSQL

## Konfigurationsschluessel

- `ObjectStore=HybridBlob`
- `HybridBlobStoragePath` (Pfad fuer Blob-Dateien)
- `HybridBlobDatabaseType` (`SQLite|MySQL|MariaDB|PostgreSQL`)
- `HybridBlobConnectionString` (DB-Verbindungsstring)
- `HybridBlobTableName` (optional, Standard: `blob_metadata`)
- `HybridBlobAutoCreatePath` (`true|false`)

## Beispiel: SQLite

```ini
[AssetStorage]
ObjectStore = HybridBlob
HybridBlobStoragePath = ./data/hybrid_blobs
HybridBlobDatabaseType = SQLite
HybridBlobConnectionString = ./data/hybrid_blobs/metadata.db
HybridBlobTableName = blob_metadata
HybridBlobAutoCreatePath = true
```

## Beispiel: MySQL/MariaDB

```ini
[AssetStorage]
ObjectStore = HybridBlob
HybridBlobStoragePath = ./data/hybrid_blobs
HybridBlobDatabaseType = MySQL
HybridBlobConnectionString = Server=127.0.0.1;Port=3306;Database=opensim;Uid=opensim;Pwd=change-me;Allow Zero DateTime=true;
HybridBlobTableName = blob_metadata
HybridBlobAutoCreatePath = true
```

## Beispiel: PostgreSQL

```ini
[AssetStorage]
ObjectStore = HybridBlob
HybridBlobStoragePath = ./data/hybrid_blobs
HybridBlobDatabaseType = PostgreSQL
HybridBlobConnectionString = Host=127.0.0.1;Port=5432;Database=opensim;Username=opensim;Password=change-me;sslmode=prefer;
HybridBlobTableName = blob_metadata
HybridBlobAutoCreatePath = true
```

## Hinweis zur Migrationsdatei

Zusatzbeispiele sind auch in folgender Datei hinterlegt:

- `OpenSim/DataS3/AssetService.DataS3.Migration.ini.example`

