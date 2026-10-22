namespace Sts2Emulator.Core;

/// <summary>The unexecuted part of an enemy side turn waiting for a player choice.</summary>
public sealed class PendingEnemyTurn
{
    public EnemyState[] RemainingEnemies = [];
    public int PlayerHpBefore;
    public int[] EnemyHpsBefore = [];

    /// <summary>Reward damage banked before an end-turn death re-read <see cref="EnemyHpsBefore"/>.</summary>
    public int EnemyDamageBanked;
}
