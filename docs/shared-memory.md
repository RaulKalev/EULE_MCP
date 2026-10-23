# Shared company memory

> **Status: all four phases are implemented** — store and recall
> ([Phase 1 as built](#phase-1-as-built)); hints, the workflow observer, hint feedback, the
> lifecycle and the company tier via proposals ([Phase 2 as built](#phase-2-as-built)); the review
> page and forget ([Phase 3 as built](#phase-3-as-built)); the consolidation pass and its unattended
> nightly run ([Phase 4 as built](#phase-4-as-built)). The design sections below are kept as written;
> the "as built" sections record where the implementation differs. Sections marked *decision* record
> choices already made, sections marked *open* still need one.

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

1. **Store and recall** *(done)*. `MemoryEntry`, `MemoryStore`, `MemoryCache`, `MemoryRecall`,
   `MemoryPolicy` with the write-time rules; `revit_memory_remember`, `revit_memory_recall`,
   `revit_memory_list`; the server-instruction lines. Personal and project tiers. Benchmark report
   before and after.
2. **Hints and learning** *(done)*. `memory` block in `revit_graph_route`; `WorkflowObserver` with recipes,
   defaults, corrections and hint feedback; lifecycle transitions; company tier via proposals.
3. **Review** *(done)*. `revit_memory_page` with confirm / retire / promote and the digest view;
   `revit_memory_forget`.
4. **Consolidation** *(done)*. `revit_memory_consolidate` as a skill a lead runs weekly; later the headless
   nightly run.

---

## Phase 1 as built

### Tools

| Tool | Permission | What it does |
|---|---|---|
| `revit_memory_remember` | Approval for `scope=project` (the default); none for `scope=personal` | Stores one note: `key` (topic path), `value`, `reason`, `tags[]`, `stage`, `source` (`agent` \| `human`), `anyProject` (personal only), `projectRoot` |
| `revit_memory_recall` | Read-only | `query`, `scope` (`all` \| `project` \| `personal`), `tags[]`, `limit` (5, max 20), `includeRetired`; ranked entries with `stale`, and the `note` field |
| `revit_memory_list` | Read-only | Newest first, filtered by `scope`, `state` (default: all but retired; `all` includes history), `tags[]`, paged with `offset`/`limit`; shows counters, flags and where each tier is stored |

`recall` and `remember` are in the `core` profile, `list` in the `memory` group. The server
instructions carry the two lines: recall before a write, remember at task end.

Per-call approval comes from a small dispatcher extension: a `RequiresApproval` tool may implement
`IConditionalApprovalTool` and say a call does not need approval (here: a personal note). It never
relaxes destructive tools, and Direct Edit still skips approval as for every other write.

### Where entries live

- **Project tier:** `<project root>\.rktools\memory\` (`memory.projectFolderName`). The project root
  is `projectRoot` when given, else the nearest folder above the **central** model file that has a
  `.rktools` folder (the project-config rule), else the central model's own folder. Cloud and server
  models have no folder: they get personal memory only unless `projectRoot` is passed.
- **Personal tier:** `%AppData%\RKTools\RevitMCP\Memory\`. A personal note is bound to the open
  project unless `anyProject=true`.
- **Mirror:** `%LOCALAPPDATA%\RKTools\RevitMCP\MemoryMirror\<project>\` holds a copy of every
  project entry read. While the project root is unreachable, reads use the mirror and writes are
  parked in its `.pending\` folder; the next read with the share back copies them over (an entry
  the share already holds with a newer `updated` is left alone).

### Refinements to the format

- **Counters are not rewritten by recall.** Incrementing `shown` in the entry file would make every
  recall a write to a shared file, and two people recalling at once would lose counts. Each user
  instead has one tally file, `.counters\<user>.json` in the tier folder, written only by that
  user; recall reports the entry's own counters plus every user's tally. The `counters` block in
  the entry keeps what was written with it.
- **Added fields:** `authors` (everyone who wrote the same key and value — the "seen from two
  users" signal for phase 2), `flags` (`flip-flop`), and the counter `remembered` (times the same
  key and value were written again).
- **Keys:** the agent passes a topic path (`tags/detector-tag-type`); the store adds the tier and
  project (`project/1626/tags/detector-tag-type`, `personal/1626/…`, `personal/…` with
  `anyProject`). Keys are lowercase ASCII with diacritics dropped.
- **Every new entry starts as `candidate`.** Recall weights candidates below confirmed entries and
  flip-flops lower still; phase 2 adds the lifecycle that confirms them.

### Write-time rules as implemented

`MemoryPolicy` refuses a note without a `reason`; a `value` or `reason` over
`memory.valueMaxLength` (400); a `value` that looks like model data (fewer than two words, or four
or more numbers of four digits — a single id cited in a sentence is fine); e-mail addresses and
phone numbers; and credential-like text (`password:`, `token=`, private keys, long base64/hex runs).
The same key with the same value (ignoring case and whitespace) reaffirms the entry; a new value
writes a new entry with `supersedes` and retires the old one; a value going back to a retired one
with two or more authors on the key gets the `flip-flop` flag. At `memory.maxEntriesPerProject`
(500) the least useful dormant entry is retired; with none dormant a new key is refused
(`memory_full`) while superseding an existing key still works.

Configuration read in phase 1 (user config first, then company): `memory.enabled`,
`memory.projectFolderName`, `memory.valueMaxLength`, `memory.maxEntriesPerProject`.

### Code

| Component | Location |
|---|---|
| `MemoryEntry`, `MemoryEntryParser`, `MemoryPolicy`, `MemoryStore`, `MemoryCache`, `MemoryRecall`, `MemorySettings`, `MemoryProjectRoot` | `RevitMCP.Addin/Memory/` (no Revit API; linked into `RevitMCP.Tests`, `MemoryTests.cs`) |
| `MemoryService` | `RevitMCP.Addin/Memory/Hosting/` (no Revit API; created in `App.OnStartup`) |
| Tools | `RevitMCP.Addin/Tools/Memory/MemoryTools.cs`; bridge methods in `RevitMCP.Bridge/RevitMcpTools.cs` |
| Conditional approval | `RevitMCP.Addin/Interfaces/IConditionalApprovalTool.cs`, used in `ExternalEventHandler` |

Benchmark (schema only): `core` goes from 19 tools ≈ 5.3k tokens to 21 tools ≈ 6.0k tokens
([before](benchmarks/20261023-213314-shared-memory-phase1-before.md),
[after](benchmarks/20261023-214349-shared-memory-phase1.md)). The memory-on/off run on a real project
is still to be recorded before hints are enabled by default in phase 2.

---

## Phase 2 as built

### Hints in `revit_graph_route`

The route response carries a `memory` block — the `note` and up to `memory.hintLimit` (5) entries
matching the intent and the detected category — when `memory.hints` is on. Hints show confirmed
entries and, of the candidates, only those the asking user wrote; dormant and retired entries never
appear. `memoryHint=false` opts out per call.

*Decision:* `memory.hints` is **off by default**. The section *Credit usage* asks for the
memory-on/off benchmark on a real project before hints are enabled by default; until that report is
recorded, a user or the company config switches them on with `"memory": { "hints": true }`. Recall,
remember, the observer and the lifecycle work either way.

### Lifecycle

`MemoryLifecycle` computes the state every time entries are read, from the counters, authors and
dates; nothing is rewritten to move an entry, so the shared folder stays conflict-free and every
machine sees the same state. The stored `state` only records what a write or a person decided
(retired by supersede; confirmed on the review page in phase 3). `revit_memory_list` shows why an
entry is in its state (`lifecycle`).

| Effective state | When |
|---|---|
| retired | Stored as retired; a recipe names a tool the connector no longer has; unused for `dormantAfterDays` + `retireAfterDays` (90 + 365) |
| dormant | Unused for `dormantAfterDays` (90); or contradicted at least `confirmAfterUses` times and more often than it worked |
| candidate | A flip-flop not settled by a person; otherwise no evidence yet |
| confirmed | Stored as confirmed; worked `confirmAfterUses` (3) times; or written (or observed) by two or more people |

"Unused" is measured from the later of `updated` and the last use in the tallies, so an entry used
again is confirmed again. Two rules from the design table are not applied: "stage passed" (the
connector does not know the project's current stage) and "model version moved on" (every sync
moves it; staleness is reported per entry instead).

### Hint feedback

Every entry a recall or a route hint shows is watched for the next 15 calls or 30 minutes of the same
user and client. A call that passes a value the entry names (an observed default: its tool and
argument) counts `used`, and `worked` when it succeeds; a rejected call counts `contradicted`, and so
does an observed default called with a different value. Counts go to the user's tally file, with
`lastUsed`. For prose notes the agent can also pass `basedOn=[ids]` to `revit_memory_remember`:
each cited entry counts as used and worked — the first answer to the open decision on `used`.

### Workflow observer

`MemoryHooks.ToolCompleted` sits next to the village hook in `ActivityLogger.WriteAsync`; a
rejection in the approval window, which is never logged as a call, reaches `MemoryHooks.ToolRejected`
from `ApprovalService.Reject`. The hook copies the tool name, client, Revit user, project number
(now captured in the document context), model path, success, status and the allow-listed argument
values, and queues them; a background task processes the queue, so a tool call never waits.

| Pattern | Rule | Entry |
|---|---|---|
| Default | A write tool called with the same allow-listed value ≥ `observer.minSupport` (3) times across ≥ `observer.minSessions` (2) sessions, and in ≥ 60 % of the calls with that argument | `defaults/<tool>/<argument>`, `match` = tool, argument, value |
| Recipe | The chain of successful tools after a `revit_graph_route` of the same kind (route + detected category, else the intent's first words), at least three tools, same thresholds | `workflow/<route>/<category>`, `requires` = the tools |
| Correction | A rejected write followed within 30 minutes by an accepted call of the same tool with another value for an allow-listed argument — reported on the first sighting, since a person rejecting a value is the signal | `corrections/<tool>/<argument>`, `match` = the accepted value |

A session ends after 30 minutes without a call. Allow-listed arguments are names, types, categories,
levels, views, parameter names, tag type ids and offsets/heights; values longer than 64 characters,
paths and arrays are skipped. Memory and discovery tools are not observed.

A pattern becomes a **personal** candidate (`source: observed`, key `personal/<project>/…`, bound
to the connector version). The user's patterns are also listed in
`<project memory>\.observed\<user>.json`; when another user's list holds the same topic and value,
the pattern is promoted to the **project** tier with both as authors — so it is confirmed — under an
id derived from the key and value, so two machines promoting it write the same file. The observer's
counts stay in `%AppData%\RKTools\RevitMCP\Memory\.observer\<project>.json`.
`memory.observer.enabled=false` switches it off.

### Company tier

`memory.companyFolder` (company config, or user config to test) is read by recall and list as the
third tier, with a local mirror like the project tier; usage of company entries is counted only when
the folder is writable. `revit_memory_remember scope=company` writes no entry: it writes
`<id>.memory.proposal.json` to `%AppData%\RKTools\RevitMCP\MemoryProposals\` with the entry text
ready to save as `<id>.md` in the company folder, like skill proposals. It needs no approval because it
changes nothing shared.

### Configuration added

`memory.companyFolder`, `memory.hints` (false), `memory.hintLimit` (5), `memory.observer.enabled`
(true), `memory.observer.minSupport` (3), `memory.observer.minSessions` (2),
`memory.confirmAfterUses` (3), `memory.dormantAfterDays` (90), `memory.retireAfterDays` (365).

### Code

| Component | Location |
|---|---|
| `MemoryLifecycle`, `WorkflowObserver` | `RevitMCP.Addin/Memory/` (no Revit API; tested in `MemoryPhase2Tests.cs`) |
| Hint feedback, observer host, company proposals | `RevitMCP.Addin/Memory/Hosting/MemoryService*.cs` |
| `MemoryHooks` | `RevitMCP.Addin/Memory/Hosting/`; called from `ActivityLogger` and `ApprovalService.Reject` |
| Route hints | `RevitMCP.Addin/Tools/Graph/GraphRouteTool.cs` |

Benchmark (schema only): `core` ≈ 6.0k → 6.1k tokens
([report](benchmarks/20261023-220217-shared-memory-phase2.md)).

---

## Phase 3 as built

### The review page

`revit_memory_page` publishes the open project's memory as a model page with the fixed id
`memory-<project>` (the same link every time) and opens it. Top to bottom:

1. Six numbers: active entries, confirmed, waiting for confirmation, dormant, hit rate
   (used / shown), proposals waiting.
2. **Needs a decision**: pending consolidation proposals (kind, the entries now, the proposed text,
   why, who) with *Accept* / *Reject*, then the disagreements (flip-flops) with *Confirm* / *Retire*.
3. **Digest** — the monthly look: unused for 30 days or dormant, contradicted by later actions,
   waiting for confirmation.
4. **All entries** with key, value, reason, tier, state (and why), flags, source, authors, counters,
   staleness and *Confirm* / *Retire* / *Promote*; the table gets search, filters, sorting and
   grouping from the page helper (`data-revit-tools="rows"`, new for tables whose rows are not model
   elements).

A button calls `revit.memory(action, {ids, proposal})` in the page helper, which posts to the
listener's new `/api/memory` route. The listener hands it to the dispatcher as `revit_memory_review`
— approval in Revit, the activity log, like any shared write — and, once approved, the tool
republishes the page, which then reloads. `/api/memory` is accepted only from `memory-…` pages, and
`revit_publish_page` refuses those ids, so a page an agent wrote cannot change memory.

| Action | Effect |
|---|---|
| Confirm | Stored state `confirmed`; a flip-flop flag is cleared (a person settled it) |
| Retire | Stored state `retired`; the file stays as history |
| Promote | A company proposal file (as `scope=company`) and the `promoted` flag on the entry, which stays in use until the company copy exists |
| Accept | Applies a consolidation proposal (below) and marks it accepted |
| Reject | Marks the proposal rejected; nothing else changes |

Company entries are shown but have no buttons: an administrator changes them.

### Forget

`revit_memory_forget ids=[…] reason=…` retires entries after approval; nothing is deleted.

## Phase 4 as built

### Proposals

A proposal is a JSON file in `<project memory>\.proposals\` (one per proposal, written atomically):
`kind`, `entryIds`, the proposed `key` / `value` / `reason`, a `target` for promotions, a `note`
for the reviewer, `status` (`pending`, `accepted`, `rejected`), who and when.

| Kind | Accepting it |
|---|---|
| `merge` (≥ 2 entries) | One entry with the proposed key, value and reason, stored as confirmed (a person accepted it), authors carried over; the merged entries retire |
| `rewrite` (1 entry) | The same for one entry: a raw observation in readable words, or a flip-flop with its explanation |
| `retire` | The entries retire |
| `promote` | `target=company`: a company proposal file; `skill` or `standard`: a `<id>.promotion.proposal.json` in `%AppData%\RKTools\RevitMCP\MemoryProposals\` naming the text and the entries, for whoever maintains the skill or standard. The entries get the `promoted` flag |

Proposals are checked when written and again when accepted (entries still exist and are active,
text passes the write rules); a merge or rewrite stays within one project, a promotion may name
entries of several.

### `revit_memory_consolidate`

The tool runs in the **bridge**, on the memory files — Revit does not need to be open. It takes
`projectRoots` (or `memory.consolidation.projectRoots` from the config) and an `action`:

- `read` reports, per project, what changed since that project's last run (`full=true` or `since`
  for more): duplicate groups (active entries whose values share ≥ 60 % of their words, joined
  transitively, reported only when one of them changed), flip-flops, raw observed entries, dormant
  entries, entries with the same topic and value in two or more of the projects (promotion
  candidates), and the number of proposals already waiting. The response carries `howTo`, the
  procedure, so any client can run the pass.
- `propose` validates and stores proposals; it never changes an entry.
- `done` records the run in `<project memory>\.consolidation.json`, so the next read starts there.

### Running it

- **Weekly, by a lead:** the bridge publishes an MCP prompt, `memory-consolidate` (in Claude Code
  `/mcp__revit-mcp__memory-consolidate`), which runs read → propose → done and reports what it
  proposed. The lead then opens `revit_memory_page` in a project and accepts or rejects.
- **Nightly, unattended:** `RevitMCP.Config\Install\Run-Memory-Consolidation.bat [project roots]`
  runs Claude Code non-interactively (`claude -p`) against a private bridge configuration with only
  the core and memory tools, allows only `revit_memory_consolidate`, and appends to
  `%LOCALAPPDATA%\RKTools\RevitMCP\Logs\memory-consolidation.log`. Schedule it on one workstation
  with `schtasks` (the command is in the script's header). A cloud scheduled task works only if the
  memory folders are mirrored somewhere it can reach, as the design says.

### Code

| Component | Location |
|---|---|
| `MemoryProposals`, `MemoryConsolidation`, `MemoryConsolidationRunner`, `MemoryPageHtml` | `RevitMCP.Addin/Memory/` (no Revit API; tested in `MemoryReviewTests.cs`) |
| Review actions and review data | `RevitMCP.Addin/Memory/Hosting/MemoryService.Review.cs` |
| `revit_memory_page`, `revit_memory_forget`, `revit_memory_review` | `RevitMCP.Addin/Tools/Memory/MemoryReviewTools.cs` |
| `/api/memory`, `revit.memory`, `data-revit-tools="rows"` | `ModelPages/ModelPageServer.cs`, `ModelPages/Hosting/ModelPageRevitHandler.cs`, `ModelPages/Viewer/pages.js` |
| `revit_memory_consolidate`, the `memory-consolidate` prompt | `RevitMCP.Bridge/RevitMcpTools.Memory.cs` (memory files linked into the bridge) |
| Nightly run | `RevitMCP.Config/Install/Run-Memory-Consolidation.bat` |

Benchmark (schema only): `core` unchanged at 21 tools ≈ 6.1k tokens; `full` 271 → 274 tools
([report](benchmarks/20261023-221627-shared-memory-phase3-4.md)).

---

## Open decisions

- *Open:* the exact support threshold for observer candidates and the N for confirmation. Implemented
  as 3 sightings across 2 sessions and 3 successful uses (configurable); tune from the hit rate.
- *Open:* whether the project tier lives next to the model (`.rktools\memory`) or under one
  server root keyed by project id. Next to the model follows the project config; one root is easier
  to back up and to point the consolidation pass at. Implemented next to the model; the pass takes a
  list of project roots (`memory.consolidation.projectRoots`) instead.
- *Open:* how `used` is detected for prose entries without an argument value to match. Implemented:
  a later call that passes a value the entry names, and `remember basedOn=[ids]`; tune from the hit
  rate on the review page.
