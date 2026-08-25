"""Run 1,000 SELECT * queries concurrently through the Python client."""
from __future__ import annotations

import argparse
import time
from concurrent.futures import ThreadPoolExecutor

from common import client_from_args


def main() -> None:
    _, client = client_from_args(__doc__)
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--table", default="python_benchmark_bulk")
    parser.add_argument("--queries", type=int, default=1_000)
    parser.add_argument("--workers", type=int, default=1_000)
    parser.add_argument("--limit", type=int, default=1_000)
    options, _ = parser.parse_known_args()
    if min(options.queries, options.workers, options.limit) <= 0:
        raise ValueError("--queries, --workers, and --limit must be positive")

    query = f"SELECT * FROM {options.table} LIMIT {options.limit}"
    started = time.monotonic()
    with ThreadPoolExecutor(max_workers=options.workers, thread_name_prefix="lsm-select") as executor:
        futures = [executor.submit(client.execute_sql, query) for _ in range(options.queries)]
        results = [future.result() for future in futures]

    elapsed = time.monotonic() - started
    rows = sum(len(result.get("rows", [])) for result in results)
    print(f"queries={len(results)} rows_returned={rows} elapsed_seconds={elapsed:.1f} queries_per_second={len(results) / elapsed:.1f}")


if __name__ == "__main__":
    main()