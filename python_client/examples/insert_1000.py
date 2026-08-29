"""Create one relational table and fire one thousand concurrent insert requests."""
from __future__ import annotations

import argparse
import json
import time
from concurrent.futures import ThreadPoolExecutor

from common import client_from_args

TABLE = "python_benchmark_bulk"
ROW_COUNT = 1000


def main() -> None:
    _, client = client_from_args(__doc__)
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--table", default=TABLE)
    parser.add_argument("--count", type=int, default=ROW_COUNT)
    parser.add_argument("--workers", type=int, default=1_024)
    parser.add_argument("--batch-size", type=int, default=10_000)
    options, _ = parser.parse_known_args()
    if options.workers <= 0 or options.batch_size <= 0:
        raise ValueError("--workers and --batch-size must be positive")

    client.execute_sql(f"CREATE TABLE {options.table} (id INTEGER PRIMARY KEY, payload VARCHAR(64) NOT NULL, ordinal INTEGER NOT NULL)")
    insert = client.prepare(f"INSERT INTO {options.table} (id, payload, ordinal) VALUES (?, ?, ?)")

    def insert_row(row_id: int) -> None:
        insert.execute(row_id, f"row-{row_id}", row_id)

    started = time.monotonic()
    submitted = 0
    futures = []
    with ThreadPoolExecutor(max_workers=options.workers, thread_name_prefix="lsm-insert") as executor:
        for batch_start in range(0, options.count, options.batch_size):
            batch_end = min(batch_start + options.batch_size, options.count)
            futures.extend(executor.submit(insert_row, row_id) for row_id in range(batch_start, batch_end))
            submitted = batch_end
            print(f"submitted={submitted} elapsed_seconds={time.monotonic() - started:.1f}", flush=True)
        # Requests are fired without waiting between submissions; collect errors only after all are submitted.
        for completed, future in enumerate(futures, start=1):
            future.result()
            if completed % options.batch_size == 0 or completed == options.count:
                elapsed = time.monotonic() - started
                print(f"completed={completed} elapsed_seconds={elapsed:.1f} rows_per_second={completed / elapsed:.1f}", flush=True)

    elapsed = time.monotonic() - started
    print(json.dumps({"table": options.table, "rows": options.count, "workers": options.workers, "elapsedSeconds": elapsed, "rowsPerSecond": options.count / elapsed}, indent=2))


if __name__ == "__main__":
    main()