# .NET Architecture Rules

Rules in this file are enforced on every change. Violations are bugs.

## Layering

The plugin is layered strictly, top to bottom:

```
Plugin      # IPlugin3 implementation. Composition root + host lifecycle (Initialize/Load/Update/Close).
Worker      # Per-controller live loop: drives PWM, runs keepalive, isolates faults.
Devices     # Controller / channel domain model. Owns identity, current state, the injected clock, and the keepalive write decision.
Transport   # IDeviceTransport seam over hid.dll and WinUSB. The ONLY place a native USB call may appear, and only in its thin Windows* adapters.
Protocol    # Pure encoders: device state in, exact byte buffer out. No I/O.
Logging     # File logger. Sink only.
```

Dependencies point **downward only**. A higher layer may depend on the layers beneath it; a lower layer never reaches up. Plugin depends on everything; Logging depends on nothing. The Worker drives the Devices it owns; the Devices model uses the Transport and Protocol seams beneath it, plus the injected clock (`IClock`) and the pure `ChannelWriteDecision`, which live in the Devices layer. A file lives in exactly one layer. An upward dependency, or a cross-dependency that skips the seam, is a bug.

## The `IDeviceTransport` Seam

`IDeviceTransport` is the abstraction over the device I/O: `hid.dll` for the HID controllers, WinUSB for the L-Wireless dongles. It exists so the rest of the plugin is testable without real hardware.

- **Native USB calls are confined to `Transport/`'s adapters.** Every P/Invoke into `hid.dll`, `winusb.dll`, `cfgmgr32.dll`, `advapi32.dll` or `kernel32.dll` lives in one of the thin `Windows*` adapters (`WindowsHidApi`, `WindowsWinUsbApi`, `WindowsConfigurationManagerApi`, `WindowsThreadCanceller`), behind the interface the rest of `Transport/` is tested through (`IHidApi`/`IHidTransfer`, `IWinUsbApi`, `IConfigurationManagerApi`, `IThreadCanceller`). Only those adapters may be `[ExcludeFromCodeCoverage]`; every decision taken on their results lives outside them and is unit-tested through fakes, and the Windows-only tests (`[WindowsFact]`) run the adapters against the real APIs. No native handle or P/Invoke type appears above `Transport/`: the Protocol, Devices, Worker, and Plugin layers know only `IDeviceTransport` and plain byte buffers.
- **No shared USB library.** The plugin does not use HidSharp, or any other HID library the host process shares: a library whose device list is process-wide can be left holding a lock behind a wedged device, and the host's own HID sources then block on it for good. Enumeration and I/O are the plugin's own, on handles and threads it owns.
- **A bounded call stops where it was abandoned.** A call run under `IDeviceCallRunner` that makes more than one native call checks the token it is handed before each one, so a call given up on never starts a native call nothing would cancel. Closing a handle is the one step that never stops for it (a handle left for its finalizer would block the host's finalizer thread instead).
- The concrete transports live in `Transport/`: `HidTransport` for the HID controllers and `WinUsbTransport` for the L-Wireless dongles. Both implement `IDeviceTransport`; nothing above `Transport/` knows which it holds.
- If you find yourself calling native code in the Worker or Plugin layer, the seam is wrong - add the capability to `IDeviceTransport` instead.

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

- `{Concept}` is the domain noun (`Transport`, `Keepalive`, `LianLiProtocol`, `Controller`, `FileLog`).
- `{Role}` is the architectural role (`Transport`, `Worker`, `Encoder`, `Logger`, `Plugin`).
- Examples: `HidTransport.cs`, `KeepaliveWorker.cs`, `LianLiProtocolEncoder.cs`, `FileLogger.cs`, `LianLiPlugin.cs`.

One primary type per file, named the same as the file. Find the closest existing file and replicate its shape exactly before adding a new one.

## Minimal Public Surface

The assembly exposes exactly one `public` type: the `IPlugin3` implementation that FanControl reflects over and instantiates. **Everything else is `internal`.**

- New types default to `internal`. Make a type `public` only if it is genuinely the host-facing plugin entry type - which there is already exactly one of.
- The test project sees internals via `InternalsVisibleTo` (configured for `FanControl.LianLi.Tests`). Tests exercise `internal` types directly; they do not force types to be `public`.
- A `public` modifier on anything other than the plugin entry type is a bug. Widening visibility to make a test compile is the wrong fix - use `InternalsVisibleTo`, which is already in place.

## No Swallowed Exceptions

Every exception must be handled or propagate. An empty `catch {}`, or a `catch` that swallows without logging, is a bug. There are two **core** sanctioned swallow points, and both must log:

1. **The file logger.** Logging must never throw back into the host. If the log write itself fails, the logger swallows that failure (after a best-effort attempt) - it cannot recurse into itself, and a logging fault must not crash FanControl.
2. **The per-controller worker resilience catch.** A fault on one controller must not take down the others. The worker loop catches around a single controller's iteration, logs the fault with full context, and continues so the remaining controllers keep running.

Beyond those, the **Plugin composition root** carries a bounded set of host-seam resilience guards, each of which must also log: device enumeration and per-device open/setup (a discovery or open fault degrades to fewer/zero controllers instead of crashing the host - operational-awareness requires that a host-seam fault never takes FanControl down), and the opt-in lighting path (a bad lighting config or rejected lighting write disables lighting for that device and never affects fan control). These are the same "do not crash the host / isolate the fault" intent as the two core points, applied at the composition seam; each carries an inline `CA1031` justification and logs the failure.

Four more categories carry the same intent one level down, and each must log too:

- **Per-device isolation inside a multi-device controller.** The wireless controller drives many RF devices over one dongle pair; it isolates each device's work and each broadcast in a tick exactly as the worker isolates each controller, so one device's failure costs only that device that second.
- **Threads the plugin starts itself** - the side-by-side controller builds, each controller's loop, a stand-in's background rebuild, the lighting-only drive, the host-log hand-on, the wireless cross-reset. An exception escaping a thread the plugin started ends FanControl's whole process, so each such thread catches, logs, and hands the failure to whoever is waiting for it (or to its late-result callback).
- **Optional L-Connect settings reads.** A saved channel, pump presentation or lighting effect that is locked, corrupt or oversized degrades only its own feature, never fan control; the guard catches the specific I/O and format exceptions, not `Exception`, and logs which file and why.
- **A transport's `Dispose`.** It runs on the plugin's own threads as well as the host's, where an exception ends FanControl's whole process, so each transport catches whatever its bounded close throws, logs it with the device, and returns, leaving the handles to Windows as it does for a close that times out.

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

Constructors take their dependencies explicitly (`IDeviceTransport`, the clock, the logger, the encoder). Validate each required dependency is non-null and throw `ArgumentNullException` on a null. Required dependencies are constructor parameters - never optional setters or `With*` for anything genuinely required.
