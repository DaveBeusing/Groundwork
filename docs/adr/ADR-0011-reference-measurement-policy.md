# ADR-0011 — Reference performance qualification

**Effective date:** 2026-11-27 upon adoption

## Decision

Groundwork and WYU use the applicable feature contract to decide what a candidate may deliver and what its evidence supports.

| Delivery | Required evidence |
| --- | --- |
| Merge | Satisfy all applicable correctness, security, review, owner approval and required CI gates on the exact candidate. A full reference performance campaign and use of a dedicated reference machine are optional. Resolve any known blocking findings. |
| Preview, demo or diagnostic package | Meet the applicable merge and package-provenance gates. Label the package and its evidence as unqualified for release or production use. Keep the affected feature opt-in where its activation gate requires it. A full reference campaign and use of a dedicated reference machine are optional. |
| Release, production or activation as the default | Pass all applicable feature gates, including approved quantitative limits, installed-route and visual verification, pressure and memory limits, provenance, required CI and reviews. Use the specified protocol, endpoints and sample counts. Use the prescribed hardware, or a documented suitable replacement where the feature contract allows it or the owner separately accepts it for the affected gates. |

This decision supersedes earlier wording that required a full reference performance campaign as a prerequisite for merge or preview. Hardware correctness tests remain required where specified, including for native rendering and resource retirement. A known blocking performance finding still requires resolution before merge or preview. Publication of immutable CI or preview packages is subject to [ADR-0006](ADR-0006-packaging-release.md) for package identity and provenance. These packages may be used to collect evidence for production qualification.

## Evidence

Record each applicable result as **PASSED**, **FAILED** or **NOT RUN**, with the scope of the result. For an omitted reference campaign, record the reason and link the remaining qualification work. For a run, record source and base SHAs, package and installer identities where relevant, hardware, driver, settings, fixtures, raw samples and all failed or interrupted attempts. Treat diagnostic and workstation observations according to their measured scope. Use actual display measurements for display endpoints and Present evidence for Present endpoints.

Keep historical qualification records and any scoped exceptions attached to their original candidates. For a permitted release-qualification hardware substitution, record the replacement configuration, its suitability for the workload and endpoints, and the applicable contract provision or owner decision. A missing or failed release gate leaves release or production activation blocked.
