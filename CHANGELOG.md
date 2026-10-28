# Changelog

## Unreleased

- Add an independent semantic card research module with a versioned schema,
  indexed feature documentation, mechanics catalog, and runnable examples.
- Separate the research entry point from the released benchmark and move the
  detailed architecture figures into a linked guide.
- Correct the v1.0.0 difficulty label below to Ascension 2, matching the retained
  evaluation configuration and results. Benchmark evidence and models are unchanged.

## 1.0.0

First curated release of a two-agent Slay the Spire 2 simulator benchmark.

- Ironclad, Ascension 2, three acts: 68 victories in 256 validation runs (26.5625%). Four battle turn limits count as nonwins.
- Exact checkpoint files, a frozen inference runtime, matching emulator/worker assemblies and corresponding source/provenance.
- Per-run results, retained validation seeds, evaluation conditions, artifact hashes and a local evaluation entry point.

This release records a maximum selected from a 30-by-42 checkpoint comparison using the same validation cohort.
