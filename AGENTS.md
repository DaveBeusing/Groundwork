# GitHub Naming and Repository Paths

- Target `master` for Groundwork pull requests.
- Name work-package branches `work/wp<number>-<slug>`.
  Example: `work/wp15-terrain-shadows`.
- For tasks without a work package, name branches
  `tasks/issue-<number>-<slug>`.
  Example: `tasks/issue-113-shadow-readback`.
- Use lowercase letters, digits, and hyphens in slugs. Use the same slug
  for the branch and its worktree.
- Name work-package issues `WP<number> · <concrete outcome>`.
  Example: `WP15 · Add terrain shadow cascades`.
- Format PR titles and commit subjects as `<type>: <scope> <English imperative>`.
  Allowed types: `feat`, `fix`, `refactor`, `test`, `docs`, `build`, `ci`.
  Example: `feat: rendering add WP15 terrain shadow cascades`.
- Link the associated issue in the PR description. Use `Closes #113` when
  the PR fully resolves the issue, or `Refs #113` when it contributes to it.
  For references between Groundwork and WYU, use `owner/repo#number`.
- Before committing or opening a PR, verify the Git root, remote, current
  branch, and diff. Resolve working paths relative to the current Git root.
- Place engine code in `src/Groundwork.<Module>/`, tests in
  `tests/Groundwork.<Module>.Tests/`, and architecture contracts in
  `docs/architecture/`. Use PascalCase for new C# filenames. Follow the
  existing WP prefixes and use kebab-case for new architecture contract
  filenames.