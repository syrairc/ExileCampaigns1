using System.Security.Cryptography;
using System.Text;

namespace ExileCampaigns.Guide;

// non-reversible short tag for a profile id, so logs + the shared diagnostics export can tell characters
// apart without ever writing the actual character name. stable per name, can't be turned back into the name.
public static class ProfileMask
{
    public static string Mask(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return "(none)";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(profile.Trim()));
        var sb = new StringBuilder("char-", 11);
        for (int i = 0; i < 3; i++) sb.Append(hash[i].ToString("x2"));   // 3 bytes -> 6 hex
        return sb.ToString();
    }
}
