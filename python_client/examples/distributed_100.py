"""Create two relational tables and commit 100 distributed transactions."""
from __future__ import annotations

import argparse
import json
import time

from common import client_from_args

TABLE_A = "python_benchmark_accounts"
TABLE_B = "python_benchmark_events"
TRANSACTION_COUNT = 100


def main() -> None:
    args, client = client_from_args(__doc__)
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--table-a", default=TABLE_A)
    parser.add_argument("--table-b", default=TABLE_B)
    parser.add_argument("--count", type=int, default=TRANSACTION_COUNT)
    options, _ = parser.parse_known_args()

    client.execute_sql(f"CREATE TABLE {options.table_a} (id INTEGER PRIMARY KEY, account VARCHAR(64) NOT NULL, amount DOUBLE NOT NULL)")
    client.execute_sql(f"CREATE TABLE {options.table_b} (id INTEGER PRIMARY KEY, account VARCHAR(64) NOT NULL, event_type VARCHAR(64) NOT NULL)")

    started = time.monotonic()
    for row_id in range(options.count):
        transaction = client.begin_distributed()
        transaction.write(options.table_a, str(row_id), json.dumps({"account": f"account-{row_id}", "amount": 1.0}))
        transaction.write(options.table_b, str(row_id), json.dumps({"account": f"account-{row_id}", "event_type": "deposit"}))
        result = transaction.commit()
        if result.get("status") != "committed":
            raise RuntimeError(f"distributed transaction {row_id} did not commit: {result}")
        print(f"committed transaction={row_id + 1}/{options.count} id={transaction.id}", flush=True)
    elapsed = time.monotonic() - started
    print(json.dumps({"tableA": options.table_a, "tableB": options.table_b, "transactions": options.count, "elapsedSeconds": elapsed}, indent=2))


if __name__ == "__main__":
    main()
