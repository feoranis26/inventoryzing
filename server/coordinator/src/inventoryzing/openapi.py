import argparse
import json
from pathlib import Path

from inventoryzing.app import create_app


def main() -> None:
    parser = argparse.ArgumentParser(description="Export the coordinator API contract")
    parser.add_argument("destination", type=Path)
    destination = parser.parse_args().destination
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(create_app().openapi(), indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
