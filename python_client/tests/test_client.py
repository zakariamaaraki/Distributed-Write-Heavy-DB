import json
import logging
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1]))
from lsmwrite_client import LsmWriteDbClient

def test_drop_table_uses_table_delete_endpoint():
    client = LsmWriteDbClient("http://example.test", retries=0)
    requests = []
    client._request = lambda method, path, body=None, **kwargs: requests.append((method, path, body)) or {"ok": True}

    assert client.drop_table("bench/table") == {"ok": True}
    assert requests == [("DELETE", "/tables/bench%2Ftable", None)]
def test_quote_and_config():
    client = LsmWriteDbClient("http://example.test/", timeout=2, retries=1)
    assert client.config.base_url == "http://example.test"
    assert client._quote("a/b c") == "a%2Fb%20c"

def test_logging_namespace():
    assert logging.getLogger("lsmwrite").name == "lsmwrite"


def test_prepare_binds_ansi_sql_literals_without_injection():
    client = LsmWriteDbClient("http://example.test", retries=0)
    requests = []
    client._request = lambda method, path, body=None, **kwargs: requests.append((method, path, body)) or {"ok": True}

    statement = client.prepare("INSERT INTO users (id, name, active) VALUES (?, ?, ?)")
    result = statement.execute(7, "O'Reilly", False)

    assert result == {"ok": True}
    assert requests[0][2]["query"] == "INSERT INTO users (id, name, active) VALUES (7, 'O''Reilly', FALSE)"


def test_execute_sql_accepts_parameters_and_transaction():
    client = LsmWriteDbClient("http://example.test", retries=0)
    requests = []
    client._request = lambda method, path, body=None, **kwargs: requests.append((method, path, body)) or body

    client.execute_sql("SELECT * FROM users WHERE id = ? AND name = ?", "tx-1", [42, "Ada"])

    assert requests[0][2] == {
        "query": "SELECT * FROM users WHERE id = 42 AND name = 'Ada'",
        "transactionId": "tx-1",
    }


def test_prepare_rejects_parameter_count_mismatch():
    client = LsmWriteDbClient("http://example.test", retries=0)
    statement = client.prepare("SELECT * FROM users WHERE id = ?")
    try:
        statement.execute()
        assert False, "expected ValueError"
    except ValueError as exc:
        assert "Not enough" in str(exc)
