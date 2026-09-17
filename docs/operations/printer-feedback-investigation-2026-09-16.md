# QL-820NWB network feedback investigation

Target: user's Brother QL-820NWB at 10.0.0.13, agent stopped.
Investigation date: 2026-09-16. No raster, feed, cut, or print commands sent.

## Observations

TCP port 9100 accepted connections in approximately 31–124 ms. Three plain
ESC i S requests and three raster-mode-switch + ESC i S requests returned
no bytes within a five-second read timeout. A raster-mode/status request
followed by a write-side shutdown caused the peer to close without returning
bytes. A further request prefixed with 400 invalidation bytes and ESC @
initialization also returned no bytes within five seconds.

These results reproduce the agent's TCP status wait independently of the
coordinator. They do not establish that TCP feedback is impossible under
every configuration. Initialization and mode commands were sent, but no
persistent configuration was written.

The unauthenticated HTTP status page reported READY and 62 x 29 mm media.
Its monitor endpoint returned READY in approximately 204 ms. This is
corroborating evidence, not the proposed production feedback protocol.

Brother's official command FAQ explicitly lists QL-820NWB among models that
provide network status through SNMP GET of OID
`1.3.6.1.4.1.2435.3.3.9.1.6.1.0`. A targeted read-only SNMPv1 query to UDP
161 using the public read community succeeded three times in 49.6, 8.8,
and 9.2 ms. No SNMP SET or broad network scan was performed.

Returned value, identical in each successful probe:

```text
80 20 42 34 41 30 04 00 00 00 3e 0b 00 00 03 00
00 1d 00 00 00 00 00 00 00 01 00 00 00 00 00 00
```

Decoded: model A (QL-820NWB), error bytes both zero, media width 62 mm,
die-cut media type 0x0b, length 29 mm, status type 0, phase 0. This matches
the installed DK-1209 roll and is incompatible with a 29 x 90 mm request.

## Conclusions and remaining verification

Use documented SNMP status for network media preflight rather than waiting
for a reply to ESC i S on the raster socket. Continue sending raster data
over TCP 9100. Feedback belongs in the Brother adapter/printing module.

This investigation verifies current media and idle/error status retrieval;
it does not yet verify physical print completion. A controlled compatible
single-label test must capture status before, during, and after printing,
including whether completion is observable through polling and how quickly
it changes. Do not equate TCP send success or an arbitrary idle response
with Completed. Missing conclusive completion evidence must remain Unknown.

The existing raster encoder's checked integer-to-byte conversion also needs
review before this test: a normal page has more than 255 raster rows.

The separate HTTP claim/report/UI delays are not explained by these printer
probes. End-to-end timings must still be checked with coordinator and agent
running after the transport change. Connection-refused claim warnings while
the coordinator is down indicate failed polling, not received print jobs.

Reproduction tools: `scripts/probe-printer-status.py` (TCP) and
`scripts/probe-printer-snmp.py` (documented read-only SNMP OID). These are
diagnostic scripts, not production SNMP implementations.

## Primary references

- [Brother command FAQ: network status and supported models](https://support.brother.com/g/s/es/dev/en/command/faq/index.html?c=eu_ot&comple=on&lang=en&navi=offall&redirect=on)
- [Brother QL-800/810W/820NWB raster command reference](https://download.brother.com/welcome/docp100278/cv_ql800_eng_raster_101.pdf)
