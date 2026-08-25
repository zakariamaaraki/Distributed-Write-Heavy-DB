"""Drop the tables created by the benchmark scripts."""
from __future__ import annotations

import argparse
import time

from common import client_from_args
from lsmwrite_client import LsmWriteDbHttpError


def drop_table(client, table: str) -> bool:
    try:
        client.drop_table(table)
        print(f"dropped table={table}", flush=True)
        return True
    except LsmWriteDbHttpError as error:
        if error.status == 404:
            print(f"table={table} already absent", flush=True)
            return False
        raise


def main() -> None:
    _, client = client_from_args(__doc__)
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--bulk-table", default="python_benchmark_bulk")
    parser.add_argument("--table-a", default="python_benchmark_accounts")
    parser.add_argument("--table-b", default="python_benchmark_events")
    options, _ = parser.parse_known_args()

    started = time.monotonic()
    dropped = sum(drop_table(client, table) for table in (options.bulk_table, options.table_a, options.table_b))
    print(f"dropped_tables={dropped} elapsed_seconds={time.monotonic() - started:.1f}")


if __name__ == "__main__":
    main()