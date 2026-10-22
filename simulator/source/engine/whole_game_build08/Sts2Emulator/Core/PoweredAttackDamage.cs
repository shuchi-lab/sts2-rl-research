namespace Sts2Emulator.Core;

/// <summary>
/// A powered hit before listener bonuses, with card-dependent multipliers deferred
/// until after those bonuses. Enchantment preprocessing is already in Amount.
/// </summary>
public readonly record struct PoweredAttackDamage(decimal Amount, decimal Multiplier = 1m)
{
    public static implicit operator PoweredAttackDamage(int amount) => new(amount);

    public static implicit operator PoweredAttackDamage(decimal amount) => new(amount);

    public static PoweredAttackDamage operator +(PoweredAttackDamage damage, decimal bonus) =>
        damage with
        {
            Amount = damage.Amount + bonus,
        };

    public static PoweredAttackDamage operator *(PoweredAttackDamage damage, decimal multiplier) =>
        damage with
        {
            Multiplier = damage.Multiplier * multiplier,
        };
}
