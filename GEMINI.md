# GEMINI.md — IProgram Project Context & Operating Rules

> This file is the stable project memory for AI coding agents working in this repository.
> Read it at the start of a session, then use targeted verification for the files involved in the current task.
> Do NOT repeatedly rediscover the entire repository unless there is a concrete reason to do so.

## 1. Repository Identity

- Product name: **IProgram**
- GitHub repository: **AlexandriaDeveloper/IProgram2**
- Default branch: **master**
- Solution: `IProgram.sln`
- Backend: **ASP.NET Core / .NET 10**
- Frontend: **Angular 17**
- Data access: **EF Core + SQL Server / Azure SQL**
- Authentication: **ASP.NET Core Identity + JWT**
- Current architecture style: Clean-Architecture-like layered solution.

Do not confuse this repository with:
- `AlexandriaDeveloper/IProgram` — not the active IProgram codebase.
- `AlexandriaDeveloper/Payroll-Auditor` — a different project.

## 2. Stable Architecture Map

### Frontend
- `Client/` — Angular application.
- Production Angular output is expected directly under `src/Api/wwwroot/`.
- The combined ASP.NET Core artifact serves the SPA from `wwwroot`.
- A legacy nested `src/Api/wwwroot/browser/` output is considered invalid by CI.

### Backend
- `src/Api/` — ASP.NET Core host, controllers/endpoints, startup/composition root.
- `src/Application/` — application use cases, DTOs, application-level services.
- `src/Core/` — domain/core models, contracts, core business rules.
- `src/Infrastructure/` — infrastructure implementations and integrations.
- `src/Persistence/` — persistence/data-access concerns.

### Tests
- `tests/` — automated tests.
- Main CI unit-test project: `tests/Auth.UnitTests/Auth.UnitTests.csproj`.
- Some tests may require a local SQL database and are intentionally filtered in standard CI.

### Operations / documentation
- `.github/workflows/ci.yml` — authoritative CI pipeline.
- `RELEASE_RUNBOOK.md` — controlled deployment/migration procedure.
- `SETUP_MACHINE_SECRETS.md` — machine secret setup.
- `script/` — operational/deployment/sync scripts.
- `docs/` — design and slice-specific technical documentation.
- `.antigravityignore` — paths that should not be scanned for normal code reasoning.

## 3. Project Priorities

The priorities below are architectural constraints, not optional suggestions:

1. **Keep the existing online application usable and available.**
2. **Protect all existing production data.**
3. **Preserve year/database isolation.**
4. **Introduce local/offline/sync capabilities incrementally without breaking the online path.**
5. **Prefer small, reviewable, reversible slices over broad rewrites.**
6. **No hidden fallback behavior that can silently route a local/offline operation to Azure.**
7. **Evidence before merge: build, tests, CI, and relevant invariants must be demonstrated.**

When a new feature conflicts with uptime, data safety, or isolation, redesign the feature rather than weakening these constraints.

## 4. Database & Data-Safety Invariants

### Production data is protected

Never perform any of the following on an operational database without an explicit user instruction for that exact operation:

- DROP database/table/column
- TRUNCATE
- destructive RECREATE
- destructive RESEED
- bulk replacement of existing production data
- uncontrolled schema migration
- ad-hoc production repair DML
- manual sync checkpoint rewriting
- automatic startup migration

Never add or enable:
- `Database.Migrate()` on startup
- `Database.EnsureCreated()` on startup

Schema changes for operational databases must remain controlled and out-of-band.

### Existing data must be preserved

- Azure data is operationally important and must not be treated as disposable test data.
- Existing IDs and relationships must be preserved unless an approved migration explicitly says otherwise.
- Local/offline work must not corrupt or silently overwrite the authoritative online dataset.

### Year isolation

IProgram supports separate operational years, including 2026 and 2027.

Database selection is security-sensitive.

Rules:
- Preserve the selected year/database across JWT claims, connection providers, caching, sync, and API calls.
- Never silently fall back from one year to another.
- Never reuse cached data across year/database boundaries.
- Never allow a request parameter to override an authenticated database binding without an explicitly designed and reviewed mechanism.
- Before any database-sensitive operation, verify the actual target database.

### SQL compatibility

The local/offline database direction is based on SQL Server with **SQL Server 2014 compatibility requirements**.

Do not change the local database engine or upgrade the compatibility assumption as a side effect of unrelated work.

## 5. Online / Local-First / Sync Model

The project is evolving toward local-first/offline operation while retaining Azure as the central authoritative online data source.

Stable principles:

- Azure remains the central authoritative source.
- Local databases are separate per year, e.g. local 2026 and local 2027 databases.
- Sync logic must preserve year isolation.
- Sync operations must be idempotent and fail closed.
- Do not invent conflict resolution silently.
- Do not use manual checkpoint manipulation as a shortcut.
- Do not bypass the production application path with ad-hoc SQL when validating sync behavior.
- Sync must be introduced in controlled slices.
- Online usability must continue while sync/offline work progresses.

The pull path currently follows the application path conceptually:

`POST /api/sync/pull`
→ `LocalDailyPullService`
→ `AzureFencedBatchReader`
→ `LocalPullTransactionCoordinator`

Treat exact class names, endpoints, feature flags, schema state, watermarks, row counts, and current rollout phase as **dynamic state**. Verify them from the current branch before modifying them.

## 6. Production Availability Rule

**Do not make the online application depend on unfinished offline/sync functionality.**

Every implementation plan must answer:

- Will the existing online app still start?
- Will login still work?
- Will 2026/2027 selection still work?
- Will normal online reads/writes still work unless the current slice explicitly changes them?
- Can the new feature remain disabled or isolated until ready?
- Is rollback possible without production data loss?

Prefer additive feature flags, isolated code paths, and backward-compatible changes.

Do not disable a working online capability merely to simplify an unfinished local-first implementation.

## 7. Context Retrieval Policy — Avoid Re-Scanning the Repository

### Start-of-session minimum freshness check

At the start of a new coding session, do only the minimum repository-state check first:

```powershell
git status --short
git branch --show-current
git log -1 --oneline
```

If the task involves a PR or remote branch, also verify the relevant branch/PR state.

Do **not** immediately run a repository-wide recursive read.

### Use the map before discovery

Use this file as the initial architecture map.

For a normal task:

1. Identify the feature/layer from this map.
2. Search for the relevant symbol/file name.
3. Read only the target file and its direct dependencies.
4. Before editing a file, re-read its current version if it may have changed.
5. After editing, use `git diff` to inspect the actual changes.
6. Expand the working set only when a concrete dependency requires it.

### Search first, read second

Prefer targeted searches such as:

```powershell
rg "ClassName|MethodName|RouteName" src Client tests
git grep "ClassName"
```

Do not open dozens of files just to locate one symbol.

### Working-set rule

Once a file was read in the current session and has not changed:
- keep its role in the session working set;
- do not repeatedly re-read the full file without a reason.

Re-read when:
- the file was edited;
- another process/user may have changed it;
- a merge/rebase occurred;
- a compiler/test error indicates the previous understanding may be stale;
- exact current code is required before a patch.

### Repository-wide scans are exceptional

A broad scan is justified only when:
- architecture has materially changed;
- the user explicitly asks for a full audit;
- a dependency cannot be located through targeted search;
- a refactor intentionally crosses many modules;
- the current architecture map is proven stale.

If a broad scan is needed, explain why internally and constrain it to relevant source paths.

## 8. Files and Paths to Ignore During Normal Reasoning

Do not inspect these unless the task explicitly requires them:

- `bin/`
- `obj/`
- `.vs/`
- `.vscode/`
- `node_modules/`
- `dist/`
- `publish/`
- generated build artifacts
- logs
- temporary files
- binary assets
- `*.xlsx`, `*.png`, `*.jpg`, `*.pdf` unless the task is about those files
- large generated `wwwroot` bundles unless diagnosing packaging/output

Prefer source files over generated output.

## 9. Dynamic State — Never Cache These as Permanent Facts

The following values change frequently and MUST be verified when relevant:

- current commit SHA
- current branch
- open PR numbers/status
- latest merged slice
- CI status
- production deployment version
- current Azure/local sync watermark
- `CurrentVersion`
- change-feed/tombstone counts
- row counts
- current feature-flag values
- migration history
- database schema state
- active lease state
- outbox state
- current production URL/hosting slot
- current secrets/configuration values

Do not put transient values into this file as permanent truth.

For production-sensitive work, prefer a fresh read-only verification over memory.

## 10. Configuration & Secrets

Never commit:
- passwords
- connection strings containing secrets
- JWT signing secrets
- API keys
- production credentials
- real user credentials

Use:
- User Secrets for development where appropriate
- environment variables
- deployment-platform application settings

Do not print secrets into logs, PR descriptions, test evidence, or terminal summaries.

Do not copy secrets from one machine into source control.

## 11. Build & Test Commands

### Backend

```powershell
dotnet restore IProgram.sln
dotnet build IProgram.sln --configuration Release
dotnet test tests/Auth.UnitTests/Auth.UnitTests.csproj --configuration Release --filter "Category!=LocalDbRequired"
```

### Frontend

```powershell
cd Client
npm ci
npm run build
cd ..
```

### Combined release artifact

The CI packaging guard expects:
- Angular output directly under `src/Api/wwwroot/`
- `src/Api/wwwroot/index.html`
- JS bundles
- CSS bundles
- no legacy `src/Api/wwwroot/browser/index.html`

Backend publish command:

```powershell
dotnet publish src/Api/Auth.Api.csproj -c Release -o publish/api
```

Use the current `.github/workflows/ci.yml` as the authoritative CI definition if these commands ever diverge.

## 12. Testing Strategy

Use the narrowest relevant verification first, then expand.

Recommended order:

1. Compile/test the affected project or focused test set.
2. Run related unit/integration tests.
3. Build Angular if frontend or packaging is affected.
4. Run full repository-required CI-equivalent checks before declaring a PR ready.

A passing build does not prove database safety.

For DB/sync work, also verify the relevant invariants.

Never run tests against operational databases if the test can mutate data.

Use isolated/transient test databases for mutation-based integration testing.

## 13. Git & Pull Request Workflow

Default workflow:

1. Start from an up-to-date `master`.
2. Create a focused branch.
3. Make the smallest coherent change.
4. Inspect `git diff`.
5. Run relevant tests.
6. Push branch.
7. Open a **Draft PR** while evidence/review is incomplete.
8. Verify CI.
9. Make the PR ready only after blockers are resolved.

Do not merge a PR unless the user explicitly instructs you to merge it.

Do not push unrelated cleanup into a focused feature PR.

Do not rewrite unrelated files merely for style.

If the working tree already contains user changes:
- preserve them;
- do not discard/reset them;
- separate your work where possible.

## 14. CI Is a Release Gate

The repository CI contains three important concerns:

- Backend .NET build/tests
- Angular frontend build
- Combined release packaging verification

Before claiming a branch is ready:
- CI must be green, or
- clearly state exactly which CI check is not yet verified.

Do not treat local success as equivalent to GitHub Actions success.

## 15. Migration Rules

Operational migrations are high risk.

Rules:
- no automatic startup migrations;
- do not generate/apply full-history scripts to existing operational DBs without explicit need;
- prefer targeted migration deltas;
- inspect generated SQL before execution;
- verify target DB with `SELECT DB_NAME()`;
- verify migration history and physical schema before DDL;
- verify backup/restore capability before operational DDL;
- fail closed on schema/history drift;
- do not auto-repair unexpected production schema state.

Use `RELEASE_RUNBOOK.md` for the current detailed procedure.

## 16. Sync-Specific Safety Rules

When touching sync code:

- preserve Azure/local separation;
- preserve 2026/2027 separation;
- preserve idempotency;
- keep push/pull semantics explicit;
- maintain transaction boundaries;
- avoid duplicate outbox generation during pull;
- never "fix" a failed sync by manually rewinding a committed checkpoint;
- do not introduce production-only test bypasses;
- test mutation paths against isolated databases;
- keep operator/test credentials isolated;
- fail closed when database binding or token claims do not match the requested year;
- verify production invariants read-only before any rollout operation.

Feature flag values are dynamic. Read current configuration instead of assuming a flag is enabled or disabled.

## 17. Decision Precedence

When instructions conflict, use this order:

1. The user's explicit current instruction.
2. Production/data-safety invariants in this file.
3. Current approved slice/PR design for the task.
4. `RELEASE_RUNBOOK.md`.
5. Current code and tests.
6. `README.md`.

If a current user request appears to require a destructive or production-sensitive operation, verify scope and safeguards before execution.

Never resolve a contradiction by silently weakening a safety invariant.

## 18. Implementation Style

Prefer:
- small changes
- explicit types
- clear boundaries
- existing patterns
- existing abstractions
- targeted tests
- deterministic behavior
- fail-closed validation for security/data boundaries

Avoid:
- speculative abstractions
- broad rewrites
- duplicate services
- hidden fallback behavior
- unrelated formatting churn
- new packages without a concrete need
- new architecture layers when an existing layer already owns the responsibility

Before creating a new class/service, search for an existing abstraction that already serves the same role.

## 19. Agent Response Contract

After completing a coding task, report concisely:

1. **What changed**
2. **Files changed**
3. **Tests/builds run and result**
4. **Database impact:** none / read-only / local isolated / production mutation
5. **Production impact:** none / backward-compatible / rollout required
6. **Git/PR status**
7. **Any remaining blocker or next concrete step**

Do not bury failures.

If something was not tested, say so directly.

## 20. Anti-Loop Rule

Do not repeatedly perform the same discovery command when the answer is already known in the current session.

Examples of unnecessary repetition:
- listing the same directory repeatedly;
- re-reading unchanged project files;
- running full-tree searches after the target file is already known;
- re-checking architecture that is documented here;
- rebuilding unrelated projects after a documentation-only change.

Repeat a check only when new state could have invalidated the previous result.

## 21. When This File Should Be Updated

Update `GEMINI.md` only for durable project knowledge such as:
- architecture changes;
- permanent safety rules;
- stable folder ownership;
- standard build/test commands;
- long-lived deployment rules.

Do NOT update it for:
- a one-off bug;
- a temporary feature flag;
- a specific current PR;
- a commit SHA;
- temporary row counts;
- a current sync watermark;
- a transient production incident.

That information belongs in PR docs, runbooks, issues, or execution evidence.

---

## Default Agent Behavior

For a normal task, the expected behavior is:

**Understand from this map → perform a minimal freshness check → locate with targeted search → read only the working set → modify narrowly → test narrowly → expand verification only as needed → report evidence.**

The goal is not to avoid reading code.

The goal is to avoid repeatedly rediscovering the entire project while still verifying the exact current code before changing it.
