# Benchmark suite

This suite measures the four supported paradigms through HTTP:

- key/value: writes, point reads, and range reads;
- document: JSON writes, point reads, and dot-path filters;
- relational: typed inserts, primary-key reads, and updates;
- full-text: indexed writes, index build time, term search, and phrase search.

It supports a single standalone node and the three-node Docker cluster. Every
run creates uniquely named tables, reports throughput, errors, p50/p95/p99
latency, and writes raw JSON plus Markdown reports under `benchmarks/results/`.

## Standalone

```powershell
docker compose -f benchmarks/docker-compose.standalone.yml up --build -d
python benchmarks/run.py --mode standalone --records 1000 --concurrency 8
docker compose -f benchmarks/docker-compose.standalone.yml down -v
```

## Three-node distributed mode

```powershell
docker compose -f docker-compose.yml -f benchmarks/docker-compose.resources.yml up --build -d
python benchmarks/run.py --mode distributed --records 1000 --concurrency 8
docker compose -f docker-compose.yml -f benchmarks/docker-compose.resources.yml down -v
```

The distributed runner uses Router A (`http://localhost:9081`) so requests
exercise leader discovery and routing. The resource override gives each node
two CPUs and 4 GiB of memory, and each Router one CPU and 1 GiB. Adjust these
limits to match the host before comparing results.

## Larger runs

```powershell
python benchmarks/run.py --mode standalone --records 100000 --concurrency 32 --output benchmarks/results
python benchmarks/run.py --mode distributed --records 100000 --concurrency 32 --output benchmarks/results
```

## Publishing results

Use the generated `.md` report for a performance summary and keep the matching `.json` file as raw evidence. A responsible performance claim should include the database version/commit, host CPU and storage, Docker CPU/memory limits, record count, payload size, concurrency, flush threshold, consistency mode, warm-up policy, and the number of repetitions. Report throughput together with p95/p99 latency; throughput without tail latency can hide saturation and retries.

Run the same workload several times on an otherwise idle host. Compare the
same record count, payload shape, concurrency, flush threshold, CPU/memory
limits, and storage medium. Setup and full-text index build time are reported
separately from steady-state operations. The runner is intentionally a baseline
harness, not a replacement for a full YCSB-style workload generator.

