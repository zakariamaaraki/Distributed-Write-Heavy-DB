# LsmWriteDb Python client

Dependency-free HTTP and SSE client for LsmWriteDb. Configure logging in the application using the standard Python logging module.

```python
import logging
from lsmwrite_client import LsmWriteDbClient

logging.basicConfig(level=logging.INFO)
client = LsmWriteDbClient("http://localhost:8080")
client.put("user:1", "Ada")
print(client.get("user:1"))

tx = client.begin()
tx.put("user:2", "Grace")
tx.commit()

for event in client.stream_changes():
    print(event)
``` 


## Prepared ANSI SQL

Relational tables support reusable, parameterized ANSI SQL statements. The client safely quotes scalar parameters before sending the SQL request, while existing `execute_sql(query)` calls remain supported.

```python
client.execute_sql("""
CREATE TABLE users (
    id INTEGER PRIMARY KEY,
    name VARCHAR(255) NOT NULL,
    active BOOLEAN
)
""")

insert_user = client.prepare(
    "INSERT INTO users (id, name, active) VALUES (?, ?, ?)"
)
insert_user.execute(42, "Ada", True)

select_user = client.prepare("SELECT id, name, active FROM users WHERE id = ?")
print(select_user.execute(42))

client.prepare("UPDATE users SET name = ? WHERE id = ?").execute("Grace", 42)
client.prepare("DELETE FROM users WHERE id = ?").execute(42)
```

For one-off statements, pass `parameters` directly:

```python
client.execute_sql("SELECT * FROM users WHERE id = ?", parameters=[42])
```
The client retries transient HTTP errors (408, 429, and 5xx) and transport failures with exponential backoff. `stream_changes()` reconnects after a disconnect using the last received sequence. SSE heartbeat comments are logged at debug level and ignored.


Docker integration test

From the repository root, run python python_client/run_integration_test.py. The script starts the Docker Compose cluster, discovers a leader, uses LsmWriteDbClient for table CRUD, transactions, SQL, change-log replay, and stats, then stops the cluster in a inally block.
