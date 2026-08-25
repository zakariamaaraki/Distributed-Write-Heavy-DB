"""Python client for the LsmWriteDb HTTP and SSE APIs."""
from __future__ import annotations

import json
import logging
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from typing import Any, Iterator, Mapping, Sequence

_LOG = logging.getLogger("lsmwrite")

class LsmWriteDbError(RuntimeError):
    """Base exception for client errors."""

class LsmWriteDbHttpError(LsmWriteDbError):
    def __init__(self, status: int, message: str, body: Any = None):
        super().__init__(f"HTTP {status}: {message}")
        self.status, self.body = status, body

@dataclass(frozen=True)
class ClientConfig:
    base_url: str = "http://localhost:8080"
    timeout: float = 30.0
    retries: int = 3
    retry_delay: float = 0.25

class LsmWriteDbClient:
    def __init__(self, base_url: str = "http://localhost:8080", *, timeout: float = 30.0,
                 retries: int = 3, retry_delay: float = 0.25,
                 logger: logging.Logger | None = None):
        self.config = ClientConfig(base_url.rstrip("/"), timeout, retries, retry_delay)
        self.log = logger or _LOG

    def _request(self, method: str, path: str, body: Any = None, *, stream: bool = False):
        url = f"{self.config.base_url}{path}"
        data = None if body is None else json.dumps(body).encode()
        headers = {"Accept": "application/json"}
        if body is not None:
            headers["Content-Type"] = "application/json"
        attempts = self.config.retries + 1
        input_log = self._short(body) if body is not None else None
        for attempt in range(attempts):
            started = time.monotonic()
            self.log.info("HTTP request method=%s url=%s attempt=%d input=%s", method, url, attempt + 1, input_log)
            try:
                request = urllib.request.Request(url, data=data, headers=headers, method=method)
                response = urllib.request.urlopen(request, timeout=self.config.timeout)
                self.log.info("HTTP response method=%s path=%s status=%d elapsed_ms=%.1f",
                              method, path, response.status, (time.monotonic() - started) * 1000)
                if stream:
                    self.log.info("HTTP stream opened method=%s path=%s output=<SSE stream>", method, path)
                    return response
                output = self._decode(response)
                self.log.info("HTTP output method=%s path=%s output=%s", method, path, self._short(output))
                return output
            except urllib.error.HTTPError as exc:
                raw = exc.read().decode("utf-8", "replace")
                try: parsed = json.loads(raw) if raw else None
                except json.JSONDecodeError: parsed = raw
                self.log.warning("HTTP error method=%s path=%s status=%d body=%s", method, path, exc.code, raw[:500])
                if exc.code in (408, 429) or exc.code >= 500:
                    if attempt + 1 < attempts:
                        time.sleep(self.config.retry_delay * (2 ** attempt)); continue
                raise LsmWriteDbHttpError(exc.code, str(parsed or raw or exc.reason), parsed) from exc
            except (urllib.error.URLError, TimeoutError) as exc:
                self.log.warning("transport error method=%s path=%s attempt=%d error=%s", method, path, attempt + 1, exc)
                if attempt + 1 < attempts:
                    time.sleep(self.config.retry_delay * (2 ** attempt)); continue
                raise LsmWriteDbError(str(exc)) from exc

    @staticmethod
    def _decode(response):
        raw = response.read()
        if not raw: return None
        try: return json.loads(raw.decode("utf-8"))
        except json.JSONDecodeError: return raw.decode("utf-8", "replace")

    @staticmethod
    def _short(value: Any, limit: int = 1000) -> str:
        rendered = json.dumps(value, ensure_ascii=False, default=str) if not isinstance(value, str) else value
        return rendered if len(rendered) <= limit else rendered[:limit] + "...<truncated>"

    @staticmethod
    def _quote(value: str) -> str:
        return urllib.parse.quote(value, safe="")

    def list_tables(self): return self._request("GET", "/tables")
    def create_table(self, table: str): return self._request("PUT", f"/tables/{self._quote(table)}")
    def drop_table(self, table: str): return self._request("DELETE", f"/tables/{self._quote(table)}")
    def get(self, key: str, table: str = "kv"):
        path = f"/kv/{self._quote(key)}" if table == "kv" else f"/tables/{self._quote(table)}/kv/{self._quote(key)}"
        try: return self._request("GET", path)
        except LsmWriteDbHttpError as exc:
            if exc.status == 404: return None
            raise
    def put(self, key: str, value: str, table: str = "kv"):
        path = f"/kv/{self._quote(key)}" if table == "kv" else f"/tables/{self._quote(table)}/kv/{self._quote(key)}"
        return self._request("PUT", path, {"value": value})
    def delete(self, key: str, table: str = "kv"):
        path = f"/kv/{self._quote(key)}" if table == "kv" else f"/tables/{self._quote(table)}/kv/{self._quote(key)}"
        return self._request("DELETE", path)
    def range(self, table: str = "kv", *, start: str | None = None, end: str | None = None, limit: int = 100):
        path = "/kv/range" if table == "kv" else f"/tables/{self._quote(table)}/kv/range"
        query = {"limit": str(limit)}
        if start is not None: query["start"] = start
        if end is not None: query["end"] = end
        return self._request("GET", f"{path}?{urllib.parse.urlencode(query)}")
    def stats(self, table: str | None = None):
        path = "/stats" if table is None else f"/tables/{self._quote(table)}/stats"
        return self._request("GET", path)
    def execute_sql(self, query: str, transaction_id: str | None = None, parameters: Sequence[Any] | None = None):
        if parameters is not None:
            query = _bind_parameters(query, parameters)
        return self._request("POST", "/sql", {"query": query, "transactionId": transaction_id})
    def prepare(self, query: str) -> "PreparedStatement":
        return PreparedStatement(self, query)
    def begin(self):
        result = self._request("POST", "/transactions")
        return Transaction(self, result["transactionId"])
    def begin_distributed(self):
        result = self._request("POST", "/distributed-transactions")
        return DistributedTransaction(self, result["transactionId"])
    def changes(self, from_sequence: int = 0, limit: int = 100):
        return self._request("GET", f"/changes?{urllib.parse.urlencode({'fromSequence': from_sequence, 'limit': limit})}")
    def stream_changes(self, from_sequence: int = 0, *, reconnect: bool = True) -> Iterator[dict[str, Any]]:
        sequence = from_sequence
        while True:
            response = None
            try:
                response = self._request("GET", f"/changes/stream?fromSequence={sequence}", stream=True)
                event_data = None
                for raw in response:
                    line = raw.decode("utf-8", "replace").rstrip("\r\n")
                    if not line:
                        if event_data:
                            event = json.loads(event_data); sequence = max(sequence, int(event["sequence"]))
                            self.log.info("SSE output sequence=%s event=%s", sequence, self._short(event))
                            yield event
                        event_data = None
                    elif line.startswith("data:"):
                        event_data = line[5:].strip()
                    elif line.startswith(":"):
                        self.log.debug("SSE heartbeat input/output=: heartbeat")
            except (LsmWriteDbError, OSError, json.JSONDecodeError) as exc:
                self.log.warning("change stream disconnected after sequence=%d: %s", sequence, exc)
                if not reconnect: raise
                time.sleep(self.config.retry_delay)
            finally:
                if response is not None: response.close()
            if not reconnect: return

def _sql_literal(value: Any) -> str:
    if value is None:
        return "NULL"
    if isinstance(value, bool):
        return "TRUE" if value else "FALSE"
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        return repr(value)
    if isinstance(value, str):
        return "'" + value.replace("'", "''") + "'"
    raise TypeError("ANSI SQL parameters must be None, bool, int, float, or str")


def _bind_parameters(query: str, parameters: Sequence[Any]) -> str:
    values = iter(parameters)
    output: list[str] = []
    quoted = False
    for character in query:
        if character == "'":
            quoted = not quoted
        if character == "?" and not quoted:
            try:
                output.append(_sql_literal(next(values)))
            except StopIteration as exc:
                raise ValueError("Not enough SQL parameters for placeholders") from exc
        else:
            output.append(character)
    try:
        next(values)
    except StopIteration:
        return "".join(output)
    raise ValueError("Too many SQL parameters for placeholders")


class PreparedStatement:
    """Reusable client-side prepared ANSI SQL statement."""

    def __init__(self, client: LsmWriteDbClient, query: str):
        self.client, self.query = client, query

    def execute(self, *parameters: Any, transaction_id: str | None = None):
        return self.client.execute_sql(self.query, transaction_id, parameters)

class DistributedTransaction:
    def __init__(self, client: LsmWriteDbClient, transaction_id: str):
        self.client, self.id = client, transaction_id
    def write(self, table: str, key: str, value: str | None = None, *, deleted: bool = False):
        return self.client._request("PUT", f"/distributed-transactions/{self.id}/writes", {"table": table, "key": key, "value": value, "isDeleted": deleted})
    def commit(self): return self.client._request("POST", f"/distributed-transactions/{self.id}/commit")
    def status(self): return self.client._request("GET", f"/distributed-transactions/{self.id}")
    def recover(self): return self.client._request("POST", f"/distributed-transactions/{self.id}/recover")
    def rollback(self): return self.client._request("DELETE", f"/distributed-transactions/{self.id}")
class Transaction:
    def __init__(self, client: LsmWriteDbClient, transaction_id: str):
        self.client, self.id = client, transaction_id
    def put(self, key: str, value: str, table: str = "kv"):
        prefix = f"/transactions/{self.id}/kv" if table == "kv" else f"/transactions/{self.id}/tables/{urllib.parse.quote(table, safe='')}/kv"
        return self.client._request("PUT", f"{prefix}/{urllib.parse.quote(key, safe='')}", {"value": value})
    def delete(self, key: str, table: str = "kv"):
        prefix = f"/transactions/{self.id}/kv" if table == "kv" else f"/transactions/{self.id}/tables/{urllib.parse.quote(table, safe='')}/kv"
        return self.client._request("DELETE", f"{prefix}/{urllib.parse.quote(key, safe='')}")
    def get(self, key: str, table: str = "kv"):
        prefix = f"/transactions/{self.id}/kv" if table == "kv" else f"/transactions/{self.id}/tables/{urllib.parse.quote(table, safe='')}/kv"
        try: return self.client._request("GET", f"{prefix}/{urllib.parse.quote(key, safe='')}")
        except LsmWriteDbHttpError as exc:
            if exc.status == 404: return None
            raise
    def commit(self): return self.client._request("POST", f"/transactions/{self.id}/commit")
    def rollback(self): return self.client._request("DELETE", f"/transactions/{self.id}")
