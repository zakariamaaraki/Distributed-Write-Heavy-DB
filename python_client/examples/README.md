# Python client benchmark examples

These scripts use the dependency-free client in the parent directory. They
expect a running Router at `http://localhost:9081`; set `LSMWRITE_URL` or pass
`--url` to target another Router or node.

```powershell
python python_client/examples/insert_1m.py
python python_client/examples/distributed_100.py
python python_client/examples/parallel_select.py
python python_client/examples/cleanup.py
```

The first script creates `python_benchmark_bulk` and inserts one million
relational rows with a prepared ANSI SQL statement. Use `--count` to run a
smaller smoke test.

The second creates two relational tables and commits 100 distributed
transactions. Each transaction stages one JSON row in each table through the
client's distributed transaction API.

The cleanup script issues `DROP TABLE` through the Python client, so all three benchmark tables and their catalog/storage metadata are removed.

The insert script defaults to 1,024 workers and submits requests without waiting between submissions. The `parallel_select.py` script fires 1,000 `SELECT * ... LIMIT 1000` requests concurrently.
