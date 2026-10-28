# Scope and limitations

## Snapshot and coverage

This is an experimental representation snapshot for source game build
`v0.111.0-24724944`. [SOURCE.json](../SOURCE.json) records the source and packaged
file hashes. The schema and metadata are copied unchanged. The catalog's
private training exception dependency is replaced by a local `ValueError`
subclass; its mechanics programs are unchanged.

The metadata contains 208 identities. The catalog admits 207 with base/upgraded
programs: 85 Ironclad, 52 admitted normal-pool colorless, and 70 reachable neutral
event/quest/token/status/curse cards. Splash is explicitly excluded because its
foreign-character generation exceeds this scope. Unknown card IDs fail visibly.
Compilation of both variants does not mean every card can be upgraded in game;
the metadata separately records `can_upgrade`.

The catalog also contains active-rule and enchantment descriptions. Some
descriptions require instance state. Mad Science needs its actual mutable type
and rider; the generic inspector refuses it instead of presenting a guessed
instance. Inspecting generated cards or enchantments in a live battle needs
the corresponding instance context.

## What the example does

- It reads metadata and encodes ordered mechanics and expressions.
- It builds an 84-field current-card row using caller-supplied numeric values,
  the selected upgrade variant, static type/rarity/target, and explicit context.
- It prints nonzero features with indices and names. Omitted positions are zero;
  known flags determine whether numeric zero is available or unknown.

The example is **not a simulator preview**. It does not calculate effective
damage, cost, hit count, legality, random outcomes, or the consequences of a
whole action. It does not sample hidden state. `--playable` is an assertion by
the caller; without it the indicator is zero, not a computed legality result.
Runtime keyword changes, enchantments, and temporary modifiers are not inferred
by the inspector.

## Components outside this module

The live semantic observation adapter, native preview worker, neural model,
training loop, checkpoints, replay, datasets, and evaluation orchestration are
not included. The 15-field preview, 31-field selection, and 20-field continuation
schemas are documented for completeness but are not emitted by this inspector.

The existing repository benchmark remains the older identity-based model pair.
Its selected 68/256 validation result is not evidence for this representation.
Passing these tests checks encoding consistency and catalog behavior, not full
simulator parity, policy performance, or coverage of future game versions.

## Schema details that must not be silently changed

- Numeric absence is distinct from a known zero. Flags generally have no
  availability channel; their zero default must be interpreted in context.
- `retained` and `temporary_copy` in the current-card schema are reserved slots
  that the source live adapter leaves at zero. The generic explicit-value helper
  can encode supplied flags, but that does not establish live support for them.
- Selection `random` is also a reserved zero slot in the source adapter.
- Continuation fields all use slog, including boolean roles. True is `ln(2)`.
- Expression arity, argument, known, snapshot, and live fields are raw values;
  only expression value and coefficient use slog.
- Preview `exact` describes the source preview status, not exact simulation of
  all downstream effects. It can coexist with a stochastic target/effect.
- Padding masks are separate from the feature rows and from numeric known flags.

Field names, order, transformations, and category order are part of
`semantic_card.schema/v1`. A change to their meaning or layout requires a new
compatibility contract and regenerated examples/reference. Do not attach this
schema to existing identity-model checkpoints or historical encoded replay.
