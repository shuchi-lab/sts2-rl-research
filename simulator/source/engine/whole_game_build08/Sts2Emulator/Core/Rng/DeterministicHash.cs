using System.IO.Hashing;
using System.Text;

namespace Sts2Emulator.Core.Rng;

public static class DeterministicHash
{
    /// <summary>v0.111.0 hashes UTF-8 bytes with xxHash64, seed zero.</summary>
    public static ulong GetDeterministicHashCode(string value) =>
        XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(value));

    /// <summary>Only run seeds with the case-sensitive "old" prefix use this.</summary>
    public static int GetDeterministicHashCodeOld(string value)
    {
        unchecked
        {
            int hash1 = 352654597;
            int hash2 = 352654597;

            for (int i = 0; i < value.Length; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ value[i];
                if (i == value.Length - 1)
                {
                    break;
                }

                hash2 = ((hash2 << 5) + hash2) ^ value[i + 1];
            }

            return hash1 + hash2 * 1566083941;
        }
    }
}
