import json
import logging
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1]))
from lsmwrite_client import LsmWriteDbClient

def test_quote_and_config():
    client = LsmWriteDbClient("http://example.test/", timeout=2, retries=1)
    assert client.config.base_url == "http://example.test"
    assert client._quote("a/b c") == "a%2Fb%20c"

def test_logging_namespace():
    assert logging.getLogger("lsmwrite").name == "lsmwrite"
