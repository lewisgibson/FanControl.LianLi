# .NET Architecture Rules

Rules in this file are enforced on every change. Violations are bugs.

## Layering

The plugin is layered strictly, top to bottom:

```
Plugin      # IPlugin2 implementation. Composition root + host lifecycle (Initialize/Load/Update/Close).
Worker      # Per-controller live loop: drives PWM, runs keepalive, isolates faults.
Devices     # Controller / channel domain model. Owns identity, current state, the injected clock, and the keepalive write decision.
Hid         # IHidTransport seam over HidSharp. The ONLY place a HidSharp type may appear.
Protocol    # Pure encoders: device state in, exact byte buffer out. No I/O.
Logging     # File logger. Sink only.
```

Dependencies point **downward only**. A higher layer may depend on the layers beneath it; a lower layer never reaches up. Plugin depends on everything; Logging depends on nothing. The Worker drives the Devices it owns; the Devices model uses the Hid and Protocol seams beneath it, plus the injected clock (`IClock`) and the pure `ChannelWriteDecision`, which live in the Devices layer. A file lives in exactly one layer. An upward dependency, or a cross-dependency that skips the seam, is a bug.

## The `IHidTransport` Seam

`IHidTransport` is the abstraction over HidSharp. It exists so the rest of the plugin is testable without real hardware and so the HID library is swappable.

- **No HidSharp type leaks past `Hid/`.** No `HidSharp.*` type may appear in a method signature, property, field, return type, or `using` outside the `Hid/` layer. The Protocol, Devices, Worker, and Plugin layers know only `IHidTransport` and plain byte buffers.
- The concrete transport that wraps HidSharp lives in `Hid/` and is the single implementation of `IHidTransport`.
- If you find yourself importing `HidSharp` in the Worker or Plugin layer, the seam is wrong - add the capability to `IHidTransport` instead.

## Pure Protocol Encoders (strategy pattern)

The Protocol layer is pure. Each encoder takes device/channel state and returns the exact byte buffer to write. It performs no I/O, holds no mutable state, and does not touch the clock or the transport.

- Encoders are strategy objects: same input always yields the same bytes. This is what makes the byte math testable in isolation.
- The exact byte layout is the contract with the hardware. Preserve it precisely. Magic offsets, report IDs, and length constants get a one-line comment explaining what each byte means - the convention is documented, not re-derived.
- Never fold I/O into an encoder "for convenience". An encoder that calls the transport is a layering violation.

## Clock-Injected Keepalive

The Lian Li controllers require a periodic keepalive write or they revert. The keepalive cadence is driven by an **injected clock abstraction**, never `DateTime.Now` / `DateTime.UtcNow` / `Stopwatch` read directly in the worker.

- Inject the clock (and any delay/timer primitive) so a test can advance time deterministically and assert that keepalive fires on schedule without real waiting.
- Reading the system clock directly inside the worker is a bug - it makes the keepalive untestable and flaky.

## File Naming

Files are named `{Concept}{Role}.cs`:

- `{Concept}` is the domain noun (`Hid`, `Keepalive`, `LianLiProtocol`, `Controller`, `FileLog`).
- `{Role}` is the architectural role (`Transport`, `Worker`, `Encoder`, `Logger`, `Plugin`).
- Examples: `HidTransport.cs`, `KeepaliveWorker.cs`, `LianLiProtocolEncoder.cs`, `FileLogger.cs`, `LianLiPlugin.cs`.

One primary type per file, named the same as the file. Find the closest existing file and replicate its shape exactly before adding a new one.

## Minimal Public Surface

The assembly exposes exactly one `public` type: the `IPlugin2` implementation that FanControl reflects over and instantiates. **Everything else is `internal`.**

- New types default to `internal`. Make a type `public` only if it is genuinely the host-facing plugin entry type - which there is already exactly one of.
- The test project sees internals via `InternalsVisibleTo` (configured for `FanControl.LianLi.Tests`). Tests exercise `internal` types directly; they do not force types to be `public`.
- A `public` modifier on anything other than the plugin entry type is a bug. Widening visibility to make a test compile is the wrong fix - use `InternalsVisibleTo`, which is already in place.

## No Swallowed Exceptions

Every exception must be handled or propagate. An empty `catch {}`, or a `catch` that swallows without logging, is a bug. There are two **core** sanctioned swallow points, and both must log:

1. **The file logger.** Logging must never throw back into the host. If the log write itself fails, the logger swallows that failure (after a best-effort attempt) - it cannot recurse into itself, and a logging fault must not crash FanControl.
2. **The per-controller worker resilience catch.** A fault on one controller must not take down the others. The worker loop catches around a single controller's iteration, logs the fault with full context, and continues so the remaining controllers keep running.

Beyond those, the **Plugin composition root** carries a bounded set of host-seam resilience guards, each of which must also log: device enumeration and per-device open/setup (a discovery or open fault degrades to fewer/zero controllers instead of crashing the host - operational-awareness requires that a host-seam fault never takes FanControl down), and the opt-in lighting path (a bad lighting config or rejected lighting write disables lighting for that device and never affects fan control). These are the same "do not crash the host / isolate the fault" intent as the two core points, applied at the composition seam; each carries an inline `CA1031` justification and logs the failure.

Anywhere outside those sanctioned points, let exceptions propagate or wrap them with context and rethrow. Do not introduce a new swallow category, and never swallow without logging.

## netstandard2.0 Constraint

The plugin targets `netstandard2.0` so it loads into the host's runtime. This is a hard constraint:

- Do not raise the target framework of the plugin project.
- Do not use an API that is not available on `netstandard2.0`, even if the installed SDK (.NET 9) offers it and it compiles locally. Nullable reference annotations are fine (they are compile-time), but runtime APIs must exist on the netstandard2.0 surface.
- The test project targets `net8.0` and may use newer APIs in test code only - never let a net8.0-only API cross into the plugin assembly.

## Comments

- If a decision was made for a specific reason, comment WHY. Byte offsets, report IDs, the keepalive interval, and ordering choices get a one-line rationale.
- NEVER reference project phases, implementation streams, milestones, or ticket IDs in code comments. Code outlives plans. `// Phase 2 keepalive` is a bug. `// Controllers revert to default after ~2s without a keepalive write` is correct.
- No comments explaining WHAT code does - names do that.

## Constructors and Dependencies

Constructors take their dependencies explicitly (`IHidTransport`, the clock, the logger, the encoder). Validate each required dependency is non-null and throw `ArgumentNullException` on a null. Required dependencies are constructor parameters - never optional setters or `With*` for anything genuinely required.
