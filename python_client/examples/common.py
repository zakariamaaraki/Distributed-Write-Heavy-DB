"""Shared helpers for the LsmWriteDb Python client examples."""
from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lsmwrite_client import LsmWriteDbClient

DEFAULT_BASE_URL = os.environ.get("LSMWRITE_URL", "http://localhost:9081")


def client_from_args(description: str):
    parser = argparse.ArgumentParser(description=description)
    parser.add_argument("--url", default=DEFAULT_BASE_URL, help="Router/node base URL (default: %(default)s)")
    parser.add_argument("--timeout", type=float, default=30.0)
    parser.add_argument("--retries", type=int, default=3)
    args, _ = parser.parse_known_args()
    return args, LsmWriteDbClient(args.url, timeout=args.timeout, retries=args.retries)


def create_relational_tables(client: LsmWriteDbClient, *definitions: str) -> None:
    for definition in definitions:
        client.execute_sql(definition)

