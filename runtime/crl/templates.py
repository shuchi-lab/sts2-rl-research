"""Template selection, scope checks and the reproducible source-run split.

Selection is explicit and reviewable: every candidate template is either selected or listed
with the reason it was excluded, and every run carries the report evidence used to decide
whether the source run was a verified victory.

Run-victory rule (see CLAUDE_PROGRESS.md, milestone 0): a run counts as a verified victory iff
its replay report certifies the final outcome AND the cohort manifest records it as a victory.
The report families certify differently:
  * ``final_victory_verified == true``                       (MCP live runs 7QK4M2RA, SCSC2A1X)
  * ``strict_pass and final_outcome_verified``                (ABB runs; true for defeats too)
  * ``full_run_match`` plus a successful final comparison     (manual runs)
                                                              (manual runs)
``final_hp`` is never used: verified wins can report hp 0 at the game-over screen.
"""

from __future__ import annotations

import gzip
import hashlib
import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from . import paths

SELECTION_SCHEMA = "crl.template_selection/v2"


@dataclass
class Selection:
    selected: list[dict]  # full template records
    audit: dict  # json-serialisable review document
    splits: dict  # {"train": [template_id...], "val": [...], "test": [...]} plus run lists

    def templates_for(self, split: str) -> list[dict]:
        ids = set(self.splits[split]["template_ids"])
        return [t for t in self.selected if t["template_id"] in ids]


def load_templates(path: Path = paths.TEMPLATES) -> list[dict]:
    with gzip.open(path, "rt", encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def run_victory_evidence(report: dict, manifest_outcome: str | None) -> dict:
    """The report keys used and the verdict, for one run."""
    ev: dict[str, Any] = {"manifest_outcome": manifest_outcome}
    certified = None
    if "final_victory_verified" in report:
        ev["final_victory_verified"] = report["final_victory_verified"]
        certified = report["final_victory_verified"] is True
        ev["certifying_key"] = "final_victory_verified"
    elif "final_outcome_verified" in report:
        ev["final_outcome_verified"] = report["final_outcome_verified"]
        ev["strict_pass"] = report.get("strict_pass")
        certified = report["final_outcome_verified"] is True and report.get("strict_pass") is True
        ev["certifying_key"] = "strict_pass + final_outcome_verified"
    elif isinstance(report.get("final_comparison"), dict):
        fc = report["final_comparison"]
        ev["final_comparison"] = {
            "differences": len(fc.get("differences") or []),
            "native_terminated": fc.get("native_terminated"),
            "expected_victory": fc.get("expected_victory"),
        }
        ev["full_run_match"] = report.get("full_run_match")
        certified = (report.get("full_run_match") is True and fc.get("differences") == []
                     and fc.get("native_terminated") is True)
        ev["certifying_key"] = "full_run_match + final_comparison"
        if fc.get("expected_victory") is not True and manifest_outcome == "victory":
            certified = False  # a win must be asserted by the comparison itself
    else:
        ev["certifying_key"] = None
        certified = False
    ev["outcome_certified"] = bool(certified)
    ev["verified_victory"] = bool(certified) and manifest_outcome == "victory"
    if "final_hp" in report:
        ev["final_hp_ignored"] = report["final_hp"]
    return ev


def load_roster(path: Path = paths.ROSTER) -> set[str]:
    return {r["entry_id"] for r in paths.read_json(path)["rows"]}


def select(
    *,
    scope: str = "first",
    require_run_victory: bool = True,
    split_seed: int = 20261001,
    fractions: tuple[float, float, float] = (0.70, 0.15, 0.15),
    min_runs_per_split: int = 2,
    templates_path: Path = paths.TEMPLATES,
    catalog: dict | None = None,
) -> Selection:
    if paths.INPUT_BUNDLE is not None:
        return select_frozen(paths.INPUT_BUNDLE, scope, require_run_victory, split_seed, fractions, min_runs_per_split)
    if scope not in ("first", "act1"):
        raise ValueError("scope must be 'first' (combat_ordinal 0) or 'act1' (act_index 0)")
    catalog = catalog if catalog is not None else paths.read_json(paths.CATALOG)
    card_entry = {c["Id"]: c["Entry"] for c in catalog["cards"]}
    roster = load_roster()
    manifest = paths.read_json(paths.COHORT_MANIFEST)
    manifest_runs = {r["seed"]: r for r in manifest["runs"]}

    rows = load_templates(templates_path)
    in_scope = [
        t for t in rows
        if (t["source"]["combat_ordinal"] == 0 if scope == "first" else t["source"]["act_index"] == 0)
    ]
    run_evidence: dict[str, dict] = {}
    for t in in_scope:
        rid = t["source"]["run_id"]
        if rid in run_evidence:
            continue
        rp = Path(t["source"]["report_path"])
        rep = paths.read_json(rp)
        ev = run_victory_evidence(rep, manifest_runs.get(rid, {}).get("outcome"))
        ev["report_path"] = str(rp)
        ev["report_sha256_now"] = paths.sha256_file(rp)
        ev["report_sha256_template"] = t["source"].get("report_sha256")
        ev["report_hash_matches_template"] = ev["report_sha256_now"] == ev["report_sha256_template"]
        manifest_run = manifest_runs.get(rid, {})
        ev["manifest_scope"] = manifest_run.get("scope")
        ev["report_sha256_manifest"] = manifest_run.get("report_sha256")
        ev["report_hash_matches_manifest"] = ev["report_sha256_now"] == ev["report_sha256_manifest"]
        run_evidence[rid] = ev

    selected, excluded = [], []
    for t in in_scope:
        rid = t["source"]["run_id"]
        ei = t["engine_input"]
        reasons = []
        if t.get("status") != "supported":
            reasons.append(f"template_status_{t.get('status')}")
        if ei.get("Character") != "Ironclad":
            reasons.append(f"character_{ei.get('Character')}")
        if ei["PlayerHp"] <= 0:
            reasons.append("entry_hp_not_positive")
        outside = sorted({card_entry.get(c["DefId"], f"id{c['DefId']}") for c in ei["Deck"]} - roster)
        if outside:
            reasons.append("deck_card_outside_roster:" + ",".join(outside))
        if not run_evidence[rid]["report_hash_matches_template"]:
            reasons.append("report_changed_since_import")
        if not run_evidence[rid]["report_hash_matches_manifest"]:
            reasons.append("report_does_not_match_cohort_manifest")
        if require_run_victory and run_evidence[rid]["manifest_scope"] != "complete":
            reasons.append("source_run_not_complete")
        if require_run_victory and not run_evidence[rid]["verified_victory"]:
            reasons.append("source_run_not_verified_victory")
        entry = {
            "template_id": t["template_id"],
            "run_id": rid,
            "combat_ordinal": t["source"]["combat_ordinal"],
            "total_floor": t["source"]["total_floor"],
            "encounter_id": ei["EncounterId"],
            "entry_hp": ei["PlayerHp"],
            "entry_max_hp": ei["PlayerMaxHp"],
            "deck_size": len(ei["Deck"]),
            "template_sha256": t.get("template_sha256"),
        }
        if reasons:
            excluded.append({**entry, "reasons": reasons})
        else:
            selected.append(t)

    runs = sorted({t["source"]["run_id"] for t in selected})
    splits = split_runs(runs, split_seed, fractions, min_runs_per_split)
    split_doc: dict[str, Any] = {}
    for name in ("train", "val", "test"):
        rset = set(splits[name])
        split_doc[name] = {
            "run_ids": splits[name],
            "template_ids": sorted(t["template_id"] for t in selected if t["source"]["run_id"] in rset),
        }
    audit = {
        "schema": SELECTION_SCHEMA,
        "scope": scope,
        "require_run_victory": require_run_victory,
        "split_seed": split_seed,
        "fractions": list(fractions),
        "min_runs_per_split": min_runs_per_split,
        "templates_file_sha256": paths.sha256_file(templates_path),
        "roster_sha256": paths.sha256_file(paths.ROSTER),
        "roster_size": len(roster),
        "cohort_manifest_sha256": paths.sha256_file(paths.COHORT_MANIFEST),
        "candidates_in_scope": len(in_scope),
        "selected_count": len(selected),
        "excluded_count": len(excluded),
        "excluded": excluded,
        "run_evidence": run_evidence,
        "victory_rule": __doc__.split("Run-victory rule")[1].strip(),
        "splits": split_doc,
    }
    audit["selection_digest"] = paths.digest({k: v for k, v in audit.items() if k != "run_evidence"})
    return Selection(selected=selected, audit=audit, splits=split_doc)


def select_frozen(bundle: Path, scope: str, require_run_victory: bool, split_seed: int,
                  fractions: tuple[float, float, float], min_runs: int) -> Selection:
    """Verify and load an already audited immutable subset; never silently change its split."""
    manifest = paths.read_json(bundle / "manifest.json")
    if manifest.get("schema") != "crl.frozen_inputs/v1":
        raise ValueError("unknown frozen-input manifest schema")
    for name, digest in manifest["files"].items():
        if Path(name).name != name or paths.sha256_file(bundle / name) != digest:
            raise ValueError(f"frozen input file changed: {name}")
    audit = paths.read_json(bundle / "selection_audit.json")
    requested = (scope, require_run_victory, split_seed, list(fractions), min_runs)
    recorded = tuple(audit[k] for k in ("scope", "require_run_victory", "split_seed", "fractions", "min_runs_per_split"))
    if requested != recorded:
        raise ValueError("requested selection differs from the frozen input bundle; prepare a new audited bundle")
    if audit["selection_digest"] != manifest["selection_digest"]:
        raise ValueError("frozen selection digest mismatch")
    selected = load_templates(bundle / "templates.jsonl.gz")
    expected = {tid for s in audit["splits"].values() for tid in s["template_ids"]}
    if {t["template_id"] for t in selected} != expected or len(selected) != len(expected):
        raise ValueError("frozen input templates do not match the audited split")
    return Selection(selected, audit, audit["splits"])


def split_runs(
    runs: list[str], seed: int, fractions: tuple[float, float, float], min_per_split: int
) -> dict[str, list[str]]:
    """Deterministic split of source runs: order by sha256(seed|run), then cut."""
    n = len(runs)
    n_val = max(min_per_split, round(fractions[1] * n))
    n_test = max(min_per_split, round(fractions[2] * n))
    n_train = n - n_val - n_test
    if n_train < min_per_split:
        raise ValueError(
            f"{n} eligible runs cannot give >= {min_per_split} runs to every split "
            f"(train {n_train}, val {n_val}, test {n_test})"
        )
    order = sorted(runs, key=lambda r: hashlib.sha256(f"crl/split/v1|{seed}|{r}".encode()).hexdigest())
    return {
        "test": sorted(order[:n_test]),
        "val": sorted(order[n_test:n_test + n_val]),
        "train": sorted(order[n_test + n_val:]),
    }


def save(selection: Selection, out_dir: Path) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)
    paths.write_json(out_dir / "selection_audit.json", selection.audit)
    paths.write_json(out_dir / "splits.json", {
        "schema": SELECTION_SCHEMA,
        "selection_digest": selection.audit["selection_digest"],
        **selection.splits,
    })
