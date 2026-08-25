# SQL Views

## Overview

A view is a read-only, named SQL query stored in the table catalog. It does not
create another LSM store or copy rows. At read time, the SQL engine evaluates
the stored `SELECT` against the current base table data.

This keeps views consistent with the existing three-paradigm model: key/value,
document, and relational tables continue to use the same storage engine, while a
view is a catalog-layer projection over one of those tables.

## SQL interface

```sql
CREATE VIEW gold_users AS
SELECT key, value FROM users WHERE value.tier = 'gold';

SELECT * FROM gold_users;
```

The current implementation accepts a single `SELECT` definition and evaluates
it when the view is read. Views are read-only; `INSERT`, `UPDATE`, and `DELETE`
against a view are rejected. The base table remains writable.

## Catalog representation

Views are persisted in `data/catalog.json` and have no directory under
`data/tables`:

```json
{
  "views": [
    {
      "name": "gold_users",
      "kind": "view",
      "query": "SELECT key, value FROM users WHERE value.tier = 'gold'"
    }
  ]
}
```

The table listing and `SHOW TABLES` expose `kind: "view"` for views and
`kind: "table"` for physical tables. Existing catalogs without a `views`
property remain compatible.

## Execution and consistency

- View definitions are metadata, not data rows, so creating a view does not
  write to a table WAL or change log.
- Every read re-evaluates the definition, so results reflect committed changes
  in the base table.
- A view does not have table storage, SSTables, indexes, or a table leader of
  its own.
- View reads use the same SQL execution path as the stored definition,
  including relational schema validation and existing JSON-property filters.
- Recursive view evaluation is not supported; expansion is bounded and fails safely.

## Future work

The initial implementation deliberately keeps the surface small. Future
iterations can add `DROP VIEW`, `CREATE OR REPLACE VIEW`, dependency tracking, cycle detection, and outer predicates/projections over a view. Peer metadata discovery and recovery are described in [the metadata discovery design](./metadata-discovery.md).