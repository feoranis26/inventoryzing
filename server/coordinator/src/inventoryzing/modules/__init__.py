from collections.abc import Callable
from importlib import import_module
from typing import cast

from fastapi import FastAPI

module_registry = {
    "labeling": "inventoryzing.modules.labeling",
    "scanning": "inventoryzing.modules.scanning",
}


def register_enabled_modules(app: FastAPI, enabled: tuple[str, ...]) -> None:
    unknown = set(enabled).difference(module_registry)
    if unknown:
        names = ", ".join(sorted(unknown))
        raise ValueError(f"Unknown coordinator module configuration: {names}")
    for name in enabled:
        module = import_module(module_registry[name])
        register = cast(Callable[[FastAPI], None], getattr(module, "register"))
        register(app)
