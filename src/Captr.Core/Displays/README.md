# Displays

Which monitors exist, which of them to record, and — the subtle part — how to refer
to a monitor in a way that stays correct across reboots, re-cabling, and hot-plugs
(SPEC §5).

The problem this folder exists for: **DXGI output indices reorder.** The index that
means "left monitor" today can mean "right monitor" after a cable swap, so a stored
index silently starts recording the wrong screen. The spec calls this out explicitly:
*never persist an index.*

The pieces:

- **DisplayInfo / DisplayIdentity** — one attached monitor. Its `StableId` is the
  monitor device path from the Windows display-configuration API
  (`QueryDisplayConfig` → `DISPLAYCONFIG_TARGET_DEVICE_NAME`), which is derived from
  the monitor's EDID and survives everything short of swapping the physical monitor.
  The DXGI output index is carried alongside for *this session only* and is never
  persisted.
- **DisplayEnumerator** — the only class that touches hardware. It joins two API
  worlds on the GDI device name (`\\.\DISPLAY3`):
  - `QueryDisplayConfig` (via CsWin32): stable device path, friendly name, refresh
    rate, and the display number Windows Settings shows.
  - DXGI via Vortice: adapter/output indices (what `ddagrab=output_idx=` needs),
    desktop coordinates, and the HMONITOR used for per-monitor DPI.
- **DisplaySelection** — pure resolution of the user's *exclusion set* against the
  currently attached displays: what to record, what was deliberately excluded, and
  which displays are new since we last looked (so the UI can announce them). Stored
  as exclusions so a newly attached display records by default (SPEC §5).
- **SeenDisplaysStore** — tiny persisted set of every stable id we have ever seen,
  which is what makes "new display" detectable. Internal state, not a setting.

Testing: `DisplaySelection` is pure and covered by fake-topology unit tests
(reorder, hot-plug, return of a deselected display). `DisplayEnumerator` is covered
by an integration test with trait `Display` that runs against the real hardware.
