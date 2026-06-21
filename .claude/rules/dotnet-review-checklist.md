# .NET Structural Review Checklist

When reviewing C# changes, verify each item. Flag violations as review comments.

## Structural Checks

1. **Nullable correctness.** `Nullable` is enabled. Every reference type is correctly annotated (`T?` where null is possible, `T` where it is not). The null-forgiving `!` operator is not used to paper over a real warning - it is allowed only where nullability is genuinely provable but not expressible, and there it carries a comment. A nullable value dereferenced without a guard is a bug.

2. **Analyzer-clean under `TreatWarningsAsErrors`.** The build must produce zero warnings. Any analyzer suppression (`#pragma warning disable`, `[SuppressMessage]`) must carry an inline justification explaining why the rule does not apply. A suppression with no reason is a bug. Never lower `AnalysisLevel`, never flip `TreatWarningsAsErrors` off, never weaken `.editorconfig` to make a warning disappear.

3. **XML docs on public members.** `GenerateDocumentationFile` is on, so a public member without a doc comment fails the build. Verify the docs say something useful (what the member does and any contract a caller must honor), not a restatement of the name.

4. **Argument validation.** Public and constructor entry points validate their inputs. Required reference dependencies throw `ArgumentNullException`; out-of-range or malformed values throw `ArgumentException` / `ArgumentOutOfRangeException`. Validation happens at the boundary, before the value is used. A constructor that stores a null dependency and dereferences it later is a bug.

5. **`IDisposable` correctness.** Any type that owns an unmanaged or disposable resource (a HID device handle, a stream, a timer, a `CancellationTokenSource`) implements `IDisposable` and releases it. `Dispose` is idempotent (safe to call twice), does not throw, and is actually called by every owner - via `using`, a field disposed in the owner's own `Dispose`, or the plugin's `Close`. A leaked HID handle or an undisposed timer is a bug. Verify the ownership chain ends somewhere that disposes.

6. **No swallowed exceptions except the sanctioned points.** Every other `catch` either handles the exception meaningfully or wraps-with-context and rethrows. An empty `catch {}`, or a `catch` that logs nothing and continues, is a bug. The sanctioned swallow points are: the two core points - the file logger (must not throw into the host) and the per-controller worker resilience catch (one device fault must not stop the others) - plus the Plugin composition root's host-seam resilience guards (device enumeration and per-device open/setup; the opt-in lighting path), as described in `dotnet-architecture.md`. Every sanctioned point MUST log the failure with context and carry an inline `CA1031` justification. Flag any swallow outside those categories, any sanctioned point that discards the error without logging, and any swallow of a broad `Exception` without a `CA1031` justification.

7. **Thread-safety of the worker.** FanControl calls into the plugin (`Update`, `Close`) on its own threads while the per-controller workers run their loops. Shared mutable state crossing that boundary must be synchronized: a lock, a `volatile`/`Interlocked` field, a concurrent collection, or a clean handoff. Verify there is no read-modify-write on shared worker state without synchronization, no field written by one thread and read by another without a memory barrier, and that shutdown (`Close`/`Dispose`) cleanly stops the loop without a race against an in-flight iteration. An unsynchronized shared field on the worker path is a bug.

8. **Exact byte math preserved.** Protocol encoders produce a precise byte buffer that the hardware depends on. Any change to an encoder must be covered by a test that asserts the full encoded buffer byte for byte - not merely that the call returned without throwing. Verify report IDs, offsets, lengths, and PWM/scaling math are unchanged unless the change is deliberate and the test was updated to match. An encoder change with no byte-level assertion is an unverified change.

9. **Layering respected.** The file lives in one layer and depends only downward (`Plugin -> Worker -> Devices -> Hid -> Protocol -> Logging`). No HidSharp type appears outside `Hid/`. No I/O or clock access inside a Protocol encoder. An upward dependency or a leaked HidSharp type is a bug.

10. **Clock injection.** Keepalive and any time-based logic in the worker go through the injected clock/timer abstraction, never `DateTime.Now`/`DateTime.UtcNow`/`Stopwatch` read directly. A direct system-clock read on the worker path makes the behavior untestable - flag it.

11. **Minimal public surface.** Only the `IPlugin2` type is `public`. Flag any newly `public` type or member that is not the host entry point; it should be `internal`. Widening visibility to make a test compile is wrong - internals are visible to the test project via `InternalsVisibleTo`.

12. **netstandard2.0 compatibility.** The plugin assembly uses only APIs available on `netstandard2.0`. Flag any net8.0/.NET 9-only API that has crossed into the plugin project even though it compiles against the installed SDK; it will fail to load in the host. Test-project (`net8.0`) code may use newer APIs.

13. **No discarded errors or results.** No empty `catch {}`. No `try`/`catch` in a test that swallows the result instead of asserting it. A discarded exception hides a silent failure.

14. **No unexplained comment removal.** If a diff removes a comment, verify the comment was genuinely stale or wrong. Removing a comment that explains WHY, documents a byte-layout constraint, or records an ordering convention is a bug. Flag it.

15. **Conventions must be documented.** If a change establishes a structural convention (member ordering, byte-field grouping, naming pattern, the keepalive interval value), there must be an inline comment making the convention explicit. Undocumented conventions rot.

16. **No tombstone comments.** If a diff adds a comment recording that code, tests, or config were removed (`// X removed`, `// deleted Y`), flag it. Git is the history. Code reflects current state only.

17. **Test coverage for new types.** Every non-trivial type has a corresponding test file (`{Type}Tests.cs`). A new encoder, worker, transport, or logger without tests is an incomplete change. Encoders specifically require byte-level assertion tests; the worker requires tests for the keepalive cadence (using the injected clock) and the fault-isolation catch.

18. **No planning artifacts in comments.** Flag "Phase", "Stream", "Stage" + number, ticket IDs, milestone references. Code comments describe WHAT and WHY, never project-management context.

19. **Full words in naming.** Flag abbreviations: "config" should be "configuration", "ctrl" should be "controller", "btn" should be "button". Use complete words in identifiers.
