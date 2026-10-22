"""Structured candidate-scoring Q network (trainable end to end).

State encoder:
  * card instance encoder (shared by every pile and by selection candidates):
    id embedding 32 + enchantment embedding 4 + static definition features + instance features
    -> 128 -> 64. Five piles (hand, draw, discard, exhaust, incoming) are pooled SEPARATELY
    (masked mean, masked max, log count), each with its own projection to 64.
  * effect row encoder (shared by all owners): BuffId embedding 32 + 10 fields = 42 -> 64 -> 32.
    Pooled per owner: the player's rows into the player block, each enemy's rows into that
    enemy only (masked mean + log count).
  * player block = 7 basic + 9 history + 32 pooled effects + 1 count = 49.
  * enemy encoder: id embedding + public scalars + declared intents + own effects -> 64;
    enemies pooled by masked mean + max for the state, kept per slot for target binding.
  * relics (id + counter) and potion inventory (ids): masked mean + count.
Candidate head: [state 256, option kind, option flags, the option's card vector (the hand slot
it plays or the selection candidate), the target enemy's vector] -> 128 -> 128 -> Q.
Optional dueling: Q = V(state) + A - masked mean(A). Padding never contributes: every pool is
masked, and padded options get -inf.

Feature widths (``feature_dims``): ``None`` is the crl.features/v2 layout (every checkpoint
written before v3 has no such key and loads unchanged). A crl.features/v3 adapter passes its
``dims``: wider card/enemy/global rows, two more piles (``pending``, ``enemy_held``), and an orb
set (OrbType embedding 8 + 4 fields -> 16, masked mean + log count, appended to the state input).
Entity sets of every kind stay variable length; nothing is capped.

Opt-in refinements (both off by default; old checkpoints without the keys load as baseline):
  * ``enemy_count``: append log1p(valid enemies) as the LAST state-encoder input, so one enemy
    and two identical enemies no longer alias (mean/max pooling cannot count).
  * ``identity_init_scale``: multiply the initial N(0,1) weights of the entity-identity tables
    (card, enchantment, effect/buff, enemy, relic, potion) by this factor; 1.0 leaves them
    untouched. Closed engine-type tables (selection kind, intent type, option kind) are never
    scaled. Applied at construction only, so ``load_state_dict`` restores learned weights exactly.

Opt-in potion candidates (``potion_options``, crl.potion_v1; off by default and absent from every
older config, which therefore loads unchanged): a fifth option kind ``use_potion`` in the SAME Q
head, and the existing 16-wide potion identity embedding reused twice more -- the candidate's own
potion (candidate head 406 -> 422) and the source potion of an open potion selection screen
(state input +16). The pooled inventory (16 + count) is unchanged.
"""

from __future__ import annotations

import math

import torch
import torch.nn as nn
import torch.nn.functional as Fn

from .features import (CARD_FEATURES, EFFECT_FIELDS, ENEMY_FEATURES, GLOBAL_EXTRA, INTENT_FEATURES,
                       OPTION_FEATURES, OPTION_KINDS, PILES, PLAYER_BASIC, PLAYER_HISTORY)

NEG = -1e9
# crl.features/v2 widths; a v3 adapter's ``dims`` override them.
V2_DIMS = {"card": len(CARD_FEATURES), "piles": len(PILES), "global_extra": len(GLOBAL_EXTRA),
           "enemy": len(ENEMY_FEATURES), "orbs": 0, "orb_types": 0}


def mlp(*dims: int) -> nn.Sequential:
    layers: list[nn.Module] = []
    for i in range(len(dims) - 1):
        layers.append(nn.Linear(dims[i], dims[i + 1]))
        if i < len(dims) - 2:
            layers.append(nn.ReLU())
    return nn.Sequential(*layers)


def masked_mean(x: torch.Tensor, mask: torch.Tensor, dim: int) -> tuple[torch.Tensor, torch.Tensor]:
    m = mask.unsqueeze(-1).to(x.dtype)
    cnt = m.sum(dim)
    return (x * m).sum(dim) / cnt.clamp(min=1.0), cnt


def masked_max(x: torch.Tensor, mask: torch.Tensor, dim: int) -> torch.Tensor:
    m = mask.unsqueeze(-1)
    out = x.masked_fill(~m, NEG).max(dim).values
    return torch.where(m.any(dim), out, torch.zeros_like(out))


class QNet(nn.Module):
    def __init__(self, sizes: dict, card_static: torch.Tensor, n_enchantments: int, *,
                 card_dim: int = 64, eff_dim: int = 32, enemy_dim: int = 64, state_dim: int = 256,
                 head: int = 128, dueling: bool = False, enemy_count: bool = False,
                 identity_init_scale: float = 1.0, feature_dims: dict | None = None,
                 potion_options: bool = False):
        super().__init__()
        identity_init_scale = float(identity_init_scale)
        if not math.isfinite(identity_init_scale) or identity_init_scale < 0:
            raise ValueError(f"identity_init_scale must be finite and >= 0, got {identity_init_scale}")
        self.config = dict(sizes=sizes, n_enchantments=n_enchantments, card_dim=card_dim, eff_dim=eff_dim,
                           enemy_dim=enemy_dim, state_dim=state_dim, head=head, dueling=dueling,
                           enemy_count=bool(enemy_count), identity_init_scale=identity_init_scale)
        if feature_dims is not None:
            self.config["feature_dims"] = dict(feature_dims)
        self.potion_options = bool(potion_options)
        if self.potion_options:  # only written when on, so older configs stay byte-identical
            self.config["potion_options"] = True
        dims = dict(V2_DIMS, **(feature_dims or {}))
        if set(dims) != set(V2_DIMS):
            raise ValueError(f"unknown feature_dims keys {sorted(set(dims) - set(V2_DIMS))}")
        self.dims = dims
        self.register_buffer("card_static", card_static.float(), persistent=True)
        self.card_emb = nn.Embedding(sizes["cards"], 32)
        self.ench_emb = nn.Embedding(n_enchantments, 4)
        n_static = card_static.shape[1]
        self.card_enc = mlp(32 + 4 + n_static + dims["card"], 128, card_dim)
        self.pile_proj = nn.ModuleList([nn.Linear(2 * card_dim + 1, 64) for _ in range(dims["piles"])])

        self.eff_emb = nn.Embedding(sizes["buffs"], 32)
        self.eff_enc = mlp(32 + len(EFFECT_FIELDS), 64, eff_dim)

        self.enemy_emb = nn.Embedding(sizes["enemies"], 32)
        self.intent_emb = nn.Embedding(sizes["intents"], 8)
        self.intent_enc = mlp(8 + len(INTENT_FEATURES), 16)
        self.enemy_enc = mlp(32 + dims["enemy"] + 16 + eff_dim + 1, 128, enemy_dim)

        self.relic_emb = nn.Embedding(sizes["relics"], 16)
        self.relic_proj = nn.Linear(17, 16)
        self.potion_emb = nn.Embedding(sizes["potions"], 16)
        self.sel_emb = nn.Embedding(sizes["selection_kinds"], 8)

        player_dim = len(PLAYER_BASIC) + len(PLAYER_HISTORY) + eff_dim + 1  # 49
        self.player_dim = player_dim
        in_state = player_dim + 64 * dims["piles"] + 2 * enemy_dim + 17 + 17 + 8 + 32 + dims["global_extra"]
        self.use_orbs = dims["orbs"] > 0
        if self.use_orbs:
            self.orb_emb = nn.Embedding(dims["orb_types"], 8)
            self.orb_enc = mlp(8 + dims["orbs"], 16)
            in_state += 17
        if self.potion_options:
            in_state += 16  # source potion of an open potion selection (shared potion_emb)
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
    def encode_cards(self, idx: torch.Tensor, ench: torch.Tensor, feat: torch.Tensor) -> torch.Tensor:
        x = torch.cat([self.card_emb(idx), self.ench_emb(ench), self.card_static[idx], feat], -1)
        return self.card_enc(x)

    def forward(self, b: dict) -> torch.Tensor:
        """Q values [B, O]; padded options are NEG."""
        B = b["glob"].shape[0]
        n_basic, n_hist = len(PLAYER_BASIC), len(PLAYER_HISTORY)

        # Cards and piles.
        cards = self.encode_cards(b["card_idx"], b["card_ench"], b["card_feat"])  # [B,C,D]
        piles = []
        for p, proj in enumerate(self.pile_proj):
            m = b["card_pile"] == p
            mean, cnt = masked_mean(cards, m, 1)
            mx = masked_max(cards, m, 1)
            piles.append(proj(torch.cat([mean, mx, torch.log1p(cnt)], -1)))

        # Effects, pooled per owner (0 = player, 1 + j = enemy j).
        M = b["enemy_idx"].shape[1]
        eff = self.eff_enc(torch.cat([self.eff_emb(b["eff_idx"]), b["eff_feat"]], -1))  # [B,E,32]
        owners = torch.arange(M + 1, device=eff.device)
        own = b["eff_owner"].unsqueeze(-1) == owners  # [B,E,1+M]
        ownf = own.to(eff.dtype)
        cnt = ownf.sum(1)  # [B,1+M]
        pooled = torch.einsum("beo,bed->bod", ownf, eff) / cnt.clamp(min=1.0).unsqueeze(-1)
        lcnt = torch.log1p(cnt).unsqueeze(-1)
        player_eff = torch.cat([pooled[:, 0], lcnt[:, 0]], -1)
        enemy_eff = torch.cat([pooled[:, 1:], lcnt[:, 1:]], -1)  # [B,M,33]

        glob = b["glob"]
        player = torch.cat([glob[:, :n_basic + n_hist], player_eff], -1)  # [B,49]

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


def build_model(ctx_or_meta, dueling: bool = False, **kw) -> QNet:
    """From a Context (fresh) — checkpoints use :func:`crl.checkpoint.load`."""
    v = ctx_or_meta.vocab
    adapter = ctx_or_meta.adapter
    n_ench = adapter.describe()["n_enchantments"]
    dims = adapter.dims if getattr(adapter, "v3", False) else None
    if getattr(adapter, "potion_options", False):
        kw.setdefault("potion_options", True)
    return QNet(v.sizes, torch.from_numpy(v.card_static), n_ench, dueling=dueling, feature_dims=dims, **kw)
