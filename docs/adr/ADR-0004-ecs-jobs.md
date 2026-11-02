# ADR-0004 — ECS identity, read lifetimes, job access and deterministic commit

**Status:** Proposed ADR; design policies **user-approved** on 2026-11-02; API and implementation pending  
**Owners:** C00, L3A and L3B  
**Trace:** [Groundwork #1](https://github.com/DaveBeusing/Groundwork/issues/1), [#5](https://github.com/DaveBeusing/Groundwork/issues/5), [#6](https://github.com/DaveBeusing/Groundwork/issues/6)

## Context

A performance-oriented engine needs multithreaded computation without race conditions, stale handles, invalidated views or nondeterministic commit order. Unbounded locking and dynamic permission discovery impose unnecessary runtime coordination. 

## Decisions

1. **User-approved generational identity (option B).** Each `EntityId` encodes (or semantically preserves) **world identity + slot + generation**. Reuse requires incrementing the generation. Stale/deleted/foreign-world handles never resolve to a different living entity. Never wrap the generation: permanently retire a slot at generation exhaustion. Exhausted ID space fails explicitly. World IDs must not alias while old handles can exist. Exact bit widths and allocation API await L3A review.
2. **User-approved single writer.** Workers compute in parallel over safe immutable sources but have no writable live ECS pointers/references. One logical ECS owner applies deferred structural and component command batches at defined barriers. The owner need not be a fixed OS thread.
3. **User-approved read lifetime.** A borrowed ECS `ReadView` is valid only through the next named synchronization/commit barrier for its world/epoch. It must be released beforehand. Jobs crossing barriers own bounded immutable data snapshots, with source version/world/target metadata; their results must be version-checked before commit. Do not deep-copy the entire world per tick.
4. **User-approved explicit access declarations (option A).** Each job declares immutable pre-dispatch component/resource read sets, partition/world scopes, privately owned outputs and intended mutation targets. No undeclared access or blanket writable live ECS. DAG dependencies are validated; independent reads/disjoint jobs may be scheduled concurrently. Worker count and queue capacity are bounded.
5. **User-approved strict conflict rejection.** Worker finish order must not determine commit order; use a reviewed stable ordering key such as (tick, phase, system, partition, source sequence). Incompatible unordered mutations to the same entity/component reject the *affected atomic batch*, with no partial writes, structured diagnostics and supervisor notification. Do not silently use priorities or last-writer-wins.

## Consequences / implementation gates

Correctness and controlled multithreading take priority over optimistic concurrent writes. L3A/L3B must finalize: representation/lease API, version tokens, `JobDescriptor` schema and validation, precise conflict domain, batch atomicity, dependency ordering, read-view invalidation and retry-free stale-result handling. Benchmarks determine appropriate access granularity; no guaranteed speedup is asserted.

Required tests: ID cross-world/reuse/exhaustion; iterator mutation; barrier-invalid view; stale snapshot; independent readers; absent declarations; DAG cycle; randomized completion order; conflict/no-partial-commit; cancellation. See [runtime-contracts](../architecture/runtime-contracts.md).