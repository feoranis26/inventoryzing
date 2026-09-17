# Direct Raster Printing and Labeling Module Plan

Updated 2026-09-16. This plan controls the next printing work where it conflicts with the older b-PAC/Winspool-first plan. The existing USB path remains available as a fallback until direct WLAN raster printing passes physical acceptance.

## Ephemeral delivery update

The labeling module does not persist labels, rendered artifacts, print jobs, attempts, observations, or printer outcomes in the database. It holds one short-lived request in memory only long enough to hand it directly to the selected printer agent and return a live result to the browser. Agent or coordinator restart drops that request; no recovery or reconciliation occurs. The existing durable `iz_print` implementation is superseded and will be retired without adding raster variants to it.

A force printer reset clears every in-memory request and re-enables the logical printer immediately. It intentionally forgets whether previously transmitted bytes finish printing. The reset is an operator escape hatch, not an evidence-backed cancellation.

## Outcome

Inventoryzing's optional labeling module owns label templates, rendering, print intent, job state, and its user interface. The separate printer daemon owns the printer connection and hardware protocol. Inventoryzing core remains unaware of labels and printers except for generic module registration.

For the Brother QL-820NWB, the preferred path sends one exact raster page directly over WLAN, waits for a terminal status from the device, and then reports the result. A direct attempt does not use an LBX file, P-touch Editor, b-PAC, a Windows printer driver, or Winspool.

There may be at most one unresolved physical label attempt for a logical printer. An ordinary second request is rejected as busy. A bulk workflow waits for each label to complete before it creates the next physical attempt.

## 1. Establish the lightweight module boundary

1. Move the coordinator label and print routes, rendering code, persistence access, and module-specific startup into `server/coordinator/src/inventoryzing/modules/labeling.py` or a small `modules/labeling/` package if one file becomes unwieldy.
2. Keep the module trusted and in process. It may use the coordinator's database engine and transaction helpers directly. Its tables and migrations retain the `iz_print_*` namespace, and its permissions retain the `label.*` and `print.*` namespaces.
3. Make the coordinator host import and register the module through one composition call. Core routes and inventory services must not import label DTOs, renderers, jobs, profiles, or device concepts.
4. Move the current SVG label route and its QR dependency out of core routes and into the labeling module.
5. Move the label panel and print controls into `web/main-ui/src/modules/labeling/`. The main UI imports a small module registration entry; its ordinary inventory components contain no printer-specific state or API calls.
6. Add a core-only startup test proving that the coordinator starts and serves inventory functions with the labeling module disabled.

This is source-level modularity for the MVP. It deliberately avoids a package loader, plugin manifest, restricted database facade, or separate deployment unit.

## 2. Enforce one active physical label

1. Introduce a logical printer slot in the coordinator. Acquiring the slot and creating an attempt occur in one transaction.
2. Permit only one unresolved attempt per logical printer. A competing create request returns `409 printer_busy` and identifies the active job and attempt so the UI can follow it.
3. Retain the slot while an attempt is `Claimed`, `DispatchAcknowledged`, `Printing`, `Blocked`, or `Unknown`.
4. Release the slot after `Completed`, a definite rejection before the first device byte, or a failure that proves no output could have occurred. Ambiguous failures become `Unknown` and retain the slot until reconciliation.
5. Give the printer daemon a corresponding exclusive per-printer lock. This protects against coordinator bugs and overlapping recovery processes.
6. Implement bulk printing as a sequential workflow: create one attempt, await its allowed terminal result, and only then create the next. Pause the bulk operation on `Blocked`, `Unknown`, or any failure needing a decision.

No ordinary request is stored as a hidden queue behind the active attempt. A future labeling-module batch controller may hold unrendered business intent, but it must still materialize only one physical attempt at a time.

## 3. Replace LBX dispatch with an exact raster artifact

Use one deterministic pipeline:

`template revision + captured inventory data -> canonical SVG -> printer profile -> exact dot grid -> immutable one-bit raster`

The template remains an Inventoryzing object. The first MVP template can be code-defined inside the labeling module; a visual editor can replace its authoring surface later without changing the printer protocol.

1. Render using an explicit media profile containing printable width and length in dots, resolution, margins, feed direction, cut policy, and monochrome mode.
2. Rasterize once in the coordinator to the exact printer grid. Do not resize or dither in the daemon, driver, or printer backend.
3. Keep an exact-size PNG as the browser preview and diagnostic download. PNG is lossless and therefore does not degrade the generated pixels. Dispatch the underlying immutable one-bit raster representation so no image decoder or scaling step sits in the physical path.
4. Align rules, text, and QR modules to the dot grid. Validate that the final raster QR code decodes before admitting a print job.
5. Start with the QL-820NWB's 300 x 300 dpi mode. Treat 300 x 600 dpi as a separate profile that requires its own physical validation.
6. Persist the template revision, captured input, profile revision, raster hash, dimensions, stride, and encoding with the attempt. A retry uses the same bytes.

## 4. Implement a pure Brother raster provider

Create a vendor-specific provider in the Windows printer daemon with no b-PAC or Winspool dependency. Keep protocol construction and parsing separate from transport.

The pure protocol library implements:

- command-mode initialization and the required invalidate/initialize sequence;
- the 32-byte status response parser;
- model, loaded-media, phase, notification, and error decoding;
- raster-mode selection and print-information commands;
- standard-resolution raster lines for the first profile;
- auto-cut, cut-at-end, margin/feed, and final-page commands;
- monochrome output with black/red mode explicitly disabled;
- a reducer that converts device statuses into neutral attempt evidence.

Build golden byte fixtures from Brother's raster command reference. Protocol unit tests must run without a printer.

Define a narrow transport with operations equivalent to connect, read status, write bytes, await the next status, and close. Implement WLAN/TCP first:

1. Configure the printer address and port as trusted agent settings. Confirm the actual open port during a read-only discovery step rather than assuming it.
2. Use bounded connect, write, idle, and total deadlines.
3. Hold one exclusive connection from preflight through terminal completion.
4. After print bytes begin, send no extra command until the printer reports completion or an error, as required by the Brother protocol.
5. Record byte counts and status transitions without logging label contents or credentials.

Direct USB raster transport is deferred. USB requires a separate Windows device transport and is substantially more involved than TCP. The existing b-PAC/Winspool provider remains the USB fallback while WLAN direct raster is validated.

## 5. Make attempt barriers and recovery explicit

Journal these barriers durably:

1. artifact verified;
2. logical printer slot acquired;
3. transport connected;
4. fresh status and media preflight accepted;
5. dispatch authorized and first byte about to be written;
6. all raster bytes written;
7. printer entered its printing phase;
8. terminal device status received;
9. coordinator report acknowledged.

Before the first byte, a failure is a definite no-output rejection and may release the slot. After the first byte, loss of evidence is `Unknown` unless the device gives definitive no-output evidence. A recoverable device condition is `Blocked` and retains the slot. If completion is known but reporting fails, restart retries only the report and never sends the raster again. If the daemon restarts after bytes were sent and no terminal status was recorded, it must not redispatch automatically.

Each attempt selects exactly one provider. Evidence from a b-PAC/Winspool attempt must not be combined with evidence from a direct-raster attempt.

## 6. Remove avoidable latency

1. Reduce browser and agent active-job polling from multi-second intervals to 200-250 ms as an interim measure.
2. Replace the claim poll with a held request or control channel after the raster path is stable.
3. Keep the raster provider warm and publish recent reachability, model, and loaded-media observations before the user presses Print.
4. Render and validate the immutable raster before the physical slot is acquired where possible.
5. Show the attempt's current barrier and last device status in the UI so a rejection is never silent.

Initial service targets are:

- durable job-create response: p95 under 150 ms;
- daemon claim: under 350 ms with interim polling and under 100 ms with a wake channel;
- fresh WLAN preflight: under 500 ms on a healthy local network;
- definite pre-dispatch rejection visible in the browser: under 750 ms.

## 7. Deliver in this order

### Phase A: boundary, serialization, and visibility

- Extract backend and frontend labeling modules.
- Add the logical printer slot and `409 printer_busy` response.
- Make bulk work sequential.
- Surface stages, device observations, and rejection details.
- Shorten active polling while retaining the USB b-PAC provider.

This phase improves the current USB system without waiting for WLAN.

### Phase B: hardware-free raster implementation

- Add the pure Brother protocol library and status reducer.
- Add the TCP transport behind a fake transport interface.
- Add golden command/status fixtures and fault-injection tests.
- Generate the exact one-bit artifact alongside the PNG preview.

### Phase C: WLAN read-only proof

After the printer is connected to WLAN:

- give it a stable address through a DHCP reservation or static configuration;
- confirm reachability and discover the configured raster port;
- request status without printing;
- verify the returned model, media width/type, and absence of blocking errors;
- measure connection and status latency.

This phase sends no label data and may be repeated safely.

### Phase D: one supervised physical acceptance print

With explicit authorization for the physical action:

- print one marked test label through direct raster;
- verify dimensions, orientation, margins, text sharpness, QR decoding, and cut behavior;
- confirm the attempt remains exclusive through terminal device status;
- confirm no Windows spool job or b-PAC process participated.

### Phase E: make direct raster the default

- Select direct WLAN raster by default for the validated printer profile.
- Remove LBX configuration from the direct provider's setup and UI.
- Retain the USB provider as an explicit fallback during a stabilization period.
- Exercise disconnects and media removal before, during, and after byte transmission.
- Add the bounded template editor only after the physical path is dependable.

## Verification gates

- Coordinator core starts with the labeling module disabled.
- Backend and frontend label code live only in their module files apart from registration calls.
- Two concurrent requests for one printer produce one attempt and one `409` response.
- Bulk tests never observe more than one unresolved physical attempt.
- Raster commands and status parsing match golden fixtures.
- Every admitted job names an explicit media profile and exact dot dimensions.
- The QR code decodes from the final one-bit raster.
- Fake transport tests cover failures before the first byte, after a partial write, after a complete write, during printing, and after completion but before report acknowledgement.
- Restart after a known completion retries reporting without retransmission.
- The direct provider has no reference to b-PAC, printer drivers, or Winspool.
- All ordinary tests remain hardware-free; the single physical acceptance test stays guarded and opt-in.

## Effort and sequencing estimate

- Phase A: one focused implementation pass.
- Phase B: roughly two to four focused development days.
- Phase C: a few hours once WLAN is configured.
- Phase D: one supervised acceptance session.
- Phase E and recovery hardening: roughly two to four additional days.

A narrow TCP prototype could put raster bytes on the printer sooner, but the recovery barriers and exclusive-slot behavior are required before it is safe to use from ordinary inventory workflows. Direct USB raster transport is outside this estimate.

## Primary references

- [Brother QL-800 series raster command reference](https://download.brother.com/welcome/docp100278/cv_ql800_eng_raster_101.pdf)
- [Brother QL-820NWB specifications](https://support.brother.com/g/b/spec.aspx?c=us&lang=en&prod=lpql820nwbeus)
