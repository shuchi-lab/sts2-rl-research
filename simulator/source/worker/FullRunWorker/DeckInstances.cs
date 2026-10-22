using Sts2Emulator.Core;

namespace FullRunWorker;

/// <summary>
/// Stable bridge-owned identities for the run's permanent deck cards. The engine's deck is a
/// list of value records with no persistent id (its <c>DeckOrigin</c> stamp is rewritten at every
/// combat entry), so the bridge aligns each new deck list against the previous one after every
/// engine call: an unchanged identity at the same position keeps its id, an in-place upgrade or
/// enchantment of the same card keeps its id, and everything else is matched by an ordered
/// longest-common-subsequence over card identity. Unmatched cards are new instances.
/// </summary>
/// <remarks>
/// An id names a card the player can point at; it is never a model feature. A transform (same
/// position, different card) is a new instance by construction, so a two-card transform cannot
/// pick the card its first pick produced.
/// </remarks>
public sealed class DeckInstances
{
    private List<CardInstance> _cards = [];
    private List<int> _ids = [];
    private int _next;

    public IReadOnlyList<int> Ids => _ids;

    public static bool SameIdentity(CardInstance a, CardInstance b) =>
        a.DefId == b.DefId && a.TinkerType == b.TinkerType && a.TinkerRider == b.TinkerRider;

    public static bool SameCard(CardInstance a, CardInstance b) =>
        SameIdentity(a, b) && a.Upgraded == b.Upgraded && a.Enchantment == b.Enchantment
        && a.EnchantAmount == b.EnchantAmount;

    public void Sync(IReadOnlyList<CardInstance> deck)
    {
        var old = _cards;
        var oldIds = _ids;
        var ids = new int[deck.Count];
        Array.Fill(ids, -1);

        if (old.Count == deck.Count)
        {
            bool aligned = true;
            for (int i = 0; i < deck.Count; i++)
            {
                if (!SameIdentity(old[i], deck[i]))
                {
                    aligned = false;
                    break;
                }
            }
            if (aligned)
            {
                _cards = [.. deck];
                _ids = [.. oldIds];
                return;
            }
        }

        // Ordered LCS on full card equality first (pure insertions/removals), then on identity
        // for what remains (an in-place upgrade/enchant next to a removal).
        Match(old, deck, oldIds, ids, SameCard);
        Match(old, deck, oldIds, ids, SameIdentity);
        for (int i = 0; i < ids.Length; i++)
        {
            if (ids[i] < 0)
                ids[i] = _next++;
        }
        _cards = [.. deck];
        _ids = [.. ids];
    }

    private static void Match(List<CardInstance> old, IReadOnlyList<CardInstance> now, List<int> oldIds, int[] ids,
        Func<CardInstance, CardInstance, bool> eq)
    {
        var usedOld = new HashSet<int>(oldIds.Select((id, i) => (id, i)).Where(x => ids.Contains(x.id)).Select(x => x.i));
        int n = old.Count, m = now.Count;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                bool ok = !usedOld.Contains(i) && ids[j] < 0 && eq(old[i], now[j]);
                dp[i, j] = ok ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }
        int a = 0, b = 0;
        while (a < n && b < m)
        {
            bool ok = !usedOld.Contains(a) && ids[b] < 0 && eq(old[a], now[b]);
            if (ok && dp[a, b] == dp[a + 1, b + 1] + 1)
            {
                ids[b] = oldIds[a];
                usedOld.Add(a);
                a++;
                b++;
            }
            else if (dp[a + 1, b] >= dp[a, b + 1])
                a++;
            else
                b++;
        }
    }

    public int IndexOf(int instanceId) => _ids.IndexOf(instanceId);

    public void CopyFrom(DeckInstances other)
    {
        _cards = [.. other._cards];
        _ids = [.. other._ids];
        _next = other._next;
    }
}
