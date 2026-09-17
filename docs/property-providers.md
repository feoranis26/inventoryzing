# Object property providers

The host creates `app.state.property_registry` before registering enabled modules.
An enabled, trusted module can register a `PropertyProvider` inside its existing
`register(app)` function. No packaging system or schema changes are needed.

```python
from inventoryzing.properties import PropertyDefinition, PropertyProvider

def register(app):
    app.state.property_registry.register(PropertyProvider(
        namespace="maintenance",
        fields=(PropertyDefinition(
            key="maintenance.next_due",
            label="Next maintenance due",
            provider="maintenance",
            type="date",
            description="Scheduled maintenance date, when applicable.",
            example="01 Aug 2036",
        ),),
        resolve=resolve_properties,
    ))

def resolve_properties(connection, object_id):
    # Read module-owned data here. Return a date, or None if not applicable.
    return {"maintenance.next_due": None}
```

Resolvers are read-only, synchronous and receive the current database connection
and object UUID. Return namespaced keys and scalar values: text, dates, timezone-aware
datetimes, numbers or booleans. Omit a key or return None for an absent property.
Providers must be fast local reads, not network calls. Each provider runs within a
savepoint; an exception rolls back that provider, is logged, and returns unavailable
fields while other providers continue. This isolates exceptions, not hung code.

For this basic version all registered fields are visible to users with inventory.read.
Only register data suitable for those users; per-field permissions are not implemented.
UI and label output use the same formatted_value, with label and raw value supplied
separately. Dates display as `01 Aug 2036`; timestamps explicitly use UTC until site
timezone configuration is introduced.

The Properties page and GET /api/properties expose definitions. GET
/api/objects/{id}/properties exposes object values and available/missing/unavailable
status. Object detail automatically displays module fields and object creation time.

Label fields select these properties and can include or omit the property label.
Text placeholders use `{maintenance.next_due}`, with optional string indexes/slices.
Missing, disabled-provider or unavailable values resolve to empty text; an empty QR
is omitted. Literal text surrounding a missing placeholder remains. Malformed syntax
still produces a validation error. Unknown well-formed keys stay blank, so templates
remain usable when a module is disabled. Core object absence remains a normal 404.

`{labeling.printed_at}` is a reserved labeling placeholder, not an object provider.
It is captured once per render, including previews, and formatted in UTC. It means
request rendering time, not confirmed physical completion time. No print history is stored.
