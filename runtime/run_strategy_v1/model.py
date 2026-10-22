"""The strategy candidate-scoring Q-network (Task 5). Every parameter is its own: no E tensor, no copied
weights. Public static metadata (card/relic/potion definitions) enters as fixed buffers beside learned
identity embeddings.

CPU performance implementation (cpu_utilization_amendment_v1): the SAME network, parameters, state_dict, schema,
features and masks as the frozen source ``reference_model.py`` (byte-identical copy, bound by sha256). Only the
candidate side of :meth:`StrategyQNet.forward` is evaluated packed: the per-candidate formulas of the reference run on
the legal (row, candidate) entries only, bundle members are encoded only where a bundle card exists, and the head
output is scattered back into a ``NEG``-filled ``[B, C]`` grid (padding is exactly ``NEG``, as ``masked_fill`` gives).
Every value the reference multiplies by a zero mask is still formed (one padding-card / padding-potion encoding),
so a nonfinite value the reference would propagate into a legal Q is propagated identically. In a gradient pass one
padding candidate per padded row also runs through the candidate MLP and head and is sunk with a zero gradient: the
reference backpropagates exact zeros through its padded entries, so a nonfinite activation that appears only in
padding still turns the parameter gradients nonfinite and the frozen learner still refuses the update.

* one shared card-row encoder for permanent deck cards, offered cards and bundle cards;
* counted mean+max pooling for deck, relics and potion slots (any size, masked);
* the public map as a graph at width 64: two rounds of masked incoming/outgoing (parent/child) mean
  aggregation, pooled, plus the current node; route candidates also get their own node vector;
* a candidate's potions (the incoming one and, on a full-belt choice, the held one it replaces or drinks)
  through the same identity + static-definition potion encoder as the belt;
* state 256, candidate 64, head 320 -> 128 -> 1; padded candidates score NEG (never chosen).
"""
from __future__ import annotations

import torch
import torch.nn as nn
import torch.nn.functional as F

from .config import ModelConfig
from .contracts import CANDIDATE_KINDS, DECISION_FLOW
from .features import CAND_NUM, DECK_FEATS, ENCODING_SCHEMA, NODE_DYN, NODE_STATIC, SCREEN
from .metadata import ANCIENTS, EVENT_IDS, MAX_ENCHANT, MAX_ENCOUNTER, SELECTION_PURPOSES, PublicMetadata

MODEL_SCHEMA = "rs1/strategy_qnet/v2"  # v2: paired candidate potions (decision flow v2)
NEG = -1e9


def masked_pool(x: torch.Tensor, mask: torch.Tensor) -> torch.Tensor:
    """[B, L, H] rows, [B, L] mask -> [B, 2H + 1]: mean, max, log1p(count)."""
    m = mask.unsqueeze(-1).to(x.dtype)
    cnt = m.sum(1)
    mean = (x * m).sum(1) / cnt.clamp_min(1.0)
    mx = x.masked_fill(~mask.unsqueeze(-1), -1e4).max(1).values
    mx = torch.where(cnt > 0, mx, torch.zeros_like(mx))
    return torch.cat([mean, mx, torch.log1p(cnt)], -1)


def segment_mean(x: torch.Tensor, seg: torch.Tensor, n: int) -> torch.Tensor:
    out = x.new_zeros(n, x.shape[-1]).index_add_(0, seg, x)
    cnt = x.new_zeros(n).index_add_(0, seg, torch.ones_like(seg, dtype=x.dtype))
    return out / cnt.clamp_min(1.0).unsqueeze(-1)


def segment_max(x: torch.Tensor, seg: torch.Tensor, n: int) -> torch.Tensor:
    out = x.new_full((n, x.shape[-1]), -1e4)
    out = out.scatter_reduce(0, seg.unsqueeze(-1).expand_as(x), x, reduce="amax", include_self=True)
    return torch.where(out <= -1e4 + 1, torch.zeros_like(out), out)


class StrategyQNet(nn.Module):
    def __init__(self, metadata: PublicMetadata, cfg: ModelConfig | None = None):
        super().__init__()
        cfg = cfg or ModelConfig()
        self.cfg = cfg
        sizes = metadata.sizes
        H, D, K = cfg.card_dim, cfg.map_dim, cfg.kind_embed
        self.register_buffer("card_static", torch.as_tensor(metadata.card_static, dtype=torch.float32))
        self.register_buffer("relic_static", torch.as_tensor(metadata.relic_static, dtype=torch.float32))
        self.register_buffer("potion_static", torch.as_tensor(metadata.potion_static, dtype=torch.float32))
        # cards: learned identity + static definition + enchantment + per-copy features
        self.card_emb = nn.Embedding(sizes["cards"] + 1, cfg.card_embed, padding_idx=0)
        self.card_static_proj = nn.Linear(sizes["card_static"], cfg.card_embed)
        self.ench_emb = nn.Embedding(MAX_ENCHANT, 8)
        self.card_mlp = nn.Sequential(nn.Linear(2 * cfg.card_embed + 8 + len(DECK_FEATS), H), nn.ReLU(), nn.Linear(H, H))
        # relics / potions
        self.relic_emb = nn.Embedding(sizes["relics"] + 1, cfg.relic_embed, padding_idx=0)
        self.relic_mlp = nn.Sequential(nn.Linear(cfg.relic_embed + sizes["relic_static"] + 3, 32), nn.ReLU())
        self.potion_emb = nn.Embedding(sizes["potions"] + 1, cfg.potion_embed, padding_idx=0)
        self.potion_mlp = nn.Sequential(nn.Linear(cfg.potion_embed + sizes["potion_static"], 16), nn.ReLU())
        # map graph
        self.node_in = nn.Linear(NODE_STATIC + NODE_DYN, D)
        self.msg = nn.ModuleList([nn.ModuleDict({"self": nn.Linear(D, D), "parent": nn.Linear(D, D, bias=False),
                                                 "child": nn.Linear(D, D, bias=False)}) for _ in range(cfg.map_passes)])
        # screen ids
        self.event_emb = nn.Embedding(len(EVENT_IDS) + 1, cfg.event_embed)
        self.ancient_emb = nn.Embedding(len(ANCIENTS) + 1, 8)
        self.purpose_emb = nn.Embedding(SELECTION_PURPOSES + 1, 8)
        self.boss_emb = nn.Embedding(MAX_ENCOUNTER, 8)
        glob_dim = self._glob_width()
        state_in = glob_dim + SCREEN + cfg.event_embed + 8 + 8 + 8 + (2 * H + 1) + (2 * 32 + 1) + (2 * 16 + 1) \
            + 2 * D + D
        self.state = nn.Sequential(nn.Linear(state_in, cfg.state_dim), nn.ReLU(),
                                   nn.Linear(cfg.state_dim, cfg.state_dim), nn.ReLU())
        # candidates
        self.kind_emb = nn.Embedding(len(CANDIDATE_KINDS) + 1, K, padding_idx=0)
        cand_in = K + H + H + H + 2 * cfg.relic_embed + 2 * 16 + cfg.event_embed + D + len(CAND_NUM) + 6
        self.cand = nn.Sequential(nn.Linear(cand_in, cfg.candidate_dim), nn.ReLU(),
                                  nn.Linear(cfg.candidate_dim, cfg.candidate_dim), nn.ReLU())
        self.head = nn.Sequential(nn.Linear(cfg.state_dim + cfg.candidate_dim, cfg.head_hidden), nn.ReLU(),
                                  nn.Linear(cfg.head_hidden, 1))

    @staticmethod
    def _glob_width() -> int:
        from .contracts import PHASES
        from .metadata import FIGHT_OUTCOMES, ROOMS
        return 5 + 3 + 4 + 8 + len(PHASES) + 3 * len(ROOMS) + len(FIGHT_OUTCOMES)

    @property
    def config(self) -> dict:
        c = self.cfg
        return {"schema": MODEL_SCHEMA, "encoding_schema": ENCODING_SCHEMA, "decision_flow": DECISION_FLOW,
                "state_dim": c.state_dim, "candidate_dim": c.candidate_dim,
                "head": [c.state_dim + c.candidate_dim, c.head_hidden, 1], "map_dim": c.map_dim,
                "map_passes": c.map_passes, "card_dim": c.card_dim, "card_embed": c.card_embed,
                "relic_embed": c.relic_embed, "potion_embed": c.potion_embed, "kind_embed": c.kind_embed,
                "event_embed": c.event_embed, "parameters": self.parameter_count()}

    def parameter_count(self) -> int:
        return sum(p.numel() for p in self.parameters())

    # ------------------------------------------------------------------ encoders
    def encode_cards(self, idx: torch.Tensor, ench: torch.Tensor, feat: torch.Tensor) -> torch.Tensor:
        x = torch.cat([self.card_emb(idx), self.card_static_proj(self.card_static[idx]),
                       self.ench_emb(ench.clamp(0, MAX_ENCHANT - 1)), feat], -1)
        return self.card_mlp(x)

    def encode_potions(self, idx: torch.Tensor) -> torch.Tensor:
        return self.potion_mlp(torch.cat([self.potion_emb(idx), self.potion_static[idx]], -1))

    def encode_map(self, b: dict) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        x = b["node_feat"]
        seg = b["node_seg"]
        B = b["glob"].shape[0]
        h = F.relu(self.node_in(x))
        e = b["edges"]
        if e.numel():
            src, dst = e[:, 0], e[:, 1]
        for layer in self.msg:
            if e.numel():
                parents = segment_mean(h[src], dst, h.shape[0])  # incoming messages
                children = segment_mean(h[dst], src, h.shape[0])  # outgoing messages
            else:
                parents = children = torch.zeros_like(h)
            h = h + F.relu(layer["self"](h) + layer["parent"](parents) + layer["child"](children))
        current = x[:, NODE_STATIC + 1].unsqueeze(-1)  # node_dyn "current" flag
        cur = h.new_zeros(B, h.shape[-1]).index_add_(0, seg, h * current)
        pooled = torch.cat([segment_mean(h, seg, B), segment_max(h, seg, B)], -1)
        return h, pooled, cur

    def forward(self, b: dict) -> torch.Tensor:
        B = b["glob"].shape[0]
        deck = self.encode_cards(b["deck_card"], b["deck_ench"], b["deck_feat"])  # [B, D, H]
        deck_pool = masked_pool(deck, b["deck_mask"])
        rel = self.relic_mlp(torch.cat([self.relic_emb(b["relic_idx"]), self.relic_static[b["relic_idx"]],
                                        b["relic_feat"]], -1))
        rel_pool = masked_pool(rel, b["relic_mask"])
        pot = self.encode_potions(b["potion_idx"])
        held = b["potion_mask"] & (b["potion_idx"] > 0)
        pot_pool = masked_pool(pot, held)
        nodes, map_pool, cur = self.encode_map(b)
        ids = b["screen_ids"]
        state = self.state(torch.cat([
            b["glob"], b["screen"], self.event_emb(ids[:, 0]), self.ancient_emb(ids[:, 1]), self.purpose_emb(ids[:, 2]),
            self.boss_emb(ids[:, 3].clamp(0, MAX_ENCOUNTER - 1)), deck_pool, rel_pool, pot_pool, map_pool, cur], -1))

        return self._packed_candidates(b, deck, nodes, state)

    def _packed_candidates(self, b: dict, deck: torch.Tensor, nodes: torch.Tensor,
                           state: torch.Tensor) -> torch.Tensor:
        """The reference candidate formulas on the legal entries (and, with gradients, one padding sentinel per
        padded row); the result is the reference ``[B, C]`` Q grid with ``NEG`` at padding."""
        mask = b["cand_mask"]
        B, C = mask.shape
        num_all = b["cand_num"]
        dt = num_all.dtype
        lb, lc = mask.nonzero(as_tuple=True)  # legal entries, row-major (the reference's element order)
        n_legal = lb.numel()
        sentinel = torch.is_grad_enabled()
        if sentinel:
            pad_rows = (~mask).any(1).nonzero(as_tuple=True)[0]
            pad_cols = (~mask).to(torch.int8).argmax(1)[pad_rows]  # any padded column: all padded inputs are equal
            rb, rc = torch.cat([lb, pad_rows]), torch.cat([lc, pad_cols])
        else:
            rb, rc = lb, lc

        def at(name):
            return b[name][rb, rc]

        num = at("cand_num")
        # the offered card's own full public row, through the same encoder as the deck
        cc = at("cand_card")
        has_card = (cc > 0).unsqueeze(-1).to(dt)
        card = self.encode_cards(cc, at("cand_ench"), at("cand_card_feat")) * has_card
        # the deck instance a selection/smith candidate names
        di = at("cand_deck")
        deck_card = deck[rb, di.clamp_min(0)] * (di >= 0).unsqueeze(-1).to(dt)
        # bundle constituents: only actual member cards are encoded; an empty slot is the reference's padding-card
        # encoding times zero (exact zero when finite, propagated when not), summed in the reference slot order
        bi = at("cand_bundle")
        bmask = bi > 0
        H = deck.shape[-1]
        zero = torch.zeros(1, dtype=torch.long, device=bi.device)
        pad_card = self.encode_cards(zero, zero, b["cand_bundle_feat"].new_zeros(1, b["cand_bundle_feat"].shape[-1]))
        benc = (pad_card * 0).expand(bi.shape[0], bi.shape[1], H).clone()
        mb, ms = bmask.nonzero(as_tuple=True)
        if mb.numel():
            members = self.encode_cards(bi[mb, ms], b["cand_bundle_ench"][rb[mb], rc[mb], ms],
                                        b["cand_bundle_feat"][rb[mb], rc[mb], ms])
            benc = benc.index_put((mb, ms), members * bmask[mb, ms].unsqueeze(-1).to(dt))
        bundle = benc.sum(1) / bmask.sum(1, keepdim=True).clamp_min(1).to(dt)
        # route candidates: their own node vector
        ni = at("cand_node")
        node = nodes[ni.clamp_min(0)] * (ni >= 0).unsqueeze(-1).to(dt)
        cr, cr2, cp, cp2 = at("cand_relic"), at("cand_relic2"), at("cand_potion"), at("cand_potion2")
        rel1 = self.relic_emb(cr)
        rel2 = self.relic_emb(cr2)
        # incoming and held potion of a full-belt choice: identity + definition, each masked when absent
        pot1 = self.encode_potions(cp) * (cp > 0).unsqueeze(-1).to(dt)
        pot2 = self.encode_potions(cp2) * (cp2 > 0).unsqueeze(-1).to(dt)
        flags = torch.stack([has_card.squeeze(-1), (di >= 0).to(dt), bmask.any(-1).to(dt),
                             (ni >= 0).to(dt), (cr > 0).to(dt), (cp2 > 0).to(dt)], -1)
        cand = self.cand(torch.cat([self.kind_emb(at("cand_kind")), card, deck_card, bundle, rel1, rel2,
                                    pot1, pot2, self.event_emb(at("cand_event")), node, num, flags], -1))
        qv = self.head(torch.cat([state[rb], cand], -1)).squeeze(-1)
        q = torch.full((B, C), NEG, dtype=qv.dtype, device=qv.device)
        q = q.index_put((lb, lc), qv[:n_legal])
        if sentinel and qv.shape[0] > n_legal:
            q = _ZeroGradientSink.apply(q, qv[n_legal:])
        return q


class _ZeroGradientSink(torch.autograd.Function):
    """Forward: ``q`` unchanged (the padding sentinels' values are discarded, as ``masked_fill`` discards padding).
    Backward: an exact zero gradient into the sentinels, the reference's zero gradient at its padded entries."""

    @staticmethod
    def forward(ctx, q, padding_q):
        ctx.padding_shape = padding_q.shape
        return q.clone()

    @staticmethod
    def backward(ctx, grad):
        return grad, grad.new_zeros(ctx.padding_shape)
