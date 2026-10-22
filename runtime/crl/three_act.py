"""Three-act source selection: verified winning runs plus complete verified late Act 3 defeats.

A source run qualifies iff ALL of the evidence checks below pass, and then either
  * it is a verified VICTORY (cohort ``victory``), or
  * it is a verified DEFEAT whose final room floor lies on one of the last five room floors of
    the final act (cohort ``act3_late_defeat``; boss floor included).
Every combat template of a qualifying run is kept unmodified, including its final loss; a run
with any failing template is excluded as a whole (no combat is ever dropped from a kept run).

Floor layout: act room-floor counts are DERIVED from the complete verified winning histories
(``len`` of each ``map_point_history`` act) and must agree across them; nothing assumes a
universal 48-floor map. A defeat history must repeat the derived lengths for every act before
its last, and its last-act local floor must lie inside that act; anything else is a
``LayoutError`` rather than a guess.

Evidence per run (all recorded in runs.json, failures become exclusion reasons):
  * cohort manifest outcome is victory/defeat, scope ``complete``, character IRONCLAD;
  * current report sha256 == manifest == every template's ``source.report_sha256``;
  * current history sha256 == manifest; the manifest says the history was used;
  * report certifies the final outcome (crl.templates.run_victory_evidence), and a
    ``final_comparison.expected_victory`` must agree with the declared outcome;
  * the history's seed/ascension/build/character/win/abandoned fields corroborate the run, and
    any report or manifest ``history_identity`` equals the history;
  * every template: status supported, engine_input hashes to template_sha256 (collector
    algorithm: sorted keys, compact, ASCII), source.cohort_manifest_sha256 == current manifest
    hash, Ironclad, positive entry HP, act/floor/room type agree with the history;
  * no hidden deletion: combat ordinals are contiguous, template floors equal the history's
    combat floors, the import report lists the same count with no rejected templates;
  * the final combat matches the declared outcome (won boss on the last floor, or the lost
    combat named by ``killed_by_encounter``).

Splits are frozen before any augmentation: the prior source-run assignment is copied exactly,
new victories go to train, and new late defeats are ordered by
sha256("crl/three_act_split/v1|<seed>|<run>") and cut test, val, then train.
"""

from __future__ import annotations

import argparse
import csv
import gzip
import hashlib
import io
import json
import os
import statistics as pystats
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from . import paths
from .templates import run_victory_evidence

SCHEMA = "crl.three_act_selection/v1"
MODULE_VERSION = "1.0"
OLD_SPLITS = paths.RUNS / "first-v2-e12000-s1" / "splits.json"
IMPORT_REPORT = paths.TEMPLATES.parent / "import_report.json"
REJECTED = paths.TEMPLATES.parent / "templates_rejected.jsonl.gz"
LATE_SPLIT_SEED = 20261002
LAST_N_FLOORS = 5
NO_CAUSE = (None, "NONE.NONE")
ROOM_CLASS = {"monster": "normal", "elite": "elite", "boss": "boss", "event": "event"}
# history map_point_type allowed for each template room class ("unknown" = "?" room)
HISTORY_POINT_TYPES = {"normal": {"monster", "unknown"}, "elite": {"elite"}, "boss": {"boss"}, "event": {"unknown"}}
CSV_COLUMNS = [
    "template_id", "run_id", "source_cohort", "split", "act", "total_floor", "act_floor", "room_type",
    "encounter_id", "encounter_name", "source_combat_won", "entry_hp", "max_hp", "hp_fraction",
    "deck_size", "upgraded_cards", "unique_cards", "active_relics", "used_relics", "potions", "gold",
    "outside_roster_card_entries", "roster_compatible",
    # extra identification columns
    "run_outcome", "combat_ordinal", "is_run_final_combat", "encounter_weak_variant",
    "outside_roster_card_count", "template_sha256",
]
OUTPUT_FILES = ("templates.jsonl.gz", "catalog.json", "selection_audit.json", "splits.json",
                "runs.json", "statistics.json", "templates.csv")


class LayoutError(ValueError):
    """A history's act/floor layout does not fit the derived cohort layout."""


def template_hash(engine_input: dict) -> str:
    """Collector algorithm (cds.common.template_hash): sorted keys, compact, ASCII."""
    return paths.sha256_bytes(json.dumps(engine_input, sort_keys=True, separators=(",", ":")).encode())


# --------------------------------------------------------------------------- floor layout

def act_lengths(history: dict) -> list[int]:
    mph = history.get("map_point_history")
    if not isinstance(mph, list) or not mph or not all(isinstance(a, list) and a for a in mph):
        raise LayoutError("history map_point_history is not a non-empty list of non-empty acts")
    return [len(a) for a in mph]


def derive_layout(victory_lengths: dict[str, list[int]], last_n: int = LAST_N_FLOORS) -> list[int]:
    """The single act layout shared by every complete verified winning history."""
    layouts = {tuple(v) for v in victory_lengths.values()}
    if not layouts:
        raise LayoutError("no complete verified winning history to derive the floor layout from")
    if len(layouts) != 1:
        raise LayoutError(f"winning histories disagree on act layout: {sorted(layouts)}")
    layout = list(layouts.pop())
    if len(layout) != 3 or layout[-1] < last_n:
        raise LayoutError(f"unexpected winning layout {layout}: need three acts and >= {last_n} final-act floors")
    return layout


def locate(total_floor: int, lengths: list[int]) -> tuple[int, int]:
    """(1-based act, act-local floor) of a total floor within the given act lengths."""
    if total_floor < 1:
        raise LayoutError(f"floor {total_floor} < 1")
    start = 0
    for i, n in enumerate(lengths):
        if total_floor <= start + n:
            return i + 1, total_floor - start
        start += n
    raise LayoutError(f"floor {total_floor} beyond layout {lengths}")


def classify_final_floor(run_lengths: list[int], layout: list[int], last_n: int = LAST_N_FLOORS) -> dict:
    """Where a run ended relative to the derived layout; raises on any unexpected shape."""
    if not run_lengths or len(run_lengths) > len(layout):
        raise LayoutError(f"run act lengths {run_lengths} do not fit layout {layout}")
    k = len(run_lengths)
    if run_lengths[:-1] != layout[:k - 1]:
        raise LayoutError(f"prior acts {run_lengths[:-1]} differ from derived layout {layout[:k - 1]}")
    local = run_lengths[-1]
    if not 1 <= local <= layout[k - 1]:
        raise LayoutError(f"act {k} local floor {local} outside 1..{layout[k - 1]}")
    first = layout[-1] - last_n + 1
    return {
        "final_act": k,
        "final_act_floor": local,
        "final_total_floor": sum(run_lengths),
        "complete_layout": run_lengths == layout,
        "in_final_act_last_floors": k == len(layout) and local >= first,
        "final_act_window": [first, layout[-1]],
    }


# --------------------------------------------------------------------------- sources

@dataclass
class Sources:
    templates: list[dict]
    catalog: dict
    manifest: dict
    roster: set[str]
    old_splits: dict
    import_report: dict
    rejected: list[dict]
    reports: dict[str, tuple[dict | None, str | None]]  # run -> (report, current sha256)
    histories: dict[str, tuple[dict | None, str | None]]
    hashes: dict[str, str]
    source_paths: dict[str, str]
    catalog_bytes: bytes = field(repr=False, default=b"")


def _read_gz_jsonl(path: Path) -> list[dict]:
    with gzip.open(path, "rt", encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def _read_hashed(path: str | None) -> tuple[dict | None, str | None]:
    if not path or not Path(path).is_file():
        return None, None
    data = Path(path).read_bytes()
    return json.loads(data), paths.sha256_bytes(data)


def load_sources(
    *,
    templates_path: Path = paths.TEMPLATES,
    catalog_path: Path = paths.CATALOG,
    manifest_path: Path = paths.COHORT_MANIFEST,
    roster_path: Path = paths.ROSTER,
    old_splits_path: Path = OLD_SPLITS,
    import_report_path: Path = IMPORT_REPORT,
    rejected_path: Path = REJECTED,
) -> Sources:
    if paths.INPUT_BUNDLE is not None:
        raise RuntimeError("three-act selection reads the original sources; unset STS2_RL_INPUT_BUNDLE")
    src_paths = {"templates": templates_path, "catalog": catalog_path, "cohort_manifest": manifest_path,
                 "roster": roster_path, "old_splits": old_splits_path, "import_report": import_report_path,
                 "templates_rejected": rejected_path}
    hashes = {k: paths.sha256_file(p) for k, p in src_paths.items()}
    manifest = paths.read_json(manifest_path)
    reports, histories = {}, {}
    for r in manifest["runs"]:
        reports[r["seed"]] = _read_hashed(r.get("report"))
        histories[r["seed"]] = _read_hashed(r.get("history"))
    catalog_bytes = Path(catalog_path).read_bytes()
    return Sources(
        templates=_read_gz_jsonl(templates_path),
        catalog=json.loads(catalog_bytes),
        manifest=manifest,
        roster={r["entry_id"] for r in paths.read_json(roster_path)["rows"]},
        old_splits=paths.read_json(old_splits_path),
        import_report=paths.read_json(import_report_path),
        rejected=_read_gz_jsonl(rejected_path),
        reports=reports,
        histories=histories,
        hashes=hashes,
        source_paths={k: str(p) for k, p in src_paths.items()},
        catalog_bytes=catalog_bytes,
    )


# --------------------------------------------------------------------------- per-run evidence

def _history_value(history: dict, key: str) -> Any:
    if key == "character":
        players = history.get("players") or []
        return players[0].get("character") if len(players) == 1 else None
    return history.get(key)


def _identity_mismatches(identity: dict, history: dict) -> list[str]:
    return sorted(k for k, v in identity.items() if _history_value(history, k) != v)


def _combat_points(history: dict) -> dict[int, dict]:
    """total floor -> {map_point_type, encounter names} for floors holding an encounter room."""
    out = {}
    floor = 0
    for act in history["map_point_history"]:
        for point in act:
            floor += 1
            encs = [r.get("model_id") for r in point.get("rooms") or []
                    if str(r.get("model_id", "")).startswith("ENCOUNTER.")]
            if encs:
                out[floor] = {"map_point_type": point.get("map_point_type"), "encounters": encs}
    return out


def evaluate_run(run_id: str, src: Sources, templates: list[dict]) -> dict:
    """Evidence record and exclusion problems for one cohort-manifest run (no floor rule yet)."""
    mrun = next(r for r in src.manifest["runs"] if r["seed"] == run_id)
    outcome = mrun.get("outcome")
    report, report_sha = src.reports.get(run_id, (None, None))
    history, history_sha = src.histories.get(run_id, (None, None))
    problems: list[str] = []
    rec: dict[str, Any] = {
        "run_id": run_id,
        "manifest_outcome": outcome,
        "manifest_scope": mrun.get("scope"),
        "recorder": mrun.get("recorder"),
        "ascension": mrun.get("ascension"),
        "report_path": mrun.get("report"),
        "report_sha256_manifest": mrun.get("report_sha256"),
        "report_sha256_now": report_sha,
        "history_path": mrun.get("history"),
        "history_sha256_manifest": mrun.get("history_sha256"),
        "history_sha256_now": history_sha,
        "template_count": len(templates),
    }
    if outcome not in ("victory", "defeat"):
        problems.append(f"manifest_outcome_not_final:{outcome}")
    if mrun.get("scope") != "complete":
        problems.append(f"manifest_scope_not_complete:{mrun.get('scope')}")
    if mrun.get("character") != "IRONCLAD":
        problems.append(f"manifest_character_{mrun.get('character')}")

    # report
    if report is None:
        problems.append("report_missing")
    else:
        if report_sha != mrun.get("report_sha256"):
            problems.append("report_sha256_mismatch_manifest")
        tpl_report = sorted({(t["source"].get("report_path"), t["source"].get("report_sha256")) for t in templates})
        rec["report_refs_in_templates"] = [list(x) for x in tpl_report]
        if any(sha != report_sha for _, sha in tpl_report):
            problems.append("report_sha256_mismatch_template")
        if any(p != mrun.get("report") for p, _ in tpl_report):
            problems.append("report_path_mismatch_template")
        if "seed" in report and report["seed"] != run_id:
            problems.append(f"report_seed_{report['seed']}")
        if "character" in report and report["character"] != "IRONCLAD":
            problems.append(f"report_character_{report['character']}")
        ev = run_victory_evidence(report, outcome)
        if isinstance(report.get("final_comparison"), dict):
            fc = report["final_comparison"]
            asserted = fc.get("expected_victory") is True
            ev["expected_victory_matches_outcome"] = asserted == (outcome == "victory")
            if not ev["expected_victory_matches_outcome"]:
                problems.append("final_comparison_expected_victory_contradicts_outcome")
            if "native_victory" in fc and (fc["native_victory"] is True) != (outcome == "victory"):
                problems.append("final_comparison_native_victory_contradicts_outcome")
            ref = fc.get("reference")
            if isinstance(ref, dict):
                ev["final_comparison_reference"] = {k: ref.get(k) for k in ("phase", "act", "floor")}
        for key in ("strict_pass", "history_identity_verified", "final_deck_and_enchantments_match_history",
                    "replay_scope", "prefix_pass"):
            if key in report:
                ev[key] = report[key]
        rec["outcome_evidence"] = ev
        if not ev["outcome_certified"]:
            problems.append("report_does_not_certify_final_outcome")

    # history
    if history is None:
        problems.append("history_missing")
    else:
        if history_sha != mrun.get("history_sha256"):
            problems.append("history_sha256_mismatch_manifest")
        if mrun.get("history_used") is not True:
            problems.append("manifest_history_not_used")
        build = str(src.manifest.get("game_version", "")).split(" ")[0]
        hid = {k: _history_value(history, k) for k in
               ("seed", "ascension", "build_id", "game_mode", "character", "win", "was_abandoned",
                "killed_by_encounter", "killed_by_event")}
        rec["history_identity"] = hid
        if hid["seed"] != run_id:
            problems.append(f"history_seed_{hid['seed']}")
        if hid["character"] != "CHARACTER.IRONCLAD":
            problems.append(f"history_character_{hid['character']}")
        if hid["build_id"] != build:
            problems.append(f"history_build_{hid['build_id']}_expected_{build}")
        if str(hid["ascension"]) != str(mrun.get("ascension")):
            problems.append("history_ascension_mismatch_manifest")
        if hid["was_abandoned"] is not False:
            problems.append("history_abandoned_or_unknown")
        if hid["win"] is not (outcome == "victory"):
            problems.append("history_win_contradicts_outcome")
        links = []
        if report is not None and isinstance(report.get("history_identity"), dict):
            bad = _identity_mismatches(report["history_identity"], history)
            if bad:
                problems.append("report_history_identity_mismatch:" + ",".join(bad))
            if report.get("history_identity_verified") is True:
                links.append("report.history_identity_verified")
        if isinstance(mrun.get("history_identity"), dict):
            bad = _identity_mismatches(mrun["history_identity"], history)
            if bad:
                problems.append("manifest_history_identity_mismatch:" + ",".join(bad))
        xc = mrun.get("history_crosscheck_by_extractor")
        if isinstance(xc, dict) and xc and all(v is True for v in xc.values()):
            links.append("manifest.history_crosscheck_by_extractor")
        if report is not None and report.get("final_deck_and_enchantments_match_history") is True:
            links.append("report.final_deck_and_enchantments_match_history")
        rec["history_link_evidence"] = links
        if not links:
            problems.append("history_not_linked_to_verified_replay")

    # templates and hidden deletion
    tproblems: dict[str, list[str]] = {}
    ordered = sorted(templates, key=lambda t: t["source"]["combat_ordinal"])
    if not ordered:
        problems.append("no_templates")
    if [t["source"]["combat_ordinal"] for t in ordered] != list(range(len(ordered))):
        problems.append("combat_ordinals_not_contiguous")
    for t in ordered:
        s, ei, tp = t["source"], t["engine_input"], []
        if t.get("status") != "supported":
            tp.append(f"template_status_{t.get('status')}")
        if template_hash(ei) != t.get("template_sha256"):
            tp.append("engine_input_hash_mismatch")
        if s.get("cohort_manifest_sha256") != src.hashes["cohort_manifest"]:
            tp.append("cohort_manifest_sha256_mismatch")
        if s.get("cohort_scope") != "complete":
            tp.append(f"template_cohort_scope_{s.get('cohort_scope')}")
        if ei.get("Character") != "Ironclad" or s.get("character") != "IRONCLAD":
            tp.append(f"character_{ei.get('Character')}")
        if not ei.get("PlayerHp", 0) > 0:
            tp.append("entry_hp_not_positive")
        if ei.get("TotalFloor") != s.get("total_floor"):
            tp.append("engine_total_floor_mismatch")
        if s.get("room_type") not in ROOM_CLASS:
            tp.append(f"room_type_{s.get('room_type')}")
        if tp:
            tproblems[t["template_id"]] = tp
    imp = next((r for r in src.import_report.get("runs", []) if r.get("seed") == run_id), None)
    rejected = sum(1 for r in src.rejected if str(r.get("template_id", "")).startswith(run_id + "-")
                   or (r.get("source") or {}).get("run_id") == run_id)
    rec["import_report"] = None if imp is None else {k: imp.get(k) for k in
                                                       ("status", "combats_captured", "templates_supported",
                                                        "templates_rejected")}
    rec["rejected_templates"] = rejected
    if imp is None or imp.get("templates_supported") != len(ordered) or imp.get("templates_rejected") != 0 \
            or imp.get("combats_captured") != len(ordered):
        problems.append("import_report_count_mismatch")
    if rejected:
        problems.append(f"rejected_templates:{rejected}")

    floors: dict[str, dict] = {}
    if history is not None and ordered:
        try:
            lengths = act_lengths(history)
        except LayoutError as e:
            problems.append(f"history_layout:{e}")
            lengths = None
        if lengths is not None:
            rec["history_act_lengths"] = lengths
            rec["history_final_total_floor"] = sum(lengths)
            points = _combat_points(history)
            hist_floors = sorted(points)
            tpl_floors = [t["source"]["total_floor"] for t in ordered]
            rec["history_combat_floors"] = len(hist_floors)
            if tpl_floors != hist_floors:
                problems.append("template_floors_differ_from_history_combat_floors:"
                                + ",".join(map(str, sorted(set(tpl_floors) ^ set(hist_floors)))))
            for t in ordered:
                f, tp = t["source"]["total_floor"], tproblems.setdefault(t["template_id"], [])
                p = points.get(f)
                if p is None:
                    continue
                if len(p["encounters"]) != 1:
                    tp.append("history_floor_has_multiple_encounters")
                    continue
                act, act_floor = locate(f, lengths)
                if t["source"].get("act_index") != act - 1:
                    tp.append("act_index_differs_from_history")
                rc = ROOM_CLASS.get(t["source"].get("room_type"))
                if rc and p["map_point_type"] not in HISTORY_POINT_TYPES[rc]:
                    tp.append(f"room_type_{rc}_vs_history_{p['map_point_type']}")
                floors[t["template_id"]] = {"act": act, "act_floor": act_floor, "encounter_name": p["encounters"][0]}
            tproblems = {k: v for k, v in tproblems.items() if v}
            # final combat must agree with the declared outcome
            last = ordered[-1]
            last_name = floors.get(last["template_id"], {}).get("encounter_name")
            if any(t["source_outcome"].get("player_won") is not True for t in ordered[:-1]):
                problems.append("non_final_combat_not_won")
            killed_enc, killed_evt = hid["killed_by_encounter"], hid["killed_by_event"]
            if outcome == "victory":
                if not (last["source"]["total_floor"] == sum(lengths) and last["source"].get("room_type") == "boss"
                        and last["source_outcome"].get("player_won") is True and killed_enc in NO_CAUSE):
                    problems.append("final_combat_not_won_last_floor_boss")
                rec["final_cause"] = "victory"
            elif outcome == "defeat":
                if killed_enc not in NO_CAUSE:
                    ok = (last["source"]["total_floor"] == sum(lengths) and last_name == killed_enc
                          and last["source_outcome"].get("player_won") is False
                          and last["source_outcome"].get("combat_finished_in_source") is True)
                    if not ok:
                        problems.append("final_lost_combat_does_not_match_killed_by_encounter")
                    rec["final_cause"] = killed_enc
                elif killed_evt not in NO_CAUSE:
                    if last["source_outcome"].get("player_won") is not True:
                        problems.append("final_combat_lost_but_history_names_event")
                    rec["final_cause"] = killed_evt
                else:
                    problems.append("defeat_cause_unrecorded")
    tproblems = {k: v for k, v in tproblems.items() if v}
    if tproblems:
        problems.append(f"templates_failing_checks:{len(tproblems)}")
    rec["template_problems"] = tproblems
    rec["problems"] = problems
    rec["_floors"] = floors
    return rec


# --------------------------------------------------------------------------- splits

def late_sort_key(seed: int, run_id: str) -> str:
    return hashlib.sha256(f"crl/three_act_split/v1|{seed}|{run_id}".encode()).hexdigest()


def assign_splits(old_splits: dict, victories: list[str], late_defeats: list[str], seed: int) -> dict:
    """Run -> split. Old assignments copied exactly; new victories train; new late defeats
    hash-ordered and cut 1 test, 1 val, rest train."""
    old: dict[str, str] = {}
    for name in ("train", "val", "test"):
        for r in old_splits[name]["run_ids"]:
            if r in old:
                raise ValueError(f"old splits assign {r} twice")
            old[r] = name
    qualified = set(victories) | set(late_defeats)
    missing = sorted(set(old) - qualified)
    if missing:
        raise ValueError(f"previously split runs no longer qualify (cannot preserve split): {missing}")
    new_late = [r for r in late_defeats if r not in old]
    if len(new_late) < 3:
        raise ValueError(f"need >= 3 new late defeats for 1 test / 1 val / train, got {len(new_late)}")
    order = sorted(new_late, key=lambda r: late_sort_key(seed, r))
    assignment = dict(old)
    for r in victories:
        assignment.setdefault(r, "train")
    assignment[order[0]] = "test"
    assignment[order[1]] = "val"
    for r in order[2:]:
        assignment[r] = "train"
    return {
        "assignment": dict(sorted(assignment.items())),
        "old_runs": dict(sorted(old.items())),
        "new_victories": sorted(r for r in victories if r not in old),
        "late_defeat_order": [{"run_id": r, "sort_key": late_sort_key(seed, r)} for r in order],
    }


# --------------------------------------------------------------------------- selection

@dataclass
class ThreeActSelection:
    selected: list[dict]  # full, unmodified template records
    audit: dict
    splits: dict  # {"train": {"run_ids", "template_ids", ...}, "val": ..., "test": ...}
    runs: list[dict]  # qualification record for every cohort run
    rows: list[dict]  # flat per-template statistics
    statistics: dict

    def templates_for(self, split: str) -> list[dict]:
        ids = set(self.splits[split]["template_ids"])
        return [t for t in self.selected if t["template_id"] in ids]


def _row(t: dict, run: dict, split: str, floors: dict, card_entry: dict, roster: set[str]) -> dict:
    ei, s = t["engine_input"], t["source"]
    deck = ei["Deck"]
    outside = [card_entry.get(c["DefId"], f"id{c['DefId']}") for c in deck]
    outside = [e for e in outside if e not in roster]
    f = floors[t["template_id"]]
    return {
        "template_id": t["template_id"],
        "run_id": s["run_id"],
        "source_cohort": run["cohort"],
        "split": split,
        "act": f["act"],
        "total_floor": s["total_floor"],
        "act_floor": f["act_floor"],
        "room_type": ROOM_CLASS[s["room_type"]],
        "encounter_id": ei["EncounterId"],
        "encounter_name": f["encounter_name"],
        "source_combat_won": bool(t["source_outcome"].get("player_won")),
        "entry_hp": ei["PlayerHp"],
        "max_hp": ei["PlayerMaxHp"],
        "hp_fraction": round(ei["PlayerHp"] / ei["PlayerMaxHp"], 6),
        "deck_size": len(deck),
        "upgraded_cards": sum(1 for c in deck if c.get("Upgraded")),
        "unique_cards": len({c["DefId"] for c in deck}),
        "active_relics": len(ei["Relics"]),
        "used_relics": len(ei.get("UsedUpRelics") or []),
        "potions": sum(1 for p in ei.get("StartingPotions") or [] if p),
        "gold": ei["Gold"],
        "outside_roster_card_entries": ";".join(sorted(set(outside))),
        "roster_compatible": not outside,
        "run_outcome": run["manifest_outcome"],
        "combat_ordinal": s["combat_ordinal"],
        "is_run_final_combat": s["combat_ordinal"] == run["template_count"] - 1,
        "encounter_weak_variant": bool(ei.get("EncounterWeakVariant")),
        "outside_roster_card_count": len(outside),
        "template_sha256": t["template_sha256"],
    }


def _summary(values: list[float]) -> dict:
    return {"mean": round(pystats.fmean(values), 4), "min": min(values), "max": max(values)} if values else {}


def _group(rows: list[dict], *keys: str) -> dict:
    out: dict[str, dict] = {}
    groups: dict[str, list[dict]] = defaultdict(list)
    for r in rows:
        groups["|".join(str(r[k]) for k in keys)].append(r)
    for k in sorted(groups):
        g = groups[k]
        out[k] = {
            "templates": len(g),
            "runs": len({r["run_id"] for r in g}),
            "source_combat_lost": sum(1 for r in g if not r["source_combat_won"]),
            "roster_compatible": sum(1 for r in g if r["roster_compatible"]),
            "hp_fraction": _summary([r["hp_fraction"] for r in g]),
            "deck_size": _summary([r["deck_size"] for r in g]),
        }
    return out


def build_statistics(rows: list[dict]) -> dict:
    enc: dict[str, dict] = {}
    for name in sorted({r["encounter_name"] for r in rows}):
        g = [r for r in rows if r["encounter_name"] == name]
        enc[name] = {
            "templates": len(g),
            "runs": len({r["run_id"] for r in g}),
            "encounter_keys": sorted({f"{r['encounter_id']}/{'weak' if r['encounter_weak_variant'] else 'normal'}" for r in g}),
            "acts": sorted({r["act"] for r in g}),
            "room_types": sorted({r["room_type"] for r in g}),
            "source_combat_lost": sum(1 for r in g if not r["source_combat_won"]),
            "by_split": dict(sorted(Counter(r["split"] for r in g).items())),
        }
    outside = Counter(e for r in rows for e in r["outside_roster_card_entries"].split(";") if e)
    return {
        "templates": len(rows),
        "runs": len({r["run_id"] for r in rows}),
        "by_source_cohort": _group(rows, "source_cohort"),
        "by_split": _group(rows, "split"),
        "by_split_cohort": _group(rows, "split", "source_cohort"),
        "by_act": _group(rows, "act"),
        "by_room_type": _group(rows, "room_type"),
        "by_act_room_type": _group(rows, "act", "room_type"),
        "by_cohort_act": _group(rows, "source_cohort", "act"),
        "by_encounter": enc,
        "roster": {
            "compatible_templates": sum(1 for r in rows if r["roster_compatible"]),
            "incompatible_templates": sum(1 for r in rows if not r["roster_compatible"]),
            "outside_entry_templates": dict(sorted(Counter(
                e for r in rows for e in set(r["outside_roster_card_entries"].split(";")) if e).items())),
            "outside_entry_card_instances": dict(sorted(outside.items())),
            "caveat": "roster compatibility is a raw deck-card annotation only; it does not establish "
                      "encoder, vocabulary or training readiness",
        },
    }


def provenance(src: Sources) -> dict:
    return {
        "module": "crl.three_act",
        "module_version": MODULE_VERSION,
        "module_sha256": paths.sha256_file(Path(__file__)),
        "schema": SCHEMA,
        "sources": {k: {"path": src.source_paths[k], "sha256": src.hashes[k]} for k in sorted(src.hashes)},
    }


def select_from_sources(src: Sources, *, split_seed: int = LATE_SPLIT_SEED,
                        last_n: int = LAST_N_FLOORS) -> ThreeActSelection:
    if src.import_report.get("templates_sha256") != src.hashes["templates"]:
        raise ValueError("templates file differs from the collector import report")
    if src.import_report.get("cohort_manifest_sha256") != src.hashes["cohort_manifest"]:
        raise ValueError("cohort manifest differs from the one the templates were imported from")
    by_run: dict[str, list[dict]] = defaultdict(list)
    for t in src.templates:
        by_run[t["source"]["run_id"]].append(t)
    manifest_ids = [r["seed"] for r in src.manifest["runs"]]
    if len(set(manifest_ids)) != len(manifest_ids):
        raise ValueError("duplicate run ids in cohort manifest")
    stray = sorted(set(by_run) - set(manifest_ids))
    if stray:
        raise ValueError(f"templates reference runs outside the cohort manifest: {stray}")

    runs = {rid: evaluate_run(rid, src, by_run.get(rid, [])) for rid in manifest_ids}
    victory_lengths = {rid: r["history_act_lengths"] for rid, r in runs.items()
                       if r["manifest_outcome"] == "victory" and not r["problems"]}
    layout = derive_layout(victory_lengths, last_n)
    for rid, r in runs.items():
        r["cohort"], r["floor_classification"] = None, None
        if r["problems"]:
            r["qualified"] = False
            r["exclusion_reasons"] = list(r["problems"])
            if "history_act_lengths" in r:
                r["floor_note"] = ("history floors recorded for reference only; run evidence failed so "
                                   "the floor rule was not applied")
            continue
        fc = classify_final_floor(r["history_act_lengths"], layout, last_n)
        r["floor_classification"] = fc
        if r["manifest_outcome"] == "victory":
            if not fc["complete_layout"]:
                raise LayoutError(f"{rid}: verified victory does not span the derived layout")
            r["cohort"] = "victory"
        elif fc["in_final_act_last_floors"]:
            r["cohort"] = "act3_late_defeat"
        r["qualified"] = r["cohort"] is not None
        r["exclusion_reasons"] = [] if r["qualified"] else [
            f"defeat_outside_final_act_last_{last_n}_floors:act{fc['final_act']}_floor{fc['final_act_floor']}"]

    victories = sorted(rid for rid, r in runs.items() if r["cohort"] == "victory")
    late = sorted(rid for rid, r in runs.items() if r["cohort"] == "act3_late_defeat")
    split_info = assign_splits(src.old_splits, victories, late, split_seed)
    assignment = split_info["assignment"]

    card_entry = {c["Id"]: c["Entry"] for c in src.catalog["cards"]}
    selected = [t for t in src.templates if runs[t["source"]["run_id"]]["qualified"]]
    rows = [_row(t, runs[t["source"]["run_id"]], assignment[t["source"]["run_id"]],
                 runs[t["source"]["run_id"]]["_floors"], card_entry, src.roster) for t in selected]
    names: dict[tuple, set] = defaultdict(set)
    for r in rows:
        names[(r["encounter_id"], r["encounter_weak_variant"])].add(r["encounter_name"])
    clash = {f"{k[0]}/{k[1]}": sorted(v) for k, v in names.items() if len(v) > 1}
    if clash:
        raise ValueError(f"engine encounter keys map to several history encounters: {clash}")

    split_doc: dict[str, Any] = {}
    for name in ("train", "val", "test"):
        rids = sorted(r for r, s in assignment.items() if s == name)
        split_doc[name] = {
            "run_ids": rids,
            "victory_run_ids": [r for r in rids if runs[r]["cohort"] == "victory"],
            "late_defeat_run_ids": [r for r in rids if runs[r]["cohort"] == "act3_late_defeat"],
            "template_ids": sorted(x["template_id"] for x in rows if x["split"] == name),
        }
    split_digest = paths.digest(split_doc)

    run_records = []
    for rid in manifest_ids:
        r = {k: v for k, v in runs[rid].items() if k != "_floors"}
        r["split"] = assignment.get(rid)
        run_records.append(r)
    excluded_templates = [
        {"template_id": t["template_id"], "run_id": t["source"]["run_id"],
         "template_sha256": t.get("template_sha256"),
         "reasons": runs[t["source"]["run_id"]]["exclusion_reasons"]
         + runs[t["source"]["run_id"]]["template_problems"].get(t["template_id"], [])}
        for t in src.templates if not runs[t["source"]["run_id"]]["qualified"]]
    audit: dict[str, Any] = {
        "schema": SCHEMA,
        "provenance": provenance(src),
        "rules": __doc__.strip(),
        "parameters": {"last_n_room_floors": last_n, "late_defeat_split_seed": split_seed,
                       "late_defeat_cut": {"test": 1, "val": 1, "train": "rest"}, "new_victory_split": "train"},
        "layout": {"act_room_floors": layout, "derived_from_victories": sorted(victory_lengths),
                   "final_act_window": [layout[-1] - last_n + 1, layout[-1]],
                   "final_act_total_floors": [sum(layout[:-1]) + layout[-1] - last_n + 1, sum(layout)]},
        "counts": {
            "cohort_runs": len(manifest_ids), "cohort_templates": len(src.templates),
            "selected_runs": len(victories) + len(late), "selected_templates": len(selected),
            "victory_runs": len(victories), "late_defeat_runs": len(late),
            "victory_templates": sum(1 for r in rows if r["source_cohort"] == "victory"),
            "late_defeat_templates": sum(1 for r in rows if r["source_cohort"] == "act3_late_defeat"),
            "excluded_runs": len(manifest_ids) - len(victories) - len(late),
            "excluded_templates": len(excluded_templates),
        },
        "victory_runs": victories,
        "late_defeat_runs": late,
        "excluded_runs": {r["run_id"]: r["exclusion_reasons"] for r in run_records if not r["qualified"]},
        "excluded_templates": excluded_templates,
        "selected_templates": [{"template_id": t["template_id"], "template_sha256": t["template_sha256"]}
                               for t in selected],
        "encounter_name_source": "history map_point_history room model_id at the template floor; the "
                                 "catalog has no encounter table (keys: " + ",".join(sorted(src.catalog)) + "); "
                                 "(EncounterId, EncounterWeakVariant) -> name verified one-to-one",
        "split_digest": split_digest,
    }
    audit["selection_digest"] = paths.digest({
        "layout": layout, "parameters": audit["parameters"], "splits": split_doc,
        "selected": audit["selected_templates"], "excluded_runs": audit["excluded_runs"],
        "sources": {k: v["sha256"] for k, v in audit["provenance"]["sources"].items()},
    })
    splits = {
        "schema": SCHEMA + "/splits",
        "selection_digest": audit["selection_digest"],
        "split_digest": split_digest,
        "procedure": [
            "splits are frozen per source run before any augmentation",
            f"the {len(split_info['old_runs'])} runs of {src.source_paths['old_splits']} keep their exact split",
            "new verified victories -> train",
            f"new late defeats ordered by sha256('crl/three_act_split/v1|{split_seed}|<run_id>'); "
            "first -> test, second -> val, rest -> train",
        ],
        "old_splits": {"path": src.source_paths["old_splits"], "sha256": src.hashes["old_splits"],
                       "selection_digest": src.old_splits.get("selection_digest"),
                       "assignment": split_info["old_runs"]},
        "new_victories": split_info["new_victories"],
        "late_defeat_order": split_info["late_defeat_order"],
        "assignment": assignment,
        **split_doc,
    }
    return ThreeActSelection(selected=selected, audit=audit, splits=splits, runs=run_records,
                             rows=rows, statistics={"schema": SCHEMA + "/statistics",
                                                    "selection_digest": audit["selection_digest"],
                                                    **build_statistics(rows)})


def select_three_act(*, split_seed: int = LATE_SPLIT_SEED, last_n: int = LAST_N_FLOORS,
                     **source_paths: Path) -> ThreeActSelection:
    return select_from_sources(load_sources(**source_paths), split_seed=split_seed, last_n=last_n)


# --------------------------------------------------------------------------- export

def _json_bytes(obj: Any) -> bytes:
    return (json.dumps(obj, indent=1, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")


def _templates_gz(rows: list[dict]) -> bytes:
    buf = io.BytesIO()
    with gzip.GzipFile(fileobj=buf, mode="wb", compresslevel=6, mtime=0) as g:
        for row in rows:
            g.write((json.dumps(row, separators=(",", ":"), ensure_ascii=False) + "\n").encode("utf-8"))
    return buf.getvalue()


def _csv_bytes(rows: list[dict]) -> bytes:
    buf = io.StringIO()
    w = csv.DictWriter(buf, fieldnames=CSV_COLUMNS, lineterminator="\n")
    w.writeheader()
    for r in rows:
        w.writerow({k: (str(r[k]).lower() if isinstance(r[k], bool) else r[k]) for k in CSV_COLUMNS})
    return buf.getvalue().encode("utf-8")


def export_files(sel: ThreeActSelection, src: Sources) -> dict[str, bytes]:
    files = {
        "templates.jsonl.gz": _templates_gz(sel.selected),
        "catalog.json": src.catalog_bytes,
        "selection_audit.json": None,
        "splits.json": _json_bytes(sel.splits),
        "runs.json": _json_bytes({"schema": SCHEMA + "/runs", "selection_digest": sel.audit["selection_digest"],
                                  "runs": sel.runs}),
        "statistics.json": _json_bytes(sel.statistics),
        "templates.csv": _csv_bytes(sel.rows),
    }
    # round-trip check: exported records still hash to their template_sha256
    back = [json.loads(line) for line in gzip.decompress(files["templates.jsonl.gz"]).decode().splitlines()]
    if back != sel.selected or any(template_hash(t["engine_input"]) != t["template_sha256"] for t in back):
        raise ValueError("exported templates do not round-trip")
    if paths.sha256_bytes(files["catalog.json"]) != src.hashes["catalog"]:
        raise ValueError("catalog changed while selecting")
    audit = dict(sel.audit)
    audit["output_sha256"] = {k: paths.sha256_bytes(v) for k, v in files.items() if v is not None}
    files["selection_audit.json"] = _json_bytes(audit)
    return files


def write_outputs(out_dir: Path, files: dict[str, bytes]) -> dict[str, str]:
    """Write every file, refusing (before writing anything) if an existing file differs."""
    out_dir = Path(out_dir)
    status, differing = {}, []
    for name, data in files.items():
        p = out_dir / name
        if p.exists():
            if p.read_bytes() == data:
                status[name] = "unchanged"
            else:
                differing.append(name)
    if differing:
        raise FileExistsError(f"refusing to overwrite differing outputs in {out_dir}: {differing}")
    out_dir.mkdir(parents=True, exist_ok=True)
    for name, data in files.items():
        if name in status:
            continue
        tmp = out_dir / f".{name}.tmp"
        tmp.write_bytes(data)
        os.replace(tmp, out_dir / name)
        status[name] = "written"
    return status


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m crl.three_act", description=__doc__.splitlines()[0])
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--split-seed", type=int, default=LATE_SPLIT_SEED)
    args = ap.parse_args(argv)
    src = load_sources()
    sel = select_from_sources(src, split_seed=args.split_seed)
    status = write_outputs(args.out, export_files(sel, src))
    c = sel.audit["counts"]
    print(f"selected {c['selected_runs']} runs / {c['selected_templates']} templates "
          f"(victory {c['victory_runs']}/{c['victory_templates']}, "
          f"late defeat {c['late_defeat_runs']}/{c['late_defeat_templates']}); "
          f"excluded {c['excluded_runs']} runs / {c['excluded_templates']} templates")
    for name in ("train", "val", "test"):
        s = sel.splits[name]
        print(f"  {name}: {len(s['run_ids'])} runs, {len(s['template_ids'])} templates, late defeats {s['late_defeat_run_ids']}")
    print(f"  selection_digest {sel.audit['selection_digest']}")
    for name, st in status.items():
        print(f"  {st:9s} {args.out / name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
