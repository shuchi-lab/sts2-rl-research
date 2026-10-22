"""battle_width_scan_v1 structured Q network: ``crl.model.QNet`` with every encoder width a parameter.

Copied from ``crl/model.py`` (shared code untouched; ``mlp`` / ``masked_mean`` / ``masked_max`` / ``NEG`` /
``V2_DIMS`` are imported from it, not redefined). The forward pass, embeddings, intent processing, every
masked set pool, activations, depth, heads, option masking (``opt_mask`` & ``opt_admissible`` -> NEG) and
target binding (gather of the target enemy's vector) are line-for-line those of QNet. Modules are created in
QNet's exact order, so configuration ``A`` under the same torch seed draws the same initial tensors and
produces a state_dict equal key-for-key and tensor-for-tensor to ``crl.model.QNet``'s.

Widths (``crl.model.QNet`` hard-codes the four marked *):

  card_hidden*  card encoder internal width            (QNet: 128)
  card_dim      card encoder output / card vector       (64)
  pile_dim*     per-pile projection output              (64; input 2 * card_dim + 1)
  eff_hidden*   effect-row encoder internal width       (64)
  eff_dim       effect-row encoder output               (32)
  enemy_hidden* enemy encoder internal width            (128)
  enemy_dim     enemy encoder output / target vector    (64)
  state_dim     post-pool state encoder width           (256; two layers)
  head          candidate-head hidden width             (128; two hidden layers)

Every downstream input widens with them: player block 7 + 9 + eff_dim + 1; state input
player + pile_dim * piles + 2 * enemy_dim + ...; enemy encoder input ... + eff_dim + 1; candidate input
state_dim + 16 + option features + card_dim + enemy_dim (+ 16 potion).

Profiles A-F (experiments/trials/battle-width-af-v1/SPEC.txt): A-D vary state_dim / head only; E widens the
effect + enemy package; F widens the card + pile package. The configuration letter and
``MODEL_SCHEMA`` are part of ``model.config`` (hence of every checkpoint's ``model_config``), so the
shared ``crl.checkpoint.load`` (which builds ``crl.model.QNet(**config)``) refuses a bw1 checkpoint with a
TypeError instead of silently building the wrong class. Load bw1 checkpoints with
:func:`battle_width_scan_v1.checkpoint.load`.
"""

from __future__ import annotations

import math

import torch
import torch.nn as nn
import torch.nn.functional as Fn

from crl.features import (EFFECT_FIELDS, INTENT_FEATURES, OPTION_FEATURES, OPTION_KINDS, PLAYER_BASIC,
                          PLAYER_HISTORY)
from crl.model import NEG, V2_DIMS, masked_max, masked_mean, mlp

from battle_width_scan_v1.profiles import (BASELINE, CONFIGURATIONS, EXPECTED_INPUTS, EXPECTED_PARAMETERS, MODEL_SCHEMA, PROFILES,
                       WIDTH_KEYS, configuration_of, expected_inputs, expected_parameters, profile)

__all__ = ["BASELINE", "CONFIGURATIONS", "EXPECTED_INPUTS", "EXPECTED_PARAMETERS", "MODEL_SCHEMA", "PROFILES",
           "REFERENCE_SHAPES", "WIDTH_KEYS", "WidthQNet", "build_model", "build_reference", "configuration_of",
           "expected_inputs", "expected_parameters", "profile"]

# The production context's vocabulary sizes / feature widths, copied from the model_config of the published
# pqt1 warm-up checkpoints (card_static is [215, 54]). Used by unit tests and offline tooling only; the
# trainer always builds from the live Context and checks EXPECTED_PARAMETERS against it.
REFERENCE_SHAPES = {
    "sizes": {"cards": 215, "relics": 299, "potions": 65, "enemies": 112, "buffs": 254, "intents": 16,
              "selection_kinds": 29},
    "n_enchantments": 23, "card_static": (215, 54),
    "feature_dims": {"card": 44, "piles": 7, "global_extra": 120, "enemy": 30, "orbs": 4, "orb_types": 6},
    "potion_options": True, "enemy_count": True, "dueling": False, "identity_init_scale": 0.1,
}


class WidthQNet(nn.Module):
    def __init__(self, sizes: dict, card_static: torch.Tensor, n_enchantments: int, *,
                 card_dim: int = 64, eff_dim: int = 32, enemy_dim: int = 64, state_dim: int = 256,
                 head: int = 128, card_hidden: int = 128, pile_dim: int = 64, eff_hidden: int = 64,
                 enemy_hidden: int = 128, configuration: str | None = None, model_schema: str = MODEL_SCHEMA,
                 dueling: bool = False, enemy_count: bool = False, identity_init_scale: float = 1.0,
                 feature_dims: dict | None = None, potion_options: bool = False, enemy_layers: list | None = None):
        super().__init__()
        if model_schema != MODEL_SCHEMA:
            raise ValueError(f"model_schema {model_schema!r} is not {MODEL_SCHEMA}")
        widths = dict(card_hidden=card_hidden, card_dim=card_dim, pile_dim=pile_dim, eff_hidden=eff_hidden,
                      eff_dim=eff_dim, enemy_hidden=enemy_hidden, enemy_dim=enemy_dim, state_dim=state_dim, head=head)
        for k, v in widths.items():
            if isinstance(v, bool) or not isinstance(v, int) or v < 1:
                raise ValueError(f"{k} must be a positive int, got {v!r}")
        if configuration is not None and profile(configuration) != widths:
            raise ValueError(f"configuration {configuration} widths {profile(configuration)} != given {widths}")
        identity_init_scale = float(identity_init_scale)
        if not math.isfinite(identity_init_scale) or identity_init_scale < 0:
            raise ValueError(f"identity_init_scale must be finite and >= 0, got {identity_init_scale}")
        # QNet's keys in QNet's order, then the bw1 identity (the shared QNet constructor rejects these keys).
        self.config = dict(sizes=sizes, n_enchantments=n_enchantments, card_dim=card_dim, eff_dim=eff_dim,
                           enemy_dim=enemy_dim, state_dim=state_dim, head=head, dueling=dueling,
                           enemy_count=bool(enemy_count), identity_init_scale=identity_init_scale)
        if feature_dims is not None:
            self.config["feature_dims"] = dict(feature_dims)
        self.potion_options = bool(potion_options)
        if self.potion_options:
            self.config["potion_options"] = True
        self.config.update(card_hidden=card_hidden, pile_dim=pile_dim, eff_hidden=eff_hidden,
                           enemy_hidden=enemy_hidden, configuration=configuration, model_schema=MODEL_SCHEMA)
        layers = [enemy_hidden] if enemy_layers is None else list(enemy_layers)
        if not layers or layers[0] != enemy_hidden or any(type(n) is not int or n < 1 for n in layers):
            raise ValueError("invalid enemy hidden layers")
        self.config["enemy_layers"] = layers
        self.enemy_layers = layers
        self.configuration = configuration
        self.widths = widths
        dims = dict(V2_DIMS, **(feature_dims or {}))
        if set(dims) != set(V2_DIMS):
            raise ValueError(f"unknown feature_dims keys {sorted(set(dims) - set(V2_DIMS))}")
        self.dims = dims
        # ---- module creation in crl.model.QNet's exact order (same RNG draws for profile A) ----
        self.register_buffer("card_static", card_static.float(), persistent=True)
        self.card_emb = nn.Embedding(sizes["cards"], 32)
        self.ench_emb = nn.Embedding(n_enchantments, 4)
        n_static = card_static.shape[1]
        self.card_enc = mlp(32 + 4 + n_static + dims["card"], card_hidden, card_dim)
        self.pile_proj = nn.ModuleList([nn.Linear(2 * card_dim + 1, pile_dim) for _ in range(dims["piles"])])

        self.eff_emb = nn.Embedding(sizes["buffs"], 32)
        self.eff_enc = mlp(32 + len(EFFECT_FIELDS), eff_hidden, eff_dim)

        self.enemy_emb = nn.Embedding(sizes["enemies"], 32)
        self.intent_emb = nn.Embedding(sizes["intents"], 8)
        self.intent_enc = mlp(8 + len(INTENT_FEATURES), 16)
        self.enemy_enc = mlp(32 + dims["enemy"] + 16 + eff_dim + 1, *self.enemy_layers, enemy_dim)

        self.relic_emb = nn.Embedding(sizes["relics"], 16)
        self.relic_proj = nn.Linear(17, 16)
        self.potion_emb = nn.Embedding(sizes["potions"], 16)
        self.sel_emb = nn.Embedding(sizes["selection_kinds"], 8)

        player_dim = len(PLAYER_BASIC) + len(PLAYER_HISTORY) + eff_dim + 1
        self.player_dim = player_dim
        in_state = (player_dim + pile_dim * dims["piles"] + 2 * enemy_dim + 17 + 17 + 8 + 32
                    + dims["global_extra"])
        self.use_orbs = dims["orbs"] > 0
        if self.use_orbs:
            self.orb_emb = nn.Embedding(dims["orb_types"], 8)
            self.orb_enc = mlp(8 + dims["orbs"], 16)
            in_state += 17
        if self.potion_options:
            in_state += 16
        self.enemy_count = bool(enemy_count)
        self.state_enc = mlp(in_state + int(self.enemy_count), state_dim, state_dim)

        self.kind_emb = nn.Embedding(len(OPTION_KINDS) + int(self.potion_options), 16)
        cand_in = state_dim + 16 + len(OPTION_FEATURES) + card_dim + enemy_dim + 16 * int(self.potion_options)
        self.state_in, self.cand_in = in_state + int(self.enemy_count), cand_in
        self.q_head = mlp(cand_in, head, head, 1)
        self.dueling = dueling
        if dueling:
            self.v_head = mlp(state_dim, head, 1)

        if identity_init_scale != 1.0:  # in place, after every module drew its init: no RNG drift
            with torch.no_grad():
                for emb in (self.card_emb, self.ench_emb, self.eff_emb, self.enemy_emb, self.relic_emb,
                            self.potion_emb):
                    emb.weight.mul_(identity_init_scale)

    # ----------------------------------------------------------------------
    def parameter_count(self) -> int:
        return sum(p.numel() for p in self.parameters())

    def architecture(self) -> dict:
        """Complete width/shape record for run metadata (config.json, checkpoints' meta, summaries)."""
        return {"model_schema": MODEL_SCHEMA, "model_class": f"{__name__}.WidthQNet",
                "configuration": self.configuration, "widths": dict(self.widths),
                "player_dim": self.player_dim, "state_input": self.state_in, "candidate_input": self.cand_in,
                "pile_input": 2 * self.widths["card_dim"] + 1, "piles": self.dims["piles"],
                "card_encoder_input": self.card_enc[0].in_features, "effect_encoder_input": self.eff_enc[0].in_features,
                "enemy_encoder_input": self.enemy_enc[0].in_features, "feature_dims": dict(self.dims),
                "potion_options": self.potion_options, "enemy_count": self.enemy_count, "dueling": self.dueling,
                "parameters": self.parameter_count(),
                "parameter_shapes": {k: list(v.shape) for k, v in self.named_parameters()}}

    def encode_cards(self, idx: torch.Tensor, ench: torch.Tensor, feat: torch.Tensor) -> torch.Tensor:
        x = torch.cat([self.card_emb(idx), self.ench_emb(ench), self.card_static[idx], feat], -1)
        return self.card_enc(x)

    def forward(self, b: dict) -> torch.Tensor:
        """Q values [B, O]; padded / inadmissible options are NEG (crl.model.QNet.forward verbatim)."""
        B = b["glob"].shape[0]
        n_basic, n_hist = len(PLAYER_BASIC), len(PLAYER_HISTORY)

        # Cards and piles.
        cards = self.encode_cards(b["card_idx"], b["card_ench"], b["card_feat"])  # [B,C,card_dim]
        piles = []
        for p, proj in enumerate(self.pile_proj):
            m = b["card_pile"] == p
            mean, cnt = masked_mean(cards, m, 1)
            mx = masked_max(cards, m, 1)
            piles.append(proj(torch.cat([mean, mx, torch.log1p(cnt)], -1)))

        # Effects, pooled per owner (0 = player, 1 + j = enemy j).
        M = b["enemy_idx"].shape[1]
        eff = self.eff_enc(torch.cat([self.eff_emb(b["eff_idx"]), b["eff_feat"]], -1))  # [B,E,eff_dim]
        owners = torch.arange(M + 1, device=eff.device)
        own = b["eff_owner"].unsqueeze(-1) == owners  # [B,E,1+M]
        ownf = own.to(eff.dtype)
        cnt = ownf.sum(1)  # [B,1+M]
        pooled = torch.einsum("beo,bed->bod", ownf, eff) / cnt.clamp(min=1.0).unsqueeze(-1)
        lcnt = torch.log1p(cnt).unsqueeze(-1)
        player_eff = torch.cat([pooled[:, 0], lcnt[:, 0]], -1)
        enemy_eff = torch.cat([pooled[:, 1:], lcnt[:, 1:]], -1)  # [B,M,eff_dim+1]

        glob = b["glob"]
        player = torch.cat([glob[:, :n_basic + n_hist], player_eff], -1)  # [B,player_dim]

        # Enemies.
        it = self.intent_enc(torch.cat([self.intent_emb(b["intent_type"]), b["intent_feat"]], -1))
        it = (it * (b["intent_type"] > 0).unsqueeze(-1).to(it.dtype)).sum(2)  # [B,M,16]
        enemies = self.enemy_enc(torch.cat([self.enemy_emb(b["enemy_idx"]), b["enemy_feat"], it, enemy_eff], -1))
        em = b["enemy_mask"]
        e_mean, _ = masked_mean(enemies, em, 1)
        e_max = masked_max(enemies, em, 1)

        # Relics / potions / selection.
        rm = b["relic_idx"] > 0
        r_mean, r_cnt = masked_mean(self.relic_proj(torch.cat([self.relic_emb(b["relic_idx"]), b["relic_feat"]], -1)), rm, 1)
        pm = b["potion_idx"] > 0
        p_mean, p_cnt = masked_mean(self.potion_emb(b["potion_idx"]), pm, 1)
        sel = torch.cat([self.sel_emb(b["sel_kind"]), self.card_emb(b["sel_src"]) * (b["sel_src"] > 0).unsqueeze(-1)], -1)

        parts = [player, *piles, e_mean, e_max, r_mean, torch.log1p(r_cnt), p_mean, torch.log1p(p_cnt), sel,
                 glob[:, n_basic + n_hist:]]
        if self.use_orbs:
            om = b["orb_idx"] > 0
            o_mean, o_cnt = masked_mean(self.orb_enc(torch.cat([self.orb_emb(b["orb_idx"]), b["orb_feat"]], -1)), om, 1)
            parts += [o_mean, torch.log1p(o_cnt)]
        if self.potion_options:
            src = b["sel_src_potion"]
            parts.append(self.potion_emb(src) * (src > 0).unsqueeze(-1).to(glob.dtype))
        if self.enemy_count:
            parts.append(torch.log1p(em.to(glob.dtype).sum(1, keepdim=True)))
        state = self.state_enc(torch.cat(parts, -1))
        state = Fn.relu(state)

        # Candidates.
        O = b["opt_kind"].shape[1]
        hand = b["opt_hand"]
        hand_vec = torch.gather(cards, 1, hand.clamp(min=0).unsqueeze(-1).expand(B, O, cards.shape[-1]))
        hand_vec = hand_vec * (hand >= 0).unsqueeze(-1).to(hand_vec.dtype)
        is_sel = (b["opt_card_idx"] > 0).unsqueeze(-1).to(cards.dtype)
        sel_vec = self.encode_cards(b["opt_card_idx"], b["opt_card_ench"], b["opt_card_feat"]) * is_sel
        card_vec = hand_vec + sel_vec
        tgt = b["opt_target"]
        tgt_vec = torch.gather(enemies, 1, tgt.clamp(min=0).unsqueeze(-1).expand(B, O, enemies.shape[-1]))
        tgt_vec = tgt_vec * (tgt >= 0).unsqueeze(-1).to(tgt_vec.dtype)
        cand_parts = [state.unsqueeze(1).expand(B, O, state.shape[-1]), self.kind_emb(b["opt_kind"]),
                      b["opt_feat"], card_vec]
        if self.potion_options:
            pot = b["opt_potion_idx"]
            cand_parts.append(self.potion_emb(pot) * (pot > 0).unsqueeze(-1).to(cards.dtype))
        cand = torch.cat(cand_parts + [tgt_vec], -1)
        q = self.q_head(cand).squeeze(-1)  # [B,O]
        mask = b["opt_mask"]
        if "opt_admissible" in b:  # crl.potion_v1 policy mask: legal but never scored or bootstrapped
            mask = mask & b["opt_admissible"]
        if self.dueling:
            a_mean, _ = masked_mean(q.unsqueeze(-1), mask, 1)
            q = self.v_head(state) + q - a_mean
        return q.masked_fill(~mask, NEG)


def build_model(ctx_or_meta, configuration: str = "A", dueling: bool = False, **kw) -> WidthQNet:
    """Fresh model of ``configuration`` from a Context (same derivation as ``crl.model.build_model``).
    Checkpoints use :func:`battle_width_scan_v1.checkpoint.load`."""
    widths = profile(configuration)
    if set(kw) & set(WIDTH_KEYS):
        raise ValueError(f"widths come from the configuration profile, not keywords: {sorted(set(kw) & set(WIDTH_KEYS))}")
    v = ctx_or_meta.vocab
    adapter = ctx_or_meta.adapter
    n_ench = adapter.describe()["n_enchantments"]
    dims = adapter.dims if getattr(adapter, "v3", False) else None
    if getattr(adapter, "potion_options", False):
        kw.setdefault("potion_options", True)
    return WidthQNet(v.sizes, torch.from_numpy(v.card_static), n_ench, dueling=dueling, feature_dims=dims,
                     configuration=configuration, **widths, **kw)


def build_reference(configuration: str = "A", *, card_static: torch.Tensor | None = None) -> WidthQNet:
    """A model on :data:`REFERENCE_SHAPES` (production vocabulary sizes, no engine). Offline/test use only."""
    r = REFERENCE_SHAPES
    static = card_static if card_static is not None else torch.zeros(r["card_static"])
    return WidthQNet(r["sizes"], static, r["n_enchantments"], configuration=configuration,
                     feature_dims=r["feature_dims"], potion_options=r["potion_options"], enemy_count=r["enemy_count"],
                     dueling=r["dueling"], identity_init_scale=r["identity_init_scale"], **profile(configuration))
