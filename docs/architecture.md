# Architecture

The plugin is layered so that the only Windows-, USB-, and FanControl-specific code lives at the edges. The core - the byte encoding and the control loop - is pure and testable without hardware.

## Layering

The code is organized into these layers, each depending only on the ones below it:

- **Plugin** - the FanControl integration surface. Implements the host's `IPlugin2` contract, registers sensors and controls, and translates host callbacks into device commands. This is the only layer the host sees.
- **Worker** - the keepalive control loop that periodically re-asserts manual mode and pushes the current duty so the controller does not fall back to its firmware curve. It owns and drives the `Devices` beneath it.
- **Devices** - per-family device models that bind a `Protocol` encoder strategy to a concrete controller (PIDs, channel count, register map). This is where the family differences (SL vs SLI vs SL v2 and so on) are resolved. It also owns the injected clock (`IClock`) and the pure `ChannelWriteDecision` that drives the keepalive staleness check.
- **Hid** - the USB HID transport. Owns device enumeration, open/close, and the raw report read/write. Hidden behind the `IHidTransport` seam (below). Enumeration also collapses the several HID interfaces one physical controller can expose into a single logical device (see "Device identity and binding stability" below).
- **Protocol** - pure functions that encode commands and decode telemetry to and from HID report byte buffers. No I/O, no state, no clock. See `docs/protocol.md` for the full byte-level contract.
- **Logging** - a thin logging abstraction so the layers above can record diagnostics without binding to the host's logger type directly.

## The IHidTransport seam

All USB access goes through a single interface, `IHidTransport`. The real implementation wraps the platform HID library; tests substitute a fake that records the bytes written and replays canned read buffers. Because every layer above Hid depends on the interface and never on the concrete transport, the entire protocol and control stack can be exercised on Linux/WSL in unit tests with no device attached. This seam is the single most important testability decision in the codebase.

## Pure-encoder strategy pattern

Each device family encodes set-speed, manual-mode, and ARGB-sync reports differently (different duty-to-byte formulas, different register bytes, different RPM offsets). Rather than branch on family inside the device or worker code, each family is represented by a pure encoder strategy: a stateless object that maps high-level intent (set channel N to D percent duty) to the exact byte buffer for that family. The encoders are pure functions of their inputs - no clock, no I/O, no mutable state - so they are trivially unit-testable and the family-specific bugs documented in `docs/protocol.md` are pinned by direct assertions on the produced bytes. The ARGB-sync encoder exists for every family, but its one call site (in `FanController`) is compiled in only for the ARGB build variant (`-p:EnableArgb=true`, the `ENABLE_ARGB` symbol); the standard build never emits it. See `docs/protocol.md` for the variant's behavior and trade-offs.

## Clock-injected keepalive worker

The keepalive worker takes its clock as a dependency rather than calling the system clock directly. Injecting the clock makes the loop deterministic in tests: a fake clock can advance time instantly and the test asserts exactly which reports were emitted on each tick, with no real waiting. In production the clock is the real monotonic clock and the loop runs on its own background thread.

## IPlugin2 tick vs background-thread keepalive split

The host calls `IPlugin2` on its own cadence to read sensors and apply control values; that callback is the tick path and it must return quickly. The keepalive - re-asserting manual mode and re-pushing duty so the firmware does not reclaim the fans - runs on a separate background thread driven by the clock-injected worker, not on the host tick. Splitting the two keeps the host callback cheap and bounded while still guaranteeing the controller stays under our control between ticks. The tick path and the keepalive path share the device state but never block on each other.

## Device identity and binding stability

FanControl persists a user's fan-curve bindings against each sensor's `Id`, so those ids must mean the same physical channel every time the plugin loads. Two enumeration facts work against that, and the Hid/Plugin layers handle both before any sensor is registered:

- **Duplicate HID interfaces.** A composite controller can surface more than one matching HID interface (top-level collection) under the same vendor/product id, which would otherwise register a duplicate Ch1-4 sensor set per interface. `HidDeviceDeduplicator` (in `Hid`) collapses interfaces that share a Windows ContainerId down to one logical controller, keeping the interface that accepts the largest output report (the fan-control collection). The USB serial is deliberately not the key: the Lian Li Uni controllers all report the same firmware-fixed serial, so a serial key would wrongly collapse distinct controllers into one - the ContainerId, which every interface of one physical device shares and which differs across devices, does not. The grouping is deliberately conservative: a device whose ContainerId could not be resolved is never collapsed (it keys on its per-interface device path), so two distinct controllers are never mistaken for one - the safe failure direction. The helper is pure, so the grouping is unit-tested without hardware.
- **Unstable enumeration order.** The OS does not guarantee a stable order across reboot/sleep/hibernate for two identical controllers, and the sensor id is keyed on the controller's index. The plugin therefore orders the located controllers by their OS device path (ordinal) before indexing, so the same physical port keeps the same index - and the same sensor id - run to run. A single-controller setup is unaffected; this only matters when two or more identical controllers are present.

Enumeration itself runs behind a host-seam resilience guard: the first access to the HID device manager can fail in some host/desktop contexts, so a failure there is logged and degrades to zero controllers rather than propagating into FanControl and crashing it - the same resilience the per-device open path already applies, one level up.

Construction of a `FanController` performs no device I/O. Its one-time setup writes (the manual-mode assert, and the ARGB-header sync in the ARGB build) live in `FanController.AssertManualMode()`, which the composition root calls after registering the controller, behind its own resilience guard. This ordering is deliberate: a hub whose firmware or HID interface rejects a setup write (a hardware family the setup path was never verified on, a stalled control transfer) must degrade to a logged fault on an otherwise working controller, never to the controller silently vanishing from FanControl. Recovery is free because the worker re-asserts manual mode before every speed write anyway.

## Channel population detection

A UNI FAN controller always has four channels in firmware, but a user rarely fills all four; surfacing the empty ones as dead controls clutters the UI. The controller reports no presence bit - its input report carries only RPM - so the plugin infers which channels have a fan from a short burst of RPM probes at startup: a channel that reads a plausible, non-zero RPM on a strict majority of probes is populated, an empty channel reads zero or occasional out-of-range garbage. The decision is a pure function (`ChannelPopulationDecision`, in `Devices`), so the majority rule is unit-tested in isolation; the probing itself is a public `FanController.DetectPopulation()` the composition root calls once after construction (never in the constructor, which would make construction do I/O and block a gated test read), guarded so a probe fault leaves the controller fully shown rather than lost. `Load` then registers sensors only for populated channels, skipping the empties via the `IsChannelPopulated` seam (the TL and Galahad II controllers, whose channels are all physical, always return `true`).

Two safeguards keep this from ever hiding a real fan: if _no_ channel looks populated the result is treated as inconclusive and all four are shown, and detection runs before the plugin drives anything, when a present fan still idles at its non-zero firmware default. The one residual limitation is a fan that is present but deliberately stopped (a 0rpm-capable fan the user has stopped) at probe time - indistinguishable from an empty channel, so it stays hidden until it next spins. Hiding a channel is safe for a user's saved config: FanControl resolves each binding independently and greys out (and later re-links) a binding whose sensor is absent rather than rejecting the whole configuration, so the populated channels' bindings are untouched.

## Minimal public surface

Only the plugin entry type is `public`; everything else (Hid, Protocol, Devices, Worker, Logging) is internal. FanControl discovers plugins by scanning the assembly for the public type that implements its plugin interface, so exposing anything more would only widen the API the host could accidentally bind to and the maintainer would have to keep stable. Keeping the surface to a single public type means the internals can be refactored freely without affecting the host contract.
