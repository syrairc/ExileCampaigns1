// ExileCampaigns/ExileCampaigns.RouteStoreJson.cs
using System.IO;
using ExileCampaigns.Guide;

namespace ExileCampaigns;

// route.json runtime paths + read/write. RouteJson does the (de)serialization (Guide); this picks paths
// and handles first-run migration from the legacy override path.
public partial class ExileCampaigns
{
    private string UserRoutePath => Path.Combine(ConfigDirectory, "route", "route.json");
    private string BundledRoutePath => Path.Combine(DirectoryFullName, "Data", "poe1", "route", "route.json");

    // load the effective RouteDocument: user copy wins, else bundled.
    private RouteDocument LoadRouteDocument()
    {
        if (File.Exists(UserRoutePath))
            return RouteJson.Read(File.ReadAllText(UserRoutePath));
        if (File.Exists(BundledRoutePath))
            return RouteJson.Read(File.ReadAllText(BundledRoutePath));
        LogError("ExileCampaigns -> no route.json found (user or bundled). No steps loaded.");
        return new RouteDocument(2, new System.Collections.Generic.List<RouteStep>());
    }

    // persist the current edit store to the user route.json (the copy that wins on load). data-only; no
    // recompile. errors are logged, never thrown into the draw loop.
    private void SaveUserRoute()
    {
        if (_routeStore == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UserRoutePath)!);
            File.WriteAllText(UserRoutePath, RouteJson.Write(_routeStore.ToDocument()));
        }
        catch (System.Exception ex) { LogError($"ExileCampaigns -> user route.json write failed: {ex.Message}"); }
    }

}
