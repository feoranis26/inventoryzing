import argparse
import getpass
import os
import secrets
from pathlib import Path

from sqlalchemy import create_engine

from inventoryzing.auth import bootstrap, create_printer_service, create_scanner_service
from inventoryzing.config import Settings


def main() -> None:
    parser = argparse.ArgumentParser(description="inventoryzing coordinator administration")
    parser.add_argument("action", choices=[
        "bootstrap", "provision-printer-agent", "provision-scanner-agent"
    ])
    parser.add_argument("--site-name")
    parser.add_argument("--login")
    parser.add_argument("--name")
    parser.add_argument("--token-file", type=Path)
    arguments = parser.parse_args()
    engine = create_engine(Settings().database_url)
    try:
        if arguments.action == "bootstrap":
            if not arguments.site_name or not arguments.login:
                parser.error("bootstrap requires --site-name and --login")
            password = getpass.getpass("Administrator password (12+ characters): ")
            if password != getpass.getpass("Confirm password: "):
                parser.error("Passwords do not match.")
            site = bootstrap(engine, arguments.site_name, arguments.login, password)
            print(f"Site initialized: {site}")
        else:
            scanner = arguments.action == "provision-scanner-agent"
            label = "scanner" if scanner else "printer"
            provision = create_scanner_service if scanner else create_printer_service
            if not arguments.name:
                parser.error(f"provision-{label}-agent requires --name")
            token_file: Path | None = arguments.token_file
            if token_file is None:
                account, token = provision(engine, arguments.name)
                print(f"{label.title()} agent provisioned: {account}")
                print(f"Bearer token (shown once): {token}")
            else:
                token_file = token_file.resolve()
                if not token_file.parent.is_dir():
                    parser.error("The token file parent directory must already exist.")
                token = secrets.token_urlsafe(48)
                descriptor = os.open(token_file, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
                try:
                    with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
                        stream.write(f"{token}\n")
                    try:
                        account, _ = provision(engine, arguments.name, token=token)
                    except BaseException:
                        token_file.unlink(missing_ok=True)
                        raise
                except BaseException:
                    token_file.unlink(missing_ok=True)
                    raise
                print(f"{label.title()} agent provisioned: {account}")
                print(f"Bearer token saved to: {token_file}")
    except ValueError as error:
        parser.error(str(error))
    finally:
        engine.dispose()


if __name__ == "__main__":
    main()
