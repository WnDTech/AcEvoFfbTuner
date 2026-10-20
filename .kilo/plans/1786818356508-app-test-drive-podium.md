# API outage — root cause found: error 2014 from ANALYZE TABLE via exec()

## Evidence (host error log, 2026-10-02 23:07)

```
[db.php] unique credit guard creation failed: SQLSTATE[HY000]: General error: 2014
  Cannot execute queries while other unbuffered queries are active... — deduping + retrying
[db.php] credit guard still failing after dedupe: (same 2014)
[beta.php] (same 2014) @ beta.php:376 / :545 / :563
```

## Root cause (my regression)

`ANALYZE TABLE beta_credits` was added to `ensureSchema` via
`$pdo->exec(...)`. `ANALYZE TABLE` **returns a result set**; `exec()` does not
consume it, leaving an unbuffered pending result on the connection. Every
subsequent query on that request then throws MySQL error 2014.

Why the symptom pattern fits exactly:
- `status` / `podium` / `me` → call `db()` → poisoned connection → 500
- unknown actions → never call `db()` → 404 (dispatch alive)
- The guard index itself already exists (`#1061` earlier); only the migration
  path is broken.

## Fix (website/api/db.php only)

1. **Remove both `ANALYZE TABLE ... exec()` calls.** `db_status` now derives
   its verdict from `SHOW CREATE TABLE` (live metadata), so the
   information_schema stats cache no longer matters — ANALYZE was unnecessary
   and is the outage source.
2. **Make the unique-guard migration conditional**: check `SHOW INDEX FROM
   beta_credits` with `query()->fetchAll()` (result consumed) and only attempt
   `CREATE UNIQUE INDEX` when the guard is missing. Removes per-request
   attempts and log spam now that the index exists.
3. **Add `PDO::MYSQL_ATTR_USE_BUFFERED_QUERY => true`** to the MySQL PDO
   options (the error's own recommendation) — hardening so a stray unconsumed
   result can never poison the connection again.
4. Keep dedupe + retry, with `error_log` only on genuine failure (no noise on
   the happy path).

`beta.php` unchanged — the :376/:545/:563 errors are collateral from the
poisoned connection.

## Verification after deploy

1. `?action=status` and `?action=podium` → **200** (currently 500)
2. Admin dashboard loads; `me` → 200; banner shows healthy (guard exists,
   evidence printed if not)
3. No new `[db.php]` / `2014` lines in the host error log
4. Click Verified → task ticks Verified, 1 credit row, `verifiedTasks: 1`

## Files

- `website/api/db.php`

## Out of scope

No commits/pushes, no app (C#) changes, README already documents the guard +
self-heal (the ANALYZE mention should be trimmed when editing README next).
