# CLAUDE.md

Project-specific instructions for Claude Code.

## Commands

- **"list tasks"** - List the open items of the Helmsman Backlog artifact (see Backlog below)

## Backlog

- The backlog is the Claude Artifact "Helmsman Backlog":
  https://claude.ai/artifact/HqxNnEgobDXaTyXTUEv9XR. It is the only source of truth for tasks.
  It is the repository owner's private page. Only the owner and editors of the artifact can write.
  If a write is refused, say so and stop.
- `BACKLOG.md` and `BACKLOG-TECHNICAL.md` were removed on 2026-11-24. Do not recreate them.
  Their last version is in git history: `git log --diff-filter=D -1 -- BACKLOG.md` finds the
  commit that removed them; its parent has the files.
- Read and change it with the `ArtifactData` tool (load it with ToolSearch `select:ArtifactData`),
  passing the URL. Read the page itself with the `Artifact` tool (`action: "read"`), never with WebFetch.
- Open items: `action: "query"`, collection `items`,
  `query: {"where": [["open","==",true]], "limit": 1000}`.
- One item: `action: "get"`, collection `items`, `doc_id` = the item ID (such as `M3.F5`) with every
  character other than letters, digits and `_.~:@+-` replaced by `-`.
- Item fields: `id`, `title`, `status` (`pending`, `in_progress`, `blocked`, `deferred`, `done`,
  `dropped`), `statusNote`, `open`, `priority` (`P0`..`P3` or ""), `size` (`XS`..`XL` or ""),
  `group` (heading path joined by " / "), `groupOrder`, `order`, `notes`, `source`, `updated`
  (ISO date). `notes` is Markdown: paragraphs, "- " list lines, inline code, bold, italic,
  https links only. Kept prose such as non-goals is in collection `notes`.
- Change an item: `action: "update"` with the `version` last read as `if_version`. Always set
  `updated` to today's date and keep `open` in step with `status` (false exactly when `done` or `dropped`).
  If `if_version` no longer matches, read the item again and redo the change on the new version.
- Add an item: `action: "set"` with all fields, the next free ID in its series, `order` one above
  the highest existing `order`, and the `groupOrder` of its group. `set` replaces a document, so
  first `get` the new `doc_id` and confirm it does not exist.
- When a task is finished, set its status to `done` and write the outcome in `notes`.
- If the artifact tools are not available in a session, say so. Do not guess and do not write a
  backlog file.

## Git worktrees

- **Always use a git worktree when implementing a task**, whether it's a feature, bug fix,
  refactor, or backlog item. Never edit code directly in the main checkout on `main`.
- Create worktrees under `.worktrees/` (already gitignored), on a new branch from `main`:
  `git worktree add .worktrees/<branch-name> -b <branch-name> main`
- Do all edits, builds, tests, and commits inside the worktree. Merge or open a PR from that branch.
- Once the branch is merged, remove the worktree with `git worktree remove .worktrees/<branch-name>`.
- Exceptions: read-only work (questions, exploration, reviews), or when the user explicitly
  says to work directly on `main`.
