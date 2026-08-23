# Public GitHub Release Plan

This plan takes the current Robot Command repository to a clean initial commit
for a public GitHub repository under the Apache License 2.0. The current
repository remains the development source until the cutover phase is complete.
After cutover, it becomes read-only and the public repository becomes the
canonical upstream.

## Release target

The initial public repository should contain:

- the Robot Command desktop application, CLI, Core, Runtime, rendering,
  simulation, SDK, examples, tests, build scripts, and documentation;
- the currently supported Ghost, PX4, and ArduPilot workflows;
- the shared Logos SDK consumed as a public NuGet package;
- reproducible source builds from a clean clone without private credentials;
- an explicit support matrix and safety limitations for real vehicles;
- no field-specific endpoints, credentials, generated runtime data, or private
  package feeds.

The public release is source-first. Signed installers and additional platform
packages may follow after the first public release if they can be maintained
reliably.

## Phase status

| Phase | Status |
|---|---|
| 0 — Release scope and ownership | COMPLETE |
| 1 — Code quality, linting, and style alignment | COMPLETE |
| 2 — Deduplication and architecture cleanup | COMPLETE |
| 3 — Critical-path safety hardening | COMPLETE |
| 4 — Repository organization and editorial review | COMPLETE |
| 5 — Security, legal, and dependency clearance | COMPLETE |
| 6 — Public SDK cutover | COMPLETE; .NET public package is consumed by Robot Command; LinkD remains deferred |
| 7 — Source-only repository hygiene | COMPLETE |
| 8 — Documentation and test-surface cleanup | COMPLETE |

| 9 - CI, packaging, and release engineering | OPEN; hosted validation remains |
| 10 - Release-candidate validation | COMPLETE; local candidate validation passed; SITL/hardware and hosted release gates remain external |

The repository remains private until the remaining release gates are complete.

### Robot Command identity cutover

Product branding and product-owned identifiers have been cut over throughout
the source tree:

- human-facing text uses **Robot Command**;
- product-owned namespaces, assemblies, project paths, solution entries,
  generated observer SDK symbols, CLI identifiers, environment variables, and
  protocol URI labels use `RobotCommand` or `robot-command`;
- `Psycraft.Logos.Api.Sdk` is the distributable package identity. The
  `Logos.Api.Sdk` assembly, namespaces, and protobuf/API identifiers remain
  unchanged intentionally for source compatibility.

Validation: no remaining product-brand references were found in the source or
documentation scan. The renamed solution builds with 0 errors and the full
test suite passes.

## Current release blockers found during audit

The original security and SDK blockers are complete. Remaining release gates
are the repository cutover and external validation:

- Complete the clean source-only import and make the approved GitHub repository
  public only after the Phase 11 review.
- Run the required hosted GitHub Actions matrix and draft-release workflow from
  a clean checkout; local Phase 10 validation does not replace those checks.
- Run the available PX4/ArduPilot SITL suites and the operator-gated hardware
  checklist before claiming those validation results.
- Keep LinkD and any private Logos operational dependencies out of the public
  build graph.

## Phase 0 — Freeze scope and assign ownership

### Decisions

- Product/display name: **Robot Command**.
- GitHub repository: rename the existing private repository to **Robot-Command**
  before publication.
- Default branch: `main`.
- First release label: `0.1.0`.
- Repository owner: Psycraft Corporation.
- Contribution model: public issues and pull requests after publication, with
  maintainer review and merge approval.
- Import strategy: clean source-only initial commit; do not copy the current
  repository history.
- Feature freeze: the current implementation is the complete `0.1.0` scope.
  Only security, legal, build, release, or blocking correctness fixes are
  allowed after the freeze.
- Product-owned project, assembly, namespace, symbol, path, URI, CLI, and
  environment identifiers use `RobotCommand`; human-facing text uses
  **Robot Command**. Logos remains only where technically required, including
  the Logos SDK, API names, generated contracts, and integration documentation.

### Ownership

- Release owner: current project maintainer.
- Legal/licensing approver: Psycraft Corporation.
- SDK/package owner: Psycraft engineering maintainers.
- Security contact: GitHub private vulnerability reporting handled by Psycraft
  repository maintainers.
- Repository administrators: Psycraft Corporation GitHub owners.
- Public contribution policy: issues and pull requests are welcome after
  publication and require maintainer review.

### Frozen `0.1.0` scope

The release scope includes the current implementation of:

- Ghost simulation and profiles;
- PX4 and ArduPilot MAVLink operations;
- missions, teams, formations, fences, and manual control;
- maps, 3D views, mesh preview, diagnostics, and CLI workflows;
- SDK, observer, rendering, and simulation projects.

The following remain documented limitations rather than new release work:

- Logos vehicle execution remains deferred where it is currently stubbed;
- hardware validation remains operator-gated;
- platform-specific GUI support remains limited to the currently supported
  Windows target;
- future mission, formation, VR, mesh, and backend enhancements are post-
  `0.1.0` work.

### Clean-import boundary

The future initial public commit includes only reviewed source and intentional
documentation:

- include source projects, tests, examples, tools, build scripts, docs,
  solution files, package metadata, and public licensing files;
- exclude local app settings, credentials, NuGet credentials, runtime `data/`,
  SITL state, logs, screenshots, generated binaries, `bin/`, `obj/`,
  `artifacts/`, `out/`, and temporary files;
- exclude internal-only instructions, field-specific configuration, and private
  endpoints;
- retain only sanitized examples using placeholders or localhost.

The clean import will be created later from a staging directory, not by copying
the current Git history. The security phase still must rotate credentials and run the
secret scan before any public visibility change.

### Completion record

```text
Phase 0 status: COMPLETE
Release: 0.1.0
Product: Robot Command
Repository: Robot-Command
Default branch: main
Owner: Psycraft Corporation
Import strategy: clean source-only initial commit
Feature freeze: active
Completed: 2026-08-22 UTC
```

### Exit criteria

- [x] Ownership recorded.
- [x] Robot Command name and repository slug approved.
- [x] Apache-2.0 target recorded for the public release.
- [x] `0.1.0` release label recorded.
- [x] `main` branch recorded.
- [x] Clean source-only import confirmed.
- [x] Feature scope frozen.
- [x] Public pull-request model recorded.

Phase 0 is complete. The next required phases are code-quality review, cleanup,
critical-path hardening, and repository organization. Security, legal, and
dependency clearance must also finish before the repository is made public.

## Phase 1 — Code-quality review and Google C# alignment

1. Establish a practical Google-style C# profile for this repository covering
   indentation, braces, naming, file layout, using directives, nullable
   annotations, XML documentation, async APIs, cancellation, exceptions, and
   test naming. Apply it consistently without introducing a broad namespace or
   public API rename before `0.1.0`.
2. Encode enforceable rules in `.editorconfig`, compiler settings, and focused
   analyzers. Classify existing warnings and fix them where they indicate real
   defects; do not hide security or build errors with a baseline.
3. Review public and internal contracts for appropriate visibility, immutable
   data, nullability, cancellation, disposal, logging, error propagation, and
   thread-affinity assumptions.
4. Format and rename code in staged areas, preserving compatibility shims where
   the frozen release scope requires them. Product-owned identifiers use the
   approved Robot Command naming map; technical Logos SDK/API identifiers remain
   unchanged.
5. Remove generated-looking comments, redundant summaries, speculative prose,
   and repetitive boilerplate. Keep comments that explain safety invariants,
   protocol behavior, compatibility decisions, or non-obvious algorithms.
6. Produce a warning and analyzer inventory before and after the review, with
   each remaining warning either fixed, explicitly justified, or scheduled
   outside the frozen release.

Exit criteria:

- the style profile is documented and automated checks are reproducible;
- public API compatibility and nullability changes are reviewed;
- no unexplained new warnings or formatting drift remain;
- code comments and naming read as maintained engineering documentation.

### Phase 1 completion record

Phase 1 status: COMPLETE
Completed: 2026-08-22T03:02:26Z
Lint: whitespace and style verification pass; error-level solution verification pass
Release build: 0 errors; remaining warnings inventoried during Phase 1 and the development checks in `docs/development.md`
Release tests: 829 passed, 0 failed, 0 skipped
Warnings-as-errors enrollment: Core, Rendering, Simulation, and SDK
Compatibility: existing `RobotCommand.*` namespaces and public contracts retained

## Phase 2 — Deduplication, dead-code removal, and architecture cleanup

1. Map duplicate implementations and competing sources of truth across map
   motion, telemetry projection, mission compilation, formation transforms,
   MAVLink commands, profile stores, rendering, and CLI/GUI workflows.
2. Select one implementation owner for each behavior. Route compatibility
   adapters through that owner instead of maintaining parallel logic in a
   view model, CLI handler, backend adapter, or legacy service.
3. Review the project graph and classify every project as application,
   reusable library, platform adapter, test fixture, example, or tool. The
   current project count is not automatically wrong, but every project must
   have a clear boundary, consumer, and build purpose.
4. Evaluate whether Rendering, Rendering.Veldrid, Simulation, and Simulator
   should remain separate. Merge
   only projects whose dependencies and lifecycles are genuinely the same;
   retain isolation where it protects Core portability, optional GPU support,
   test-only tools, or worker-process boundaries.
5. Identify and remove proven-unused projects, orphan roots, stale feature
   flags, obsolete compatibility paths, generated files, and unreferenced
   fixtures. Move intentionally deferred code to a clearly named boundary
   with a reason rather than leaving dead branches in active workflows.
6. Consolidate repeated validation, result/error models, localization helpers,
   persistence utilities, and rendering math where doing so reduces behavior
   drift without creating an indiscriminate utility layer.
7. Update tests and documentation with each consolidation. Record before/after
   project and code counts, coverage changes, and any compatibility decision.

Exit criteria:

- each project has a documented purpose and at least one real consumer or
  explicit test/tool role;
- each significant behavior has one implementation owner;
- dead code is removed or justified, with no stale references;
- Core remains free of GUI, renderer, platform, and transport dependencies;
- the solution builds and tests cleanly after any project consolidation.

### Phase 2 completion record

Phase 2 status: COMPLETE
Project consolidation: Rendering.Meshes merged into Rendering
Repository projects: 14 -> 13
Solution projects: 13 -> 12
Dead code policy: proven-unused code only
Public API compatibility: reviewed and retained
Release build: passing (0 errors; existing warning inventory retained, including NU1903)
Release tests: passing (832 passed, 0 failed, 0 skipped)
Tracked C# source files: 594 -> 595 (architecture coverage added)
Formatting: whitespace and style verification passing
MeshProbe: OBJ inspect smoke test passing
Completed: 2026-08-22T03:27:39Z

Implementation notes:

- The approved project-consolidation exception was used for the portable mesh
  pipeline. Mesh source files now live in `RobotCommand.Rendering`; the
  `RobotCommand.Rendering.Meshes` namespace remains for source-level
  compatibility, while the old project and assembly reference were removed.
- Rendering, Runtime, Avalonia, Veldrid, Simulation, Simulator, SDK, CLI,
  GUI, tests, Observer, and MeshProbe retain their documented boundaries.
- Canonical workflow owners and dependency direction are documented in
  `docs/architecture.md`. Compatibility adapters, safety paths, and deferred
  backends were retained intentionally.
- No low-confidence dead code was removed. The reviewed 3D math helper remains
  active in Runtime and GUI paths; no stale project references or generated
  source references remain.

## Phase 3 — Critical-path hardening against established GCS behavior

1. Use Mission Planner and QGroundControl behavior as operational references,
   not as code to copy and not as proof that identical labels imply identical
   backend semantics. Record the expected behavior for each backend.
2. Define and test invariants for reviewed commands, preflight findings,
   ACK/status-text handling, mode confirmation, completion, timeouts, stale
   telemetry, target loss, operator takeover, cancellation, and shutdown.
3. Audit Arm, Disarm, Takeoff, Go To, altitude, heading, Hold, RTL, and Land;
   mission upload/execution; fences; manual-control release; and formation
   entry/movement/release for Ghost, PX4, and ArduPilot.
4. Preserve explicit native paths: PX4 Offboard/Hold, ArduPilot Guided/Brake/
   AUTO, and Ghost local simulation. Ensure recovery commands remain available
   where safe and mixed-backend Team actions fail atomically rather than leaving
   only part of a Team moving.
5. Harden MAVLink transfer and command state machines against duplicate,
   delayed, malformed, unsupported, denied, and out-of-order messages. Never
   report success from an ACK alone when observed state is required.
6. Add failure-injection coverage for transport loss, mode loss, stale
   telemetry, failsafe, worker failure, cancellation, reviewed-plan expiry,
   and shutdown. Ensure every failure has an actionable human-readable reason.
7. Compare CLI, GUI, and command-history wording and completion criteria with
   established GCS behavior while retaining Robot Command's generic frontend
   terminology and exposing backend-native detail where it matters.
8. Run Ghost, PX4 SITL, and ArduPilot SITL checks where available. Mark real
   hardware validation as pending unless it was physically performed and
   independently recorded.

Exit criteria:

- every critical operation has explicit preflight, dispatch, completion,
  timeout, interruption, and safe-release behavior;
- mixed-backend operations cannot silently continue with partial membership;
- status text and ACK failures preserve actionable native reasons;
- test evidence distinguishes Ghost, SITL, and hardware validation.

### Phase 3 completion record

Phase 3 status: COMPLETE
Safety parity scope: critical-path behavior only
Frontend workflow parity: Robot Command workflows retained
PX4 SITL validation: explicitly unavailable in this run; no heartbeat arrived on the configured listener
ArduPilot SITL validation: passing heartbeat/diagnostics smoke; full command sequence remains separately documented and hardware-gated
Ghost validation: passing
Hardware validation: pending operator-run testing
Release build: passing
Release tests: passing (834 passed, 0 failed, 0 skipped)
Formatting: whitespace and style verification passing
Completed: 2026-08-22T04:07:35Z

## Phase 4 — Repository organization and human editorial review

1. Classify root files and directories as source, test, example, tool,
   documentation, build configuration, generated output, local state, or
   obsolete. Keep the public tree easy to scan: `src/`, `tests/`, `examples/`,
   `tools/`, `build/`, and `docs/` should have obvious purposes.
2. Remove orphan root directories, stale outputs, temporary files, duplicate
   documentation, and generated content. Confirm references before removing
   legacy `Views/` or `ViewModels/` roots and similar leftovers.
3. Review README, docs, comments, examples, test names, fixtures, and release
   text for repetitive AI-style boilerplate, unsupported superlatives,
   generic roadmap language, redundant explanations, and claims without
   evidence. Replace them with concise, specific, maintainer-reviewed prose.
4. Keep operational UI text compact; put rationale and protocol detail in
   documentation instead of adding persistent explanatory blocks to the app.
5. Apply Robot Command branding consistently in public-facing names while
   retaining `Logos` only for the SDK dependency, technical APIs, legacy
   namespaces, and integration documentation where it is accurate.
6. Review examples and fixtures for realistic authorship, concrete expected
   behavior, sanitized endpoints, and reproducible results. Do not add AI
   attribution or detection language to source or documentation.

Exit criteria:

- the repository tree is intentional and easy for a new contributor to scan;
- no obsolete, generated, private, or machine-specific content remains in the
  release staging tree;
- public prose is concise, evidence-backed, and reviewed by a human maintainer;
- Robot Command is the consistent public product identity.

## Phase 5 — Security, legal, and dependency clearance

1. Revoke and rotate every credential found in `.env.local`, local NuGet
   configuration, app settings, shell history, CI logs, or Git history. Do not
   copy any credential into the public repository.
2. Scan the complete source tree and any history that will be published with a
   secret scanner such as Gitleaks or an equivalent approved tool. Review every
   finding manually and retain the scan report outside the repository if it
   contains sensitive values.
3. Replace the MIT license with the approved Apache License 2.0 text. Add
   copyright ownership and a `NOTICE` file where required.
4. Inventory every dependency, bundled runtime, generated source, image, map
   asset, and example. Record license, version, source URL, redistribution
   permission, and required notices.
5. Preserve third-party licenses. Apache-2.0 applies to Robot Command-owned
   source; it does not relicense Avalonia, Mapsui, Veldrid, GStreamer, SDK
   dependencies, map data, or other third-party material.
6. Review GStreamer packaging and notices for each supported platform. Keep
   `THIRD-PARTY-NOTICES/GStreamer.txt` and any required plugin/runtime notices
   beside release artifacts.
7. Review Logos branding, SDK terms, API examples, map attributions, sample
   files, and screenshots for permission to redistribute.
8. Replace `SECURITY.md` with a real vulnerability-reporting process and a
   monitored security contact. Do not use a personal credential or private
   endpoint in the policy.

Exit criteria:

- secret scan passes for the exact source/history import;
- all exposed credentials are revoked;
- license, notices, dependency inventory, and security contact are approved;
- redistribution rights are recorded for bundled assets.

## Phase 6 — Public Logos SDK cutover

1. Publish the approved Logos .NET SDK package to the public NuGet feed with:
   package metadata, README, license expression, repository URL, source link,
   symbols/source packages where appropriate, and a public version.
2. Keep the LinkD package out of the active application dependency graph. Its
   compatibility surface may remain in source, but it is unsupported and
   deferred without a public package dependency.
3. Update `Directory.Packages.props` to the approved public SDK version.
4. Replace `NuGet.config.template` with a public-source configuration. The
   public default must not contain an Azure source, credential placeholder, or
   private package mapping.
5. Remove private-feed assumptions from README, package documentation, build
   scripts, CI, and developer setup instructions.
6. Verify that the application still consumes the SDK as an external package;
   do not add project references to an internal SDK checkout.
7. Test package restore from a clean machine/container with no Azure token and
   no private NuGet configuration.

Exit criteria:

- `Psycraft.Logos.Api.Sdk` `0.1.0-beta.8` is published publicly on NuGet.org;
- Robot Command restores and consumes `0.1.0-beta.8` from NuGet.org;
- the `Logos-dotnet` repository may remain private;
- LinkD remains explicitly deferred and is not part of the supported build
  graph.

### Phase 6 .NET SDK public-preview decisions

The first public SDK release is a preview and uses the existing generated
client identity:

- Package ID: `Psycraft.Logos.Api.Sdk`; namespaces and assembly remain
  `Logos.Api.Sdk` for source compatibility;
- Generated repository: `Psycraft-Corporation/Logos-dotnet`;
- Public repository branch: `main`;
- Preview version: `0.1.0-beta.8`;
- License: Apache-2.0;
- Publication authority: the existing internal Azure DevOps Edge pipeline;
- Initial public scope: .NET SDK only; Python, TypeScript, and C++ remain
  private until their own release gates pass.

The public pipeline path is explicitly guarded. It targets only
`Logos-dotnet`, publishes only `Psycraft.Logos.Api.Sdk` to NuGet.org, refuses to
overwrite an existing public version, and retains the existing private SDK
paths for later releases. The generated SDK repository remains a release
mirror of the internal protobuf definitions and generation scripts; it is not
the source of truth for API changes.

Phase 6 completion evidence is limited to the public .NET package and its
consumer. The generated `Logos-dotnet` repository may remain private, and the
LinkD package is outside this phase.

Verification on 2026-08-23: the NuGet.org flat-container endpoint for
`Psycraft.Logos.Api.Sdk` `0.1.0-beta.8` returned HTTP 200. Robot Command restored
the complete solution using a NuGet.org-only configuration, built Release with
0 errors, and passed the current deterministic test suite. The local ignored NuGet.config was
cleaned of the previous Azure credential and private/test feed mappings.

### Phase 6 completion record

The public package and clean NuGet.org consumer restore have been verified:

```text
Phase 6 status: COMPLETE
Public package: Psycraft.Logos.Api.Sdk 0.1.0-beta.8 on NuGet.org
Robot Command restore: passing from NuGet.org
Logos-dotnet repository: private by choice
LinkD: explicitly deferred and unsupported
Completed: 2026-08-23T05:08:58Z
```

## Phase 7 — Repository hygiene and source-only staging

Phase 7 keeps the repository private while producing the exact source tree that
will later be imported publicly.

- Audit tracked and untracked roots and classify source, tests, examples, tools,
  documentation, legal files, generated output, local state, and obsolete files.
- Exclude credentials, local configuration, NuGet credentials, SITL state, logs,
  screenshots, telemetry, runtime data, generated output, and internal instructions.
- Strengthen `.gitignore` and run `tools/validate-source-tree.ps1` against the
  clean staging tree. The validator rejects secrets, private Azure endpoints,
  generated output, local state, oversized files, and unapproved binaries.
- Keep examples sanitized and use Robot Command for public branding. Logos remains
  only for the SDK, generated contracts, and technical compatibility identifiers.
- Record the final tree inventory and file counts in this plan.

Exit criteria:

- source-only staging validation passes;
- no credentials, private endpoints, field data, or generated output is present;
- a clean checkout can restore and build using documented prerequisites;
- Phase 6 remains incomplete until the public .NET SDK package and NuGet.org
  consumer restore are verified.

## Phase 8 — Documentation and test-surface cleanup

The public documentation surface is intentionally small and human-maintained.
The canonical documents are:

- `README.md`;
- `docs/architecture.md`;
- `docs/development.md`;
- `docs/operations-and-safety.md`;
- `docs/support.md`;
- `docs/sdk-and-packages.md`;
- `docs/dependency-inventory.md` and the required legal/security/release records.

Superseded implementation-history, code-drop, framework-status, and duplicate
feature documents are removed rather than mechanically summarized. YouTube
how-to material is kept outside this repository cleanup.

- `README.md` is the build and orientation entry point.
- Architecture, development, safety, support, and SDK/package guidance are concise
  and link-checked with `tools/validate-docs.ps1`.
- The single test project remains single-project to avoid more fragmentation.
  Tests are organized by behavior and use `Fast`, `Integration`, `SITL`, and
  `Hardware` categories where applicable.
- Retain tests for safety, protocol state machines, persistence, missions, fences,
  formations, manual control, SDK compatibility, and critical GUI/CLI behavior.
- Remove or merge tests only when they target deleted code, duplicate an observable
  contract, contain no meaningful assertion, or rely on stale fixtures. Record the
  before/after counts and every removal rationale.

Exit criteria:

- a new contributor can build and test from `README.md`;
- documentation claims match the implementation;
- default tests are deterministic and live SITL/hardware tests are explicitly gated;
- the Release build, test suite, formatting checks, source validator, and docs-link
  validator pass.

## Phase 9 — CI, build, packaging, and release engineering

1. Add GitHub Actions for:
   - restore/build/test on every pull request;
   - Core, Runtime, Rendering, Rendering.Veldrid, Simulation, and Simulator portable
     builds;
   - Windows GUI and CLI Debug/Release builds;
   - MeshProbe and software-renderer tests;
   - package validation and secret scanning.
2. Add Linux/macOS CI for portable projects. Do not claim the Avalonia desktop
   application is cross-platform until its platform-specific dependencies are
   separately validated.
3. Make CI independent of local `NuGet.config`, Azure credentials, field
   connections, GStreamer installations, and running SITL containers.
4. Add deterministic version injection from the release tag and verify package
   and assembly metadata.
5. Define release artifacts:
   - source archive;
   - Windows portable application package;
   - CLI package if distributed separately;
   - checksums and release manifest;
   - third-party notices;
   - optional SDK NuGet packages.
6. Verify bundled GStreamer paths, plugins, licenses, and startup behavior on a
   clean Windows machine. Keep software rendering available on machines with
   no dedicated GPU.
7. Add a packaging smoke test that starts and shuts down the application, CLI,
   and simulator without leaving worker processes or temporary state.
8. Add dependency update automation and define how security advisories are
   triaged, especially for the known old Newtonsoft.Json warning.

Exit criteria:

- required CI checks pass on a clean pull request;
- release artifacts are reproducible from a tag;
- portable projects pass Linux/macOS CI;
- Windows packaging and GStreamer notices pass clean-machine validation.

Phase 9 implementation record:

- GitHub Actions workflows are present for deterministic CI and tag-based
  draft releases, with public-only restore and least-privilege permissions.
- Release tags are authoritative; Windows application and CLI archives include
  SHA-256 checksums, a release manifest, and third-party notices.
- Linux and macOS validate portable projects only. Windows binaries are
  unsigned in this phase. Robot Command does not publish NuGet or LinkD;
  Azure DevOps remains responsible for the generated SDK package.
- Local validation is required before completion. The hosted matrix and draft
  release job must also run successfully before Phase 9 is marked COMPLETE.

## Phase 10 — Release-candidate validation

Create a release-candidate branch or tag from the cleaned source and run:

```powershell
./build/validate-release-candidate.ps1 -Version 0.1.0-rc.1
```

The validator uses `NuGet.config.template`, the current `RobotCommand.sln`,
and the current `src/sdk/RobotCommand.Sdk` project path. It writes a JSON
checklist under `artifacts/release-candidate/` and packages the Windows
application and CLI with the tag/commit metadata supplied to the script.

Also validate:

- Ghost CLI creation, missions, teams, formation entry/movement, fences, and
  clean deletion/shutdown;
- PX4 and ArduPilot adapter tests and any available SITL smoke suites;
- manual-control safety paths and reviewed operations;
- map and 3D software rendering;
- mesh probe and asset preview tests;
- public SDK package restore and a minimal external consumer;
- JSON/NDJSON CLI output;
- English/French resource completeness;
- no secret, private endpoint, or stale developer-preview claim in the final
  source tree;
- clean install/uninstall or portable-package startup on the supported Windows
  version.

Record the results in a release-candidate checklist with commit SHA, toolchain
versions, test counts, artifact hashes, and explicit hardware-gated items.

Phase 10 implementation record:

- The release-candidate validator now uses the public-only NuGet template,
  current solution/project paths, explicit MeshProbe restore, and a clean
  generated package directory before packaging.
- The validator records every step in a machine-readable checklist and runs
  source, dependency, documentation, CLI, 3D, MeshProbe, packaging, archive,
  and startup checks in one fail-fast path.
- Formation target limiting was corrected so a pending rotation or resize is
  included in the combined translation/transform speed budget before the first
  transform tick is published.
- Formation timing assertions were made resilient to normal test-runner thread
  pool scheduling without weakening the motion or safety assertions.
- MeshProbe now restores its current portable project graph before its smoke
  test, preventing stale assets from an earlier repository layout from masking
  the independent tool check.

See [docs/release-candidate-checklist.md](docs/release-candidate-checklist.md)
for the local evidence record and artifact hashes.

Phase 10 status: COMPLETE
Release candidate: 0.1.0-rc.1
Local source-only validation: passing
Deterministic tests: 827 passed
Public SDK consumer: passing (`Psycraft.Logos.Api.Sdk` 0.1.0-beta.8)
Windows application package: passing
Windows CLI package: passing
GStreamer verification: passing (1.26.11)
Release manifest and checksums: passing
SITL validation: not requested in this local candidate run
Hardware validation: pending operator-run testing
Hosted GitHub CI and draft-release validation: retained as the Phase 9 external gate
Completed: 2026-08-23T13:55:13Z
The canonical checklist and command are documented in
`docs/release-candidate-checklist.md`.

## Phase 11 — Repository cutover

### Single-commit public import policy

Development may continue in the private repository while the release gates are
being completed. Private preparation history is not part of the public release
history. Before publication, create a fresh source-only staging tree and import
it into the GitHub repository as one orphan commit on `main`:

- do not copy the existing `.git` directory or private repository history;
- exclude credentials, local configuration, runtime data, generated output,
  release artifacts, logs, screenshots, and SITL state;
- use the commit message `Initial Robot Command 0.1.0 source release`;
- force-update the empty or placeholder GitHub `main` branch only after the
  staging and release checks pass;
- retain a private backup of the pre-cutover repository until the public commit
  and release artifacts have been verified;
- after cutover, use ordinary commits and pull requests in the public
  repository.

The final public repository must show exactly one initial commit. This is a
history boundary, not a product-versioning exception: the release tag and
artifact metadata still identify the approved `0.1.0` release commit.

1. Create the new GitHub repository with the approved owner, description,
   visibility, Apache-2.0 license, default branch, and security settings.
2. Freeze feature work in the private repository and run the final release,
   legal, security, and source-only staging checks.
3. Enable secret scanning/push protection, Dependabot, issue templates, PR
   templates, discussions or a support issue policy, and branch protection.
4. Import only the approved clean source tree as the single orphan initial
   commit on `main`. Do not
   copy local data, `.env.local`, `NuGet.config`, app settings, artifacts, or
   private Git history.
5. Tag the initial release and publish checksums, notices, source archive, and
   binary artifacts from the same commit.
6. Update repository URLs in `Directory.Build.props`, package metadata, README,
   docs, and SDK metadata.
7. Replace the old repository README with a deprecation notice pointing to the
   public repository. Make the old repository read-only/archive it after the
   public repository is verified.
8. Keep a short overlap period in which the old repository is not accepting
   new feature work. All subsequent changes go through the public repository.

Initial-commit acceptance checklist:

- [ ] Apache-2.0 `LICENSE` and required `NOTICE` are present.
- [ ] Public SDK package is published and restore succeeds without Azure.
- [ ] No credentials, private endpoints, field data, or generated output are
      present.
- [ ] README, CONTRIBUTING, SECURITY, and support matrix are complete.
- [ ] CI is green from a clean clone.
- [ ] Windows GUI and CLI Release artifacts are reproducible.
- [ ] Portable Core/Rendering/Simulation/CLI builds are green where promised.
- [ ] Third-party notices and GStreamer notices ship with artifacts.
- [ ] Ghost smoke tests and available PX4/ArduPilot SITL evidence are recorded.
- [ ] Release tag and checksums match the published source commit.
- [ ] Old repository points to the new canonical repository and is read-only.

## Phase 12 — Post-release operations

- Keep the public repository as the sole canonical source.
- Use SemVer and release notes that separate fixes, backend changes, and
  safety-relevant behavior.
- Publish SDK updates before changing Robot Command package references.
- Maintain a compatibility table for .NET, Avalonia, GStreamer, MAVLink, PX4,
  ArduPilot, and operating-system support.
- Require issue/PR templates to collect backend, connection type, logs, and
  reproducible steps without requesting credentials or flight data.
- Run scheduled dependency, secret, license, and clean-build audits.
- Keep hardware validation explicitly operator-run and never infer it from CI or
  SITL results.

## Suggested execution order

The practical order is:

1. Complete Phase 0 and keep the feature freeze active.
2. Run the code-quality and Google C# review in Phase 1.
3. Audit the project graph, consolidate duplicate behavior, and remove dead
   code in Phase 2.
4. Harden command, mission, fence, manual-control, and formation critical paths
   against the recorded GCS behavior in Phase 3.
5. Organize the repository and complete the human editorial review in Phase 4.
6. Revoke/rotate exposed credentials and complete the legal/dependency audit in
   Phase 5.
7. Publish the Logos SDK publicly and prove a credential-free restore in Phase 6.
8. Produce the clean source-only repository import in Phase 7, then complete
   public documentation and contributor setup in Phase 8.
9. Add CI, packaging, and release automation in Phase 9.
10. Build, test, package, and validate the release candidate in Phase 10.
11. Create the public GitHub repository, make the initial commit, tag the
    release, and deprecate the old repository in Phase 11.

No public repository should be made visible until Phases 1–8 are complete and
the public SDK restore gate has passed.

### Phase 4 completion record

Phase 4 status: COMPLETE
Project organization: functional source folders adopted
Project consolidation: Rendering.Avalonia merged into the GUI application
Veldrid boundary: retained as a portable project for MeshProbe and future non-GUI consumers
Dart SDK: moved to sdks/dart
Stale/generated content: removed or excluded from release staging
Public editorial review: complete
Robot Command public branding: complete
Public API compatibility: reviewed and retained
Release build: passing
Release tests: passing
Formatting checks: passing
Clean source-only staging check: passing
Repository .NET projects: 13 -> 12 (including MeshProbe)
Solution projects: 12 -> 11 (Rendering.Avalonia removed)
Tracked source files checked: 722
Completed: 2026-08-22T05:24:08Z

### Phase 5 security clearance status

Phase 5 security, legal, and dependency clearance is complete.

- Apache-2.0 `LICENSE`, `NOTICE`, `SECURITY.md`, dependency inventory, and
  third-party notices are present.
- Source-only Gitleaks and Git-history scans pass with redacted reports held
  outside the repository.
- The previously exposed Azure Artifacts credential was revoked and rotated by
  the owner. The local ignored `NuGet.config` was cleaned and contains only
  the public NuGet.org source.
- Psycraft legal approval of the Apache-2.0 license and notice set is complete.

The source-only validator and external scan reports are the non-secret scan record; sensitive reports remain outside the repository.

Phase 5 status: COMPLETE
Repository-side security/legal preparation: complete
Secret scan of source-only staging: passing
Git-history scan: passing
Credential rotation: complete
Legal approval: complete
License: Apache-2.0
NOTICE: approved
Dependency inventory: complete
Third-party notices: complete
Security policy: GitHub private vulnerability reporting
Private SDK cutover: deferred to Phase 6
Release build: passing with documented dependency/analyzer warnings
Release tests: passing
Clean source-only staging check: passing
Completed: 2026-08-23T05:10:17Z

### Phase 7 completion record

Phase 7 status: COMPLETE
Source-only staging: passing
Private state excluded: passing
Repository hygiene review: complete
Public branding review: complete
Source-only files checked: 695
Completed: 2026-08-23T00:11:12Z

### Phase 8 completion record

Phase 8 status: COMPLETE
Canonical documentation set: established (6 docs plus legal/release records)
Documentation cleanup: complete (44 docs -> 6 canonical docs)
Test suite: reorganized and reviewed in one project
Duplicate/dead tests: no low-confidence removals; retained tests protect observable behavior
Test files: 143 before -> 143 after organization
Default deterministic tests: 827 passing
Full test suite: 834 passing
SITL tests: explicitly categorized and gated
Release build: passing (0 errors; existing warning inventory retained)
Release tests: passing
Formatting checks: passing
Source-only staging check: passing
Documentation link check: passing
Completed: 2026-08-23T00:11:12Z
