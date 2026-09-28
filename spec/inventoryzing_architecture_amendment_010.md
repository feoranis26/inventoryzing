# inventoryzing Architecture Amendment 010

**Status:** Accepted architectural amendment  
**Scope:** Reusable property groups, fixed-or-range quantities, and electrical units  
**Applies to:** Original architecture handoff + Amendments 001–009  
**Intent:** Compose independent capabilities without introducing multiple object-type inheritance

---

## 1. Purpose and precedence

Object types retain the single-parent hierarchy defined by Amendment 009. A reusable property group
MAY make a collection of existing property definitions applicable to a type. This permits a concrete
type such as a buck/boost converter to use both `DC input` and `DC output` fields without inheriting
from two object types.

Where this amendment conflicts with earlier wording, this amendment takes precedence.

## 2. Property groups

A property group has stable identity, a human-readable name and description, and references to
existing property definitions. It does not own copies of those definitions. A type MAY attach any
number of groups; its descendants inherit those attachments through the existing type hierarchy.

Effective property applicability is the union of:

- direct property declarations on the type and its ancestors; and
- properties referenced by groups attached to the type or its ancestors.

The same property reached through multiple declarations or groups appears once, by property identity.
Removing one source of applicability leaves the property available if another source remains.

Groups define applicability only in the initial implementation. Property defaults remain atomic values
on object types, and object overrides remain atomic values on objects. Removing a group member,
detaching a group, deleting a group, or reparenting a type MUST be rejected if it would strand a stored
value or explicit-unset entry. These operations MUST NOT silently delete property values.

Group deletion is permitted only when the group is not attached to any type. Deleting a group does not
delete its referenced property definitions.

## 3. Fixed-or-range quantities

A quantity property MAY use the `fixed-or-range` shape. Its value is one compound typed value in one
of two modes:

| Mode | User representation | Canonical bounds |
|---|---|---|
| Fixed | one amount and unit | lower = upper = canonical amount |
| Adjustable range | minimum, maximum, and one unit | canonical lower and upper |

Both endpoints of an adjustable range are required and the minimum MUST NOT exceed the maximum.
Absence remains distinct from both modes. Values are optional unless another future rule explicitly
requires them.

The mode is persisted explicitly even when both canonical bounds are equal. Inheritance and override
operate on the entire compound value. A child or object MUST NOT inherit one endpoint while overriding
the other.

Queries compare canonical bounds. A fixed value matches an amount when its equal bounds contain that
amount; an adjustable range matches when its lower bound is less than or equal to the amount and its
upper bound is greater than or equal to it. Display-unit changes do not alter canonical bounds.

This structure is reusable for voltage, current, and other dimensions. It does not introduce a general
constraint or rules language.

## 4. Electrical dimensions

The shared exact-decimal unit catalog includes these electrical dimensions:

- voltage;
- current;
- resistance;
- power;
- energy;
- capacitance;
- inductance;
- frequency; and
- electric charge / battery capacity.

Each dimension has one canonical SI unit. Prefixes and derived display units convert to that canonical
unit using exact decimal factors. Ampere-hour and watt-hour units therefore remain query-compatible
with coulombs and joules respectively. Units from different dimensions MUST be rejected even if a
property definition was malformed.

