using Sts2Emulator.Core.Rng;

namespace Sts2Emulator.Core.Run;

/// <summary>Which divination the sphere is set to. Mirrors the game's enum, values and all.</summary>
public enum CrystalSphereTool
{
    None = 0,
    Small = 1,
    Big = 2,
}

/// <summary>The five things the fog can be hiding, in the game's own enum order.</summary>
public enum CrystalSphereItemKind
{
    CardReward = 0,
    Curse = 1,
    Gold = 2,
    Potion = 3,
    Relic = 4,
}

/// <summary>
/// One thing buried in the sphere: what it is, how big a footprint it has, and -- once
/// placed -- where its top-left corner sits. Size is what makes the grid a game: a relic
/// covers sixteen cells and needs all sixteen uncovered before it is yours, while a small
/// gold covers one.
/// </summary>
public sealed class CrystalSphereItem
{
    public CrystalSphereItemKind Kind { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Card rarity for a card reward, potion rarity for a potion; unused otherwise.</summary>
    public int Rarity { get; init; }

    /// <summary>A big gold pile is worth 30 and covers two cells; a small one 10 and covers one.</summary>
    public bool IsBigGold { get; init; }

    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public bool Placed { get; set; }

    public CrystalSphereItem Clone() =>
        new()
        {
            Kind = Kind,
            Width = Width,
            Height = Height,
            Rarity = Rarity,
            IsBigGold = IsBigGold,
            X = X,
            Y = Y,
            Placed = Placed,
        };
}

/// <summary>
/// The Crystal Sphere minigame: an 11x11 grid of fog, fifteen things buried under it, and
/// three or six divinations to spend clearing cells. An item is only yours once every cell
/// it covers is clear, which is what makes the big tool -- a 3x3 blast -- and the small one
/// -- a single cell -- a real choice rather than a strictly-worse pair.
///
/// Everything here comes off the event's own Rng, so the board is fixed by the run seed the
/// moment the event is entered, and the same seed lays out the same board whichever option
/// opened it: both options roll nothing between CalculateVars and the constructor.
///
/// The order of the item list is load-bearing -- it is the order the game places them in,
/// and each placement is one draw -- as is the order cells are cleared within a single
/// divination, because that is the order items are revealed in and rewards are rolled in.
/// </summary>
public sealed class CrystalSphereGame
{
    public const int Size = 11;
    public const int CellCount = Size * Size;

    /// <summary>Fog, indexed <c>x * Size + y</c> to match the game's <c>cells[x, y]</c>.</summary>
    public bool[] Hidden = new bool[CellCount];

    /// <summary>Index into <see cref="Items"/> for the thing under each cell, or -1.</summary>
    public int[] CellItem = new int[CellCount];

    public List<CrystalSphereItem> Items = [];

    /// <summary>Items fully uncovered, in the order they came out. Rewards roll in this order.</summary>
    public List<int> Revealed = [];

    public int Divinations;
    public CrystalSphereTool Tool = CrystalSphereTool.Big;

    /// <summary>
    /// The game's <c>PlacedAllItems</c>. False means the board ran out of room for
    /// something, which on a fresh 11x11 it never does -- but the retry loop that follows
    /// it is what makes the draw count depend on it, so it is tracked rather than assumed.
    /// </summary>
    public bool PlacedAllItems;

    public static int Index(int x, int y) => x * Size + y;

    public bool IsHidden(int x, int y) => Hidden[Index(x, y)];

    public int ItemAt(int x, int y) => CellItem[Index(x, y)];

    public bool IsFinished => Divinations == 0;

    /// <summary>
    /// Lays out a board, exactly as the game's constructor does: fog everywhere, the four
    /// corners cleared out to a two-step diamond, then the fifteen items placed in order.
    /// </summary>
    public static CrystalSphereGame Create(GameRng rng, int divinations)
    {
        var game = new CrystalSphereGame { Divinations = divinations };
        Array.Fill(game.Hidden, true);
        Array.Fill(game.CellItem, -1);

        // The game seeds a list with the four corners and grows it twice by each cell's
        // horizontal and vertical neighbours, which reaches everything within two steps.
        // Duplicates are left in and simply cleared twice.
        var frontier = new List<(int X, int Y)>
        {
            (0, 0),
            (Size - 1, 0),
            (Size - 1, Size - 1),
            (0, Size - 1),
        };
        for (int round = 0; round < 2; round++)
        {
            var grown = new List<(int X, int Y)>(frontier);
            foreach (var cell in frontier)
            {
                grown.AddRange(Horizontal(cell.X, cell.Y));
            }

            foreach (var cell in frontier)
            {
                grown.AddRange(Vertical(cell.X, cell.Y));
            }

            frontier = grown;
        }

        foreach (var cell in frontier)
        {
            game.Hidden[Index(cell.X, cell.Y)] = false;
        }

        // PopulateItems is retried up to ten times if anything failed to fit, and the
        // game does NOT clear the item list in between -- so a failure leaves the earlier
        // attempt's items on the board and adds a second set. Mirrored rather than
        // tidied: an 11x11 board always fits on the first pass, and if it ever did not,
        // the draw count would depend on this.
        int attempts = 0;
        do
        {
            game.PlacedAllItems = game.PopulateItems(rng);
            attempts++;
        } while (!game.PlacedAllItems && attempts < 10);

        game.Tool = CrystalSphereTool.Big;
        return game;
    }

    /// <summary>
    /// The fifteen items, in the game's placement order. Each placement is one draw, so
    /// the order is the seed's order -- and the <c>flag = flag &amp;&amp; Place(...)</c>
    /// short-circuit means that once one item fails to fit, no later item is even
    /// attempted and no further draw is taken.
    /// </summary>
    private bool PopulateItems(GameRng rng)
    {
        var toPlace = new List<CrystalSphereItem>
        {
            new()
            {
                Kind = CrystalSphereItemKind.Relic,
                Width = 4,
                Height = 4,
            },
            CommonPotion(),
            CommonPotion(),
            new()
            {
                Kind = CrystalSphereItemKind.Potion,
                Width = 2,
                Height = 2,
                Rarity = (int)PotionRarity.Rare,
            },
            CardReward(CardRarity.Common),
            CardReward(CardRarity.Uncommon),
            CardReward(CardRarity.Rare),
            new()
            {
                Kind = CrystalSphereItemKind.Curse,
                Width = 2,
                Height = 2,
            },
        };
        for (int i = 0; i < 5; i++)
        {
            toPlace.Add(
                new CrystalSphereItem
                {
                    Kind = CrystalSphereItemKind.Gold,
                    Width = 1,
                    Height = 1,
                    IsBigGold = false,
                }
            );
        }

        for (int i = 0; i < 2; i++)
        {
            toPlace.Add(
                new CrystalSphereItem
                {
                    Kind = CrystalSphereItemKind.Gold,
                    Width = 2,
                    Height = 1,
                    IsBigGold = true,
                }
            );
        }

        bool placedAll = true;
        foreach (var item in toPlace)
        {
            if (placedAll)
            {
                placedAll = Place(item, rng);
            }

            Items.Add(item);
        }

        return placedAll;
    }

    private static CrystalSphereItem CommonPotion() =>
        new()
        {
            Kind = CrystalSphereItemKind.Potion,
            Width = 1,
            Height = 3,
            Rarity = (int)PotionRarity.Common,
        };

    private static CrystalSphereItem CardReward(CardRarity rarity) =>
        new()
        {
            Kind = CrystalSphereItemKind.CardReward,
            Width = 2,
            Height = 2,
            Rarity = (int)rarity,
        };

    /// <summary>
    /// Collects every top-left corner the item fits in -- x outer, y inner, which is the
    /// order the draw indexes into -- and takes one.
    /// </summary>
    private bool Place(CrystalSphereItem item, GameRng rng)
    {
        var spots = new List<(int X, int Y)>();
        for (int x = 0; x < Size; x++)
        {
            for (int y = 0; y < Size; y++)
            {
                if (CanPlaceHere(item, x, y))
                {
                    spots.Add((x, y));
                }
            }
        }

        if (spots.Count == 0)
        {
            return false;
        }

        var spot = rng.NextItem(spots);
        item.X = spot.X;
        item.Y = spot.Y;
        item.Placed = true;
        int index = Items.Count;
        for (int dx = 0; dx < item.Width; dx++)
        {
            for (int dy = 0; dy < item.Height; dy++)
            {
                CellItem[Index(spot.X + dx, spot.Y + dy)] = index;
            }
        }

        return true;
    }

    /// <summary>An item may only go where every cell it would cover is still fogged and empty.</summary>
    private bool CanPlaceHere(CrystalSphereItem item, int x, int y)
    {
        for (int dx = 0; dx < item.Width; dx++)
        {
            for (int dy = 0; dy < item.Height; dy++)
            {
                int cx = x + dx;
                int cy = y + dy;
                if (cx < 0 || cx >= Size || cy < 0 || cy >= Size)
                {
                    return false;
                }

                int cell = Index(cx, cy);
                if (!Hidden[cell] || CellItem[cell] != -1)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Spends one divination on a cell and returns the items it finished uncovering, in
    /// the order they came out. The big tool clears the eight neighbours and then the cell
    /// itself, which is the order the game walks them in and therefore the order rewards
    /// roll in.
    /// </summary>
    public List<int> Click(int x, int y)
    {
        var revealed = new List<int>();
        Divinations--;
        if (Tool != CrystalSphereTool.Big)
        {
            Clear(x, y, revealed);
        }
        else
        {
            foreach (var cell in Adjacent(x, y))
            {
                Clear(cell.X, cell.Y, revealed);
            }
        }

        Revealed.AddRange(revealed);
        return revealed;
    }

    /// <summary>
    /// Whether a divination here would uncover anything at all. A click on ground that is
    /// already clear is legal in the game and simply burns the divination; the action mask
    /// leaves it out, since nothing an agent could learn makes it worth taking.
    /// </summary>
    public bool WouldUncover(int x, int y, CrystalSphereTool tool)
    {
        if (tool == CrystalSphereTool.Big)
        {
            return Adjacent(x, y).Any(cell => Hidden[Index(cell.X, cell.Y)]);
        }

        return Hidden[Index(x, y)];
    }

    private void Clear(int x, int y, List<int> revealed)
    {
        int cell = Index(x, y);
        if (!Hidden[cell])
        {
            return;
        }

        Hidden[cell] = false;
        int item = CellItem[cell];
        if (item >= 0 && AllCellsClear(Items[item]))
        {
            revealed.Add(item);
        }
    }

    private bool AllCellsClear(CrystalSphereItem item)
    {
        for (int dx = 0; dx < item.Width; dx++)
        {
            for (int dy = 0; dy < item.Height; dy++)
            {
                if (Hidden[Index(item.X + dx, item.Y + dy)])
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The big tool's footprint: horizontal, then vertical, then diagonal, then the cell itself.</summary>
    private static List<(int X, int Y)> Adjacent(int x, int y)
    {
        var cells = new List<(int X, int Y)>();
        cells.AddRange(Horizontal(x, y));
        cells.AddRange(Vertical(x, y));
        for (int dx = -1; dx <= 1; dx += 2)
        {
            for (int dy = -1; dy <= 1; dy += 2)
            {
                if (x + dx >= 0 && x + dx < Size && y + dy >= 0 && y + dy < Size)
                {
                    cells.Add((x + dx, y + dy));
                }
            }
        }

        cells.Add((x, y));
        return cells;
    }

    private static IEnumerable<(int X, int Y)> Horizontal(int x, int y)
    {
        for (int dx = -1; dx <= 1; dx += 2)
        {
            if (x + dx >= 0 && x + dx < Size)
            {
                yield return (x + dx, y);
            }
        }
    }

    private static IEnumerable<(int X, int Y)> Vertical(int x, int y)
    {
        for (int dy = -1; dy <= 1; dy += 2)
        {
            if (y + dy >= 0 && y + dy < Size)
            {
                yield return (x, y + dy);
            }
        }
    }

    /// <summary>
    /// Moves everything the fog is still hiding, for a search that wants a plausible board
    /// rather than the real one. An item with any cell uncovered stays put -- the player
    /// has seen where it is, and the game shows a footprint the moment one cell of it
    /// clears -- while an item that has not shown itself at all is re-placed among the
    /// cells still under fog.
    ///
    /// A plain uniform draw, deliberately not the event's own stream: the point is to
    /// sample a world, not to reproduce this one. See docs/agent-interface.md.
    /// </summary>
    public void ResampleUnseenItems(Random rng)
    {
        var unseen = Items.Where(item => item.Placed && !IsTouched(item)).ToList();
        foreach (var item in unseen)
        {
            for (int dx = 0; dx < item.Width; dx++)
            {
                for (int dy = 0; dy < item.Height; dy++)
                {
                    CellItem[Index(item.X + dx, item.Y + dy)] = -1;
                }
            }
        }

        foreach (var item in unseen)
        {
            var spots = new List<(int X, int Y)>();
            for (int x = 0; x < Size; x++)
            {
                for (int y = 0; y < Size; y++)
                {
                    if (CanPlaceHere(item, x, y))
                    {
                        spots.Add((x, y));
                    }
                }
            }

            if (spots.Count == 0)
            {
                // Nowhere left, which the original layout proves is possible only if the
                // fog has shrunk a lot. Put it back where it was rather than losing it.
                spots.Add((item.X, item.Y));
            }

            var spot = spots[rng.Next(spots.Count)];
            item.X = spot.X;
            item.Y = spot.Y;
            int index = Items.IndexOf(item);
            for (int dx = 0; dx < item.Width; dx++)
            {
                for (int dy = 0; dy < item.Height; dy++)
                {
                    CellItem[Index(spot.X + dx, spot.Y + dy)] = index;
                }
            }
        }
    }

    /// <summary>Whether any cell of an item is out of the fog, which is when the game names it.</summary>
    private bool IsTouched(CrystalSphereItem item)
    {
        for (int dx = 0; dx < item.Width; dx++)
        {
            for (int dy = 0; dy < item.Height; dy++)
            {
                if (!Hidden[Index(item.X + dx, item.Y + dy)])
                {
                    return true;
                }
            }
        }

        return false;
    }

    public CrystalSphereGame Clone() =>
        new()
        {
            Hidden = (bool[])Hidden.Clone(),
            CellItem = (int[])CellItem.Clone(),
            Items = [.. Items.Select(item => item.Clone())],
            Revealed = [.. Revealed],
            Divinations = Divinations,
            Tool = Tool,
            PlacedAllItems = PlacedAllItems,
        };
}
