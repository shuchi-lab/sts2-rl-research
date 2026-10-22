namespace Sts2Emulator.Core.Rng;

/// <summary>
/// Port of the game's <c>MegaCrit.Sts2.Core.Helpers.SeedHelper</c>.
/// </summary>
public static class SeedHelper
{
    /// <summary>
    /// The alphabet the game generates seeds from — note there is no <c>I</c> and no
    /// <c>O</c>, which is why <see cref="Canonicalize"/> folds them into 1 and 0.
    /// </summary>
    public const string Characters = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>
    /// Fold a typed seed the way the game does before it ever hashes one.
    /// <para>
    /// <c>StartRunLobby.BeginRunLocally</c> runs every chosen seed through this, so the
    /// run's gen seed comes from the canonical form — hash the raw string instead and
    /// any seed containing lowercase, <c>I</c>, <c>O</c> or stray whitespace silently
    /// derives a different seed than the live run it is supposed to reproduce.
    /// </para>
    /// </summary>
    public static string Canonicalize(string seed) =>
        seed.ToUpperInvariant().Replace('O', '0').Replace('I', '1').Trim();
}
