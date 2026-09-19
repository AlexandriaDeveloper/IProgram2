# Local SQL Server Express Bootstrap Foundation (Slice 4.2A)

## 1. Overview
This directory contains scripts and tooling to establish and verify the Local-First operational database foundation for IProgram.

Under Slice 4.2A:
- The local operational database engine is **SQL Server Express** (preferred named instance: `localhost\SQLEXPRESS`).
- Operational database naming contract:
  - `IProgramLocalDb2026` for canonical year 2026
  - `IProgramLocalDb2027` for canonical year 2027
- Remote Azure production databases (`IProgramDb2026`, `IProgramDb2027`) remain authoritative and untouched.
- `LocalFirst:Enabled` is set to `false` by default, preserving current Azure-routed application behavior.

---

## 2. Scripts Included

| Script | Purpose | Elevation |
| :--- | :--- | :--- |
| `check_local_sql_readiness.ps1` | Scans local SQL instances, tests TCP & Windows Integrated Security, checks for reserved databases, and outputs sanitized audit JSON. | Standard User |
| `install_sqlexpress.ps1` | Downloads and silently installs SQL Server 2022 Express instance `SQLEXPRESS` with Windows Integrated Security and local TCP/IP enabled. | **Administrator** |

---

## 3. Usage Instructions

### Checking Local Readiness:
Run in PowerShell (standard permissions):
```powershell
powershell -ExecutionPolicy Bypass -File .\script\local-bootstrap\check_local_sql_readiness.ps1 -OutputJsonPath .\docs\audit\sync-slice-4-2a\local_sql_readiness_report.json
```

### Installing SQL Server 2022 Express:
If `localhost\SQLEXPRESS` is not yet installed:
1. Open PowerShell **as Administrator**.
2. Run:
```powershell
powershell -ExecutionPolicy Bypass -File .\script\local-bootstrap\install_sqlexpress.ps1
```
3. Re-run `check_local_sql_readiness.ps1` to confirm readiness.

---

## 4. Security Hardening & Isolation Rules

1. **Windows Integrated Security:**
   - No SQL Server `sa` account or SQL authentication passwords are required or configured.
   - Access is granted exclusively to local Windows Administrators and the installing Windows account.
2. **Local Loopback Isolation:**
   - The instance is intended strictly for local operational access (`localhost`).
   - Remote connections across public networks or LAN should remain disabled unless explicitly required.
3. **No Credential Logging:**
   - Readiness diagnostics and connection resolvers strictly omit credentials, connection strings, and tokens.
4. **Boundary Protection:**
   - `LocalSyncContext` strictly validates that it never connects to remote Azure databases (`IProgramDb2026` / `IProgramDb2027`).
   - `AzureDatabaseBinding` strictly validates that it never binds to local databases (`IProgramLocalDb2026` / `IProgramLocalDb2027`).
5. **Write-Gating:**
   - Local operational writes are blocked server-side by `ILocalBootstrapWriteGate` until `BootstrapManifest` has status `VERIFIED_READY` and `IsWriteAllowed = true` (enforced during Slice 4.2B).
