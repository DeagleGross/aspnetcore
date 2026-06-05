"""Compute user-facing signals for the pr-docs-check workflow.

This script is invoked by `.github/workflows/pr-docs-check.md` as a
pre-agent step. It writes a JSON document containing a fixed catalog of
boolean "user-facing change" signals derived from objective evidence:

  - Hard exclusions   (force docs_optional regardless of positive signals)
  - Changed-file paths       (Group A)
  - Diff hunk contents       (Group B)
  - PR body regexes          (Group C)
  - PR labels                (Group D)
  - An advisory `only_test_or_build_changes` flag (never gates)

The agent reads the resulting file verbatim and treats
`recommendation == "docs_required"` as a hard "draft a docs PR" gate.
Each triggered signal carries evidence (file path + matching diff
fragment or PR-body snippet) so the audit trail is reproducible.

The catalog is tuned for `dotnet/aspnetcore`:

  - `PublicAPI.Unshipped.txt` additions are the dominant signal
    (the Microsoft.CodeAnalysis.PublicApiAnalyzers convention used
    across the repo means any new public API MUST appear there).
  - `PublicAPI.Shipped.txt` removals are an append-only breaking-change
    indicator.
  - New `*.csproj` under `src/<area>/<name>/src/` flag new shipping
    NuGet packages.
  - Backport PRs (label or `[release/X.Y]` title prefix) are hard-
    excluded — the original docs PR was drafted from the source PR on
    `main`, and re-drafting on a release branch produces duplicate work.

Per the user requirement on this workflow, the goal is to recommend
docs ONLY for new functionality / new APIs — bug fixes, refactors,
infra changes, and backports should not gate.

Usage
-----

    python3 compute_signals.py <pr.json> <files.json> <out.json>

where `pr.json` is the body of `GET /repos/dotnet/aspnetcore/pulls/{N}`
and `files.json` is the concatenated body of
`GET /repos/dotnet/aspnetcore/pulls/{N}/files?per_page=100` (paginated
and JSON-arrayed).

The catalog is intentionally broad on the positive side. The worst case
for a false positive is a drafted docs PR that a human closes (drafted
PRs never auto-merge), while a false negative ships an undocumented
user-facing change. Favor recall over precision — except for hard
exclusions, which are deliberately narrow (label + title prefix) so
they don't silently swallow legitimate feature PRs.

Running the tests
-----------------

    python3 -m unittest discover -s .github/workflows/pr-docs-check -v
"""

from __future__ import annotations

import json
import re
import sys
from typing import Callable, Pattern


# ============================================================
# Hard exclusions
# ============================================================
# When any of these fires, `recommendation` is forced to `docs_optional`
# and `excluded_by` is populated. This is the user's "skip backports /
# fixes" constraint, encoded narrowly so a real feature can't be
# silently suppressed:
#
#   - `backport_label`         — a label whose normalized name contains
#                                `backport`. Backport bots (dotnet/runtime,
#                                aspnetcore) all apply such a label.
#   - `backport_title_marker`  — title starts with `[release/<X>.<Y>]`
#                                (the convention the dotnet backport bot
#                                uses for branch-specific backports).
#
# A backport that legitimately introduces a new API will still trigger
# `public_api_unshipped_added` *and* the hard exclusion; the exclusion
# wins, which is the intent: docs are drafted off the original PR on
# `main` and ported to release branches by the docs team, not by this
# workflow firing twice.
_BACKPORT_LABEL_RE = re.compile(r"(?i)(^|[\s\-_/:])backport(\b|[\s\-_/:])")
_BACKPORT_TITLE_RE = re.compile(
    r"(?i)^\s*\[\s*(?:backport|release[\s/]+[0-9]+\.[0-9]+(?:\.[0-9]+)?)\s*\]"
)

HARD_EXCLUSIONS: dict[str, Callable[[dict, list[str]], bool]] = {
    "backport_label": lambda pr, labels: any(
        _BACKPORT_LABEL_RE.search(lab or "") for lab in labels
    ),
    "backport_title_marker": lambda pr, labels: bool(
        _BACKPORT_TITLE_RE.match(pr.get("title") or "")
    ),
}


# ============================================================
# Group A: Path-pattern triggers
# ============================================================
# Each entry: (signal_name, status_filter, path_regex)
# status_filter is one of:
#   "added" — file is brand new in this PR (status == "added")
#   "any"   — file was added, modified, or renamed
#
# Examples of paths these patterns must match:
#   src/Mvc/Mvc.Core/src/Microsoft.AspNetCore.Mvc.Core.csproj     -> new_package_added
#   src/Http/Routing/src/Microsoft.AspNetCore.Routing.csproj      -> new_package_added
#   src/ProjectTemplates/Web.ProjectTemplates/content/...         -> new_project_template_added / project_template_content_changed
#   src/Http/Http/src/HttpDefaults.cs                             -> defaults_or_constants_file_changed
#   src/Servers/Kestrel/Core/src/KestrelServerLimits.Constants.cs -> defaults_or_constants_file_changed
#
# Test projects (`/test/`, `*.Tests.csproj`, `*.Test.csproj`) and
# helpers under `/testassets/` are excluded from `new_package_added`
# because they don't ship as NuGet packages.
PATH_TRIGGERS: list[tuple[str, str, str]] = [
    # A new shipping .csproj. aspnetcore convention is
    # `src/<area>/<package>/src/<package>.csproj` for shipping
    # packages; `src/<area>/<package>/test/...` for tests. Requiring
    # a literal `/src/` segment excludes test, perf, and sample
    # projects automatically.
    (
        "new_package_added",
        "added",
        r"^src/(?!ProjectTemplates/).+/src/[^/]+\.csproj$",
    ),
    # A brand-new project template file under
    # src/ProjectTemplates/*/content/. New templates ship via
    # `dotnet new` and need a docs page.
    (
        "new_project_template_added",
        "added",
        r"^src/ProjectTemplates/[^/]+/content/.+",
    ),
    # Any change to existing project-template content. Templates
    # are user-facing — a change to scaffolded code, package
    # references, or sample copy may require a docs update.
    (
        "project_template_content_changed",
        "any",
        r"^src/ProjectTemplates/[^/]+/content/.+",
    ),
    # Files whose name ends in `Defaults.cs` or `Constants.cs`
    # typically hold shipping default values (timeouts, retry
    # counts, well-known property names, hostnames, paths).
    # Excludes Tests/ folders so internal test constants don't trip.
    (
        "defaults_or_constants_file_changed",
        "any",
        r"^src/(?!.*(?:/test/|/tests/|/testassets/|/perf/|/benchmarks/|/samples?/|/playground/))"
        r".+(?:Defaults|Constants)\.cs$",
    ),
]


# ============================================================
# Group B: Diff-content triggers
# ============================================================
# Each entry: (signal_name, path_regex, direction, line_regex)
# direction is one of:
#   "added"   — scan added lines (those starting with "+")
#   "removed" — scan removed lines (those starting with "-")
#   "any"     — scan both directions
# All directions skip the diff file headers "+++ b/..." and "--- a/...".
#
# Examples of lines these patterns must match:
#   `+Microsoft.AspNetCore.Builder.WebApplicationBuilder.UseFoo() -> void`
#     (inside src/Foo/src/PublicAPI.Unshipped.txt)
#     -> public_api_unshipped_added
#   `-Microsoft.AspNetCore.Routing.RouteBuilder.MapDelete(...) -> RouteHandlerBuilder`
#     (inside src/Http/Routing/src/PublicAPI.Shipped.txt)
#     -> breaking_api_removal
#   `+    [Obsolete("Use AddBar instead.")]`
#     -> obsolete_attribute_added
#   `+    [Experimental("ASPCORE001")]`
#     -> experimental_attribute_added
#   `+public sealed class FooMiddleware`
#     -> new_public_type_declaration
DIFF_TRIGGERS: list[tuple[str, str, str, Pattern[str]]] = [
    # The single most important signal. aspnetcore-wide convention
    # (Microsoft.CodeAnalysis.PublicApiAnalyzers) requires every new
    # public API surface to be tracked in `PublicAPI.Unshipped.txt`
    # — so any addition to that file is concrete, machine-verifiable
    # evidence of a new public API that ships. Skip:
    #   - blank lines
    #   - the `#nullable enable` header that PublicApiAnalyzers writes
    #     at the top of each file
    (
        "public_api_unshipped_added",
        r"^src/.+/PublicAPI\.Unshipped\.txt$",
        "added",
        re.compile(r"^(?!\s*$)(?!\s*#nullable\b).+"),
    ),
    # Removed line from any PublicAPI.Shipped.txt. Shipped APIs are
    # append-only between releases (the analyzer enforces this), so
    # a committed removal is a hard breaking-change indicator.
    # Whitespace-only reformats can also trip this — acceptable
    # under the "favor recall" policy.
    (
        "breaking_api_removal",
        r"^src/.+/PublicAPI\.Shipped\.txt$",
        "removed",
        re.compile(r"^(?!\s*$)(?!\s*#nullable\b).+"),
    ),
    # [Obsolete(...)] addition anywhere in src/, excluding test
    # projects and testassets. Tolerates either the shorthand
    # attribute name or the *Attribute form.
    (
        "obsolete_attribute_added",
        r"^src/(?!.*(?:/test/|/tests/|/testassets/|/perf/|/benchmarks/|/samples?/|/playground/))"
        r".+\.cs$",
        "added",
        re.compile(r"\[Obsolete(?:Attribute)?\s*[\(\]]"),
    ),
    # [Experimental(...)] addition — marks new preview /
    # experimental APIs that users opt into via a diagnostic ID.
    (
        "experimental_attribute_added",
        r"^src/(?!.*(?:/test/|/tests/|/testassets/|/perf/|/benchmarks/|/samples?/|/playground/))"
        r".+\.cs$",
        "added",
        re.compile(r"\[Experimental(?:Attribute)?\s*[\(\]]"),
    ),
    # New public type declaration in non-test source. Matches
    # class / interface / struct / record / record class /
    # record struct / enum / delegate.
    (
        "new_public_type_declaration",
        r"^src/(?!.*(?:/test/|/tests/|/testassets/|/perf/|/benchmarks/|/samples?/|/playground/))"
        r".+\.cs$",
        "added",
        re.compile(
            r"^\s*public\s+"
            r"(?:static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+|ref\s+|unsafe\s+|new\s+)*"
            r"(?:class|interface|struct|record(?:\s+(?:class|struct))?|enum|delegate)\s+\w+"
        ),
    ),
]


# ============================================================
# Group C: PR-body triggers
# ============================================================
# Author-supplied prose signals.
#
# Examples of bodies these match:
#   "## User-facing usage\n```csharp\nbuilder.Services.AddFoo();"
#     -> pr_body_has_user_facing_section
#   "Breaking change: removes deprecated AddBar overload."
#     -> pr_body_has_breaking_change_marker AND
#        pr_body_has_deprecation_marker
BODY_TRIGGERS: dict[str, Pattern[str]] = {
    # Common headers in PR bodies that signal user-facing intent.
    "pr_body_has_user_facing_section": re.compile(
        r"(?im)^\s{0,3}#{1,6}\s*"
        r"(user[-_ ]?facing|usage|how\s+to\s+use|public\s+api|breaking\s+change)\b"
    ),
    # The literal phrase "breaking change" anywhere in the body.
    "pr_body_has_breaking_change_marker": re.compile(
        r"(?i)\bbreaking[\s\-]?change\b"
    ),
    # Deprecation phrasing: `deprecat*`, `obsolet*`, or
    # "<api> has been removed/sunset/retired" patterns.
    "pr_body_has_deprecation_marker": re.compile(
        r"(?i)("
        r"\bdeprecat\w+"
        r"|\bobsolet\w+"
        r"|\b(?:api|method|class|property|option|flag|package|attribute|extension|middleware|service)s?\s+"
        r"(?:is|are|has\s+been|have\s+been|were|will\s+be|now)\s+"
        r"(?:removed|sunset|retired)\b"
        r")"
    ),
}


# ============================================================
# Group D: PR-label triggers
# ============================================================
# Author/maintainer-curated labels are very signal-dense when set.
# Examples of label names these match:
#   "breaking-change" / "api: breaking" / "kind:breaking"
#     -> pr_label_breaking_change
#   "api-needs-review" / "new-api"
#     -> pr_label_new_api
_NEW_API_LABEL_RE = re.compile(r"(?i)(\bapi-needs-review\b|\bnew-api\b)")
LABEL_TRIGGERS: dict[str, Callable[[list[str]], bool]] = {
    "pr_label_breaking_change": lambda labs: any(
        "breaking" in (lab or "").lower() for lab in labs
    ),
    "pr_label_new_api": lambda labs: any(
        bool(_NEW_API_LABEL_RE.search(lab or "")) for lab in labs
    ),
}


# ============================================================
# Advisory: only_test_or_build_changes
# ============================================================
# True iff EVERY changed file falls in test/build/CI/playground/
# docs/agent buckets. Used only by the prompt's Step 5 allowlist —
# it never forces docs_required and is deliberately excluded from
# `triggered_signals`.
#
# aspnetcore-specific: tests live under
# `src/<area>/<package>/test[s]/` (NOT a top-level `tests/` folder),
# so the "advisory" signal also matches paths with a `/test/` or
# `/tests/` segment anywhere.
_ONLY_TEST_OR_BUILD_TOP_RE = re.compile(
    r"^(eng/|playground/|samples?/|docs/|\.github/|\.config/|\.devcontainer/|"
    r"\.editorconfig$|global\.json$|NuGet\.config$|"
    r"Directory\.(Build|Packages)\.props$|Directory\.Build\.targets$)"
)
_TEST_PATH_SEGMENT_RE = re.compile(
    r"(^|/)(test|tests|testassets|perf|benchmarks|sample|samples|playground)/"
)


def _is_only_test_or_build(filename: str) -> bool:
    """One path matches the advisory test/build/infra bucket."""
    if _ONLY_TEST_OR_BUILD_TOP_RE.match(filename):
        return True
    if _TEST_PATH_SEGMENT_RE.search(filename):
        return True
    return False


def _trim_hint(text: str, limit: int = 200) -> str:
    """Trim a single-line evidence hint so signals.json stays readable."""
    text = text.strip()
    if len(text) > limit:
        text = text[: limit - 3] + "..."
    return text


def compute_signals(pr: dict, files: list[dict]) -> dict:
    """Compute the full signals.json document for a given PR payload.

    Args:
        pr: The body of `GET /repos/dotnet/aspnetcore/pulls/{N}` as a dict.
        files: The concatenated body of `GET .../files?per_page=100`
            (already paginated and flattened into a list).

    Returns:
        A dict ready to be JSON-serialized as `.pr-docs-check/signals.json`.
    """
    pr_body = pr.get("body") or ""
    pr_labels = [(lab.get("name") or "") for lab in (pr.get("labels") or [])]

    signals: dict[str, bool] = {}
    evidence: dict[str, list[dict]] = {}

    def record(signal_name: str, file_path: str, hint: str) -> None:
        evidence.setdefault(signal_name, []).append({"file": file_path, "hint": hint})

    # ---- Hard exclusions ----
    # These are checked first and recorded with the same evidence
    # shape as positive signals so the agent's audit trail in
    # Step 6 can quote them.
    excluded_by: list[str] = []
    for excl_name, predicate in HARD_EXCLUSIONS.items():
        fired = bool(predicate(pr, pr_labels))
        signals[excl_name] = fired
        if not fired:
            continue
        excluded_by.append(excl_name)
        if excl_name == "backport_label":
            matched_labels = [
                lab for lab in pr_labels if _BACKPORT_LABEL_RE.search(lab or "")
            ]
            record(excl_name, "<pr-labels>", _trim_hint(", ".join(matched_labels)))
        elif excl_name == "backport_title_marker":
            record(excl_name, "<pr-title>", _trim_hint(pr.get("title") or ""))

    # ---- Group A: path triggers ----
    for signal_name, status_filter, path_regex in PATH_TRIGGERS:
        regex = re.compile(path_regex)
        signals.setdefault(signal_name, False)
        for f in files:
            filename = f.get("filename") or ""
            status = f.get("status") or ""
            if status_filter == "added" and status != "added":
                continue
            if regex.match(filename):
                signals[signal_name] = True
                record(signal_name, filename, f"path matched {path_regex}")

    # ---- Group B: diff-content triggers ----
    #
    # The GitHub API omits the `patch` field for very large files. We
    # record those skips as the global
    # `diff_scan_skipped_due_to_missing_patch` signal so the agent
    # treats them as conservative gating evidence — the failure mode
    # we are guarding against is shipping an undocumented user-facing
    # change because the diff was too large to scan.
    signals.setdefault("diff_scan_skipped_due_to_missing_patch", False)

    for signal_name, path_regex, direction, line_regex in DIFF_TRIGGERS:
        path_re = re.compile(path_regex)
        signals.setdefault(signal_name, False)
        for f in files:
            filename = f.get("filename") or ""
            if not path_re.match(filename):
                continue
            patch = f.get("patch") or ""
            if not patch:
                signals["diff_scan_skipped_due_to_missing_patch"] = True
                record(
                    "diff_scan_skipped_due_to_missing_patch",
                    filename,
                    f"would have scanned for {signal_name} (path matched {path_regex})",
                )
                continue
            for line in patch.splitlines():
                if line.startswith("+++") or line.startswith("---"):
                    continue
                if direction == "added":
                    if not line.startswith("+"):
                        continue
                elif direction == "removed":
                    if not line.startswith("-"):
                        continue
                elif direction == "any":
                    if not (line.startswith("+") or line.startswith("-")):
                        continue
                else:
                    raise ValueError(
                        f"Unknown direction {direction!r} for signal {signal_name!r}"
                    )
                content = line[1:]
                m = line_regex.search(content)
                if m:
                    signals[signal_name] = True
                    prefix = "+" if line.startswith("+") else "-"
                    record(signal_name, filename, _trim_hint(f"{prefix}{content}"))
                    break

    # ---- Group C: PR-body triggers ----
    for signal_name, regex in BODY_TRIGGERS.items():
        m = regex.search(pr_body)
        signals[signal_name] = bool(m)
        if m:
            start = max(0, m.start() - 20)
            end = min(len(pr_body), m.end() + 60)
            hint = pr_body[start:end].replace("\n", " ").replace("\r", " ")
            record(signal_name, "<pr-body>", _trim_hint(hint))

    # ---- Group D: PR-label triggers ----
    for signal_name, predicate in LABEL_TRIGGERS.items():
        matched = predicate(pr_labels)
        signals[signal_name] = bool(matched)
        if matched:
            matched_labels = [lab for lab in pr_labels if predicate([lab])]
            record(signal_name, "<pr-labels>", _trim_hint(", ".join(matched_labels)))

    # ---- Advisory: only_test_or_build_changes ----
    if files:
        signals["only_test_or_build_changes"] = all(
            _is_only_test_or_build(f.get("filename") or "") for f in files
        )
    else:
        signals["only_test_or_build_changes"] = False

    # Positive signals that gate `docs_required`. Excludes:
    #   - hard exclusions (recorded under `excluded_by` instead)
    #   - the advisory `only_test_or_build_changes`
    advisory_names = {"only_test_or_build_changes"}
    exclusion_names = set(HARD_EXCLUSIONS.keys())
    gating_signals = [
        name
        for name, v in signals.items()
        if v and name not in advisory_names and name not in exclusion_names
    ]

    # Final recommendation:
    #   1. Any hard exclusion fires       -> docs_optional (forced)
    #   2. Otherwise any gating signal    -> docs_required
    #   3. Otherwise                      -> docs_optional
    if excluded_by:
        recommendation = "docs_optional"
    elif gating_signals:
        recommendation = "docs_required"
    else:
        recommendation = "docs_optional"

    return {
        "source_pr_number": int(pr.get("number") or 0),
        "recommendation": recommendation,
        "excluded_by": sorted(excluded_by),
        "triggered_signals": sorted(gating_signals),
        "signal_count": len(gating_signals),
        "signals": signals,
        "evidence": {k: v for k, v in evidence.items() if signals.get(k)},
    }


def main(argv: list[str]) -> int:
    if len(argv) != 4:
        print(
            "usage: compute_signals.py <pr.json> <files.json> <out.json>",
            file=sys.stderr,
        )
        return 2
    pr_json_path, files_json_path, out_path = argv[1], argv[2], argv[3]

    with open(pr_json_path, "r", encoding="utf-8") as f:
        pr = json.load(f)
    with open(files_json_path, "r", encoding="utf-8") as f:
        files = json.load(f)

    result = compute_signals(pr, files)

    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2, sort_keys=True)
        f.write("\n")

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
