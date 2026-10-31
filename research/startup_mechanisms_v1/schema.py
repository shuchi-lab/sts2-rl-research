"""Frozen v1 field order; all positions in this module are zero-based."""

SCHEMA = "startup-mechanisms/v1"
MECHANISMS = ("rupture", "demon_form", "exhaust_ashen", "hellraiser")
ROLES = ("core", "producer", "payoff", "supply")
STATUS = ("build_enabled", "online", "payoff_in_hand", "payoff_legal_now")
CORES = ("Rupture", "DemonForm", "AshenStrike", "Hellraiser")
CARD_FIELDS = tuple(f"mechanism:{m}:{r}" for m in MECHANISMS for r in ROLES)
GLOBAL_FIELDS = tuple(f"mechanism:{m}:{s}" for m in MECHANISMS for s in STATUS) + (
    "mechanism:strength", "mechanism:rupture_rate", "mechanism:demon_form_rate",
    "mechanism:exhaust_count", "mechanism:draw_count", "mechanism:strike_draw_count",
    "mechanism:strike_discard_count", "mechanism:effect_draw_allowed_now",
)
BASE_CARD_WIDTH = 84
EXHAUST_THRESHOLD = 3
NUMERIC_GLOBAL_SLICE = slice(16, 23)
