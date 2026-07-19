# Fork Changelog

Notable differences between this fork (**Dadarrs/nzbdav**) and upstream
[nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav).

> The auto-generated [`CHANGELOG.md`](CHANGELOG.md) tracks upstream's per-version
> history. **This** file tracks only what the fork adds on top of upstream.

This fork's guiding principle is to **stay close to upstream** and remain
image-swap compatible with it (the operational database schema is never
changed), while adding STAT pipelining, smarter health checks/repairs,
provider ordering, and a large amount of UI. Where a feature was brought in from
another community fork it is credited; everything else was built here.

**Provenance legend**

| Tag | Meaning |
| --- | --- |
| 🟢 **Fork** | Built in this fork (Dadarrs/nzbdav) |
| 🔵 **nzbdavex** | Ported and adapted from [qooode/nzbdavex](https://github.com/qooode/nzbdavex) |
| 🟣 **Pukabyte** | Ported and adapted from [Pukabyte/nzbdav](https://github.com/Pukabyte/nzbdav) |
| ⚪ **Upstream PR** | Applied from an upstream pull request not yet in a tagged release |

"Ported" means the idea and often the initial implementation came from that
project, then was reworked to fit this codebase — attribution is best-effort at
the feature level, not a claim of line-by-line copying.

---

## v0.7.0 — first versioned fork release

Forked from upstream at `794948b` (*fix(nntp): tag provider name in
connection-lock and command-error logs (#441)*). Everything below is **on top of**
that upstream baseline.

### Usenet: STAT pipelining & health-check engine

- 🟢 **Pipelined STAT article health checks.** Existence checks for both the
  on-add and background health passes can be pipelined behind a global master
  switch plus a per-provider opt-in, running a batch on a single borrowed
  connection with a linear fallback. This is the headline reason the fork
  exists.
- 🟢 **Backup providers can join health checks.** A "Use Backup Providers for
  health checks" setting lets *Backup & Health Checks*-type providers take STAT
  traffic alongside pooled providers (STAT transfers no article bytes, so block
  accounts absorb it for ~45 B each). Provider **type is the sole gate**; the
  pipelining tickbox only selects pipelined vs linear STATs.
- 🟢 **Pipelining-support probe.** A test endpoint/button on the Usenet settings
  page checks whether a provider handles pipelined STATs correctly (requires a
  positive STAT control so providers that always 430 are not falsely reported as
  supported).
- 🟢 **Connection-total accounting** now counts stat-eligible backup providers,
  so the sidebar reflects the true health-check fan-out.
- 🟢 **Robustness fixes:** an unexpected NNTP reply (e.g. `480` from a refused
  session) is treated as a provider fault and re-queried, never as a false
  "article missing"; and pool free-capacity is measured from committed permits
  rather than established connections, so bursts spill across providers instead
  of piling onto one.

### Usenet: provider ordering & configurable timeouts

- 🟢 **Drag-and-drop provider priority.** Providers can be reordered within the
  Pooled and Backup sections; list order is the priority, with cascade fill to
  the next provider as each saturates.
- 🟢 **Configurable NNTP timeouts** (command, TLS handshake, pipelined-STAT idle)
  via the config file, backed by matching knobs in the fork's UsenetSharp
  submodule ([Dadarrs/UsenetSharp](https://github.com/dadarrs/UsenetSharp)). The
  TLS handshake is now bounded, closing an unbounded-hang path on bad providers.
- ⚪ **TLS certificate-validation env vars** `NNTP_TLS_IGNORE_NAME_MISMATCH` and
  `NNTP_TLS_IGNORE_CERT_DOMAINS`, applied from
  [nzbdav-dev/UsenetSharp#2](https://github.com/nzbdav-dev/UsenetSharp/pull/2).

### Health checks & repairs

- 🟣 **Dynamic repair trigger.** When a stream hits missing articles, an
  immediate repair is queued for that item instead of waiting for the scheduled
  pass.
- 🔵 **Health page pagination, search and manual "check now"** — base ported
  from nzbdavex; extended in-fork with debounced multi-token server-side search,
  a `/api/trigger-health-check` front-queue endpoint, per-row/per-page selection,
  and a fix for a pre-existing infinite-refetch loop on small queues.
- 🟢 **Repaired/Deleted browse view.** Clicking the Repaired or Deleted counts
  opens a browse section listing the exact files, each deep-linking to the owning
  Radarr/Sonarr item (captured at repair/delete time), with an optional public
  URL per arr instance and a 24h/7d/30d/1y/All window selector over all-time
  overview cards.
- 🟢 **Health page usable with repairs disabled** — schedule and search still
  render, and the stream tracker starts eagerly.

### Overview dashboard & metrics

- 🔵 **Overview dashboard** and the **separate `metrics.sqlite` store** — ported
  from nzbdavex (the operational `db.sqlite` schema is deliberately left
  untouched so images can still be swapped with upstream in both directions).
- 🔵/🟢 **NNTP + WebDAV instrumentation.** The metrics schema is nzbdavex-derived;
  the per-attempt `SegmentFetch` records, failover-miss attribution, and
  per-provider byte counting via `CountingYencStream` were built here.
- 🟢 **Per-provider health-check traffic column** and all-time tile.
- 🟢 **Per-account provider identity** — metrics, caps and usage key on a
  nickname (or deduped hostname `host`, `host2`, …), so several accounts on one
  backbone track separately; metrics follow account **renames** and legacy
  host-keyed rows are folded in at startup.
- 🟢 **Paginated stream-history panel** on the overview, plus **Clear history**
  (a watermark that hides list entries without deleting the underlying stats).
- 🟢 **Instrumented the `/view` endpoint** like the WebDAV GET path so its reads
  are counted.
- 🟢 **Overview load performance:** stopped materializing raw fetch rows; 24h
  load dropped from ~6.8 s to ~1.2 s at ~1.5 M fetches/day.

### Queue, history & live views

- 🔵 **Paginated queue and history tables** — ported from nzbdavex.
- 🔵 **Live log viewer** with a websocket tail and download — ported from nzbdavex.
- 🟣 **Live active-streams widget** with per-stream speed and connection counts —
  ported from Pukabyte.
- 🟢 **Bulk history delete across pages** and a warning-gated "Check ALL" in Health.
- 🟢 **Per-import time & traffic stats.** History rows expand to show the
  download (blue) and verify (green) phase durations and per-provider bytes for
  each phase, with the failure message inlined.
- 🟢 **Streaming/metrics hygiene:** skip 0-byte read sessions, merge a viewing
  within a 15-minute gap into one row, exclude sidecar/metadata files, a
  newest/oldest log toggle, and a corrected "buffered" connection count that no
  longer inflates past `max-download-connections`.
- 🟢 **Client-abort propagation** through the frontend proxy, so abandoned
  streams stop fetching instead of downloading the whole file, plus a rebalanced
  connection counter.

### Provider settings UI

- 🔵 **Provider nicknames, data caps and usage bars** — ported from nzbdavex,
  including the usage endpoint and an over-limit gate that stops a block account
  being drained past its purchased size.
- 🟢 **Sectioned provider settings** (Pooled / Backup / Disabled), a
  reveal-password toggle, and unique-validated nicknames auto-filled from the
  deduped hostname.

### Configuration

- 🟢 **Live-editable `config.json`.** Every setting is mirrored to a
  human-editable file in `/config`; external edits are picked up (file watch +
  poll), the DB stays the source of truth, secrets can be referenced as
  `${ENV_VAR}`, and process-env overrides live under an `env.` section.
- 🟢 **WebUI-shaped config sections.** The file's sections and key order mirror
  the settings tabs, with a startup file-vs-DB check log and per-key change logs
  that obfuscate passwords.

### Theming & docs

- 🟢 **Theme system** with a Settings → System tab (multiple built-in themes; all
  colors tokenized so themes recolor the whole app).
- 🟢 **`design.md`** — a UI design guide (tokens, typography, component recipes)
  followed for all new UI in the fork.

### Build & CI

- 🟢 UsenetSharp is consumed as the **Dadarrs/UsenetSharp** submodule via
  `ProjectReference`; Docker and CI check out submodules recursively.
- 🟢 `:latest` tracks every push to `main` (pre-release workflow); versioned
  releases are cut manually. Docker Hub publishing was dropped in favor of GHCR
  (`ghcr.io/dadarrs/nzbdav`).

---

## Relationship to upstream

- **No operational schema changes.** All fork data lives in a separate
  `metrics.sqlite` or in additive, optional config JSON fields, so a `/config`
  directory can move between this fork and upstream `nzbdav-dev/nzbdav` in either
  direction without a migration.
- Upstream fixes and features are merged in as they land; this file tracks only
  what the fork adds on top.
