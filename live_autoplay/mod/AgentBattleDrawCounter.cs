using System.Collections.Generic;
using System.Linq;

namespace STS2_MCP;

// Frozen build08 CardsDrawnThisCombat increments only in CardEffects.DrawOneCard. The opening
// hand (5 + ExtraOpeningHandDraw: Bag of Preparation, Ring of the Snake, ...) is dealt by
// CombatFactory.DrawOpeningHand, which moves cards without counting them; later turn-start
// hand draws (DrawHandCards) and every effect/relic draw go through DrawOneCard and count.
// Native v0.111.0 records one CardDrawnEntry per drawn card. Its only FromHandDraw=true draw
// is CombatManager.SetupPlayerTurn, and the entry snapshots the player's TurnNumber (1 until
// the first turn ends), so the uncounted opening hand is exactly FromHandDraw on turn 1.
readonly record struct BattleDrawnCard(bool FromHandDraw, int PlayerTurn);

static class BattleDrawCounter
{
    public static int CardsDrawnThisCombat(IEnumerable<BattleDrawnCard> draws) =>
        draws.Count(d => !(d.FromHandDraw && d.PlayerTurn == 1));
}
