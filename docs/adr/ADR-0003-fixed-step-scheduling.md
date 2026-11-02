# ADR-0003 — Fixed-step simulation and bounded overload recovery

**Status:** Proposed ADR; scheduling policy **user-approved** on 2026-11-02; implementation not yet present  
**Owner:** L2 / C00  
**Trace:** [Groundwork #1](https://github.com/DaveBeusing/Groundwork/issues/1)

## Context

Variable-size physics time steps can make collision behavior unstable and non-repeatable. Unlimited catch-up causes a spiral of death under sustained overload. Completely skipping simulation steps can break game-state consistency.

## Decision

- Use a configurable **60-Hz fixed simulation base tick** and **20-Hz gameplay/production default** (every third base tick), tracked through drift-safe integral ticks or equivalent. Physical simulation always receives one fixed base delta.
- Accept buffered inputs at a documented boundary; publish immutable observer state after coordinated systems and commit barriers. The exact order of job command commit relative to phase-specific updates requires L2/L3 review.
- On a stall, process **at most five** fixed simulation ticks in one scheduler iteration, with a **configurable initial maximum backlog of 250 milliseconds**. Prioritize catch-up over rendering within responsiveness limits.
- During persistent overload, cap debt and discard excess *elapsed wall-clock time only*, so game time may lag real time; **never** skip required physics ticks or process a larger variable delta. Record capped time, pending debt, steps applied and overload count.
- Pause freezes accumulation and advancement while host events and cancellation continue; resume samples a new monotonic clock to prevent a huge catch-up. Rendering interpolation is observer-only and bounded to `[0,1)`.
- Use a synthetic, monotonic test time source independent of UTC/logging clocks.

## Alternatives / consequences

Rejected: unbounded catch-up, variable physics steps and unreported time skipping. Bounded scheduling preserves stability and responsiveness at the cost of slower game-time progression under sustained overload. Numeric defaults require benchmark-driven tuning, without changing these safety semantics.

## Required tests

A synthetic-clock second produces exactly 60 base and 20 gameplay steps; cover jitter, 250-ms stalls, overload, pause/resume, cancellation, interpolation bounds, stable phase ordering and metrics. This ADR does not claim the scheduler or tests exist.