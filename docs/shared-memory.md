# Shared company memory (design)

> **Status: design proposal, nothing implemented yet.** The tools, folders and components named
> here do not exist in the connector. This document is the plan agreed for building them; sections
> marked *decision* record choices already made, sections marked *open* still need one.

Shared memory is a store of durable notes about how the company and each project work — decisions,
preferences, exceptions, corrections and recurring workflows — that every colleague's agent reads
automatically in its next session, whatever client it runs in (Claude Code, Codex, Antigravity).
It lives inside the connector so it works identically for every client and stays fully offline.

Four principles, carried over from the model graph and the Project Village:

1. **Memory is a hint, never a fact.** Every entry records the model version it was written
   against, every recall says so in a `note` field, and an agent verifies live before it writes.
2. **Logic decides, people approve, AI summarises.** Storing, counting, ranking and expiring
   entries is deterministic add-in code. Humans approve what enters a shared tier. An AI pass only
   compresses, dedupes and proposes, on a schedule, producing proposals rather than changes.
3. **No background credits.** Nothing in the memory system calls a model on its own. Credits are
   spent only when an agent in a session recalls or remembers, and those calls are small.
4. **Always in sync, no rebuild.** The shared folder is the source of truth; recall reads it
   directly. There is no index to build and no lock is ever held on a shared file.

---

## What the connector already shares

| Layer | Location | Shared how | Gap |
|---|---|---|---|
| Company master skills | `%ProgramData%\RKTools\MCP\Skills\` | Deployed by an administrator; project overrides become proposals via `revit_propose_master_skill_update` | Settings only, not knowledge |
| Standards lookup | Folders in `StandardsSources.json`, indexed to `%AppData%\RKTools\RevitMCP\StandardsIndex\` | A source may point at a network share | Needs `standards_index_sources`; documents, not notes |
| Configuration | company → project (`.rktools/mcp.project.config.json`) → user | Files on the server and next to the model | Fixed keys, not free knowledge |
| Model graph | `graph.sharedFolder` | One graph per model on a share, copy-on-read | Routing ids only, by design |
| Server instructions | `RevitMCP.Bridge/ServerInstructions.cs` | Sent to every client at the handshake | Static text |

Memory is a fifth layer built on the same patterns: company → project → personal, files on the
server, proposals for the company tier, and a line in the server instructions so every client uses it.

---

## Tiers

| Tier | Location | Who writes | Approval | Visible to |
|---|---|---|---|---|
| Personal | `%AppData%\RKTools\RevitMCP\Memory\` | The user's own agent, the workflow observer | None | That user |
| Project | `<project root>\.rktools\memory\` (next to the central model, like the project config) | Agents, the review page, promoted observer entries | Approval queue, like any write | Everyone on the project |
| Company | `\\server\...\MCP\Memory\` (`memory.companyFolder` in the company config) | Administrator only | Proposal file, like skills | Everyone |

`FileAccessPolicy` keeps `AllowProgramDataWrites` off, so an agent cannot write the company tier
directly; a `remember` with `scope=company` writes a `.memory.proposal.json` into the user's
proposals folder, exactly as skills do.

---

## Entry format

One file per entry, named by GUID, Markdown with YAML front matter. One file per entry is what makes
the store conflict-free: two colleagues writing in the same second never touch the same file, and a
synced folder (OneDrive, Dropbox) or an SMB share never has anything to merge.

```markdown
---
id: 8b2f5c1e-3c1a-4f6e-9d2b-0a7e1c4d5f60
key: project/1626/tags/detector-tag-type        # tier/project/topic/setting — merge key
scope: project
project: "1626"
stage: PP                                       # EP | PP | TP | TJ | null
model: 1626_PP_EN
modelVersion: "LatestCentralVersion=412"
source: agent                                   # observed | agent | human
author: raul.kalev
client: Claude Code
created: 2026-10-23T09:12:00Z
updated: 2026-10-23T09:12:00Z
state: candidate                                # candidate | confirmed | dormant | retired
supersedes: null
tags: [tags, fire-alarm]
counters: { shown: 0, used: 0, worked: 0, contradicted: 0 }
---
value: Detector tags use "ATS_Tag_Andur_Nr" (type id 712345), not the default tag.
reason: The client's drawing standard shows the detector number only; the default tag adds the
        zone, which the sheet legend already carries. Confirmed by the user on 2026-10-23.
```

Rules enforced at write time:

- **Decisions, not facts.** Anything readable from the model or the graph — ids as values, names,
  counts, current parameter values — is refused with a hint to read it live. Memory holds *why*
  a choice was made: preferences, exceptions, client requirements, corrections.
- **Every entry has a key.** A new entry with an existing key merges into it (counter +1, `updated`
  bumped). A new entry with the same key and a different value supersedes the old one, which is
  kept as history (`supersedes`).
- **Flip-flops are flagged.** When a key changes value back and forth across users, neither value
  is trusted; the entry gets `state: candidate` and the review page shows it as a disagreement.
- **Length cap** on `value` and `reason` (e.g. 400 characters each) so an entry is a fact about the
  project, not a session diary.
- **Per-project cap** (e.g. 500 entries). When reached, the lowest-scoring dormant entries retire.
- **Privacy allow-list.** No parameter values, file contents, credentials or personal data of
  third parties. The observer (below) stores only allow-listed argument keys.

---

## Always in sync, no rebuild

*Decision:* there is no index. Entries are small and few (hundreds to low thousands per project),
so recall enumerates the folder directly.

- **Atomic writes.** `remember` writes to a temp file in the same folder and renames it into place.
  Readers never see a half-written entry; no write lock is ever taken.
- **Hot in-memory copy.** The add-in holds a `MemoryCache` per open project, filled on first use,
  kept fresh by a `FileSystemWatcher` on the folder and, as a safety net, by comparing the folder's
  last-write time on every recall. A note written on one machine is visible to the next recall on
  every other machine with no step in between.
- **Staleness is computed, not rebuilt.** Recall compares each entry's `modelVersion` with the open
  document's current version (the same reads `revit_graph_status` uses) and marks older entries
  `"written against an earlier model"` in the response.
- **Offline fallback.** When the share is unreachable the add-in reads and writes a local mirror
  under `%LOCALAPPDATA%\RKTools\RevitMCP\MemoryMirror\<project>\` and copies pending entries to the
  share when it returns. Because entries are independent files, replay is a plain copy.

---

## Recall

Two paths, both read-only:

1. **Hints inside `revit_graph_route`.** Every session calls it first, so the top entries matching
   the intent (max `memory.hintLimit`, default 5) ride along with the call plan in a `memory`
   block. Opt out per call with `memoryHint=false`, per user with `memory.hints=false`.
2. **`revit_memory_recall`** `query= scope?= project?= tags?= limit?=` for a deliberate search.

Ranking: text match on key, value, reason and tags × tier weight (project > company > personal) ×
recency × usefulness (`used`/`shown`) × same-user bonus; dormant and retired entries never appear
in hints, retired ones only with `includeRetired=true`.

Every recall response carries:

```json
{ "note": "Memory entries are hints written by people and agents at a point in time; verify live before writing.",
  "entries": [ { "id": "…", "key": "…", "value": "…", "reason": "…", "state": "confirmed", "stale": false, "source": "agent", "updated": "…" } ] }
```

Showing an entry in a recall increments its `shown` counter; what happens next decides `used` and
`worked` (see *Learning*).

---

## Learning from users' workflows

`ActivityLogger.WriteAsync` already sees every completed tool call: tool, arguments, success,
status code, duration, user, client and document context. The Project Village proves this
observation path is cheap and safe (one hook call, never throws, no-op when disabled). A
`WorkflowObserver` hangs off the same hook and derives memory **deterministically**, with no AI
call, so it costs no credits.

What it learns:

| Pattern | Derived entry | Example |
|---|---|---|
| **Recipes** — tool chains that recur per project and intent | `workflow/<project>/<intent>` | "Detector tagging here: graph route → graph query (category) → `revit_get_elements_info` → `revit_place_tags`" |
| **Project defaults** — argument values that recur per project | `project/<id>/<tool>/<argument>` | "`revit_place_tags` is called with `tagTypeId=712345` in 1626" |
| **Corrections** — a rejected write followed by an accepted one with different arguments | `correction/<project>/<tool>/<argument>` | "Offset 2400 was rejected; 2700 was accepted" |
| **Hint feedback** — calls after a recall match an entry's value, and succeed or are approved | counters on the entry | `used +1`, `worked +1`, or `contradicted +1` |

Rules:

- Only **allow-listed argument keys** are read (category, family and type names, tag types,
  offsets, view names, parameter *names*), each capped in length — the same approach as
  `VillageCategoryArgument`. Free text values, file paths and credentials never enter memory.
- A pattern becomes a **candidate** only after a minimum support (e.g. seen 3 times across 2
  sessions). Candidates from the observer start in **personal** memory and are promoted to the
  project tier only when seen from two or more users, or confirmed by a human or an agent note.
- Recipes are bound to the connector version and the tool names they use, so a renamed or
  consolidated tool retires its recipes instead of leaving stale instructions.
- Agents write their own notes at task end: the server instructions ask for a `remember` of a user
  correction, a decision and its reason, or an exception to a rule — never a summary of the session.
  Every entry carries `source`, so provenance is never ambiguous.

---

## Keeping only what is useful: the lifecycle

*Usefulness is measured by effect, not judged by content.* An entry is useful when it changed a
later action for the better, which the observer can see: the hint was shown, the following calls
matched it (`used`), and they succeeded or were approved (`worked`).

| State | Enters when | Shown in hints | Leaves when |
|---|---|---|---|
| **candidate** | Observed pattern over the support threshold; agent note not yet reused; flagged flip-flop | Author only | Used and worked N times, seen from ≥2 users, or confirmed on the review page → **confirmed** |
| **confirmed** | As above | Yes | Unused for `memory.dormantAfterDays` (default 90), stage passed, or model version moved on without re-confirmation → **dormant**; contradicted by a newer confirmed entry → **retired** |
| **dormant** | As above | No (searchable) | Used again → **confirmed**; dormant for `memory.retireAfterDays` (default 365) → **retired** |
| **retired** | As above | No | Never deleted; history for review and proposals |
| **promoted** | Stable across several projects | — | Leaves memory: becomes a skill setting, a standards document or a company default via the proposal flow |

All transitions are computed by `MemoryPolicy` from the counters and dates; nothing here needs a
model.

---

## Who does what

*Decision:* three layers, each with the tool that fits.

| Work | Done by | Cost | Why |
|---|---|---|---|
| Storing, merging by key, allow-list, counters, lifecycle, caps, staleness, sync | **Add-in logic**, always on | None | Counting what people did afterwards is a better judge than any model's opinion, and it runs on every call |
| Writing a good note (correction, decision + reason, exception) | **The agent already in the session**, at task end | One small tool call | It has the context; project scope passes the approval queue, so the person at the Revit screen filters it for free |
| Compressing raw observations into readable entries, deduping prose entries that say the same thing, explaining flip-flops, drafting promotions into skills or standards | **Weekly consolidation pass** — `revit_memory_consolidate` run by a lead from their own Claude Code session | A few dozen entries once a week | Reads only entries changed since its last run; writes **candidates and proposals, never confirmed entries**; the lead approves on the review page |

A standing AI reviewer was rejected: it would spend credits continuously on decisions the counters
make better, judge entries before there is usage evidence, and be a second writer competing with
the agents and the people.

The consolidation pass needs no Revit open (memory is plain files). Later, the same skill can run
headless nightly on one workstation with the Claude Code CLI in non-interactive mode, writing
proposals for the morning; a cloud scheduled task only if the memory folder is mirrored somewhere
reachable.

---

## Human review

- **Approval queue.** A `remember` with `scope=project` is an approval-gated write like any other.
- **Review page.** `revit_memory_page` publishes the project's memory as a model page
  ([model-pages.md](model-pages.md)): one table, each entry with its state, source, counters and
  staleness, with confirm / retire / promote actions that write back through the same
  approval path. Flip-flops and consolidation proposals sit at the top.
- **Monthly digest.** The review page has a *digest* view: unused, contradicted and flagged entries,
  and candidates waiting for confirmation.
- **Company tier.** Proposal files only, applied by an administrator — identical to skills.

---

## Credit usage

| Item | Credits |
|---|---|
| Observer, lifecycle, sync, staleness, review page | None (add-in code) |
| Memory hints in `revit_graph_route` (≤5 entries) | ~200–400 tokens on a call every session makes anyway |
| One `remember` at task end | One small tool call |
| One deliberate `revit_memory_recall` | Only when the agent chooses to |
| Weekly consolidation pass | A few dozen entries, once a week, run by a lead |
| What a hit replaces | Live reads of a category (thousands of tokens) or a rejected-and-retried write round |

The direction is a saving; the size is to be proven with the benchmark (`RevitMCP.Benchmark`,
[benchmarks/README.md](benchmarks/README.md)) on a real project session with memory on and off,
before the hints are enabled by default. If the project-wide hit rate (`used`/`shown`) falls, the
hint cap or the promotion threshold is tightened.

---

## Components

Following the development notes in `CLAUDE.md`: pure logic in a domain folder linked into
`RevitMCP.Tests.csproj`, one `IRevitMcpTool` per operation registered in `App.OnStartup`, and a
matching `[McpServerTool]` in `RevitMCP.Bridge/RevitMcpTools.cs`.

| Component | Location | Responsibility |
|---|---|---|
| `MemoryEntry`, `MemoryEntryParser` | `RevitMCP.Addin/Memory/` | Front-matter format, validation, length caps |
| `MemoryStore` | `RevitMCP.Addin/Memory/` | Tier paths, atomic write, enumerate, mirror and replay |
| `MemoryCache` | `RevitMCP.Addin/Memory/` | In-memory copy per project, `FileSystemWatcher`, last-write check |
| `MemoryRecall` | `RevitMCP.Addin/Memory/` | Matching and ranking, staleness against the document version |
| `MemoryPolicy` | `RevitMCP.Addin/Memory/` | Key merge, supersede, flip-flop flag, lifecycle transitions, caps, allow-list |
| `WorkflowObserver` | `RevitMCP.Addin/Memory/` | Recipes, defaults, corrections, hint feedback; fed by one hook in `ActivityLogger.WriteAsync` next to `VillageHooks.ToolCompleted` |
| `MemoryService` | `RevitMCP.Addin/Memory/Hosting/` | Wiring, config, the watcher's lifetime, the approval binding |
| Tools | `RevitMCP.Addin/Tools/Memory/` | `revit_memory_recall`, `revit_memory_remember`, `revit_memory_list`, `revit_memory_forget` (approval-gated; sets `retired`, never deletes), `revit_memory_page`, `revit_memory_consolidate` |
| Hooks into existing tools | `GraphRouteTool`, `ServerInstructions` | `memory` block in the route response; two lines: recall before a write, remember at task end |

Configuration keys (`memory.*`, user then company config): `enabled`, `companyFolder`,
`projectFolderName` (default `.rktools\memory`), `hints`, `hintLimit`, `observer.enabled`,
`observer.minSupport`, `dormantAfterDays`, `retireAfterDays`, `maxEntriesPerProject`,
`valueMaxLength`.

---

## Phases

1. **Store and recall.** `MemoryEntry`, `MemoryStore`, `MemoryCache`, `MemoryRecall`,
   `MemoryPolicy` with the write-time rules; `revit_memory_remember`, `revit_memory_recall`,
   `revit_memory_list`; the server-instruction lines. Personal and project tiers. Benchmark report
   before and after.
2. **Hints and learning.** `memory` block in `revit_graph_route`; `WorkflowObserver` with recipes,
   defaults, corrections and hint feedback; lifecycle transitions; company tier via proposals.
3. **Review.** `revit_memory_page` with confirm / retire / promote and the digest view;
   `revit_memory_forget`.
4. **Consolidation.** `revit_memory_consolidate` as a skill a lead runs weekly; later the headless
   nightly run.

---

## Open decisions

- *Open:* the exact support threshold for observer candidates and the N for confirmation. Start
  with 3 sightings across 2 sessions and 3 successful uses; tune from the hit rate.
- *Open:* whether the project tier lives next to the model (`.rktools\memory`) or under one
  server root keyed by project id. Next to the model follows the project config; one root is easier
  to back up and to point the consolidation pass at.
- *Open:* how `used` is detected for prose entries without an argument value to match. First
  version: an agent `remember` that cites the entry id, plus the consolidation pass.
