namespace Sts2Emulator.Core;

public enum OrbType
{
    Lightning,
    Frost,
    Dark,
    Plasma,
    Glass,
}

/// <summary>
/// One channelled orb. Most orbs are pure functions of their type and the player's Focus,
/// but two carry state of their own and it is per-ORB rather than per-player.
/// </summary>
/// <param name="EvokeValue">
/// <c>DarkOrb._evokeVal</c>: starts at a literal 6 — NOT Focus-modified, unlike everything
/// else about the orb — and each passive adds <c>PassiveVal</c>, which is. So Focus raises
/// what a Dark orb ACCUMULATES and not what it starts with.
/// </param>
/// <param name="PassiveValue">
/// <c>GlassOrb._passiveVal</c>: starts at 4 and DECAYS by one every time the orb triggers,
/// with a floor of zero. Focus is applied on top when the value is read, so a decayed
/// Glass orb under Focus is still worth something.
/// </param>
public readonly record struct OrbState(OrbType Type, int EvokeValue = 0, int PassiveValue = 0);
