using System.Text;

namespace ExileCampaigns.Guide;

// join between a curated quest-flag entry and a route step. in Guide (no ExileCore / Newtonsoft dep) so
// the runtime flag-advance poller and the tests share the exact same logic.
public static class QuestFlagMatch
{
    // does step text contain the objective the flag completes? "match" mirrors the fragment source (e.g.
    // "kill Beira of the Rotten Pack", "reward_quest|+20 Max Life"): take the part after the last '|',
    // normalise both sides to lowercase alphanumerics, test containment. normalising drops punctuation diffs.
    public static bool Matches(string stepText, string matchSpec)
    {
        if (string.IsNullOrEmpty(matchSpec)) return false;
        var pipe = matchSpec.LastIndexOf('|');
        var core = pipe >= 0 ? matchSpec[(pipe + 1)..] : matchSpec;
        var normCore = Normalize(core);
        if (normCore.Length < 3) return false;                       // too generic to trust
        return Normalize(stepText).Contains(normCore);
    }

    // Lowercase, keep [a-z0-9 ], collapse whitespace. So "reward_quest|+20 Max Life" -> "20 max life",
    // and the rendered "take +20 Max Life" -> "take 20 max life", which contains it.
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        bool lastSpace = false;
        foreach (var ch in s)
        {
            char c = char.ToLowerInvariant(ch);
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(c);
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }
        return sb.ToString().Trim();
    }
}
