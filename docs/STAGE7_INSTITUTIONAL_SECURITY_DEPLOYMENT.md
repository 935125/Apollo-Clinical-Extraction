# Institutional security deployment contract — not activated

No running database credentials, HBA rules or clinical schema were changed by Stage 7. This procedure requires the institution's deployment owner. The current local demonstration remains loopback-only and unauthenticated.

## Identity and patient access

Configure the real HTTPS OIDC authority/audience and institutional ClinicalReader, ClinicalReviewer and ClinicalAdministrator app roles. Validate access tokens, never ID tokens or client-supplied role/identity headers. Use issuer plus subject as the audit actor. Role assignment does not establish patient entitlement.

Implement `IClinicalDataAccess` using a trusted server-side extraction-to-tenant/patient/encounter binding and current subject entitlement. The resource route is supplied along with the operation and record identifier; quarantine identifiers must resolve through their extraction binding. Fail closed on unknown bindings, absent entitlement, connector failure and cross-tenant access. No administrator bypass is implicit. New extractions require verified patient/encounter authorization and transactional binding before returning a clinical record. The default adapter denies all secure-mode clinical operations.

Existing repository lists/searches are globally scoped. They remain forbidden in secure mode, even for administrators, until repository queries apply tenant/patient scope before pagination and similarity ranking. Do not enable them by fetching globally and filtering afterwards. FHIR, review history, review writes and alternate API routes use the same resource boundary. No patient identity is inferred from note text or trusted merely because a client submitted patientId.

Contract acceptance tests: an entitled reader can read only the bound record; another record returns indistinguishable 404; no reviewer may decide without role plus entitlement; fabricated X-Patient-ID/X-Tenant-ID headers have no effect; unconfigured adapters deny. Integrate clinician sign-in with an institutional SPA authorization-code/PKCE flow. Do not store secrets in Angular or use demo mode to bypass this integration.

## V0008 deployment and reversal

1. Use a maintenance session and validated backup/restore plan. Confirm target database/schema and that the application runs as a non-owner, non-superuser role. Take a schema-only dump of the existing audit table and capture row count, current indexes, owner and exact ACL before any DDL. Never log passwords or patient rows in deployment artifacts.
2. Execute `backend/db/platform/stage7_review_preflight.sql` read-only. Stop on duplicates, invalid keys, orphan reviews, unexpected owner or lock contention. Do not delete audit entries to make the migration pass. The application-database preflight in Stage 7 does not authorize applying V0008 there.
3. Rehearse the exact migration on a restored or isolated schema. Wrap V0008 in `BEGIN; SET LOCAL lock_timeout='5s'; ... COMMIT;`. Test concurrent decisions, version uniqueness, immutable source snapshots and runtime INSERT/SELECT with denied UPDATE/DELETE/TRUNCATE. Verify that other role memberships do not restore those privileges.
4. Deploy only after the owner approves the reviewed target and rehearsal. V0008's `CREATE TABLE IF NOT EXISTS` does not retrofit its CHECK constraints onto a pre-existing table. Its foreign key permits parent-delete cascade; institutions needing legally immutable retention require a separate reviewed retention design.
5. Rollback before commit is `ROLLBACK` and leaves rows/ACL unchanged. For post-commit reversal, quiesce review writes, restore **the exact captured pre-deployment ACL**, and drop only an index proven to have been newly introduced by this migration, if reverting the index is necessary. Do not drop the audit table or delete rows. Preserve any reviews written since deployment. A generic rollback that blindly grants UPDATE/DELETE is unsafe and is intentionally not provided.

Stage 7 verifies transaction rollback/reapplication, row preservation and runtime privileges in an isolated database schema. The production-like application database receives read-only preflight only.

## PostgreSQL authentication change procedure

Loopback `trust` allows any local process able to connect to select any PostgreSQL role without a password. It does not verify that a process is an authorized clinician or application. A role named cx_runtime is not protection against a local user selecting postgres under trust. Local DB TLS is also not established by HTTP API TLS.

Do not edit the running HBA or reset existing credentials automatically. In an isolated cluster, rehearse a new restricted application role, secret-manager delivery and SCRAM-SHA-256 credentials over TLS with server-name/certificate validation. Prefer a new role to rotating a shared working credential blindly. Preserve an owner recovery session and a byte-for-byte HBA backup before a scheduled deployment. Configure hostssl entries for the exact DB/role/network and reject unintended connections; avoid broad trust fall-through. Check `pg_hba_file_rules` for parsing errors and separately test fresh permitted/rejected connections. That view describes file contents, not proof of last-loaded rules. Windows rule reload behavior must be verified for the installed server.

Rollback restores the captured HBA and prior application secret reference, signals/reloads as appropriate, and tests a fresh connection. Retain the recovery session until both forward and reverse checks pass. Never remove the old credential until the replacement is confirmed and institutional rotation policy permits it. No password or reload steps were executed on the running cluster in Stage 7.

Use direct HTTPS or a known trusted TLS proxy; reject untrusted forwarded headers. Store DB/Foundry secrets in approved secret providers and use least-privilege roles, verified database TLS, backups, external audit retention, distributed rate controls and institutional privacy approval before production.

References: [ASP.NET resource authorization](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/resource-based?view=aspnetcore-10.0), [PostgreSQL HBA](https://www.postgresql.org/docs/current/auth-pg-hba-conf.html), [HBA file-rule diagnostics](https://www.postgresql.org/docs/18/view-pg-hba-file-rules.html).
