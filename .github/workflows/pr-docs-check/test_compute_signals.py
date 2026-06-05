"""Unit tests for compute_signals.py.

Run with::

    python3 -m unittest discover -s .github/workflows/pr-docs-check -v

The tests are organized by signal group, with one TestCase class per
category plus an integration-style class at the bottom that exercises
the recommendation logic and the hard-exclusion override.
"""

from __future__ import annotations

import json
import os
import sys
import tempfile
import unittest

# Make `compute_signals` importable when this file is run via
# `python3 -m unittest discover -s .github/workflows/pr-docs-check`.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from compute_signals import (  # noqa: E402  (sys.path mutation above)
    compute_signals,
    main,
)


def _pr(
    *,
    number: int = 12345,
    title: str = "Add new feature",
    body: str = "",
    labels: list[str] | None = None,
) -> dict:
    return {
        "number": number,
        "title": title,
        "body": body,
        "labels": [{"name": lab} for lab in (labels or [])],
    }


def _file(
    filename: str,
    *,
    status: str = "modified",
    patch: str | None = "",
) -> dict:
    return {"filename": filename, "status": status, "patch": patch}


# ============================================================
# Hard exclusions
# ============================================================
class HardExclusionsTests(unittest.TestCase):
    def test_backport_label_excludes_even_when_positive_signal_fires(self) -> None:
        pr = _pr(labels=["backport", "area-routing"])
        files = [
            _file(
                "src/Http/Routing/src/PublicAPI.Unshipped.txt",
                status="modified",
                patch="@@\n+#nullable enable\n+Microsoft.AspNetCore.Routing.Foo.Bar() -> void\n",
            )
        ]
        result = compute_signals(pr, files)
        self.assertEqual(result["recommendation"], "docs_optional")
        self.assertIn("backport_label", result["excluded_by"])
        self.assertTrue(result["signals"]["backport_label"])
        # Positive signal still recorded for transparency.
        self.assertTrue(result["signals"]["public_api_unshipped_added"])
        # But because of the hard exclusion, the agent will skip.
        self.assertIn("public_api_unshipped_added", result["triggered_signals"])

    def test_backport_label_case_insensitive(self) -> None:
        pr = _pr(labels=["Backport"])
        result = compute_signals(pr, [])
        self.assertIn("backport_label", result["excluded_by"])

    def test_backport_label_compound_name(self) -> None:
        # The dotnet/aspnetcore bot uses names like `:cherry_picker: backport/10.0`
        # or `backport-9.0`.  Both must be detected.
        for name in ["backport-9.0", "backport/10.0", "kind:backport", "backport 9.0"]:
            with self.subTest(label=name):
                pr = _pr(labels=[name])
                result = compute_signals(pr, [])
                self.assertIn(
                    "backport_label",
                    result["excluded_by"],
                    msg=f"label {name!r} should fire backport_label",
                )

    def test_backport_label_does_not_match_unrelated_words(self) -> None:
        # A label that just happens to share letters with "backport"
        # (e.g. `back-end`) must NOT trip the exclusion.
        for name in ["back-end", "area-back", "compatport"]:
            with self.subTest(label=name):
                pr = _pr(labels=[name])
                result = compute_signals(pr, [])
                self.assertNotIn("backport_label", result["excluded_by"])

    def test_backport_title_marker_release_format(self) -> None:
        pr = _pr(title="[release/10.0] Fix routing edge case")
        result = compute_signals(pr, [])
        self.assertIn("backport_title_marker", result["excluded_by"])

    def test_backport_title_marker_release_three_part(self) -> None:
        pr = _pr(title="[release/9.0.1] Backport: do thing")
        result = compute_signals(pr, [])
        self.assertIn("backport_title_marker", result["excluded_by"])

    def test_backport_title_marker_explicit_word(self) -> None:
        pr = _pr(title="[backport] Fix issue #123 on release branch")
        result = compute_signals(pr, [])
        self.assertIn("backport_title_marker", result["excluded_by"])

    def test_backport_title_marker_does_not_match_unrelated_brackets(self) -> None:
        pr = _pr(title="[main] Some routine cleanup")
        result = compute_signals(pr, [])
        self.assertNotIn("backport_title_marker", result["excluded_by"])

    def test_no_exclusion_means_normal_recommendation_path(self) -> None:
        pr = _pr(title="Add Foo middleware", labels=[])
        files = [
            _file(
                "src/Middleware/Foo/src/PublicAPI.Unshipped.txt",
                status="modified",
                patch="@@\n+Microsoft.AspNetCore.FooMiddleware -> Microsoft.AspNetCore.FooMiddleware\n",
            )
        ]
        result = compute_signals(pr, files)
        self.assertEqual(result["excluded_by"], [])
        self.assertEqual(result["recommendation"], "docs_required")


# ============================================================
# Group A: path triggers
# ============================================================
class PathTriggerTests(unittest.TestCase):
    def test_new_package_added_matches_canonical_shipping_csproj(self) -> None:
        files = [
            _file(
                "src/Middleware/Foo/src/Microsoft.AspNetCore.Foo.csproj",
                status="added",
            )
        ]
        result = compute_signals(_pr(), files)
        self.assertTrue(result["signals"]["new_package_added"])

    def test_new_package_added_ignores_modified_csproj(self) -> None:
        files = [
            _file(
                "src/Middleware/Foo/src/Microsoft.AspNetCore.Foo.csproj",
                status="modified",
            )
        ]
        result = compute_signals(_pr(), files)
        self.assertFalse(result["signals"]["new_package_added"])

    def test_new_package_added_excludes_test_projects(self) -> None:
        # Aspnetcore convention puts tests under `.../test/...`, not `.../src/...`.
        # The regex requires a literal `/src/` segment so tests never match.
        files = [
            _file(
                "src/Middleware/Foo/test/Microsoft.AspNetCore.Foo.Tests.csproj",
                status="added",
            )
        ]
        result = compute_signals(_pr(), files)
        self.assertFalse(result["signals"]["new_package_added"])

    def test_new_package_added_excludes_project_templates(self) -> None:
        # A new template's own .csproj infra shouldn't trip "new package".
        # `project_template_*` signals cover that case instead.
        files = [
            _file(
                "src/ProjectTemplates/Web.ProjectTemplates/src/Web.ProjectTemplates.csproj",
                status="added",
            )
        ]
        result = compute_signals(_pr(), files)
        self.assertFalse(result["signals"]["new_package_added"])

    def test_new_project_template_added(self) -> None:
        files = [
            _file(
                "src/ProjectTemplates/Web.ProjectTemplates/content/NewTemplate/Program.cs",
                status="added",
            )
        ]
        result = compute_signals(_pr(), files)
        self.assertTrue(result["signals"]["new_project_template_added"])
        self.assertTrue(result["signals"]["project_template_content_changed"])

    def test_project_template_content_changed_on_modify(self) -> None:
        files = [
            _file(
                "src/ProjectTemplates/Web.ProjectTemplates/content/Existing/Program.cs",
                status="modified",
            )
        ]
        result = compute_signals(_pr(), files)
        self.assertFalse(result["signals"]["new_project_template_added"])
        self.assertTrue(result["signals"]["project_template_content_changed"])

    def test_defaults_or_constants_matches_defaults(self) -> None:
        for path in [
            "src/Http/Http/src/HttpDefaults.cs",
            "src/Servers/Kestrel/Core/src/Internal/KestrelDefaults.cs",
        ]:
            with self.subTest(path=path):
                result = compute_signals(_pr(), [_file(path)])
                self.assertTrue(result["signals"]["defaults_or_constants_file_changed"])

    def test_defaults_or_constants_matches_constants(self) -> None:
        path = "src/Servers/Kestrel/Core/src/KestrelServerLimits.Constants.cs"
        result = compute_signals(_pr(), [_file(path)])
        self.assertTrue(result["signals"]["defaults_or_constants_file_changed"])

    def test_defaults_or_constants_excludes_tests(self) -> None:
        path = "src/Middleware/Foo/test/FooDefaults.cs"
        result = compute_signals(_pr(), [_file(path)])
        self.assertFalse(result["signals"]["defaults_or_constants_file_changed"])

    def test_defaults_or_constants_excludes_playground(self) -> None:
        path = "src/Hosting/playground/SampleApp/SampleDefaults.cs"
        result = compute_signals(_pr(), [_file(path)])
        self.assertFalse(result["signals"]["defaults_or_constants_file_changed"])


# ============================================================
# Group B: diff-content triggers
# ============================================================
class DiffTriggerTests(unittest.TestCase):
    def test_public_api_unshipped_added_for_new_member(self) -> None:
        patch = (
            "@@ -1,3 +1,5 @@\n"
            " #nullable enable\n"
            " \n"
            "+Microsoft.AspNetCore.Builder.FooExtensions\n"
            "+static Microsoft.AspNetCore.Builder.FooExtensions.UseFoo(this Microsoft.AspNetCore.Builder.IApplicationBuilder! builder) -> Microsoft.AspNetCore.Builder.IApplicationBuilder!\n"
        )
        files = [_file("src/Middleware/Foo/src/PublicAPI.Unshipped.txt", patch=patch)]
        result = compute_signals(_pr(), files)
        self.assertTrue(result["signals"]["public_api_unshipped_added"])

    def test_public_api_unshipped_added_ignores_nullable_header(self) -> None:
        # A diff that ONLY adds `#nullable enable` (e.g. a brand-new
        # but empty Unshipped.txt) must NOT trip the signal.
        patch = "@@ -0,0 +1,2 @@\n+#nullable enable\n+\n"
        files = [
            _file(
                "src/Middleware/Foo/src/PublicAPI.Unshipped.txt",
                status="added",
                patch=patch,
            )
        ]
        result = compute_signals(_pr(), files)
        self.assertFalse(result["signals"]["public_api_unshipped_added"])

    def test_public_api_unshipped_added_ignores_blank_lines(self) -> None:
        patch = "@@ -1,1 +1,3 @@\n #nullable enable\n+\n+\n"
        files = [_file("src/Foo/src/PublicAPI.Unshipped.txt", patch=patch)]
        result = compute_signals(_pr(), files)
        self.assertFalse(result["signals"]["public_api_unshipped_added"])

    def test_public_api_unshipped_added_under_multitarget_subfolder(self) -> None:
        # Several aspnetcore packages have per-TFM subfolders:
        # src/Caching/StackExchangeRedis/src/PublicAPI/net10.0/PublicAPI.Unshipped.txt
        path = "src/Caching/StackExchangeRedis/src/PublicAPI/net10.0/PublicAPI.Unshipped.txt"
        patch = "@@\n+Microsoft.Extensions.Caching.StackExchangeRedis.Foo -> void\n"
        result = compute_signals(_pr(), [_file(path, patch=patch)])
        self.assertTrue(result["signals"]["public_api_unshipped_added"])

    def test_breaking_api_removal_from_shipped(self) -> None:
        patch = (
            "@@ -3,5 +3,4 @@\n"
            " Microsoft.AspNetCore.Routing.RouteBuilder\n"
            "-Microsoft.AspNetCore.Routing.RouteBuilder.MapDelete(...) -> Microsoft.AspNetCore.Routing.RouteHandlerBuilder!\n"
            " Microsoft.AspNetCore.Routing.RouteBuilder.MapGet(...) -> Microsoft.AspNetCore.Routing.RouteHandlerBuilder!\n"
        )
        files = [_file("src/Http/Routing/src/PublicAPI.Shipped.txt", patch=patch)]
        result = compute_signals(_pr(), files)
        self.assertTrue(result["signals"]["breaking_api_removal"])

    def test_breaking_api_removal_ignores_blank_line_removal(self) -> None:
        patch = "@@\n-\n"
        files = [_file("src/Http/Routing/src/PublicAPI.Shipped.txt", patch=patch)]
        result = compute_signals(_pr(), files)
        self.assertFalse(result["signals"]["breaking_api_removal"])

    def test_obsolete_attribute_added(self) -> None:
        patch = "@@\n+    [Obsolete(\"Use AddBar instead.\")]\n+    public void AddFoo() {}\n"
        files = [_file("src/Foo/src/FooExtensions.cs", patch=patch)]
        result = compute_signals(_pr(), files)
        self.assertTrue(result["signals"]["obsolete_attribute_added"])

    def test_obsolete_attribute_long_form(self) -> None:
        patch = "@@\n+    [ObsoleteAttribute]\n"
        files = [_file("src/Foo/src/FooExtensions.cs", patch=patch)]
        result = compute_signals(_pr(), files)
        self.assertTrue(result["signals"]["obsolete_attribute_added"])

    def test_obsolete_in_test_project_is_ignored(self) -> None:
        patch = "@@\n+    [Obsolete(\"only for tests\")]\n"
        for path in [
            "src/Foo/test/FooTests.cs",
            "src/Foo/tests/FooTests.cs",
            "src/Foo/testassets/Helper.cs",
            "src/Foo/perf/FooPerf.cs",
        ]:
            with self.subTest(path=path):
                files = [_file(path, patch=patch)]
                result = compute_signals(_pr(), files)
                self.assertFalse(
                    result["signals"]["obsolete_attribute_added"],
                    msg=f"path {path!r} should be excluded from obsolete_attribute_added",
                )

    def test_experimental_attribute_added(self) -> None:
        patch = "@@\n+[Experimental(\"ASPCORE001\")]\n+public class FooClient {}\n"
        files = [_file("src/Foo/src/FooClient.cs", patch=patch)]
        result = compute_signals(_pr(), files)
        self.assertTrue(result["signals"]["experimental_attribute_added"])

    def test_new_public_type_class(self) -> None:
        patch = "@@\n+namespace Bar;\n+public sealed class MyMiddleware\n+{\n+}\n"
        files = [_file("src/Middleware/Bar/src/MyMiddleware.cs", patch=patch)]
        result = compute_signals(_pr(), files)
        self.assertTrue(result["signals"]["new_public_type_declaration"])

    def test_new_public_type_record(self) -> None:
        patch = "@@\n+public record MyRecord(int Value);\n"
        files = [_file("src/Foo/src/MyRecord.cs", patch=patch)]
        result = compute_signals(_pr(), files)
        self.assertTrue(result["signals"]["new_public_type_declaration"])

    def test_new_public_type_record_struct(self) -> None:
        patch = "@@\n+public record struct Coord(int X, int Y);\n"
        files = [_file("src/Foo/src/Coord.cs", patch=patch)]
        result = compute_signals(_pr(), files)
        self.assertTrue(result["signals"]["new_public_type_declaration"])

    def test_new_public_type_interface(self) -> None:
        patch = "@@\n+public interface IFooService\n+{\n+}\n"
        files = [_file("src/Foo/src/IFooService.cs", patch=patch)]
        result = compute_signals(_pr(), files)
        self.assertTrue(result["signals"]["new_public_type_declaration"])

    def test_new_public_type_internal_not_matched(self) -> None:
        patch = "@@\n+internal class Helper { }\n"
        files = [_file("src/Foo/src/Helper.cs", patch=patch)]
        result = compute_signals(_pr(), files)
        self.assertFalse(result["signals"]["new_public_type_declaration"])

    def test_diff_scan_skipped_when_patch_missing(self) -> None:
        # File is too large to return a patch — must be flagged as
        # a conservative gating signal, NOT silently swallowed.
        files = [
            _file(
                "src/Foo/src/PublicAPI.Unshipped.txt",
                status="modified",
                patch=None,
            )
        ]
        result = compute_signals(_pr(), files)
        self.assertTrue(result["signals"]["diff_scan_skipped_due_to_missing_patch"])
        # The original signal must NOT be set (we couldn't verify it).
        self.assertFalse(result["signals"]["public_api_unshipped_added"])

    def test_diff_header_lines_do_not_trigger_signals(self) -> None:
        # `+++ b/src/...PublicAPI.Unshipped.txt` is a diff header
        # line and must NOT be counted as an "added" payload line.
        patch = (
            "--- a/src/Foo/src/PublicAPI.Unshipped.txt\n"
            "+++ b/src/Foo/src/PublicAPI.Unshipped.txt\n"
            "@@\n"
            " #nullable enable\n"
        )
        files = [_file("src/Foo/src/PublicAPI.Unshipped.txt", patch=patch)]
        result = compute_signals(_pr(), files)
        self.assertFalse(result["signals"]["public_api_unshipped_added"])

    def test_break_on_first_match_does_not_skip_other_files(self) -> None:
        # If two files both match the same signal, both should
        # contribute evidence (one per file).
        patch = "@@\n+Microsoft.Foo.Bar -> void\n"
        files = [
            _file("src/Foo/src/PublicAPI.Unshipped.txt", patch=patch),
            _file("src/Bar/src/PublicAPI.Unshipped.txt", patch=patch),
        ]
        result = compute_signals(_pr(), files)
        self.assertTrue(result["signals"]["public_api_unshipped_added"])
        ev = result["evidence"]["public_api_unshipped_added"]
        self.assertEqual(len(ev), 2)
        self.assertEqual(
            sorted(e["file"] for e in ev),
            [
                "src/Bar/src/PublicAPI.Unshipped.txt",
                "src/Foo/src/PublicAPI.Unshipped.txt",
            ],
        )


# ============================================================
# Group C: PR-body triggers
# ============================================================
class BodyTriggerTests(unittest.TestCase):
    def test_user_facing_section_h2(self) -> None:
        result = compute_signals(_pr(body="## User-facing\nUse it like this."), [])
        self.assertTrue(result["signals"]["pr_body_has_user_facing_section"])

    def test_user_facing_section_variant_headings(self) -> None:
        for body in [
            "### Usage\n",
            "## How to use\n",
            "# Public API\n",
            "##### Breaking change\n",
        ]:
            with self.subTest(body=body):
                result = compute_signals(_pr(body=body), [])
                self.assertTrue(result["signals"]["pr_body_has_user_facing_section"])

    def test_breaking_change_marker(self) -> None:
        result = compute_signals(_pr(body="This is a Breaking-Change for routing."), [])
        self.assertTrue(result["signals"]["pr_body_has_breaking_change_marker"])

    def test_deprecation_marker_obsolete(self) -> None:
        result = compute_signals(_pr(body="The AddFoo extension is obsoleted."), [])
        self.assertTrue(result["signals"]["pr_body_has_deprecation_marker"])

    def test_deprecation_marker_removed_phrase(self) -> None:
        result = compute_signals(_pr(body="The AddBar API has been removed."), [])
        self.assertTrue(result["signals"]["pr_body_has_deprecation_marker"])

    def test_body_none_is_safe(self) -> None:
        result = compute_signals(_pr(body=""), [])
        self.assertFalse(result["signals"]["pr_body_has_user_facing_section"])
        self.assertFalse(result["signals"]["pr_body_has_breaking_change_marker"])


# ============================================================
# Group D: PR-label triggers
# ============================================================
class LabelTriggerTests(unittest.TestCase):
    def test_breaking_change_label_simple(self) -> None:
        result = compute_signals(_pr(labels=["breaking-change"]), [])
        self.assertTrue(result["signals"]["pr_label_breaking_change"])

    def test_breaking_change_label_compound(self) -> None:
        for name in ["api: breaking", "kind:breaking", "breaking"]:
            with self.subTest(label=name):
                result = compute_signals(_pr(labels=[name]), [])
                self.assertTrue(result["signals"]["pr_label_breaking_change"])

    def test_new_api_label_api_needs_review(self) -> None:
        result = compute_signals(_pr(labels=["api-needs-review"]), [])
        self.assertTrue(result["signals"]["pr_label_new_api"])

    def test_new_api_label_new_api_variant(self) -> None:
        result = compute_signals(_pr(labels=["new-api"]), [])
        self.assertTrue(result["signals"]["pr_label_new_api"])

    def test_unrelated_label_does_not_match(self) -> None:
        result = compute_signals(_pr(labels=["area-routing", "needs-review"]), [])
        self.assertFalse(result["signals"]["pr_label_new_api"])
        self.assertFalse(result["signals"]["pr_label_breaking_change"])


# ============================================================
# Advisory: only_test_or_build_changes
# ============================================================
class AdvisoryTests(unittest.TestCase):
    def test_only_tests_in_src(self) -> None:
        files = [
            _file("src/Foo/test/FooTests.cs"),
            _file("src/Bar/tests/BarTests.cs"),
        ]
        result = compute_signals(_pr(), files)
        self.assertTrue(result["signals"]["only_test_or_build_changes"])

    def test_only_build_eng_files(self) -> None:
        files = [
            _file("eng/Versions.props"),
            _file("Directory.Build.props"),
            _file(".github/workflows/some.yml"),
        ]
        result = compute_signals(_pr(), files)
        self.assertTrue(result["signals"]["only_test_or_build_changes"])

    def test_mixed_src_and_test_is_false(self) -> None:
        files = [
            _file("src/Foo/test/FooTests.cs"),
            _file("src/Foo/src/Foo.cs"),
        ]
        result = compute_signals(_pr(), files)
        self.assertFalse(result["signals"]["only_test_or_build_changes"])

    def test_empty_file_list_is_false(self) -> None:
        # An empty PR (shouldn't happen in practice, but defensive).
        result = compute_signals(_pr(), [])
        self.assertFalse(result["signals"]["only_test_or_build_changes"])

    def test_advisory_is_excluded_from_triggered_signals(self) -> None:
        files = [_file("src/Foo/test/FooTests.cs")]
        result = compute_signals(_pr(), files)
        self.assertNotIn("only_test_or_build_changes", result["triggered_signals"])


# ============================================================
# Recommendation logic
# ============================================================
class RecommendationTests(unittest.TestCase):
    def test_no_signals_gives_docs_optional(self) -> None:
        files = [_file("src/Foo/src/InternalHelper.cs", patch="@@\n+internal class X {}\n")]
        result = compute_signals(_pr(), files)
        self.assertEqual(result["recommendation"], "docs_optional")
        self.assertEqual(result["signal_count"], 0)
        self.assertEqual(result["triggered_signals"], [])

    def test_single_gating_signal_gives_docs_required(self) -> None:
        files = [
            _file(
                "src/Foo/src/PublicAPI.Unshipped.txt",
                patch="@@\n+Microsoft.Foo.Bar() -> void\n",
            )
        ]
        result = compute_signals(_pr(), files)
        self.assertEqual(result["recommendation"], "docs_required")
        self.assertGreaterEqual(result["signal_count"], 1)

    def test_hard_exclusion_overrides_positive_signal(self) -> None:
        pr = _pr(title="[release/10.0] Cherry-pick fix")
        files = [
            _file(
                "src/Foo/src/PublicAPI.Unshipped.txt",
                patch="@@\n+Microsoft.Foo.Bar() -> void\n",
            )
        ]
        result = compute_signals(pr, files)
        self.assertEqual(result["recommendation"], "docs_optional")
        self.assertIn("backport_title_marker", result["excluded_by"])
        # Diagnostic transparency: gating signal still recorded for audit.
        self.assertIn("public_api_unshipped_added", result["triggered_signals"])

    def test_evidence_only_contains_triggered_signals(self) -> None:
        files = [
            _file(
                "src/Foo/src/PublicAPI.Unshipped.txt",
                patch="@@\n+Microsoft.Foo.Bar() -> void\n",
            )
        ]
        result = compute_signals(_pr(), files)
        for k in result["evidence"]:
            self.assertTrue(
                result["signals"].get(k, False) or k in (result["excluded_by"] or []),
                msg=f"evidence carries an entry for {k!r} which is not a triggered signal",
            )

    def test_source_pr_number_round_trips(self) -> None:
        result = compute_signals(_pr(number=98765), [])
        self.assertEqual(result["source_pr_number"], 98765)

    def test_recommendation_field_present_even_with_no_data(self) -> None:
        result = compute_signals(_pr(), [])
        self.assertIn("recommendation", result)
        self.assertEqual(result["recommendation"], "docs_optional")
        self.assertEqual(result["excluded_by"], [])
        self.assertEqual(result["triggered_signals"], [])


# ============================================================
# CLI entry point
# ============================================================
class MainTests(unittest.TestCase):
    def test_main_writes_valid_json(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            pr_path = os.path.join(td, "pr.json")
            files_path = os.path.join(td, "files.json")
            out_path = os.path.join(td, "out.json")
            with open(pr_path, "w", encoding="utf-8") as f:
                json.dump(_pr(number=42, body="## Usage\nthings"), f)
            with open(files_path, "w", encoding="utf-8") as f:
                json.dump(
                    [
                        {
                            "filename": "src/Foo/src/PublicAPI.Unshipped.txt",
                            "status": "modified",
                            "patch": "@@\n+Microsoft.Foo.Bar() -> void\n",
                        }
                    ],
                    f,
                )
            rc = main(["compute_signals.py", pr_path, files_path, out_path])
            self.assertEqual(rc, 0)
            with open(out_path, encoding="utf-8") as f:
                doc = json.load(f)
            self.assertEqual(doc["source_pr_number"], 42)
            self.assertEqual(doc["recommendation"], "docs_required")
            self.assertIn("public_api_unshipped_added", doc["triggered_signals"])

    def test_main_arg_count_errors_clearly(self) -> None:
        rc = main(["compute_signals.py", "only", "two"])
        self.assertEqual(rc, 2)


if __name__ == "__main__":
    unittest.main()
