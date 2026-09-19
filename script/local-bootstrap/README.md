# Local SQL Server Bootstrap Foundation (Slice 4.2A)

## 1. Overview
This directory contains scripts and tooling to establish and verify the Local-First operational database foundation for IProgram.

Under Slice 4.2A:
- **Business Owner Decision:** The existing local SQL Server 2014 instance on the workstation is retained as the development/test SQL engine. Installation of SQL Server 2022 Express and creation of final local operational databases are deferred to an explicitly authorized future slice.
- Operational database naming contract:
  - `IProgramLocalDb2026` for canonical year 2026
  - `IProgramLocalDb2027` for canonical year 2027
- Remote Azure production databases (`IProgramDb2026`, `IProgramDb2027`) remain authoritative and untouched.
- `LocalFirst:Enabled` is set to `false` by default, preserving current Azure-routed application behavior.

---

## 2. Scripts Included

| Script | Purpose | Elevation |
| :--- | :--- | :--- |
| `check_local_sql_readiness.ps1` | Scans local SQL instances, tests transport protocol via `net_transport` & Windows Integrated Security, checks for reserved databases, and outputs sanitized audit JSON. | Standard User |
| `install_sqlexpress.ps1` | Reference automated installer for SQL Server 2022 Express instance `SQLEXPRESS` with Windows Integrated Security (installation deferred per Business Owner decision). | **Administrator** |

---

## 3. Usage Instructions

### Checking Local Readiness:
Run in PowerShell (standard permissions):
```powershell
powershell -ExecutionPolicy Bypass -File .\script\local-bootstrap\check_local_sql_readiness.ps1 -OutputJsonPath .\docs\audit\sync-slice-4-2a\local_sql_readiness_report.json
```

---

## 4. Security Hardening & Isolation Rules

1. **Windows Integrated Security:**
   - No SQL Server `sa` account or SQL authentication passwords are required or configured.
   - Access is granted exclusively to local Windows accounts via Integrated Security.
2. **Local Loopback Isolation:**
   - Operational connections are strictly validated to point to local host endpoints (`localhost`, `.`, `127.0.0.1`, `(local)`). Remote endpoints for local operational databases are rejected fail-closed.
3. **No Credential Logging:**
   - Readiness diagnostics, audit reports, and connection resolvers strictly omit machine names, user names, credentials, connection strings, and tokens.
4. **Boundary Protection:**
   - `LocalSyncContext` strictly validates that it never connects to remote Azure databases (`IProgramDb2026` / `IProgramDb2027`).
   - `AzureDatabaseBinding` strictly validates that it never binds to local databases (`IProgramLocalDb2026` / `IProgramLocalDb2027`).
5. **Central Write-Gating:**
   - ApplicationContext writes are automatically intercepted server-side by `LocalBootstrapWriteGateInterceptor` when LocalFirst is enabled, throwing `BootstrapNotVerifiedException` unless `BootstrapManifest` has status `VERIFIED_READY` and `IsWriteAllowed = true`.
