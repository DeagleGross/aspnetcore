---
on:
  pull_request:
    types: [closed]
    branches:
      - main
      - release/*
  workflow_dispatch:
    inputs:
      pr_number:
        description: "PR number to analyze"
        required: true
        type: string
  stale-check: false

if: >-
  (github.event.pull_request.merged == true || github.event_name == 'workflow_dispatch')
  && github.repository_owner == 'dotnet'

description: |
  Analyzes merged pull requests for significant user-facing changes. When a
  PR is merged against main or release/* branches, this workflow determines
  whether dotnet/AspNetCore.Docs needs a documentation PR. If documentation
  updates are required, it creates a draft PR with the changes following the
  doc-writer skill conventions. The draft PR targets the AspNetCore.Docs
  branch resolved from the source PR's base / milestone reasoning, using the
  matching release/* branch when it already exists and falling back to
  AspNetCore.Docs main otherwise. It also comments on the original PR with
  a link to the draft PR (or a "no docs needed" message).

  Gating policy: docs PRs are drafted only for *new functionality / new
  APIs*. Bug fixes, refactors, infra, test changes, and backports are
  excluded by the deterministic signal step that runs before the agent.

checkout:
  # Use AspNetCore.Docs as the current workspace because that is where
  # documentation changes are authored, and keep a mirrored checkout under
  # _repos so the safe-outputs create_pull_request tool can reliably
  # rediscover the target repo in multi-repo mode.
  - repository: dotnet/AspNetCore.Docs
    github-app:
      app-id: ${{ secrets.ASPNETCORE_DOCS_BOT_APP_ID }}
      private-key: ${{ secrets.ASPNETCORE_DOCS_BOT_PRIVATE_KEY }}
      owner: "dotnet"
      repositories: ["AspNetCore.Docs"]
    current: true
    # Fetch release/* refs in addition to the default branch so the
    # `Resolve target AspNetCore.Docs branch` pre-agent step (and the
    # agent itself, when it switches the workspace to the effective
    # branch) can enumerate AspNetCore.Docs's release/* branches locally
    # from `refs/remotes/origin/release/*`.
    fetch: ["release/*"]
  - repository: dotnet/AspNetCore.Docs
    path: _repos/AspNetCore.Docs
    github-app:
      app-id: ${{ secrets.ASPNETCORE_DOCS_BOT_APP_ID }}
      private-key: ${{ secrets.ASPNETCORE_DOCS_BOT_PRIVATE_KEY }}
      owner: "dotnet"
      repositories: ["AspNetCore.Docs"]

permissions:
  contents: read
  pull-requests: read
  issues: read

network:
  allowed:
    - defaults
    - github

tools:
  github:
    toolsets: [repos, issues, pull_requests]
    # Keep the guard policy explicit so gh-aw does not inject a separate
    # auto-lockdown github-script step with an independently resolved
    # action pin.
    min-integrity: approved
    allowed-repos:
      - dotnet/*
    github-app:
      app-id: ${{ secrets.ASPNETCORE_DOCS_BOT_APP_ID }}
      private-key: ${{ secrets.ASPNETCORE_DOCS_BOT_PRIVATE_KEY }}
      owner: "dotnet"
      repositories: ["AspNetCore.Docs", "aspnetcore"]

safe-outputs:
  github-app:
    app-id: ${{ secrets.ASPNETCORE_DOCS_BOT_APP_ID }}
    private-key: ${{ secrets.ASPNETCORE_DOCS_BOT_PRIVATE_KEY }}
    owner: "dotnet"
    repositories: ["AspNetCore.Docs", "aspnetcore"]
  steps:
    - name: Mirror target repo checkout
      if: contains(needs.agent.outputs.output_types, 'create_pull_request')
      uses: actions/checkout@v6.0.2
      with:
        repository: dotnet/AspNetCore.Docs
        # Seed the mirrored workspace at AspNetCore.Docs main. The
        # safe-outputs handler will fetch and use the agent-provided
        # `base` override when creating the PR, restricted by
        # `allowed-base-branches` below.
        ref: main
        token: ${{ steps.safe-outputs-app-token.outputs.token }}
        persist-credentials: false
        path: _repos/AspNetCore.Docs
        fetch-depth: 1
    - name: Configure mirrored target repo Git credentials
      if: contains(needs.agent.outputs.output_types, 'create_pull_request')
      working-directory: _repos/AspNetCore.Docs
      env:
        REPO_NAME: "dotnet/AspNetCore.Docs"
        SERVER_URL: ${{ github.server_url }}
        GIT_TOKEN: ${{ steps.safe-outputs-app-token.outputs.token }}
      run: |
        git config --global user.email "github-actions[bot]@users.noreply.github.com"
        git config --global user.name "github-actions[bot]"
        git config --global am.keepcr true
        SERVER_URL_STRIPPED="${SERVER_URL#https://}"
        git remote set-url origin "https://x-access-token:${GIT_TOKEN}@${SERVER_URL_STRIPPED}/${REPO_NAME}.git"
        echo "Mirrored checkout configured with standard GitHub Actions identity"
  create-pull-request:
    title-prefix: "[docs] "
    labels: [docs-from-code]
    # NOTE: do NOT set a static `reviewers:` here. The drafted PR's
    # reviewer is the SME identified from the source aspnetcore PR's
    # reviews at runtime, and that decision can't live in static
    # frontmatter. The `notify-source-pr` safe-output job below requests
    # the SME on the drafted PR after creation.
    draft: true
    # Default to AspNetCore.Docs main, but allow the agent to override
    # the PR base per run using the base-branch / milestone reasoning
    # in the prompt body. Restrict overrides to main and release/*.
    base-branch: main
    allowed-base-branches:
      - main
      - release/*
    target-repo: "dotnet/AspNetCore.Docs"
    # Copilot workflows automatically protect AGENTS.md alongside
    # dependency manifests and repository security config unless this
    # policy is intentionally relaxed.
    protected-files: blocked
    fallback-as-issue: true
  jobs:
    notify-source-pr:
      name: "Notify source PR"
      description: |
        Post the documentation analysis result as a comment on the source
        dotnet/aspnetcore pull request and (when a draft documentation PR
        was opened on dotnet/AspNetCore.Docs) request a review from the
        SME identified from the source PR.

        Emit exactly one `notify_source_pr` item per run, after you've
        finished any `create_pull_request` or no-docs-needed reasoning.
        Use `result: "drafted"` when you just emitted a
        `create_pull_request`; use `result: "skipped"` when no docs PR
        is needed. DO NOT try to embed the drafted PR's URL or number in
        the `summary` text — the workflow knows them from the
        safe-outputs handler and will substitute the real values.
      runs-on: ubuntu-latest
      needs: [safe_outputs]
      permissions:
        contents: read
      inputs:
        source_pr_number:
          description: "PR number on dotnet/aspnetcore that triggered this run."
          required: true
          type: number
        result:
          description: "'drafted' if a docs PR was opened on dotnet/AspNetCore.Docs, or 'skipped' if no docs PR was needed."
          required: true
          type: string
        sme_login:
          description: "GitHub login of the SME from the source PR (preferred reviewer for the drafted docs PR). Empty string if no SME was identified."
          required: false
          type: string
        target_branch:
          description: "Effective target branch on dotnet/AspNetCore.Docs (only meaningful when result is 'drafted')."
          required: false
          type: string
        summary:
          description: "Short markdown summary of the documentation changes (when drafted) or rationale (when skipped). 1-3 sentences plus optional bullet list. DO NOT include the drafted PR URL or number — the workflow injects those automatically."
          required: true
          type: string
      steps:
        - name: Mint app token (dotnet/aspnetcore)
          id: aspnetcore-token
          uses: actions/create-github-app-token@v3.1.1
          with:
            app-id: ${{ secrets.ASPNETCORE_DOCS_BOT_APP_ID }}
            private-key: ${{ secrets.ASPNETCORE_DOCS_BOT_PRIVATE_KEY }}
            owner: dotnet
            repositories: aspnetcore
        - name: Mint app token (dotnet/AspNetCore.Docs)
          id: aspnetcore-docs-token
          if: needs.safe_outputs.outputs.created_pr_url != ''
          uses: actions/create-github-app-token@v3.1.1
          with:
            app-id: ${{ secrets.ASPNETCORE_DOCS_BOT_APP_ID }}
            private-key: ${{ secrets.ASPNETCORE_DOCS_BOT_PRIVATE_KEY }}
            owner: dotnet
            repositories: AspNetCore.Docs
        - name: Post status comment on source PR
          uses: actions/github-script@v9
          env:
            DRAFT_PR_URL: ${{ needs.safe_outputs.outputs.created_pr_url }}
            DRAFT_PR_NUMBER: ${{ needs.safe_outputs.outputs.created_pr_number }}
          with:
            github-token: ${{ steps.aspnetcore-token.outputs.token }}
            script: |
              const fs = require('fs');
              const MARKER = '<!-- pr-docs-check:notify-source-pr -->';
              const SUMMARY_MAX = 2000;

              const outputPath = process.env.GH_AW_AGENT_OUTPUT;
              if (!outputPath || !fs.existsSync(outputPath)) {
                core.warning(`Agent output file not found at ${outputPath}; skipping comment.`);
                return;
              }

              let payload;
              try {
                payload = JSON.parse(fs.readFileSync(outputPath, 'utf8'));
              } catch (e) {
                core.warning(`Failed to parse agent output: ${e.message}`);
                return;
              }
              const items = (payload && Array.isArray(payload.items)) ? payload.items : [];
              const item = items.find(i => i && i.type === 'notify_source_pr');
              if (!item) {
                core.info('No notify_source_pr item in agent output; nothing to post.');
                return;
              }

              const agentNumber = parseInt(String(item.source_pr_number), 10);
              if (!Number.isInteger(agentNumber) || agentNumber <= 0 || agentNumber > 10_000_000) {
                core.warning(`Invalid source_pr_number from agent: ${item.source_pr_number}; skipping comment.`);
                return;
              }
              const sourcePrNumber = agentNumber;

              const result = (item.result || '').toString().trim().toLowerCase();
              if (result !== 'drafted' && result !== 'skipped') {
                core.warning(`Invalid notify_source_pr.result: '${item.result}'. Expected 'drafted' or 'skipped'.`);
                return;
              }
              const targetBranch = (item.target_branch || '').toString().trim();
              const draftUrl = (process.env.DRAFT_PR_URL || '').trim();
              const draftNumber = (process.env.DRAFT_PR_NUMBER || '').trim();

              let summary = (item.summary || '').toString().trim();
              if (summary.length > SUMMARY_MAX) {
                summary = summary.slice(0, SUMMARY_MAX) + '\n\n_(summary truncated)_';
              }

              let body;
              if (result === 'drafted' && draftUrl) {
                const branchSuffix = targetBranch ? ` targeting \`${targetBranch}\`` : '';
                const numberDisplay = draftNumber || '?';
                body = [
                  MARKER,
                  `📝 Documentation has been drafted in [dotnet/AspNetCore.Docs#${numberDisplay}](${draftUrl})${branchSuffix}.`,
                  '',
                  summary,
                  '',
                  '> [!NOTE]',
                  '> This draft PR needs human review before merging.'
                ].join('\n');
              } else if (result === 'drafted') {
                body = [
                  MARKER,
                  '⚠️ Documentation drafting was attempted but the draft PR could not be confirmed.',
                  '',
                  `See the workflow run for details: ${process.env.GITHUB_SERVER_URL}/${process.env.GITHUB_REPOSITORY}/actions/runs/${process.env.GITHUB_RUN_ID}`,
                  '',
                  summary
                ].join('\n');
              } else {
                body = [
                  MARKER,
                  '✅ No documentation update needed.',
                  '',
                  summary
                ].join('\n');
              }

              try {
                const existing = await github.paginate(github.rest.issues.listComments, {
                  owner: 'dotnet',
                  repo: 'aspnetcore',
                  issue_number: sourcePrNumber,
                  per_page: 100,
                });
                for (const c of existing) {
                  if (c.body && c.body.includes(MARKER)) {
                    try {
                      await github.graphql(
                        `mutation($id: ID!) { minimizeComment(input: { subjectId: $id, classifier: OUTDATED }) { minimizedComment { isMinimized } } }`,
                        { id: c.node_id }
                      );
                    } catch (e) {
                      core.warning(`Failed to minimize comment ${c.id}: ${e.message}`);
                    }
                  }
                }
              } catch (e) {
                core.warning(`Failed to enumerate prior comments: ${e.message}`);
              }

              await github.rest.issues.createComment({
                owner: 'dotnet',
                repo: 'aspnetcore',
                issue_number: sourcePrNumber,
                body,
              });
              core.info(`Posted ${result || 'unknown'} comment on dotnet/aspnetcore#${sourcePrNumber}`);
        - name: Request SME review on draft PR
          if: needs.safe_outputs.outputs.created_pr_url != ''
          uses: actions/github-script@v9
          env:
            DRAFT_PR_NUMBER: ${{ needs.safe_outputs.outputs.created_pr_number }}
          with:
            github-token: ${{ steps.aspnetcore-docs-token.outputs.token }}
            script: |
              const fs = require('fs');

              const outputPath = process.env.GH_AW_AGENT_OUTPUT;
              if (!outputPath || !fs.existsSync(outputPath)) {
                core.info('Agent output file not found; skipping reviewer request.');
                return;
              }
              let payload;
              try {
                payload = JSON.parse(fs.readFileSync(outputPath, 'utf8'));
              } catch (e) {
                core.warning(`Failed to parse agent output: ${e.message}`);
                return;
              }
              const items = (payload && Array.isArray(payload.items)) ? payload.items : [];
              const item = items.find(i => i && i.type === 'notify_source_pr');
              if (!item) {
                core.info('No notify_source_pr item; skipping reviewer request.');
                return;
              }
              const sme = (item.sme_login || '').toString().trim().replace(/^@/, '');
              if (!sme) {
                core.info('No SME login provided; leaving draft PR without an explicit reviewer.');
                return;
              }

              const draftNumber = parseInt(String(process.env.DRAFT_PR_NUMBER || ''), 10);
              if (!Number.isInteger(draftNumber) || draftNumber <= 0) {
                core.warning(`Invalid draft PR number: ${process.env.DRAFT_PR_NUMBER}`);
                return;
              }
              try {
                await github.rest.pulls.requestReviewers({
                  owner: 'dotnet',
                  repo: 'AspNetCore.Docs',
                  pull_number: draftNumber,
                  reviewers: [sme],
                });
                core.info(`Requested @${sme} as reviewer on dotnet/AspNetCore.Docs#${draftNumber}`);
              } catch (e) {
                core.warning(`Failed to request reviewer @${sme} on dotnet/AspNetCore.Docs#${draftNumber}: ${e.message}`);
              }

# The agent that follows used to resolve the target dotnet/AspNetCore.Docs
# branch itself (PR base -> milestone -> "does that release branch exist
# on AspNetCore.Docs?"). That worked only inconsistently because it
# depended on the model running the right git/gh commands and
# interpreting them correctly.
#
# These pre-agent steps do the resolution deterministically before the
# agent starts and write the results to .pr-docs-check/target.json and
# .pr-docs-check/signals.json. The agent reads those files verbatim and
# never re-derives the values.
pre-agent-steps:
  # Mint a short-lived installation token for the resolver/signal steps
  # below. The default GITHUB_TOKEN is scoped only to the workflow's own
  # repo and cannot reliably read branches of dotnet/AspNetCore.Docs,
  # even though it is public — cross-repo reads with the workflow token
  # are blocked by GitHub's scoping.
  - name: Mint app token for pre-agent steps
    id: resolve-target-app-token
    uses: actions/create-github-app-token@v3.1.1
    with:
      app-id: ${{ secrets.ASPNETCORE_DOCS_BOT_APP_ID }}
      private-key: ${{ secrets.ASPNETCORE_DOCS_BOT_PRIVATE_KEY }}
      owner: dotnet
      repositories: |
        aspnetcore
        AspNetCore.Docs
  - name: Resolve target AspNetCore.Docs branch
    env:
      GH_TOKEN: ${{ steps.resolve-target-app-token.outputs.token }}
      PR_NUMBER: "${{ github.event.pull_request.number || github.event.inputs.pr_number }}"
    run: |
      set -euo pipefail

      mkdir -p .pr-docs-check
      OUT=.pr-docs-check/target.json

      if [ -z "${PR_NUMBER}" ]; then
        echo "ERROR: PR_NUMBER is empty; cannot resolve target branch." >&2
        exit 1
      fi
      if ! [[ "${PR_NUMBER}" =~ ^[1-9][0-9]*$ ]]; then
        echo "ERROR: PR_NUMBER '${PR_NUMBER}' is not a positive integer." >&2
        exit 1
      fi

      echo "Resolving target dotnet/AspNetCore.Docs branch for dotnet/aspnetcore#${PR_NUMBER}"

      # --- 1. Fetch source PR metadata from dotnet/aspnetcore -------------
      PR_JSON="$(mktemp)"
      gh api "/repos/dotnet/aspnetcore/pulls/${PR_NUMBER}" > "${PR_JSON}"

      PR_MILESTONE_TITLE="$(jq -r '.milestone.title // empty' "${PR_JSON}")"
      PR_BASE_REF="$(jq -r '.base.ref // empty' "${PR_JSON}")"

      echo "PR milestone : '${PR_MILESTONE_TITLE}'"
      echo "PR base ref  : '${PR_BASE_REF}'"

      # --- 2. Pick a candidate target branch -----------------------------
      # Priority:
      #   1. PR base ref if it matches release/<major>.0
      #   2. PR milestone title normalized to release/<major>.0
      #   3. main
      #
      # aspnetcore's release cadence is yearly-major: release/9.0,
      # release/10.0, etc. There are no mid-major release branches.
      # Milestone normalization accepts titles like "10.0", "v10.0",
      # "10.0.0", "10.0-rc1", "next-10.0" and maps them to release/10.0.
      normalize_milestone() {
        local title="$1"
        local m
        m="$(printf '%s' "${title}" | grep -oE '[0-9]+\.[0-9]+(\.[0-9]+)?' | head -n1 || true)"
        if [ -z "${m}" ]; then
          return 1
        fi
        # Strip any trailing .Z if present so 10.0.0 also maps to release/10.0
        local major minor
        major="${m%%.*}"
        local rest="${m#*.}"
        minor="${rest%%.*}"
        printf 'release/%s.%s' "${major}" "${minor}"
      }

      CANDIDATE=""
      CANDIDATE_SOURCE=""
      CANDIDATE_SOURCE_DETAIL=""

      if [[ "${PR_BASE_REF}" =~ ^release/[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
        CANDIDATE="${PR_BASE_REF}"
        CANDIDATE_SOURCE="pr_base"
        CANDIDATE_SOURCE_DETAIL="${PR_BASE_REF}"
      fi

      if [ -z "${CANDIDATE}" ] && [ -n "${PR_MILESTONE_TITLE}" ]; then
        if c="$(normalize_milestone "${PR_MILESTONE_TITLE}")"; then
          CANDIDATE="${c}"
          CANDIDATE_SOURCE="pr_milestone"
          CANDIDATE_SOURCE_DETAIL="${PR_MILESTONE_TITLE}"
        fi
      fi

      if [ -z "${CANDIDATE}" ]; then
        CANDIDATE="main"
        CANDIDATE_SOURCE="fallback_main"
        CANDIDATE_SOURCE_DETAIL="no release/* base ref or milestone resolved"
      fi

      echo "Candidate     : ${CANDIDATE} (source: ${CANDIDATE_SOURCE})"

      # --- 3. Enumerate release/* branches on dotnet/AspNetCore.Docs -----
      # Primary: local git on the current workspace (AspNetCore.Docs is
      # checked out with release/* fetched). Fallback: gh api.
      ENUMERATION_SOURCE=""
      RELEASE_BRANCHES_FILE="$(mktemp)"
      : > "${RELEASE_BRANCHES_FILE}"

      if git -C "${GITHUB_WORKSPACE}" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
        git -C "${GITHUB_WORKSPACE}" for-each-ref \
          --format='%(refname:short)' 'refs/remotes/origin/release/*' \
          | sed 's|^origin/||' > "${RELEASE_BRANCHES_FILE}" || true
      fi

      if [ -s "${RELEASE_BRANCHES_FILE}" ]; then
        ENUMERATION_SOURCE="git"
      else
        echo "Local git enumeration returned no release/* branches; falling back to gh api"
        if gh api --paginate "/repos/dotnet/AspNetCore.Docs/branches?per_page=100" \
            | jq -r '.[].name | select(startswith("release/"))' \
            > "${RELEASE_BRANCHES_FILE}" 2>/dev/null; then
          ENUMERATION_SOURCE="gh_api"
        else
          echo "  WARN: gh api fallback for AspNetCore.Docs branches failed; treating list as empty"
          : > "${RELEASE_BRANCHES_FILE}"
          ENUMERATION_SOURCE="empty"
        fi
      fi

      sort -u -o "${RELEASE_BRANCHES_FILE}" "${RELEASE_BRANCHES_FILE}"

      AVAILABLE_BRANCHES_JSON="$(jq -R -s 'split("\n") | map(select(length > 0))' "${RELEASE_BRANCHES_FILE}")"
      echo "Release branches on AspNetCore.Docs (source=${ENUMERATION_SOURCE}):"
      if [ -s "${RELEASE_BRANCHES_FILE}" ]; then
        sed 's/^/  - /' "${RELEASE_BRANCHES_FILE}"
      else
        echo "  <none>"
      fi

      # --- 4. Compute the effective target branch ------------------------
      # Policy:
      #   1. exact_match            — candidate is a release/* and it
      #                              exists on AspNetCore.Docs.
      #   2. release_branch_missing — candidate is a release/* that does
      #                              NOT exist on the docs repo. Fall
      #                              back to main (docs for an upcoming
      #                              release land on main until the docs
      #                              team forks the release branch).
      #   3. main                   — candidate is main; use main as-is.
      EFFECTIVE=""
      RESOLUTION=""
      if [ "${CANDIDATE}" = "main" ]; then
        EFFECTIVE="main"
        RESOLUTION="main"
      elif grep -Fxq "${CANDIDATE}" "${RELEASE_BRANCHES_FILE}"; then
        EFFECTIVE="${CANDIDATE}"
        RESOLUTION="exact_match"
      elif [ "${ENUMERATION_SOURCE}" = "empty" ]; then
        # Both local git and the gh api fallback failed to list any
        # release/* branches on dotnet/AspNetCore.Docs. We cannot tell
        # whether the candidate release branch is missing or whether
        # enumeration itself broke (auth, rate limit, transient GitHub
        # outage), so it would be unsafe to silently route a release
        # PR to docs main. Fail loudly so the workflow re-runs after
        # the transient issue clears, rather than drafting against
        # the wrong base.
        echo "ERROR: candidate '${CANDIDATE}' is a release branch but" >&2
        echo "release/* enumeration on dotnet/AspNetCore.Docs returned" >&2
        echo "no results AND the gh api fallback failed. Refusing to" >&2
        echo "silently fall back to main for a release-branch PR." >&2
        exit 1
      else
        EFFECTIVE="main"
        RESOLUTION="release_branch_missing_fallback"
        echo "Candidate ${CANDIDATE} not present on dotnet/AspNetCore.Docs; falling back to main"
      fi

      rm -f "${RELEASE_BRANCHES_FILE}" "${PR_JSON}"

      echo "Effective     : ${EFFECTIVE} (resolution=${RESOLUTION})"

      # --- 5. Emit target.json -------------------------------------------
      jq -n \
        --argjson pr_number "${PR_NUMBER}" \
        --arg pr_base_ref "${PR_BASE_REF}" \
        --arg candidate "${CANDIDATE}" \
        --arg candidate_source "${CANDIDATE_SOURCE}" \
        --arg candidate_source_detail "${CANDIDATE_SOURCE_DETAIL}" \
        --arg effective "${EFFECTIVE}" \
        --arg resolution "${RESOLUTION}" \
        --argjson available "${AVAILABLE_BRANCHES_JSON}" \
        --arg enumeration_source "${ENUMERATION_SOURCE}" \
        '{
           source_pr_number: $pr_number,
           source_pr_base_ref: $pr_base_ref,
           candidate_target_branch: $candidate,
           candidate_source: $candidate_source,
           candidate_source_detail: $candidate_source_detail,
           effective_target_branch: $effective,
           target_resolution: $resolution,
           available_release_branches: $available,
           enumeration_source: $enumeration_source
         }' > "${OUT}"

      echo "--- ${OUT} ---"
      cat "${OUT}"
  # Compute deterministic "is this PR user-facing?" signals from the PR
  # diff, body, and labels before the agent starts. The catalog lives in
  # a standalone Python script (compute_signals.py) with a matching
  # unittest suite (test_compute_signals.py).
  - name: Check out signal-computation script
    # The `checkout:` block above made dotnet/AspNetCore.Docs the
    # current workspace. We need a sparse, side-by-side checkout of
    # dotnet/aspnetcore to bring the signal-computation script into
    # the runner. Sparse: only .github/workflows/pr-docs-check.
    uses: actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd # v6.0.2
    with:
      repository: dotnet/aspnetcore
      path: _repos/aspnetcore
      sparse-checkout: |
        .github/workflows/pr-docs-check
      sparse-checkout-cone-mode: false
  - name: Compute user-facing signals
    env:
      GH_TOKEN: ${{ steps.resolve-target-app-token.outputs.token }}
      PR_NUMBER: "${{ github.event.pull_request.number || github.event.inputs.pr_number }}"
    run: |
      set -euo pipefail

      mkdir -p .pr-docs-check
      OUT=.pr-docs-check/signals.json

      if [ -z "${PR_NUMBER}" ]; then
        echo "ERROR: PR_NUMBER is empty; cannot compute signals." >&2
        exit 1
      fi
      if ! [[ "${PR_NUMBER}" =~ ^[1-9][0-9]*$ ]]; then
        echo "ERROR: PR_NUMBER '${PR_NUMBER}' is not a positive integer." >&2
        exit 1
      fi

      echo "Computing user-facing signals for dotnet/aspnetcore#${PR_NUMBER}"

      PR_JSON="$(mktemp)"
      FILES_JSON="$(mktemp)"
      gh api "/repos/dotnet/aspnetcore/pulls/${PR_NUMBER}" > "${PR_JSON}"
      gh api --paginate "/repos/dotnet/aspnetcore/pulls/${PR_NUMBER}/files?per_page=100" \
        | jq -s 'add // []' > "${FILES_JSON}"

      FILE_COUNT="$(jq 'length' "${FILES_JSON}")"
      echo "Files in PR  : ${FILE_COUNT}"

      python3 _repos/aspnetcore/.github/workflows/pr-docs-check/compute_signals.py \
        "${PR_JSON}" "${FILES_JSON}" "${OUT}"

      rm -f "${PR_JSON}" "${FILES_JSON}"

      echo "--- ${OUT} ---"
      cat "${OUT}"

timeout-minutes: 20
---

# PR Documentation Check

Analyze a merged pull request in `dotnet/aspnetcore` and determine whether
documentation updates are needed on the `dotnet/AspNetCore.Docs` documentation
site. If updates are needed, create a draft PR with the actual documentation
changes.

## Context

- **Source repository**: `dotnet/aspnetcore`
- **PR Number**: `${{ github.event.pull_request.number || github.event.inputs.pr_number }}`
- **PR Title**: `${{ github.event.pull_request.title }}`

> [!NOTE]
> The agent runs with `dotnet/AspNetCore.Docs` as the current workspace and
> also has a mirrored checkout at `_repos/AspNetCore.Docs`, so use GitHub
> tools for the source `dotnet/aspnetcore` PR details and diff instead of
> expecting a local checkout of the merged PR contents to remain available.
>
> The target `dotnet/AspNetCore.Docs` branch (`release/X.0` or `main`) has
> already been resolved deterministically by a `pre-agent-steps:` shell
> step and written to `.pr-docs-check/target.json` in the workspace.
> **Do not** re-derive the target branch from milestones or the source PR
> base — read `effective_target_branch` from that file and use it verbatim.
>
> For security, this workflow only auto-activates for merged PRs whose
> head repository is `dotnet/aspnetcore`. Fork-based PRs are intentionally
> excluded from automatic activation; use `workflow_dispatch` with
> `pr_number` when a maintainer wants to run the docs check manually for
> a merged fork PR.

## Step 1: Gather PR Information

Use the GitHub tools to read the pull request details from `dotnet/aspnetcore`
for the PR number above, including:

- Title and description (the full PR body)
- Author username
- Base branch (e.g., `main` or `release/X.0`)
- Milestone (if any)
- Labels applied to the PR
- Any issues linked via `Closes #N` / `Fixes #N` / `Resolves #N` in the PR body
- The list of changed files (filenames, status, additions/deletions)
- **Issue conversation comments** on the source PR
  (`GET /repos/dotnet/aspnetcore/issues/{N}/comments`) — author intent,
  reviewer questions and answers, follow-up clarifications, and any
  "this is how a user will experience it" prose that doesn't appear in
  the PR body
- **Review comments** (inline comments tied to specific diff lines)
  (`GET /repos/dotnet/aspnetcore/pulls/{N}/comments`) — reviewer concerns
  about wording, default values, error messages, and any decisions made
  during code review that affect what users see

Start with the PR metadata, the changed-file list, and a pass over the PR
body and comment threads. Only inspect diff hunks for files that are likely
to affect user-facing behavior, configuration, or public API surface, or
when the significance is unclear from filenames alone.

**Treat the PR description, the changed files, and the PR/review comment
threads together as the canonical context for this PR.** Steps 5, 8, 9,
10, and 11 must all draw from this combined context — not from the
filenames alone, and not from the PR body alone.

If this was triggered via `workflow_dispatch`, use the `pr_number` input
to look up the PR details.

## Step 2: Identify the Subject-Matter Expert (SME)

Determine which human is the best fit to review the drafted documentation
PR. The SME is the person most familiar with the change in the source
`dotnet/aspnetcore` PR — typically the human who reviewed/approved it,
except when the PR was authored by GitHub Copilot Coding Agent (in which
case the SME is the human who **initiated the Copilot session**, not
whoever happened to approve the bot's output).

### Step 2a: If the source PR was authored by Copilot Coding Agent

Fetch the source PR's `user.login` and `user.type`. If the PR was authored
by a Copilot bot — that is, `user.type == "Bot"` AND `user.login` matches
`Copilot`, `copilot-swe-agent`, or any login containing `copilot` and
ending in `[bot]` — then the **human session originator** (the person who
assigned `@copilot` to an issue and therefore initiated the session) is
recorded in the PR's `assignees[]` field alongside the `Copilot` bot
itself. This person is the SME because they framed the original problem
and have the deepest context for the change, even though they didn't
author the code.

Apply the following logic:

1. Read `pull_request.assignees[]` from the source PR.
2. Filter out bot accounts: any login matching `Copilot`,
   `copilot-swe-agent`, anything ending in `[bot]`, or matching
   `dependabot`, `github-actions`, `dotnet-bot`, `dotnet-maestro`.
3. If exactly one human assignee remains, set `SME_LOGIN` = that login
   and **skip the rest of Step 2**.
4. If multiple human assignees remain, prefer the assignee whose latest
   review state on the source PR is `APPROVED`. If still ambiguous, pick
   the one whose login appears earliest in `assignees[]`.
5. If no human assignees remain, fall through to Step 2b.

### Step 2b: For human-authored PRs (or as a fallback from Step 2a)

Use the GitHub tools to list pull request reviews for the source PR
(`GET /repos/dotnet/aspnetcore/pulls/{N}/reviews`) and apply the following
logic:

1. **Collapse reviews by reviewer.** For each unique reviewer login, keep
   only their *most recent* review event (the latest `submitted_at`).
2. **Exclude** the source PR author and any bot account (login ending in
   `[bot]`, or matching `dependabot`, `github-actions`, `dotnet-bot`,
   `dotnet-maestro`, `copilot`, etc.).
3. **Prefer** reviewers whose latest collapsed state is `APPROVED`. Among
   those, pick the one with the most recent `submitted_at`.
4. **Fallback A**: if no `APPROVED` reviewer exists, pick the reviewer
   with any non-`COMMENTED`-only state (for example, `CHANGES_REQUESTED`)
   whose latest `submitted_at` is most recent.
5. **Fallback B**: if no reviews exist at all, look at CODEOWNERS for the
   changed files in `dotnet/aspnetcore` and use the first individual
   login (skip team handles). Treat this as a hint, not a strong signal.
6. **Final fallback**: leave `SME_LOGIN` empty (the workflow will draft
   the PR without an explicit reviewer rather than guess).

Capture the chosen login as `SME_LOGIN`. Do NOT include the `@` prefix.
You will pass this to the `notify_source_pr` safe output later.

## Step 3: Read the Pre-Resolved Target Branch

The target `dotnet/AspNetCore.Docs` branch was resolved before the agent
started by a deterministic `pre-agent-steps:` shell step. Its result is
at `.pr-docs-check/target.json` in the workspace. **Do not re-derive the
target branch from the source PR base or milestone** — those inputs were
already considered and the final answer is in this file.

Read `.pr-docs-check/target.json`. The fields you will use are:

| Field | Purpose |
| --- | --- |
| `effective_target_branch` | The branch you must base all docs edits and the draft PR on (`main` or `release/X.0`). |
| `candidate_target_branch` | The branch the resolution *wanted* before checking existence on `dotnet/AspNetCore.Docs`. |
| `candidate_source` | Why `candidate_target_branch` was chosen: `pr_base`, `pr_milestone`, or `fallback_main`. |
| `candidate_source_detail` | The raw milestone title or base ref that drove the choice (use this in the PR description). |
| `target_resolution` | How `effective_target_branch` was finally chosen: `exact_match` (candidate exists on `dotnet/AspNetCore.Docs`), `release_branch_missing_fallback` (candidate is a release/* that doesn't exist on the docs repo yet, so main is used), or `main` (candidate was already main). |
| `available_release_branches` | The full list of `release/*` branches that exist on `dotnet/AspNetCore.Docs` (for context only — don't second-guess the resolution). |

The current workspace is `dotnet/AspNetCore.Docs`. Switch it to
`effective_target_branch` before editing docs:

- If `effective_target_branch` is `main`, you are already on the right
  branch by default; no switch is required.
- If `effective_target_branch` starts with `release/`, run
  `git checkout <effective_target_branch>`.

Do **not** create new branches or modify the resolution. The
`create_pull_request` safe output's `base` field must be set to exactly
`effective_target_branch`.

## Step 4: Read the Pre-Computed User-Facing Signals

Whether a docs PR is required is gated by a fixed catalogue of objective
signals computed by the `Compute user-facing signals` pre-agent step.
The result is at `.pr-docs-check/signals.json` in the workspace. **Do
not re-derive these signals from the diff or the PR body** — the file is
the source of truth.

Read `.pr-docs-check/signals.json`. The fields you will use are:

| Field | Purpose |
| --- | --- |
| `recommendation` | `"docs_required"` if any gating signal fired AND no hard exclusion fired; otherwise `"docs_optional"`. This is the primary gate for Step 5. |
| `excluded_by` | Names of hard-exclusion signals that fired (`backport_label`, `backport_title_marker`). When non-empty, the recommendation is forced to `docs_optional`. |
| `triggered_signals` | The names of the boolean gating signals that fired (advisory `only_test_or_build_changes` is excluded). |
| `signal_count` | `len(triggered_signals)`. |
| `signals` | The full boolean map, including the advisory `only_test_or_build_changes`. |
| `evidence` | Per-triggered-signal list of `{ file, hint }` entries showing the changed file and the matching diff fragment or PR-body snippet. Use these to write the PR description and the `notify_source_pr` summary. |

The signal catalog is intentionally tight on what it gates on. The goal
of this workflow is to recommend docs ONLY for *new functionality / new
APIs* — bug fixes, refactors, and infra changes should not produce a
drafted docs PR.

**Hard exclusions** (when fired, recommendation is forced to
`docs_optional` regardless of any positive signals — used to skip
backports cleanly):

| Signal | Meaning |
| --- | --- |
| `backport_label` | A label containing the word `backport` (matched as a word boundary, not a substring). |
| `backport_title_marker` | PR title starts with `[release/X.Y]` or `[backport]` — the bracket convention the dotnet backport bot uses. |

**Group A — path-pattern signals**:

| Signal | Meaning |
| --- | --- |
| `new_package_added` | A new `*.csproj` under `src/<area>/<package>/src/`. A new shipping NuGet package was introduced. |
| `new_project_template_added` | A new file was added under `src/ProjectTemplates/*/content/`. A new `dotnet new` template was introduced. |
| `project_template_content_changed` | Existing project-template content under `src/ProjectTemplates/*/content/` was modified. |
| `defaults_or_constants_file_changed` | A file whose name ends in `Defaults.cs` or `Constants.cs` changed in non-test source. Typically holds shipping default values (timeouts, retry counts, well-known property names). |

**Group B — diff-content signals**:

| Signal | Meaning |
| --- | --- |
| `public_api_unshipped_added` | A non-trivial line was added to a `src/**/PublicAPI.Unshipped.txt` (skipping the `#nullable enable` header and blank lines). **This is the dominant signal** — aspnetcore enforces public-API tracking; any new public API MUST be added to Unshipped.txt. |
| `breaking_api_removal` | A line was removed from a `src/**/PublicAPI.Shipped.txt`. Shipped APIs are append-only between releases, so a committed removal is a strong breaking-change indicator. |
| `obsolete_attribute_added` | An `[Obsolete(...)]` attribute was added in non-test src/. An API was deprecated. |
| `experimental_attribute_added` | An `[Experimental(...)]` attribute was added in non-test src/. A preview / experimental API surface was introduced or expanded. |
| `new_public_type_declaration` | A `public class / interface / struct / record / enum / delegate` declaration was added in non-test source. |

**Group C — PR-body signals**:

| Signal | Meaning |
| --- | --- |
| `pr_body_has_user_facing_section` | PR body contains a heading like `## User-facing`, `## Usage`, `## How to use`, `## Public API`, or `## Breaking change`. |
| `pr_body_has_breaking_change_marker` | PR body contains the literal phrase `breaking change`. |
| `pr_body_has_deprecation_marker` | PR body contains `deprecat*` / `obsolet*` wording, or "<surface> has been removed / sunset / retired". |

**Group D — PR-label signals**:

| Signal | Meaning |
| --- | --- |
| `pr_label_breaking_change` | A label whose name contains `breaking`. |
| `pr_label_new_api` | The `api-needs-review` or `new-api` label is applied to the PR. |

**Advisory** (not gating):

| Signal | Meaning |
| --- | --- |
| `only_test_or_build_changes` | *Advisory only* — `true` iff every changed file is under tests, perf, samples, playground, `eng/`, `.github/`, top-level docs, or top-level build config. Never forces `docs_required`. |

**Conservative-recall fallback** (gating):

| Signal | Meaning |
| --- | --- |
| `diff_scan_skipped_due_to_missing_patch` | A file matched a Group B path regex but the GitHub Pulls/Files API omitted its `patch` (typically because the diff exceeds the per-file 3000-line cap). Group B scanning is skipped for that file, so this signal fires to keep recall conservative. |

Before deciding in Step 5, **enumerate the triggered signals in your
internal reasoning**. The PR description you write in Step 10 and the
`summary` you emit in Step 11 must both cite at least one `evidence`
entry per triggered signal category so a human auditor can verify the
decision.

## Step 5: Decide Whether a Docs PR Is Required

The decision is driven by `recommendation` and `excluded_by` in
`.pr-docs-check/signals.json`:

### When `excluded_by` is non-empty

A hard exclusion fired. **Do not draft a docs PR.** Proceed directly to
Step 6 and emit a `notify_source_pr` with `result: "skipped"`. In the
summary, state which exclusion(s) fired and why (for example, `"This is
a backport (labeled 'backport-9.0'). The docs PR for the original change
on main is the source of truth."`).

### When `recommendation == "docs_required"`

A documentation PR is **mandatory**. Proceed to Step 7 and beyond.

There is exactly one allowed exception, and it has a hard evidentiary
bar. You may switch to the `skipped` path **only** when every triggered
signal is already documented by name in the existing
`dotnet/AspNetCore.Docs` docs — that is, the docs already mention the
specific new API surface, type, attribute, or behavior that the signal
identifies. To use this exception you **must** do all of the following:

1. For each triggered signal, search the `aspnetcore/` content tree of
   the AspNetCore.Docs workspace for the exact identifier from the
   signal's `evidence` (for example, a new `public class` name, the new
   API line from `PublicAPI.Unshipped.txt`, or the package ID from a new
   `.csproj`).
2. Open the matching docs file and quote a sentence or code block that
   mentions the identifier by name.
3. In the `notify_source_pr` `summary` (Step 11), include — per
   triggered signal — the docs file path **and** the quoted text. Plain
   statements like *"the existing docs cover this area"* or *"this is
   internal"* are not acceptable; the audit trail must show the
   identifier appears in the docs verbatim.

If you cannot meet this bar for **every** triggered signal, draft the
docs PR.

### When `recommendation == "docs_optional"` (with no exclusion)

No deterministic user-facing signal fired. A docs PR is still required
when the change matches any of these positive triggers that the pre-step
cannot detect mechanically:

- A user-visible behavior change to an already-documented feature
  (for example, default values, error messages, or HTTP status codes
  that ship to users and that the docs describe).
- A new environment variable, configuration key, or `appsettings.json`
  property exposed through existing public surface.
- A new supported version, runtime target, or platform mentioned in a
  docs `prerequisites` list.

Otherwise, the `skipped` path is allowed **only** when the change falls
cleanly into one of these explicit allowlist categories:

| Allowlist category | Definition |
| --- | --- |
| `test_only` | Only files under test, perf, benchmarks, or sample paths changed (matches `only_test_or_build_changes` *and* no source files changed). |
| `build_or_ci_only` | Only files under `eng/`, `.github/`, top-level build config (`global.json`, `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`), or `NuGet.config` changed. |
| `dependency_bump` | Only `eng/Versions.props` (or equivalent package version files) changed and the change is purely a version bump with no behavior or surface change. |
| `internal_refactor` | The change touches `src/` but introduces no new or changed public types, methods, options, or strings. The PR title and body confirm this is purely internal. |
| `formatting_or_comment_only` | Pure typo fix, formatting, or code-comment-only change with no behavioral effect. |
| `bug_fix_restores_documented_behavior` | A bug fix that brings the implementation back in line with already-documented behavior. The docs already describe the intended behavior — the bug was the discrepancy. |

If the change does not match exactly one of these categories, draft the
docs PR.

### Ambiguity rule

When the evidence is mixed or you are unsure, **draft the PR**. A drafted
docs PR that a human closes is far cheaper than a user-facing change
shipping undocumented. The drafted PR is in `draft:` state; it does not
merge until a human flips it out of draft.

## Step 6: Emit the No-Docs Outcome (only when Step 5 allowed it)

This step runs **only** when Step 5 produced an allowed `skipped`
result. Emit a single `notify_source_pr` safe output with:

- `source_pr_number`: the source PR number from Step 1.
- `result`: `"skipped"`.
- `sme_login`: `SME_LOGIN` from Step 2 (or an empty string if none was
  found).
- `summary`: a structured markdown rationale that proves the decision.
  It **must** include:
  1. The Step 5 branch you took, named explicitly: one of
     `"hard exclusion: <name>"`, `"docs_required → already documented
     by name"`, or `"docs_optional → <allowlist_category>"`.
  2. The list of triggered signals from `.pr-docs-check/signals.json`
     (or "no signals triggered" when `signal_count == 0`).
  3. For the `already documented by name` branch: the per-signal docs
     file path and quoted sentence/code block from Step 5.
  4. For the allowlist branch: the changed-file globs that justify the
     chosen category.
  5. For the hard-exclusion branch: the matched label name(s) or the
     title prefix that fired the exclusion.

Then **stop**. Do **not** emit `create_pull_request` or any other safe
output on the no-docs path.

## Step 7: Read the doc-writer Skill

Read the file `.github/skills/doc-writer/SKILL.md` from the checked-out
`dotnet/AspNetCore.Docs` workspace. This skill contains comprehensive
guidelines for writing documentation on the ASP.NET Core docs site,
including:

- Site structure under `aspnetcore/` (file naming, TOC, includes)
- DocFx Markdown conventions and YAML frontmatter requirements
- Versioned content via `:::moniker range="..."` blocks
- Cross-references via `<xref:Symbol.Full.Name>` and link shortening
- Code-snippet conventions (`:::code` references into `samples/`)
- Image, table, and admonition conventions

**You must follow all guidelines in the doc-writer skill when writing
documentation.** If the skill file is missing (the docs team has not
ported it yet), fall back to these baseline rules:

- Use plain Markdown (`.md`), not MDX.
- Add YAML frontmatter with at least `title`, `description`, `author`,
  `ms.author`, `ms.date`, `uid` (or `ms.topic`).
- For multi-version content, wrap version-specific sections in
  `:::moniker range="..."` ... `:::moniker-end`.
- Cross-reference public APIs with `<xref:Microsoft.AspNetCore.Foo.Bar>`.
- Prefer updating existing pages over creating new ones; only create a
  new page when no existing one covers the area.
- Honor the existing `toc.yml` for any directory you touch.

## Step 8: Browse Existing Documentation

Explore the existing documentation in the `aspnetcore/` folder of the
AspNetCore.Docs workspace to:

- Identify pages that cover the affected feature area
- Confirm the documentation gap you identified in Step 5
- Determine whether existing pages need updates or new pages should be created
- Understand the current documentation structure, naming conventions, and patterns
- Find related pages that should be cross-referenced
- Locate the relevant `toc.yml` so new pages are linked into navigation

## Step 9: Write Documentation Changes

Based on your analysis, make the actual file changes in the workspace:

- **For updates to existing pages**: Edit the relevant `.md` files in place.
- **For new pages**: Create new `.md` files in the appropriate directory
  following the doc-writer skill's conventions for frontmatter, content,
  and navigation.

**Ground the documentation in the context you gathered in Step 1.**
Specifically:

- Use the **source PR description** as the primary statement of what the
  change does and why.
- Use the **list of changed files** (and their diff hunks for the
  surface-area files identified in Step 4's signals) to confirm the
  exact identifiers, type names, default values, and package IDs you
  cite in the docs. Never re-invent names; copy them verbatim from the
  diff (the PublicAPI.Unshipped.txt entries are the canonical source).
- Use the **PR conversation and review comments** to capture nuance
  that doesn't appear in the PR body — reviewer-driven naming changes,
  follow-up clarifications about defaults, edge cases the author
  acknowledged, and any "we decided to do X for this reason" exchanges.

If the PR description and comments contradict the diff, trust the diff
for identifiers and ask the SME via the `notify_source_pr` summary to
clarify before merging the docs PR.

Keep the changes focused on the significant user-facing change that
triggered this workflow. Prefer updating the smallest correct set of
pages over broad speculative edits.

Ensure all changes follow the doc-writer skill guidelines from Step 7.
Include:
- Proper frontmatter (`title`, `description`, `author`, `ms.author`,
  `ms.date`, `uid`)
- `:::moniker range="..."` blocks when the new API is only available in
  a specific version
- `<xref:>` cross-references to the new public symbols
- Code examples where appropriate
- A link from any related parent page (and, if a new page, an entry in
  the relevant `toc.yml`)

## Step 10: Create Draft PR

Create a draft pull request on `dotnet/AspNetCore.Docs` with:

**Base branch**: the `effective_target_branch` value from
`.pr-docs-check/target.json` (read in Step 3). When emitting the
`create_pull_request` safe output, set its `base` field to that exact
string (for example, `release/10.0`, `release/9.0`, or `main`). Do not
derive or modify this value.

**Title**: A clear, concise title describing the documentation work (the
`[docs]` prefix will be added automatically).

**Description** that includes:
- A prominent link to the source PR: `Documents changes from dotnet/aspnetcore#<number>`
- The PR author mention: `@<author>`
- The target branch and how it was chosen, using `candidate_source`,
  `candidate_source_detail`, and `target_resolution` from
  `.pr-docs-check/target.json`. For example:
  - `exact_match`: "Targeting `release/10.0` based on the source PR base ref."
  - `release_branch_missing_fallback`: "Targeting `main` because the
    docs repo does not yet have a `release/10.0` branch — docs for the
    upcoming release land on `main` until the docs team forks the
    release branch."
  - `main`: "Targeting `main` because the source PR targeted `main`."
- Why this PR is needed (the significant change and the docs gap it
  addresses), with citations of the triggered signals' evidence
- A summary of what documentation was added or changed
- A list of files modified or created
- Whether pages were updated or newly created

Do **not** include `reviewers` in the `create_pull_request` emission.
The SME identified in Step 2 is requested as a reviewer by the
`notify_source_pr` safe-output job, not by `create_pull_request`.

## Step 11: Notify Source PR

After emitting `create_pull_request`, emit a single `notify_source_pr`
safe output with:

- `source_pr_number`: the source PR number from Step 1.
- `result`: `"drafted"`.
- `sme_login`: `SME_LOGIN` from Step 2 (or an empty string if none was
  found).
- `target_branch`: the `effective_target_branch` value from
  `.pr-docs-check/target.json` (read in Step 3) — for example,
  `release/10.0` or `main`. Do not derive or modify this value.
- `summary`: a short markdown summary (1–3 sentences plus optional
  bullet list) of the documentation changes made. List the files
  modified or created. Do **not** describe links here — the workflow
  injects the drafted PR's URL automatically.

> [!IMPORTANT]
> Do **not** try to compose the drafted PR's URL or PR number yourself
> in the `summary` text. The `notify_source_pr` safe-output job knows
> the real values from the safe-outputs handler and will substitute
> them when posting the comment. Likewise, do **not** call `add_comment`
> for either the "drafted" or "skipped" path — `notify_source_pr` is
> the only commenting path used by this workflow.
