# Four mechanism features v1

**Motivation:** Learning when to activate a card engine and how to use its
supporting cards is difficult, so we expose each card's role and the engine's
current combat state to the Battle agent.

This experimental snapshot documents **`startup-mechanisms/v1`**, the encoding
used by the completed mechanism-focused training runs. It adds **16 role flags
per card** and **24 shared combat-state values** to the semantic representation.
The Battle agent still learns which legal action to choose.

## The four mechanisms

| Mechanism | Interaction we want the agent to learn | Example |
| --- | --- | --- |
| **Rupture** | Activate the power, trigger qualifying self HP loss, then use Strength-sensitive attacks | Rupture → Bloodletting → Sword Boomerang |
| **Demon Form** | Activate the power, gain Strength at turn starts, then benefit from that Strength | Demon Form → later-turn Sword Boomerang |
| **Exhaust / Ashen Strike** | Build the exhaust pile and use Ashen Strike's exhaust-dependent damage | Burning Pact → Ashen Strike |
| **Hellraiser** | Activate the power and use draws to trigger automatic plays of Strike-tagged cards | Hellraiser → draw with Pommel Strike |

These are interactions to learn, not prescribed action sequences. Costs, HP,
targets and immediate kill opportunities still matter.

## How we encode them

Each mechanism has the same four card roles: **`[core, producer, payoff, supply]`**.
The groups are ordered **Rupture, Demon Form, Exhaust/Ashen, Hellraiser**.
Roles can overlap, and supply depends on eligible cards in the visible piles.

| Card | Rupture role vector | Meaning |
| --- | --- | --- |
| Rupture | `[1, 0, 0, 0]` | Establishes the engine |
| Bloodletting | `[0, 1, 0, 0]` | Can provide a self HP-loss trigger |
| Sword Boomerang | `[0, 0, 1, 0]` | Damage benefits from Strength |
| Headbutt, with a Strike in discard | `[0, 0, 1, 1]` | Benefits from Strength and can return a relevant card to draw |
| Defend | `[0, 0, 0, 0]` | No Rupture role; its ordinary block features remain available |

The card's existing **84 dense fields become 100**. The new flags occupy positions
**84–99**, alongside the separate semantic effect description used by the card encoder.

Combat state uses **`[build_enabled, online, payoff_in_hand, payoff_legal_now]`**
for each mechanism, followed by eight shared values: current Strength, Rupture
rate, Demon Form rate, exhaust count, draw count, Strike counts in draw/discard,
and whether effect draws are allowed now.

For example, before activating Rupture its status can be `[1, 0, 1, 1]`;
after activation it becomes `[1, 1, 1, 1]`. One qualifying Bloodletting trigger
with ordinary Rupture raises current Strength from 0 to 1 while the rate stays 1.
Numeric values use signed `log(1 + |x|)`, so Strength 1 becomes **0.693147**.
Flags remain 0 or 1.

See the [feature reference](docs/feature-reference.md) for every position,
exact role rules, limitations, and the path through the Battle network.

## Observed result

On the same historical **5,000-combat diagnostic benchmark**, the best observed
pooled boss win rate increased from **53.1% to 68.7%**, a difference of
**15.6 percentage points** compared with the previous published lethality experiment.

| Best observed boss checkpoint | Boss wins | Overall wins at that same checkpoint |
| --- | --- | --- |
| Previous lethality model · L2 · 17.5k online fights | 956 / 1,800 (**53.1%**) | 3,931 / 5,000 (78.6%) |
| Four-mechanism continuation · L3 · S2 · 62.5k cumulative online fights | 1,237 / 1,800 (**68.7%**) | 4,240 / 5,000 (84.8%) |

The newer point is S2 at **+30k continuation fights**, the highest boss result
among 32 regular checkpoints across two seeds; the previous value is the highest
among eight lethality checkpoints from one seed. These are observed peaks on a
diagnostic bank with source overlap. Model depth, training duration, curriculum,
runtime and seed/checkpoint coverage also changed, so the difference does not
isolate the effect of these features or establish an independent final-test result.
These are individual combat results, separate from the released three-act benchmark.

[All 32 checkpoint aggregates and comparison provenance](results/checkpoints.json)
record the counts behind the summary. Enemy maximum-HP/HP-fraction masking and
the existing current-hand lethality signals remain in both experiment families.

## Try the examples

From the repository root, with Python 3.11 or later:

```sh
python -m pip install -r research/startup_mechanisms_v1/requirements.txt
python research/startup_mechanisms_v1/examples/inspect_features.py --mechanism rupture
python research/startup_mechanisms_v1/examples/inspect_features.py --json
python -m unittest discover -s research/startup_mechanisms_v1/tests -v
```

The examples use eleven source-derived, unupgraded card programs and nine
illustrative states covering all four mechanisms. They inspect encodings;
they do not simulate battles or run a trained policy. Only NumPy is required.
[`schema.py`](schema.py) defines the positions and [`encoding.py`](encoding.py)
contains the standalone calculation. [SOURCE.json](SOURCE.json) records the
retained source and the adaptation boundary.
