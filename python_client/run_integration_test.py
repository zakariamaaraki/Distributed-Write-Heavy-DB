"""Local Docker integration test for the LsmWriteDb Python client.

Run from the repository root:
    python python_client/run_integration_test.py
"""
from __future__ import annotations

import logging
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from lsmwrite_client import LsmWriteDbClient, LsmWriteDbHttpError

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
log = logging.getLogger("lsmwrite.integration")
ROOT = Path(__file__).resolve().parent.parent
PORTS = (8081, 8082, 8083)


def compose(*args: str) -> None:
    command = ["docker", "compose", *args]
    log.info("running command=%s", " ".join(command))
    subprocess.run(command, cwd=ROOT, check=True)


def wait_for_leader(timeout: float = 30.0) -> LsmWriteDbClient:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        for port in PORTS:
            client = LsmWriteDbClient(f"http://localhost:{port}", timeout=3, retries=0)
            try:
                state = client._request("GET", "/raft/state")
                role = state.get("role")
                log.debug("raft state port=%d role=%s term=%s leader=%s", port, role, state.get("currentTerm"), state.get("leaderId"))
                if str(role).lower() == "leader" or role == 2:
                    log.info("selected leader node port=%d term=%s", port, state.get("currentTerm"))
                    return client
            except Exception as exc:
                log.debug("node not ready port=%d error=%s", port, exc)
        time.sleep(1)
    raise TimeoutError("No Docker node became leader before timeout")


def main() -> int:
    compose("up", "-d", *([] if __import__("os").environ.get("LSM_SKIP_BUILD") else ["--build"]))
    try:
        client = wait_for_leader()
        table = "python_integration"
        key = "user:integration"

        log.info("creating table")
        client.create_table(table)
        log.info("put/get/delete CRUD")
        client.put(key, "Ada", table)
        assert client.get(key, table)["value"] == "Ada"
        assert client.range(table, start="user:", end="user:z")[0]["key"] == key
        client.delete(key, table)
        assert client.get(key, table) is None

        log.info("transaction commands")
        transaction = client.begin()
        transaction.put("user:2", "Grace", table)
        assert transaction.get("user:2", table)["value"] == "Grace"
        transaction.commit()
        assert client.get("user:2", table)["value"] == "Grace"

        log.info("SQL command")
        sql_result = client.execute_sql("SELECT * FROM python_integration")
        assert sql_result["rowsAffected"] >= 1

        log.info("change-log replay")
        events = client.changes(0, limit=1000)
        assert any(event["table"] == table and event["key"] == "user:2" for event in events)

        log.info("database stats")
        stats = client.stats(table)
        assert stats["table"] == table
        log.info("integration test passed")
        return 0
    except (AssertionError, LsmWriteDbHttpError, TimeoutError) as exc:
        log.exception("integration test failed: %s", exc)
        return 1
    finally:
        compose("down")


if __name__ == "__main__":
    raise SystemExit(main())