# CharacterConceptAI — Windows LAN Deployment Manual

This manual provides authoritative, step-by-step instructions for deploying the **CharacterConceptAI** system in a Windows Local Area Network (LAN) studio environment.

---

## 1. System Architecture Overview

In a LAN deployment:
- **Server PC (GPU Host):**
  - OS: Windows 10/11 x64.
  - Hardware: NVIDIA RTX GPU (e.g. RTX 4050 6GB or higher), 16GB+ RAM.
  - Software:
    - Microsoft SQL Server 2025 Express (local instance, loopback access only).
    - ASP.NET Core API Service (`CharacterConceptAIServer`) running as a managed Windows Service (ports 5000/5001).
    - ComfyUI running as a supervised user-session background process (`http://127.0.0.1:8188`).
    - Dedicated storage root: `D:\CharacterConceptAIData\Media` and `D:\CharacterConceptAIData\Backups`.
- **Client PCs (Workstations):**
  - OS: Windows 10/11 x64.
  - Software: Self-contained WPF Desktop App (`dist/desktop/CharacterConceptAI.Desktop.exe`).
  - **Prerequisites:** **Zero SDK, zero Visual Studio** required. Only standard Windows Graphics/DirectX drivers.
  - Network: Connected to LAN, trusting the server root CA certificate.

---

## 2. Server Installation & Configuration

### Step 2.1: Publish Release Binaries
On the build machine or server repository, generate self-contained binaries:
```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy\publish-all.ps1
```
Output artifacts are placed in:
- `dist/server/` — Self-contained ASP.NET Core API Server with Windows Service host.
- `dist/desktop/` — Self-contained WPF client application.
- `dist/CHECKSUMS.txt` & `dist/release_manifest.json` — Release hashes.

### Step 2.2: Generate LAN TLS/HTTPS Certificate
Clients must communicate securely over HTTPS without disabling certificate validation.
Run on the server host:
```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy\generate-lan-cert.ps1 `
    -DnsName "concept-server.lan" `
    -IpAddresses @("192.168.0.108", "192.168.1.80") `
    -ValidityYears 3
```
This produces:
- `dist/certs/concept-server.pfx` (installed on server for Kestrel HTTPS binding).
- `dist/certs/concept-server-root.cer` (distributed to client PCs).
- `dist/certs/trust-cert-on-client.ps1` (client installation helper).

### Step 2.3: Configure Windows Firewall
Restrict inbound traffic to API endpoints only, keeping SQL Server and ComfyUI isolated:
```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy\configure-firewall.ps1 -AllowedSubnet "LocalSubnet"
```
Rules created:
- `CharacterConceptAI-API-HTTP` (TCP 5000) -> Allowed for LocalSubnet.
- `CharacterConceptAI-API-HTTPS` (TCP 5001) -> Allowed for LocalSubnet.
- Port 1433 (SQL Server) -> Blocked from LAN (Loopback only).
- Port 8188 (ComfyUI) -> Blocked from LAN (Loopback only).

### Step 2.4: Deploy Database Migrations
Generate and execute idempotent SQL migrations against SQL Server:
```powershell
# 1. Generate SQL migration script
powershell -ExecutionPolicy Bypass -File scripts\deploy\generate-migration-script.ps1

# 2. Execute against target database
sqlcmd -S localhost -d CharacterConceptAI_DEV -i dist\database\deploy_migrations.sql -b
```

### Step 2.5: Bootstrap Initial Administrator Account
Initialize the root administrator securely:
```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy\bootstrap-admin.ps1 `
    -AdminEmail "admin@conceptai.local" `
    -DisplayName "Studio Administrator" `
    -ForcePassword "Admin@Secured2026"
```
*(Note: In production environments, omit `-ForcePassword` to generate a random 20-character secret).*

### Step 2.6: Install Server Windows Service
Register and configure the ASP.NET Core server as an automatic background service:
```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy\install-server-service.ps1 `
    -InstallPath "C:\Program Files\CharacterConceptAI\Server" `
    -DataRoot "D:\CharacterConceptAIData" `
    -ListenPort 5001 `
    -UseHttps
```
Service attributes:
- Service Name: `CharacterConceptAIServer`
- Startup Type: Automatic (Delayed Start)
- Recovery: Auto-restart on failure (1min / 2min / 5min intervals).

### Step 2.7: Start ComfyUI Under GPU-Enabled User Session
Windows Services (Session 0) cannot reliably allocate desktop CUDA/DirectX GPU context on consumer Windows drivers. ComfyUI must run under the interactive desktop user session:
```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy\start-comfyui-supervised.ps1 `
    -ComfyUiDir "C:\GulaStudio\ComfyUI" `
    -ListenIp "127.0.0.1" `
    -Port 8188
```

---

## 3. Client Workstation Deployment

### Step 3.1: Copy Client Binaries
Copy the `dist/desktop/` directory to client workstations:
```text
Destination: C:\Program Files\CharacterConceptAI\Desktop\
Executable:  CharacterConceptAI.Desktop.exe
```

### Step 3.2: Trust the Server Root Certificate
On each client PC, run with Administrator privileges:
```powershell
powershell -ExecutionPolicy Bypass -File dist\certs\trust-cert-on-client.ps1
```
This imports `concept-server-root.cer` into `Cert:\LocalMachine\Root`, enabling secure HTTPS verification without TLS errors.

### Step 3.3: Configure Server Connection
Create a `client-config.json` file in the application directory:
```json
{
  "ServerUrl": "https://192.168.0.108:5001"
}
```
Alternatively, launch via command line:
```cmd
CharacterConceptAI.Desktop.exe --server https://192.168.0.108:5001
```

---

## 4. Verification & Diagnostics

| Diagnostic Action | Expected Result | Troubleshooting |
| :--- | :--- | :--- |
| `curl -k https://<server-ip>:5001/api/v1/health` | HTTP 200 `{"status":"Healthy"}` | Check Windows Service `CharacterConceptAIServer` status and firewall port 5001 |
| Client launches `CharacterConceptAI.Desktop.exe` | Login dialog displays cleanly | Check `client-config.json` and network reachability |
| Admin / User Login | JWT bearer issued, Main Dashboard opens | Verify user credentials in database and server system clock |
| GPU Job Execution | ComfyUI console logs prompt execution, PNG asset stored | Verify ComfyUI process listening on `127.0.0.1:8188` |
