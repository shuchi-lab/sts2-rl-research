using Sts2Emulator.Core.Rng;

namespace Sts2Emulator.Core.Run;

/// <summary>
/// The game's <c>RelicGrabBag</c>: a run's relics are shuffled into per-rarity queues once,
/// at run start, and every relic reward pulls from a queue rather than rolling afresh.
///
/// This is not an optimisation of a roll -- it is a different distribution. A relic pulled
/// is gone for the run, the rarity is rolled separately from the relic, and an exhausted
/// rarity escalates into the next one up rather than re-rolling. The emulator used to
/// re-roll uniformly from a flat pool on the wrong stream, so every relic it ever handed
/// out was the wrong one.
///
/// Where the shuffle sits in the stream is the load-bearing part. <c>InitializeNewRun</c>
/// runs BEFORE <c>GenerateRooms</c>, so these are the first draws on <c>UpFront</c> --
/// which is why the emulator's opaque 232-draw prefix ahead of map generation was very
/// nearly this: 112 draws for the shared bag and 118 for the player's, 230 of the 232.
/// The two left over are <c>GenerateRooms</c>' own prefix.
/// </summary>
public sealed class RelicGrabBag
{
    /// <summary>
    /// The only rarities a player's bag holds. Starter, Event and Ancient relics are
    /// never rolled as a reward, so <c>Populate</c> drops them.
    /// </summary>
    private static readonly RelicRarity[] GrabBagRarities =
    [
        RelicRarity.Common,
        RelicRarity.Uncommon,
        RelicRarity.Rare,
        RelicRarity.Shop,
    ];

    private readonly Dictionary<RelicRarity, List<int>> _deques = [];

    /// <summary>
    /// The order the rarities were first seen while bucketing, because that is the order
    /// the game shuffles them in. <c>Populate</c> walks the pool and inserts a rarity's
    /// list on first sight, then iterates <c>_deques.Values</c> -- and a .NET Dictionary
    /// with no removals enumerates in insertion order. Shuffling the same buckets in a
    /// different order draws the same numbers into different queues.
    /// </summary>
    private readonly List<RelicRarity> _shuffleOrder = [];

    private readonly bool _refreshAllowed;
    private List<int> _originalRelics = [];

    public RelicGrabBag(bool refreshAllowed = false)
    {
        _refreshAllowed = refreshAllowed;
    }

    public bool IsPopulated => _deques.Count > 0;

    /// <summary>Event eligibility probes the same available queues without taking a relic.</summary>
    public bool HasAvailableRelics(Func<int, bool> isAllowedInRun)
    {
        foreach (var rarity in GrabBagRarities)
        {
            if (AvailableDeque(rarity, isAllowedInRun, _ => true) is not null)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Bucket the pool by rarity in pool order, then shuffle each bucket. The player's
    /// bag drops every rarity outside <see cref="GrabBagRarities"/> first; the shared bag
    /// keeps the pool exactly as given, which is why the two consume different numbers of
    /// draws from the same stream.
    /// </summary>
    public void Populate(IReadOnlyList<int> relicIds, GameRng rng, bool filterRarities)
    {
        if (IsPopulated)
        {
            throw new InvalidOperationException("Grab bag was already populated.");
        }

        foreach (int relicId in relicIds)
        {
            var rarity = GeneratedData.Relics.Get(relicId).Rarity;
            if (filterRarities && !GrabBagRarities.Contains(rarity))
            {
                continue;
            }

            _originalRelics.Add(relicId);
            if (!_deques.TryGetValue(rarity, out var deque))
            {
                deque = [];
                _deques[rarity] = deque;
                _shuffleOrder.Add(rarity);
            }

            deque.Add(relicId);
        }

        foreach (var rarity in _shuffleOrder)
        {
            rng.Shuffle(_deques[rarity]);
        }
    }

    /// <summary>
    /// <c>RelicFactory.RollRarity</c>: a single <c>NextFloat</c> off the player's rewards
    /// stream, split at 0.5 and 0.83. Shop rarity is never rolled -- shops ask for it by
    /// name.
    /// </summary>
    public static RelicRarity RollRarity(GameRng rng)
    {
        float roll = rng.NextFloat();
        return roll < 0.5f ? RelicRarity.Common
            : roll < 0.83f ? RelicRarity.Uncommon
            : RelicRarity.Rare;
    }

    /// <summary>
    /// Take the next relic of this rarity, from the front (rewards) or the back (shops),
    /// and remove it for the rest of the run. Returns null when nothing anywhere passes
    /// the filter.
    /// </summary>
    /// <param name="isAllowedInRun">
    /// <c>RelicModel.IsAllowed</c> for the run as it stands. A relic that fails is struck
    /// from the bag permanently, which is what the game's
    /// <c>RemoveDisallowedRelicsFromDeques</c> does on every pull -- so a chest relic
    /// stops being eligible once the run passes floor 41 and never comes back.
    /// </param>
    /// <param name="filter">
    /// Which relics this particular pull will accept. Unlike
    /// <paramref name="isAllowedInRun"/> this does not remove anything: a relic the
    /// filter skips is still there for the next pull.
    /// </param>
    public int? Pull(
        RelicRarity rarity,
        bool fromFront,
        Func<int, bool> isAllowedInRun,
        Func<int, bool>? filter = null
    )
    {
        filter ??= _ => true;
        var deque = AvailableDeque(rarity, isAllowedInRun, filter);
        if (deque is null || deque.Count == 0)
        {
            return null;
        }

        // The game walks the whole deque for the first entry passing the filter rather
        // than assuming the end is eligible.
        if (fromFront)
        {
            for (int i = 0; i < deque.Count; i++)
            {
                if (filter(deque[i]))
                {
                    int relicId = deque[i];
                    deque.RemoveAt(i);
                    return relicId;
                }
            }
        }
        else
        {
            for (int i = deque.Count - 1; i >= 0; i--)
            {
                if (filter(deque[i]))
                {
                    int relicId = deque[i];
                    deque.RemoveAt(i);
                    return relicId;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A relic pulled from the player's bag is struck from the shared one too
    /// (<c>RelicFactory.PullNextRelicFromFront</c>), and events that hand over a named
    /// relic take it out of circulation the same way.
    /// </summary>
    public void Remove(int relicId)
    {
        foreach (var deque in _deques.Values)
        {
            deque.Remove(relicId);
        }
    }

    /// <summary>
    /// The deque this pull will actually read. An exhausted rarity escalates
    /// Shop -> Common -> Uncommon -> Rare and then gives up; a bag that allows refreshing
    /// restores the rarity from its original contents first.
    /// </summary>
    private List<int>? AvailableDeque(
        RelicRarity rarity,
        Func<int, bool> isAllowedInRun,
        Func<int, bool> filter
    )
    {
        RemoveDisallowed(isAllowedInRun);
        var deque = Deque(rarity);
        if (deque.Count == 0 && _refreshAllowed)
        {
            RefreshRarity(rarity);
            RemoveDisallowed(isAllowedInRun);
            deque = Deque(rarity);
        }

        while (deque is not null && !deque.Any(filter))
        {
            rarity = rarity switch
            {
                RelicRarity.Shop => RelicRarity.Common,
                RelicRarity.Common => RelicRarity.Uncommon,
                RelicRarity.Uncommon => RelicRarity.Rare,
                _ => RelicRarity.None,
            };
            deque = rarity == RelicRarity.None ? null : Deque(rarity);
        }

        return deque;
    }

    private List<int> Deque(RelicRarity rarity)
    {
        if (!_deques.TryGetValue(rarity, out var deque))
        {
            deque = [];
            _deques[rarity] = deque;
            _shuffleOrder.Add(rarity);
        }

        return deque;
    }

    private void RefreshRarity(RelicRarity rarity)
    {
        var deque = Deque(rarity);
        deque.Clear();
        deque.AddRange(
            _originalRelics.Where(relicId => GeneratedData.Relics.Get(relicId).Rarity == rarity)
        );
    }

    private void RemoveDisallowed(Func<int, bool> isAllowedInRun)
    {
        foreach (var deque in _deques.Values)
        {
            deque.RemoveAll(relicId => !isAllowedInRun(relicId));
        }
    }

    /// <summary>
    /// <c>RelicModel.IsAllowed</c> for a solo run at this floor. Three relics gate on the
    /// player count -- Massive Scroll is multiplayer-only, Silver Crucible and Winged
    /// Boots are solo-only -- and a group of chest relics gate on
    /// <c>IsBeforeAct3TreasureChest</c>, which is <c>TotalFloor &lt; 41</c> solo. Lasting
    /// Candy also refuses a profile that has never finished a run; the emulator models a
    /// mature profile, so it falls through to the floor rule with the rest.
    /// </summary>
    public static Func<int, bool> AllowedInSoloRun(int totalFloor) =>
        relicId =>
        {
            // `MassiveScroll.IsAllowed => Players.Count > 1`. It is not unimplemented --
            // being absent from a solo run IS the whole of its behaviour, and its
            // AfterObtained (a three-card reward drawn from the MultiplayerOnly cards) is
            // unreachable here because nothing can ever hand it over.
            if (relicId == RunConstants.RelicMassiveScroll)
            {
                return false;
            }

            // `IsAllowed => IsBeforeAct3TreasureChest`, read off the generated data rather
            // than a list kept here. The list was transcribed from the relics' own
            // overrides and had drifted to fourteen where the game declares SEVENTEEN --
            // Meal Ticket, Old Coin and White Beast Statue were missing, so all three kept
            // being offered past floor 41. There is no moment at which a hand-kept list is
            // discovered to be short; a generated one is a diff.
            return !GeneratedData.Relics.Get(relicId).StopsAfterAct3Chest || totalFloor < 41;
        };

    /// <summary>Every relic still in the bag, for tests and for reporting.</summary>
    public IEnumerable<int> Remaining => _deques.Values.SelectMany(deque => deque);

    public RelicGrabBag Clone()
    {
        var copy = new RelicGrabBag(_refreshAllowed) { _originalRelics = [.. _originalRelics] };
        foreach (var rarity in _shuffleOrder)
        {
            copy._deques[rarity] = [.. _deques[rarity]];
            copy._shuffleOrder.Add(rarity);
        }

        return copy;
    }
}
