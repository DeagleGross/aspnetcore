# `pr-docs-check` workflow

A [gh-aw](https://github.com/githubnext/gh-aw) agentic workflow that
analyzes merged pull requests in `dotnet/aspnetcore` and — when the
change introduces new user-facing functionality — drafts a corresponding
documentation pull request on
[`dotnet/AspNetCore.Docs`](https://github.com/dotnet/AspNetCore.Docs).
A status comment is posted back on the source `dotnet/aspnetcore` PR
either way.

Adapted from the equivalent workflow in
[`microsoft/aspire`](https://github.com/microsoft/aspire/blob/main/.github/workflows/pr-docs-check.md).

## Scope (what triggers a docs PR)

The workflow drafts a docs PR for **new functionality / new APIs only**.
Backports, bug fixes, refactors, infra changes, and test-only changes
are not in scope. The decision is made deterministically by
[`compute_signals.py`](./compute_signals.py) before the agent ever
runs — the agent's role is to *write* the docs, not to decide whether
docs are needed.

See the "Signal catalog" section below for the full list of signals
considered.

## Files in this folder

| File | Purpose |
| --- | --- |
| `compute_signals.py` | Deterministic "is this PR user-facing?" signal computation. Reads the source PR's metadata and changed files (as JSON dumps from the `gh api` calls in the workflow) and writes `.pr-docs-check/signals.json`. |
| `test_compute_signals.py` | Unit tests for `compute_signals.py`. Run with `python3 -m unittest discover -s .github/workflows/pr-docs-check -v`. |
| `README.md` | This file. |

The workflow source itself lives at
[`../pr-docs-check.md`](../pr-docs-check.md). The generated lockfile
is at `../pr-docs-check.lock.yml` — do **not** hand-edit it; regenerate
with `gh aw compile`.

## Prerequisites

### 1. The `aspnetcore-docs-bot` GitHub App

The workflow uses a GitHub App for two cross-repo capabilities:

1. **Reading** the source PR's metadata, diff, comments, and reviews from
   `dotnet/aspnetcore` from inside an Actions run on `dotnet/aspnetcore`
   (the default `GITHUB_TOKEN` works for this) **and** reading branch
   listings from `dotnet/AspNetCore.Docs` (the default token cannot do
   this — cross-repo reads with the workflow token are blocked even for
   public repos).
2. **Writing** a draft PR to `dotnet/AspNetCore.Docs` and requesting a
   reviewer on it.

This means the App must be **installed on both repos** with the
following permissions:

| Repository | Permission | Access | Why |
| --- | --- | --- | --- |
| `dotnet/aspnetcore` | Contents | Read | Read the PR's diff and metadata. |
| `dotnet/aspnetcore` | Pull requests | Read | Read PR base ref, milestone, labels, reviews, and review comments. |
| `dotnet/aspnetcore` | Issues | Read & write | Post the status comment on the source PR (PR comments go through the Issues API). |
| `dotnet/AspNetCore.Docs` | Contents | Read & write | Branch the workspace, edit docs files, push the docs branch. |
| `dotnet/AspNetCore.Docs` | Pull requests | Read & write | Create the draft PR and request the SME as reviewer. |
| `dotnet/AspNetCore.Docs` | Metadata | Read (auto) | List branches when resolving the target branch. |

The App is also exposed to the agent as a tool credential via
`tools.github` so the agent can read PR/review threads from
`dotnet/aspnetcore` and browse the existing docs in
`dotnet/AspNetCore.Docs`. The agent is instructed to perform writes
**only** through the `safe-outputs.create-pull-request` and
`notify_source_pr` paths (Steps 6, 10, and 11), never via direct GitHub
tool calls. This matches the design of the equivalent aspire workflow.

After installing the App, the following **repository secrets** must
exist on `dotnet/aspnetcore`:

| Secret | Value |
| --- | --- |
| `ASPNETCORE_DOCS_BOT_APP_ID` | The numeric App ID. |
| `ASPNETCORE_DOCS_BOT_PRIVATE_KEY` | The full PEM private key contents (including `-----BEGIN ...-----` / `-----END ...-----` lines). |

> [!NOTE]
> Until the App is created and these secrets exist, the workflow will
> fail at the first `actions/create-github-app-token` step. Keep the
> workflow disabled (delete `pr-docs-check.lock.yml`, or set
> `if: false` on the trigger) until the App is provisioned.

### 2. The `doc-writer/SKILL.md` skill on `dotnet/AspNetCore.Docs`

Step 7 of the agent prompt reads
`.github/skills/doc-writer/SKILL.md` from the AspNetCore.Docs
workspace. This file is **not** part of this repository — it lives on
`dotnet/AspNetCore.Docs` and codifies the docs site's authoring
conventions (file structure, YAML frontmatter, `:::moniker` ranges,
`<xref:>` cross-references, `toc.yml` rules, etc.).

If the skill file does not exist, the agent falls back to a baseline
set of rules embedded in Step 7 of the workflow prompt. Quality will
improve significantly once a real skill file is in place.

### 3. The `docs-from-code` label on `dotnet/AspNetCore.Docs`

Drafted docs PRs are tagged with `docs-from-code`. Create this label
on `dotnet/AspNetCore.Docs` so PRs are easy to filter.

## How it runs

| Trigger | When |
| --- | --- |
| `pull_request.closed` (with `merged == true`) on `main` or `release/*` | Every merged PR. The `if:` guard also requires `github.repository_owner == 'dotnet'` so the workflow only auto-activates for upstream PRs. |
| `workflow_dispatch` with `pr_number` | Manual re-run for any merged PR (e.g. to re-draft after the docs team closes the first draft). |

Inside one run:

1. **`pre-agent-steps`** (deterministic, no LLM):
   1. Mint a GitHub App installation token.
   2. Resolve the target `dotnet/AspNetCore.Docs` branch from the source
      PR's base ref and milestone. Write the result to
      `.pr-docs-check/target.json`. Falls back to `main` if the
      candidate `release/*` branch is confirmed absent on the docs repo.
      **Fails the workflow** if the candidate is a `release/*` branch
      AND both branch-enumeration paths (local git for-each-ref + the
      `gh api` fallback) returned no data — silent fallback in that
      case could route a release PR to docs `main` during a transient
      auth/API outage.
   3. Sparse-checkout the signal-computation script (`compute_signals.py`)
      from `dotnet/aspnetcore` into `_repos/aspnetcore/`.
   4. Run `compute_signals.py` against the source PR's `gh api`
      payloads. Write `.pr-docs-check/signals.json`.
2. **Agent**: reads both JSON files, gathers PR context from
   `dotnet/aspnetcore`, identifies the SME, decides "docs required" vs
   "no docs needed", and either drafts a PR on `dotnet/AspNetCore.Docs`
   or skips.
3. **`safe-outputs`**:
   - `create-pull-request` materializes the agent's `create_pull_request`
     emission as a real draft PR on `dotnet/AspNetCore.Docs`.
   - The custom `notify-source-pr` job posts a comment on the source
     `dotnet/aspnetcore` PR (drafted / skipped) and requests the SME as
     a reviewer on the drafted docs PR.

## Signal catalog (what gates "docs required")

Computed by `compute_signals.py`. Full details in the script's
docstrings; quick reference:

**Hard exclusions** — when fired, force `docs_optional` regardless of
positive signals:

| Signal | Detection |
| --- | --- |
| `backport_label` | A label whose name contains the word `backport` (word-boundary match). |
| `backport_title_marker` | PR title starts with `[release/X.Y]` or `[backport]`. |

**Group A — path patterns** (positive):

| Signal | Detection |
| --- | --- |
| `new_package_added` | A new `*.csproj` whose path contains the literal `/src/` segment (so it lives under `src/<area>/<package>/src/`, the canonical shipping-package layout). Heuristically excludes ProjectTemplates content and anything under test, perf, samples, or playground paths via the segment regex; does **not** otherwise inspect the csproj contents, so packages whose folder name itself contains `Test` (e.g. `Microsoft.AspNetCore.Analyzer.Testing`, `Specification.Tests`) may still match. The agent's Step 5 logic handles the residual false-positive case via the `internal_refactor` allowlist branch. |
| `new_project_template_added` | A new file under `src/ProjectTemplates/*/content/`. |
| `project_template_content_changed` | An existing file under `src/ProjectTemplates/*/content/` was modified. |
| `defaults_or_constants_file_changed` | A `Defaults.cs` / `Constants.cs` file in non-test source changed. |

**Group B — diff content** (positive):

| Signal | Detection |
| --- | --- |
| `public_api_unshipped_added` | A non-trivial `+` line in any `src/**/PublicAPI.Unshipped.txt`. Dominant signal — aspnetcore enforces public-API tracking, so every new public API surfaces here. |
| `breaking_api_removal` | A `-` line in any `src/**/PublicAPI.Shipped.txt`. Shipped APIs are append-only between releases, so a removal is a strong breaking-change indicator. |
| `obsolete_attribute_added` | A `+` line adds `[Obsolete(` in non-test src/. |
| `experimental_attribute_added` | A `+` line adds `[Experimental(` in non-test src/. |
| `new_public_type_declaration` | A `+` line adds `public class / interface / struct / record / enum / delegate ...` in non-test src/. |

**Group C — PR body** (positive):

| Signal | Detection |
| --- | --- |
| `pr_body_has_user_facing_section` | A heading like `## User-facing`, `## Usage`, `## How to use`, `## Public API`, or `## Breaking change`. |
| `pr_body_has_breaking_change_marker` | Body contains the literal phrase `breaking change`. |
| `pr_body_has_deprecation_marker` | `deprecat*` / `obsolet*` wording or "<surface> has been removed / sunset / retired". |

**Group D — labels** (positive):

| Signal | Detection |
| --- | --- |
| `pr_label_breaking_change` | A label containing `breaking`. |
| `pr_label_new_api` | The `api-needs-review` or `new-api` label. |

**Advisory** (not gating):

| Signal | Detection |
| --- | --- |
| `only_test_or_build_changes` | Every changed file is under tests, perf, samples, playground, `eng/`, `.github/`, top-level docs, or top-level build config. Never forces `docs_required` — used by the agent for the `test_only` / `build_or_ci_only` allowlist branch in Step 5. |

**Conservative-recall fallback** (gating):

| Signal | Detection |
| --- | --- |
| `diff_scan_skipped_due_to_missing_patch` | A file matched a Group B path regex but the GitHub Pulls/Files API omitted its `patch` (typically when a per-file diff exceeds GitHub's 3000-line cap). The file is skipped for Group B scanning so this signal fires instead, gating the PR conservatively. |

### Recommendation rule

```
if excluded_by:                                  recommendation = "docs_optional"
elif any (Group A | B | C | D | fallback) fired: recommendation = "docs_required"
else:                                            recommendation = "docs_optional"
```

`only_test_or_build_changes` is never counted in the "any fired" check.

## Local development

```powershell
# From the repo root:
python -m unittest discover -s .github/workflows/pr-docs-check -v

# To re-run the signal script against a real PR locally (requires gh CLI auth):
gh api /repos/dotnet/aspnetcore/pulls/12345 > /tmp/pr.json
gh api --paginate /repos/dotnet/aspnetcore/pulls/12345/files | jq -s 'add // []' > /tmp/files.json
python .github/workflows/pr-docs-check/compute_signals.py /tmp/pr.json /tmp/files.json /tmp/signals.json
cat /tmp/signals.json
```

## Regenerating the lockfile

The `.lock.yml` next to `pr-docs-check.md` is generated by `gh aw`. After
editing `pr-docs-check.md` (or any file under `pr-docs-check/`):

```bash
gh aw compile pr-docs-check
```

The compiled YAML reflects the frontmatter (triggers, permissions,
checkouts, `safe-outputs`, `pre-agent-steps`) and the agent prompt
verbatim. Commit both files together — CI will fail if the lockfile is
stale.

## Operating notes

- **Manual re-run after a closed draft.** If the docs team closes a
  drafted PR without merging (e.g. because they want to author the docs
  differently), re-run by dispatching the workflow with the same
  `pr_number`. The agent will draft a fresh PR; no state from the
  previous draft is preserved.
- **Backports.** The hard-exclusion rules drop backport PRs cleanly
  before the agent runs, so a docs PR is never drafted for a backport.
  The source-PR comment will explain why.
- **Fork PRs.** Auto-activation is restricted to PRs whose head repo is
  `dotnet/aspnetcore`. Fork PRs are intentionally excluded from
  auto-activation; use `workflow_dispatch` to run the check manually
  once a maintainer has reviewed and merged a fork PR.
- **Token scope.** The workflow's pre-agent steps mint a separate
  installation token because the default `GITHUB_TOKEN` cannot read
  branches of `dotnet/AspNetCore.Docs` even though the repo is public.
- **Protected files.** The `safe-outputs.create-pull-request`
  configuration sets `protected-files: blocked` so the agent cannot
  modify `AGENTS.md`, dependency manifests, or other security-sensitive
  files in the docs repo.
