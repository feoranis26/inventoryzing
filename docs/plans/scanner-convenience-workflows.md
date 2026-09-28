# Scanner convenience workflows

This note records the next scanner-driven convenience features. Scanner observations remain ephemeral
inputs routed to one bound terminal UI. They are not durable jobs and do not mutate inventory unless an
active, authorized UI operation submits a normal inventory command.

## Shared object-selector behavior

Every object-selection control should accept scanner input through one shared component and dialog-level
router. When an object resolves successfully, route it in this order:

1. the focused eligible object selector in the topmost dialog;
2. otherwise, the first visible, enabled, empty selector in tab order;
3. after filling a selector, advance to the next empty selector.

A focused selector may be replaced. If every selector is populated and none is focused, make no change
and return the configurable amber/no-action feedback. While a dialog has an eligible selector, it takes
precedence over idle scan-to-object navigation. Hidden, disabled, read-only, and background-dialog fields
must not receive scans.

Resolution and field validation remain distinct. An unknown identifier produces amber feedback. A known
object rejected by the field's rules produces red feedback. Examples include selecting an object as its
own parent, creating a containment cycle, using the same holding as both sides of a transfer, or selecting
an object that does not meet a field's eligibility rule. Filling a field is selection feedback, not the
command-success tone, because nothing has been committed yet.

Initial adopters should include Set parent, move source/destination, stock source/destination, contents
verification container, label-preview object, and future checkout/workflow object slots. Controls should
show a scanner affordance and accessible text explaining that focus selects the destination field.

## Shared workflow behavior

Scanner task pages keep their mode and working target visible, accept repeated scans without per-item
confirmation, and show a running result list. Normal success commits immediately where the workflow is
mutating. Pages should provide Pause, Finish, and guarded Undo/Reverse where reversal is meaningful.
Undo must target the exact prior command and refuse to overwrite an intervening change.

Decode feedback from the scanner is not proof of an inventory operation. Use the configured terminal
feedback after resolution and processing:

- selection/navigation success for a resolved, non-mutating scan;
- command success for a committed mutation;
- red/falling feedback for a known scan whose operation failed; and
- amber/double-short feedback when the terminal has no applicable action.

## Workflow slices

### Move into here

Select or scan one destination, then move each scanned object into it. Report Moved, Already here, or a
specific failure without leaving the page. Moving a container preserves its subtree. Mounted, installed,
or located placements require confirmation before this workflow moves them.

### Verify contents

Select a container and scan the objects physically present. Show expected-and-seen, expected-but-unseen,
and unexpected objects. The initial version is read-only: it must not move unexpected objects or mark
unseen objects as lost. Before implementation, decide whether a session verifies direct children only or
the entire descendant tree; direct children are the safer default.

### Scan to reprint

Select a printer and label template once, then scan objects requiring replacement labels. Respect the
printer module's single-active-request rule and show each request/result in the session list.

### Scan to classify

Select a tag once, then add it to each scanned object without replacing existing tags. Already-classified
objects are successful no-ops. Undo removes only the assignment made by that exact session command.

### Checkout and return

Checkout and return belong to optional, site-specific modules, not the inventory core or the required
scanner roadmap. Sites may implement different processes or have no checkout process at all. Shared
scanner routing, object selection, and configurable feedback should be reusable by these modules.

A checkout module may offer sticky-principal, pair, or order-independent principal/asset scans. That module
owns its checkout records, availability rules, permissions, history, and corrections. Checkout remains
separate from ownership and placement; installing or enabling it must not be required for inventory use.

### Stock operations

Receive, consume, adjust, transfer, and split already have inventory command primitives. A scanner page
should use those commands rather than introduce a scanner-specific stock store. The exact interaction still
needs a small design decision: whether the operator scans or preselects the holding, and whether quantity is
entered per operation or supplied by a configured increment. Do not infer a quantity merely from a scan.

## Suggested implementation order

1. Shared scan-aware object selector and topmost-dialog routing.
2. Read-only Verify contents.
3. Scan to reprint and Scan to classify.
4. Stock workflow after its quantity interaction is specified.
5. Optional site workflow modules, including checkout/return, only when a site needs them.

Favorites/recents and workstation presets can improve all of these pages later without changing their
command or scanner boundaries.
