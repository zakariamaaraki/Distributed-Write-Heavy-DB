#!/usr/bin/env python3
"""HTTP benchmark runner for key/value, document, relational, and full-text modes."""
from __future__ import annotations
import argparse, concurrent.futures, json, math, subprocess, threading, time
import urllib.error, urllib.request
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

@dataclass
class Result:
    paradigm: str
    workload: str
    operations: int
    elapsed_seconds: float
    errors: int
    p50_ms: float
    p95_ms: float
    p99_ms: float
    @property
    def ops_per_second(self) -> float:
        return self.operations / self.elapsed_seconds if self.elapsed_seconds else 0.0

class Api:
    def __init__(self, url: str, timeout: float, strong_reads: bool = False):
        self.url, self.timeout, self.strong_reads = url.rstrip("/"), timeout, strong_reads
    def request(self, method: str, path: str, body: Any = None, headers: dict[str, str] | None = None) -> Any:
        data = None if body is None else json.dumps(body).encode()
        req = urllib.request.Request(self.url + path, data=data, method=method,
            headers={"Accept": "application/json", "Content-Type": "application/json", **(headers or {})})
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as response:
                raw = response.read()
                return json.loads(raw) if raw else None
        except urllib.error.HTTPError as error:
            detail = error.read().decode("utf-8", "replace")
            raise RuntimeError(f"HTTP {error.code}: {detail[:300]}") from error
    def read(self, path: str) -> Any:
        headers = {"X-Read-Consistency": "strong"} if self.strong_reads else None
        return self.request("GET", path, headers=headers)
    def sql(self, query: str) -> Any:
        read_query = query.lstrip().upper().startswith(("SELECT", "SHOW", "SEARCH"))
        headers = {"X-Read-Consistency": "strong"} if self.strong_reads and read_query else None
        return self.request("POST", "/sql", {"query": query, "transactionId": None}, headers=headers)
    def put(self, table: str, key: str, value: str) -> Any:
        return self.request("PUT", f"/tables/{table}/kv/{key}", {"value": value})

def percentile(values: list[float], fraction: float) -> float:
    if not values: return 0.0
    return sorted(values)[min(len(values) - 1, math.ceil(len(values) * fraction) - 1)]

def measure(paradigm: str, workload: str, count: int, workers: int, operation: Callable[[int], None]) -> Result:
    values, errors, samples, mutex = [], 0, [], threading.Lock()
    def invoke(index: int) -> None:
        nonlocal errors
        started = time.perf_counter()
        try: operation(index)
        except Exception as error:
            with mutex:
                errors += 1
                if len(samples) < 3: samples.append(str(error))
        finally:
            with mutex: values.append((time.perf_counter() - started) * 1000)
    started = time.perf_counter()
    with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as pool:
        list(pool.map(invoke, range(count)))
    elapsed = time.perf_counter() - started
    if samples: print(f"  sample errors: {samples}")
    return Result(paradigm, workload, count, elapsed, errors,
        percentile(values, .50), percentile(values, .95), percentile(values, .99))

def literal(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"

def key_value(api: Api, table: str, n: int, workers: int) -> list[Result]:
    api.request("PUT", f"/tables/{table}")
    value = "x" * 256
    return [
        measure("key_value", "put", n, workers, lambda i: api.put(table, f"k{i:08d}", value)),
        measure("key_value", "get", n, workers, lambda i: api.read(f"/tables/{table}/kv/k{i:08d}")),
        measure("key_value", "range", max(1, n // 10), workers,
            lambda i: api.read(f"/tables/{table}/kv/range?start=k00000000&end=k{n:08d}&limit=100")),
    ]

def document(api: Api, table: str, n: int, workers: int) -> list[Result]:
    api.request("PUT", f"/tables/{table}")
    def value(i: int) -> str:
        return json.dumps({"name": f"user-{i}", "tier": "gold" if i % 2 == 0 else "silver", "score": i})
    return [
        measure("document", "put", n, workers, lambda i: api.put(table, f"doc{i:08d}", value(i))),
        measure("document", "get", n, workers, lambda i: api.read(f"/tables/{table}/kv/doc{i:08d}")),
        measure("document", "dot_path_filter", max(1, n // 10), workers,
            lambda i: api.sql(f"SELECT key, value FROM {table} WHERE value.tier = 'gold' LIMIT 100")),
    ]

def relational(api: Api, table: str, n: int, workers: int) -> list[Result]:
    api.sql(f"CREATE TABLE {table} (id INTEGER PRIMARY KEY, name VARCHAR(255) NOT NULL, active BOOLEAN)")
    def insert(i: int) -> None:
        active = "TRUE" if i % 2 == 0 else "FALSE"
        api.sql(f"INSERT INTO {table} (id, name, active) VALUES ({i}, {literal(f'user-{i}')}, {active})")
    return [
        measure("relational", "insert", n, workers, insert),
        measure("relational", "point_select", n, workers,
            lambda i: api.sql(f"SELECT id, name, active FROM {table} WHERE id = {i}")),
        measure("relational", "update", n, workers,
            lambda i: api.sql(f"UPDATE {table} SET name = {literal(f'updated-{i}')} WHERE id = {i}")),
    ]

def full_text(api: Api, table: str, index: str, n: int, workers: int) -> list[Result]:
    api.request("PUT", f"/tables/{table}")
    def value(i: int) -> str:
        return json.dumps({"title": f"distributed database article {i}",
            "body": "write heavy storage engine with durable sstables"})
    writes = measure("full_text", "indexed_put", n, workers,
        lambda i: api.put(table, f"article{i:08d}", value(i)))
    started = time.perf_counter()
    api.request("PUT", f"/search/indexes/{index}",
        {"table": table, "fields": ["value.title", "value.body"]})
    build = time.perf_counter() - started
    results = [Result("full_text", "index_build", 1, build, 0, build * 1000, build * 1000, build * 1000), writes]
    results += [
        measure("full_text", "term_search", max(1, n // 10), workers,
            lambda i: api.request("POST", f"/search/{index}", {"query": "distributed database", "limit": 20})),
        measure("full_text", "phrase_search", max(1, n // 10), workers,
            lambda i: api.request("POST", f"/search/{index}", {"query": '"write heavy"', "limit": 20})),
    ]
    return results

def stats(prefix: str) -> list[dict[str, str]]:
    try:
        output = subprocess.check_output(["docker", "stats", "--no-stream", "--format",
            "{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}"], text=True, stderr=subprocess.DEVNULL)
    except (OSError, subprocess.CalledProcessError):
        return []
    return [dict(zip(("container", "cpu", "memory"), line.split("|", 2)))
        for line in output.splitlines() if line.startswith(prefix)]

def wait_ready(api: Api, seconds: float = 90) -> None:
    deadline = time.monotonic() + seconds
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        try:
            api.request("GET", "/tables")
            return
        except Exception as error:
            last_error = error
            time.sleep(.5)
    raise RuntimeError(f"database did not become ready: {last_error}")

def markdown_report(report: dict[str, Any]) -> str:
    run = report["run"]
    lines = [
        "# Benchmark {}".format(run["id"]), "",
        "- Mode: {}".format(run["mode"]),
        "- Endpoint: {}".format(run["base_url"]),
        "- Records per paradigm: {}".format(run["records"]),
        "- Concurrency: {}".format(run["concurrency"]), "",
        "| Paradigm | Workload | Ops/s | p50 ms | p95 ms | p99 ms | Errors |",
        "|---|---|---:|---:|---:|---:|---:|",
    ]
    for item in report["phases"]:
        lines.append("| {paradigm} | {workload} | {ops:.1f} | {p50:.2f} | {p95:.2f} | {p99:.2f} | {errors} |".format(
            paradigm=item["paradigm"], workload=item["workload"], ops=item["ops_per_second"],
            p50=item["p50_ms"], p95=item["p95_ms"], p99=item["p99_ms"], errors=item["errors"]))
    lines += ["", "## Interpretation", "",
        "Throughput is completed operations divided by elapsed wall-clock time. Latencies are measured per HTTP operation. Compare runs only when record count, payloads, concurrency, Docker limits, storage, and consistency mode are identical."]
    return "\n".join(lines)
def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=("standalone", "distributed"), default="standalone")
    parser.add_argument("--base-url")
    parser.add_argument("--records", type=int, default=1000)
    parser.add_argument("--concurrency", type=int, default=8)
    parser.add_argument("--timeout", type=float, default=30)
    parser.add_argument("--paradigm", choices=("all", "key_value", "document", "relational", "full_text"), default="all")
    parser.add_argument("--output", default="benchmarks/results")
    args = parser.parse_args()
    base = args.base_url or ("http://localhost:18080" if args.mode == "standalone" else "http://localhost:9081")
    api, run_id = Api(base, args.timeout, args.mode == "distributed"), datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    wait_ready(api)
    prefix = f"bench_{args.mode}_{run_id.lower()}"
    builders = {
        "key_value": lambda: key_value(api, prefix + "_kv", args.records, args.concurrency),
        "document": lambda: document(api, prefix + "_doc", args.records, args.concurrency),
        "relational": lambda: relational(api, prefix + "_rel", args.records, args.concurrency),
        "full_text": lambda: full_text(api, prefix + "_fts", prefix + "_search", args.records, args.concurrency),
    }
    selected = list(builders) if args.paradigm == "all" else [args.paradigm]
    report = {"run": {"id": run_id, "mode": args.mode, "base_url": base,
        "records": args.records, "concurrency": args.concurrency},
        "container_stats_before": stats("lsm-write-db" if args.mode == "standalone" else "lsmwritedb"), "phases": []}
    for name in selected:
        print(f"Running {name}")
        for result in builders[name]():
            item = asdict(result) | {"ops_per_second": result.ops_per_second}
            report["phases"].append(item)
            print(f"  {result.workload}: {result.ops_per_second:.1f} ops/s p95={result.p95_ms:.2f}ms errors={result.errors}")
    report["container_stats_after"] = stats("lsm-write-db" if args.mode == "standalone" else "lsmwritedb")
    output = Path(args.output); output.mkdir(parents=True, exist_ok=True)
    destination = output / f"{run_id}-{args.mode}.json"
    destination.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"Raw results: {destination}")
    markdown = destination.with_suffix(".md")
    markdown.write_text(markdown_report(report), encoding="utf-8")
    print(f"Markdown report: {markdown}")
    return 0 if all(item["errors"] == 0 for item in report["phases"]) else 1

if __name__ == "__main__":
    raise SystemExit(main())

