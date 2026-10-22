using System.Collections.Frozen;
using System.Text.Json;
using Sts2Emulator.GeneratedData;

namespace Sts2Emulator.Core.Run;

/// <summary>
/// An immutable unlock snapshot for a run. Eligibility is distinct from whether
/// a card's effect is implemented by the emulator, and from reward rarity filters.
/// </summary>
public sealed class ProgressionContext
{
    private readonly FrozenSet<string> _revealedEpochs;
    private readonly FrozenSet<string> _encountersSeen;
    private readonly FrozenSet<string>? _discoveredActs;
    private readonly FrozenDictionary<string, int> _characterWins;

    public int NumberOfRuns { get; }

    public static ProgressionContext AllUnlocked { get; } =
        new(BetaProgressionData.Epochs, [], 999999999);

    public ProgressionContext(
        IEnumerable<string> revealedEpochs,
        IEnumerable<string> encountersSeen,
        int numberOfRuns
    )
        : this(revealedEpochs, encountersSeen, numberOfRuns, null) { }

    public ProgressionContext(
        IEnumerable<string> revealedEpochs,
        IEnumerable<string> encountersSeen,
        int numberOfRuns,
        IEnumerable<string>? discoveredActs,
        IEnumerable<KeyValuePair<string, int>>? characterWins = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(numberOfRuns);
        _revealedEpochs = revealedEpochs
            .Where(BetaProgressionData.Epochs.Contains)
            .ToFrozenSet(StringComparer.Ordinal);
        _encountersSeen = encountersSeen.ToFrozenSet(StringComparer.Ordinal);
        // Existing callers without a discovery snapshot retain mature-profile behavior.
        _discoveredActs = discoveredActs?.ToFrozenSet(StringComparer.Ordinal);
        _characterWins = (characterWins ?? []).ToFrozenDictionary(StringComparer.Ordinal);
        NumberOfRuns = numberOfRuns;
    }

    /// <summary>
    /// combat_data_simulator fork: the constructor inputs, so a battle template can carry
    /// the unlock snapshot and rebuild an equal context. Sorted for stable output.
    /// </summary>
    public (string[] RevealedEpochs, string[] EncountersSeen, int NumberOfRuns, string[]? DiscoveredActs, KeyValuePair<string, int>[] CharacterWins) ToSnapshot() =>
        (
            _revealedEpochs.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            _encountersSeen.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            NumberOfRuns,
            _discoveredActs?.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            _characterWins.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray()
        );

    public bool IsEpochRevealed(string epoch) => _revealedEpochs.Contains(epoch);

    public bool IsActDiscovered(string actId) => _discoveredActs?.Contains(actId) ?? true;

    public int WinsForCharacter(string entry) => _characterWins.GetValueOrDefault(entry);

    public bool IsCardAvailableInSinglePlayer(string entry) =>
        BetaProgressionData.Cards.Contains(entry)
        && !BetaProgressionData.MultiplayerCards.Contains(entry)
        && (
            !BetaProgressionData.CardEpochs.TryGetValue(entry, out var epoch)
            || _revealedEpochs.Contains(epoch)
        );

    public bool IsRelicUnlocked(string entry) =>
        IsUnlocked(entry, BetaProgressionData.Relics, BetaProgressionData.RelicEpochs);

    public bool IsPotionUnlocked(string entry) =>
        IsUnlocked(entry, BetaProgressionData.Potions, BetaProgressionData.PotionEpochs);

    public bool IsCharacterUnlocked(string entry) =>
        IsUnlocked(entry, BetaProgressionData.Characters, BetaProgressionData.CharacterEpochs);

    public bool IsSharedAncientUnlocked(string entry) =>
        IsUnlocked(
            entry,
            BetaProgressionData.SharedAncients,
            BetaProgressionData.SharedAncientEpochs
        );

    public bool AllCharactersUnlocked => BetaProgressionData.Characters.All(IsCharacterUnlocked);

    internal bool IsPotionIdUnlocked(int id) =>
        BetaProgressionData.PotionEntries.TryGetValue(Potions.Get(id).Name, out var entry)
        && IsPotionUnlocked(entry);

    private bool IsUnlocked(
        string entry,
        FrozenSet<string> known,
        FrozenDictionary<string, string> gates
    ) =>
        known.Contains(entry)
        && (!gates.TryGetValue(entry, out var epoch) || _revealedEpochs.Contains(epoch));

    public static ProgressionContext FromProgressSaveJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("schema_version").GetInt32() != 24)
            throw new ArgumentException("Expected v0.111.0 progress schema 24.", nameof(json));
        var epochs = root.GetProperty("epochs")
            .EnumerateArray()
            .Where(e => e.GetProperty("state").GetString() == "revealed")
            .Select(e => e.GetProperty("id").GetString()!);
        var encounters = root.GetProperty("encounter_stats")
            .EnumerateArray()
            .Select(e => e.GetProperty("encounter_id").GetString()!);
        int runs = root.GetProperty("character_stats")
            .EnumerateArray()
            .Sum(c =>
                c.GetProperty("total_wins").GetInt32() + c.GetProperty("total_losses").GetInt32()
            );
        var discoveredActs = root.TryGetProperty("discovered_acts", out var discovered)
            ? discovered.EnumerateArray().Select(a => a.GetString()!)
            : [];
        var characterWins = root.GetProperty("character_stats")
            .EnumerateArray()
            .Where(c => c.TryGetProperty("id", out _))
            .Select(c => new KeyValuePair<string, int>(
                c.GetProperty("id").GetString()!.Split('.')[^1],
                c.GetProperty("total_wins").GetInt32()
            ));
        return new ProgressionContext(epochs, encounters, runs, discoveredActs, characterWins);
    }
}
