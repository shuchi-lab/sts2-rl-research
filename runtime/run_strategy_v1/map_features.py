"""Inexpensive backward dynamic-programming summaries of the PUBLIC act map (Task 3).

Inputs are only the displayed node types and directed edges (no ``RunMapNode.EncounterId``, no
future normal/elite/event sequence). For every node: whether a path reaches the boss, the distance
to it, the log number of paths, the minimum/maximum count of each room type over all remaining
paths (the node itself included), whether a rest site is reachable before any elite, and whether
the node is reachable from the current position. A cycle or an edge to a missing node is a
contract error.
"""
from __future__ import annotations

import math
from collections import deque

from .contracts import ContractError

NODE_NONE, NODE_NORMAL, NODE_ELITE, NODE_REST, NODE_SHOP, NODE_TREASURE, NODE_BOSS, NODE_UNKNOWN, NODE_ANCIENT = range(9)
NODE_TYPES = {0: "none", 1: "normal", 2: "elite", 3: "rest", 4: "shop", 5: "treasure", 6: "boss", 7: "unknown",
              8: "ancient"}
COUNTED = ((NODE_NORMAL, "normal"), (NODE_ELITE, "elite"), (NODE_REST, "rest"), (NODE_SHOP, "shop"),
           (NODE_TREASURE, "treasure"), (NODE_UNKNOWN, "unknown"))
FEATURE_NAMES = (["reaches_boss", "distance_to_boss", "log_paths"]
                 + [f"min_{n}" for _, n in COUNTED] + [f"max_{n}" for _, n in COUNTED]
                 + ["rest_before_elite", "reachable_from_current"])
DIST_SCALE, LOG_SCALE, COUNT_SCALE = 16.0, 5.0, 8.0


def _check(n: int, edges) -> list[list[int]]:
    children = [[] for _ in range(n)]
    for a, b in edges:
        if not (0 <= a < n and 0 <= b < n):
            raise ContractError(f"map edge ({a},{b}) has a missing endpoint ({n} nodes)")
        children[a].append(b)
    return children


def topological_order(n: int, edges) -> list[int]:
    children = _check(n, edges)
    indeg = [0] * n
    for a, b in edges:
        indeg[b] += 1
    q = deque(i for i in range(n) if indeg[i] == 0)
    order = []
    while q:
        v = q.popleft()
        order.append(v)
        for c in children[v]:
            indeg[c] -= 1
            if indeg[c] == 0:
                q.append(c)
    if len(order) != n:
        raise ContractError("the public map contains a cycle")
    return order


def reachable_from(n: int, edges, current: int) -> set[int]:
    if current < 0:
        return set()
    children = _check(n, edges)
    seen, stack = {current}, [current]
    while stack:
        for c in children[stack.pop()]:
            if c not in seen:
                seen.add(c)
                stack.append(c)
    return seen


def public_path_features(nodes: list[dict], edges: list[tuple[int, int]], current: int) -> dict[int, list[float]]:
    n = len(nodes)
    order = topological_order(n, edges)
    children = _check(n, edges)
    types = [int(x["node_type"]) for x in nodes]
    if types.count(NODE_BOSS) != 1:
        raise ContractError(f"the public map must show exactly one boss node, found {types.count(NODE_BOSS)}")
    INF = float("inf")
    reach = [False] * n
    dist = [INF] * n
    paths = [0] * n
    mins = {t: [INF] * n for t, _ in COUNTED}
    maxs = {t: [-INF] * n for t, _ in COUNTED}
    rbe = [False] * n
    for v in reversed(order):
        t = types[v]
        if t == NODE_BOSS:
            reach[v], dist[v], paths[v] = True, 0, 1
            for c, _ in COUNTED:
                mins[c][v] = maxs[c][v] = 0
        else:
            kids = [c for c in children[v] if reach[c]]
            if kids:
                reach[v] = True
                dist[v] = 1 + min(dist[c] for c in kids)
                paths[v] = sum(paths[c] for c in kids)
                for c, _ in COUNTED:
                    own = int(t == c)
                    mins[c][v] = own + min(mins[c][k] for k in kids)
                    maxs[c][v] = own + max(maxs[c][k] for k in kids)
        rbe[v] = t == NODE_REST or (t != NODE_ELITE and any(rbe[c] for c in children[v]))
    here = reachable_from(n, edges, current)
    out = {}
    for v in range(n):
        if reach[v]:
            row = [1.0, dist[v] / DIST_SCALE, math.log1p(paths[v]) / LOG_SCALE]
            row += [mins[c][v] / COUNT_SCALE for c, _ in COUNTED] + [maxs[c][v] / COUNT_SCALE for c, _ in COUNTED]
        else:
            row = [0.0, 0.0, 0.0] + [0.0] * (2 * len(COUNTED))
        row += [float(rbe[v]), float(v in here)]
        out[v] = row
    return out
