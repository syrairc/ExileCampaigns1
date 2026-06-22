// ExileCampaigns/Guide/Fingerprint.cs
namespace ExileCampaigns.Guide;

// stable content hash of a step's text, punctuation/case-insensitive (shares QuestFlagMatch.Normalize) so it
// tracks real wording, not formatting noise. used to detect base drift under a stable (area, ordinal) key.
public static class Fingerprint
{
    public static string Of(string text)
    {
        var norm = QuestFlagMatch.Normalize(text ?? "");
        uint h = 2166136261;
        foreach (var c in norm) { h ^= c; h *= 16777619; }
        return "fnv1a:" + h.ToString("x8");
    }
}
