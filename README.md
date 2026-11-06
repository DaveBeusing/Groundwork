# Groundwork

Groundwork is a reusable C# / .NET 10 game engine, independent from Worlds Yet
Unseen. Windows is the initial target, with a Direct3D 12 provider.

WP01 Core provides shared host contracts and the state authority for job,
world and safety coordination. The six approved modules and their dependency
boundaries are documented in [module boundaries](docs/architecture/module-boundaries.md).

See docs/build-bootstrap.md for prerequisites, build commands, package
identity/provenance policy, ownership boundaries and verification limitations.
Changes are made through scoped PRs targeting master.

The [WP01 foundation handover](docs/wp01-handover.md) records the verified
engine and WYU baselines, reproduction steps and WP02 entry conditions.

The [Groundwork and WYU roadmap](docs/roadmap.md) tracks completed handovers
and planned milestones across both repositories.

## License

Groundwork is licensed under the [Apache License, Version 2.0](LICENSE)
(SPDX identifier: `Apache-2.0`). See the root LICENSE for the complete terms.
