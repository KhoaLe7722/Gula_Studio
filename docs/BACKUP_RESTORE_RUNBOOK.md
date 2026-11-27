# CharacterConceptAI — Backup & Disaster Recovery Runbook

This runbook establishes standard operating procedures for backing up, verifying, and recovering the **CharacterConceptAI** system (SQL Server database and physical media storage) with zero data corruption and zero orphan assets.

---

## 1. Disaster Recovery Objectives

- **Recovery Point Objective (RPO):** < 24 hours (scheduled nightly backups) or on-demand before software upgrades.
- **Recovery Time Objective (RTO):** < 15 minutes to full operational readiness.
- **Integrity Rule:** Every stored image asset MUST be verifiable against the database `ImageAssets.Sha256` hash.
- **Safety Rule:** Restoration rehearsals MUST target an isolated database (`CharacterConceptAI_RESTORE_TEST`) and isolated media root. **Never execute a destructive restore over the active production database without explicit authorization.**

---

## 2. Backup Execution Runbook

### 2.1 Automated Coordinated Backup
Run the automated backup script from an elevated PowerShell terminal:
```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy\backup-system.ps1 `
    -ServerInstance "localhost" `
    -DatabaseName "CharacterConceptAI_DEV" `
    -MediaRoot "D:\CharacterConceptAIData\Media" `
    -BackupDestination "D:\CharacterConceptAIData\Backups"
```

### 2.2 Backup Package Contents
Each backup creates a uniquely timestamped directory (`CCA_BACKUP_YYYYMMDD_HHMMSS/`) containing:
1. `CharacterConceptAI_DEV.bak` — Full native SQL Server database backup with page checksums.
2. `Media/` — Full directory tree of all image assets organized by `{projectId}/{yyyyMM}/{fileId}.png`.
3. `manifest.json` — Cryptographic ledger documenting:
   - Backup ID, timestamp, and host details.
   - Database backup file SHA-256 hash.
   - Every media file's relative path, size, and SHA-256 hash.
   - Database `ImageAssets` table records for cross-verification.

### 2.3 Scheduling via Windows Task Scheduler
To configure automated daily backups at 02:00 AM:
```powershell
$action = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File C:\GulaStudio\scripts\deploy\backup-system.ps1"

$trigger = New-ScheduledTaskTrigger -Daily -At 2:00AM

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries:$false -DontStopIfGoingOnBatteries

Register-ScheduledTask `
    -TaskName "CharacterConceptAI_DailyBackup" `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Description "Automated coordinated backup for CharacterConceptAI DB and Media" `
    -User "SYSTEM"
```

---

## 3. Restore & Verification Runbook

### 3.1 Isolated Rehearsal (Standard Operating Verification)
To verify a backup without disrupting the active production database, restore into the isolated test sandbox:
```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy\restore-system.ps1 `
    -BackupDir "D:\CharacterConceptAIData\Backups\CCA_BACKUP_YYYYMMDD_HHMMSS" `
    -TargetDatabaseName "CharacterConceptAI_RESTORE_TEST" `
    -TargetMediaRoot "D:\CharacterConceptAIData\Media_RESTORE_TEST"
```

### 3.2 Automated Integrity Verification Stages
The restore script executes 6 automated validation stages:
1. **Manifest Validation:** Reads `manifest.json`, validates schema and source database metadata.
2. **Cryptographic Validation:**
   - Computes SHA-256 of `.bak` file and confirms exact match with `manifest.json`.
   - Computes SHA-256 of every file in `Media/` and confirms 100% hash match.
3. **Logical Inspection:** Queries `RESTORE FILELISTONLY` to determine logical data and log streams.
4. **Isolated SQL Restore:** Restores the database with `MOVE` directives to isolate MDF and LDF files.
5. **Media Directory Sync:** Synchronizes physical media files to the target media root.
6. **Cross-System Audit & Asset Matching:**
   - Queries `SELECT * FROM dbo.ImageAssets` in the restored database.
   - Confirms that **every asset record** has a corresponding file on disk.
   - Re-hashes the file on disk and verifies exact SHA-256 equality with the database column.
   - Writes `restore_verification_report.json` with PASS/FAIL audit results.

### 3.3 Production Disaster Recovery (Cold Standby Restore)
In the event of physical hardware failure on the server host:
1. Ensure SQL Server 2025 Express is installed on the replacement host.
2. Transfer the latest backup package (`CCA_BACKUP_YYYYMMDD_HHMMSS`) to `D:\CharacterConceptAIData\Backups\`.
3. Execute restore to the production database:
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\deploy\restore-system.ps1 `
       -BackupDir "D:\CharacterConceptAIData\Backups\CCA_BACKUP_YYYYMMDD_HHMMSS" `
       -TargetDatabaseName "CharacterConceptAI_DEV" `
       -TargetMediaRoot "D:\CharacterConceptAIData\Media" `
       -Force
   ```
4. Start the API Windows Service:
   ```powershell
   Start-Service CharacterConceptAIServer
   ```
5. Launch ComfyUI via `start-comfyui-supervised.ps1`.
6. Run `scripts\deploy\verify-two-client-lan-flow.py` to confirm full studio operations.

---

## 4. Recovery Audit Sign-off Checklist

- [ ] Database backup file SHA-256 matched manifest exactly.
- [ ] 100% of media files restored with matching SHA-256 hashes.
- [ ] Zero missing disk assets referenced in `ImageAssets` table.
- [ ] Zero orphan disk assets unreferenced in `ImageAssets` table.
- [ ] Projects, Characters, Scripts, Scenes, and Concept Approvals verified.
- [ ] `restore_verification_report.json` marked as **PASS**.
