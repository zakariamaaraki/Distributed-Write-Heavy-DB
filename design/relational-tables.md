# Relational Tables

## Overview

Relational tables are a schema-enforced layer over the existing per-table LSM
storage. The physical representation remains a string key plus a JSON value,
so WAL recovery, memtables, SSTables, change-log publication, snapshots, and
replication continue to use the existing paths.

A relational table is created with one primary-key column and zero or more JSON
columns:

```sql
CREATE TABLE users (
    id INT PRIMARY KEY,
    name TEXT NOT NULL,
    active BOOLEAN
)
```

The physical row for `id = 42` is:

```text
key:   "42"
value: {"name":"Ada","active":true}
```

The primary key is deliberately not duplicated in the JSON value.

## Catalog representation

The table catalog remains backward compatible with the existing `tables` array
and adds relational schema entries:

```json
{
  "tables": ["kv", "users"],
  "relationalTables": [
    {
      "table": "users",
      "columns": [
        { "name": "id", "type": "int", "isPrimaryKey": true, "isNullable": false },
        { "name": "name", "type": "text", "isPrimaryKey": false, "isNullable": false },
        { "name": "active", "type": "boolean", "isPrimaryKey": false, "isNullable": true }
      ]
    }
  ]
}
```

Old catalogs without `relationalTables` continue to load as schemaless tables.

## Validation rules

- Exactly one column must be marked `PRIMARY KEY`.
- Supported types are `TEXT`, `INT`, `BIGINT`, `BOOLEAN`, and `DOUBLE`.
- The physical key must parse as the declared primary-key type.
- Row values must be JSON objects.
- The primary-key property must not appear in the JSON value.
- Unknown properties are rejected.
- Missing `NOT NULL` properties and type mismatches are rejected.
- Nullable properties may be omitted or explicitly set to `null`.
- Primary-key changes are not supported; use delete plus insert.

Validation runs before writes enter the WAL, including direct database writes,
SQL writes, and transaction commits. Schemaless `CREATE TABLE` tables retain the
existing behavior.

## Scope and follow-up work

This first layer intentionally does not implement foreign keys, multi-column
primary keys, `UNIQUE` constraints, or schema migrations. Those can be added to
the catalog and validation pipeline later without changing the physical row
format.