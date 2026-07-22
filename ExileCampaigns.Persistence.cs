using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ExileCampaigns.Build;
using ExileCampaigns.Guide;
using Newtonsoft.Json.Linq;

namespace ExileCampaigns;

// persists route position per character (reload/char swap keeps your place) + copies bundled routes into
// the config folder for editing. one profile file per character under ConfigDirectory\profiles, active one follows the logged-in name
public partial class ExileCampaigns
{
    private int _lastSavedStep = -1;
    private string _charName = "";     // active profile = character name; "" before a char is loaded

    private string ProfilesDir => Path.Combine(ConfigDirectory, "profiles");
    private string LegacyProgressPath => Path.Combine(ConfigDirectory, "progress.json");
    private string ProgressPath => string.IsNullOrEmpty(_charName)
        ? LegacyProgressPath                                              // pre-login fallback
        : Path.Combine(ProfilesDir, SanitizeProfile(_charName) + ".json");

    // switch active profile on character change. banks the outgoing one, then loads (or starts fresh) the
    // incoming one. no-op if name is unchanged/empty
    private void SwitchProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == _charName) return;

        SaveProgress();   // bank current profile (under the old name) before switching away

        var wasDefault = string.IsNullOrEmpty(_charName);
        _charName = name;

        try { Directory.CreateDirectory(ProfilesDir); } catch { /* config dir not writable */ }

        // one-time migration: first real character inherits the pre-profiles progress.json
        if (wasDefault && !File.Exists(ProgressPath) && File.Exists(LegacyProgressPath))
        {
            try { File.Move(LegacyProgressPath, ProgressPath); } catch { /* leave legacy in place */ }
        }

        // brand-new profile (no saved file even after migration) -> offer league-start mode once it's drawn
        if (!File.Exists(ProgressPath)) _leagueStartPromptPending = true;

        LoadProgress();
        InitStats();      // new char -> fresh run timer/splits
        // masked, not the raw name: this lands in ExileCore's shared Verbose log
        LogMessage($"ExileCampaigns -> profile {ProfileMask.Mask(_charName)} active (step {_route.Current + 1}).");
    }

    private void LoadProgress()
    {
        _build = new BuildPlan();      // never inherit the outgoing char's plan
        _buildIndex.Rebuild(_build);
        try
        {
            if (!File.Exists(ProgressPath))
            {
                _route.SetCurrent(0);     // no saved progress -> start at the top
                _lastSavedStep = _route.Current;
                return;
            }
            var o = JObject.Parse(File.ReadAllText(ProgressPath));
            // build is optional: profiles written before this feature simply have no key
            _build = o["build"]?.ToObject<BuildPlan>() ?? new BuildPlan();
            _buildIndex.Rebuild(_build);
            var savedId = (string?)o["stepId"];
            int savedIndex = (int?)o["step"] ?? 0;
            SeekSavedStep(savedId, savedIndex);
        }
        catch { /* invalid saved progress */ }
    }

    // seek the live route to a saved position: prefer stepId (survives reshuffles/inserts), else the index.
    // SetCurrent clamps + snaps off headers either way.
    private void SeekSavedStep(string? savedId, int savedIndex)
    {
        if (!string.IsNullOrEmpty(savedId))
        {
            for (int i = 0; i < _route.Steps.Count; i++)
                if (_route.Steps[i].Model?.Id == savedId) { _route.SetCurrent(i); _lastSavedStep = _route.Current; return; }
        }
        _route.SetCurrent(savedIndex);
        _lastSavedStep = _route.Current;
    }

    // copy another profile's route position + build into the active profile. keeps our identity (character
    // + filename), adopts their step and plan, then persists. irreversible: overwrites current progress+build.
    private void CopyProfileInto(string sourceName)
    {
        if (string.IsNullOrEmpty(_charName) || sourceName == _charName) return;
        try
        {
            var srcPath = Path.Combine(ProfilesDir, SanitizeProfile(sourceName) + ".json");
            if (!File.Exists(srcPath)) { ShowToast("Source profile not found", ToastLevel.Error); return; }
            var src = JObject.Parse(File.ReadAllText(srcPath));

            _build = src["build"]?.ToObject<BuildPlan>() ?? new BuildPlan();
            _buildIndex.Rebuild(_build);
            SeekSavedStep((string?)src["stepId"], (int?)src["step"] ?? 0);

            SaveProgress();   // write into the active profile file under _charName
            ShowToast($"Copied '{sourceName}' into '{_charName}'", ToastLevel.Success);
        }
        catch (Exception ex)
        {
            LogError($"ExileCampaigns -> copy profile failed: {ex.Message}");
            ShowToast("Copy failed", ToastLevel.Error);
        }
    }

    // write current step on change (called each Tick; cheap, only writes when changed)
    private void MaybeSaveProgress()
    {
        if (_route.Current == _lastSavedStep) return;
        SaveProgress();
    }

    private void SaveProgress()
    {
        _lastSavedStep = _route.Current;
        try
        {
            var dir = Path.GetDirectoryName(ProgressPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // stepId goes alongside step index so id-based restore can take over once the route is stable
            File.WriteAllText(ProgressPath, new JObject
            {
                ["character"] = _charName,
                ["area"] = _areaId,
                ["step"] = _route.Current,
                ["stepId"] = _route.CurrentStep?.Model?.Id ?? "",
                ["build"] = JObject.FromObject(_build),
            }.ToString());
        }
        catch { /* config dir not writable */ }
    }

    // reset a profile back to step 1. if it's active, reset the live route too; else just rewrite its file on disk
    private void ResetProfileProgress(string name)
    {
        if (name == _charName)
        {
            _route.SetCurrent(0);
            _lastSavedStep = _route.Current;
            SaveProgress();
            return;
        }
        try
        {
            Directory.CreateDirectory(ProfilesDir);
            var path = Path.Combine(ProfilesDir, SanitizeProfile(name) + ".json");
            // merge into the existing doc, don't replace it - a fresh object would wipe the build key
            var o = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
            o["character"] = name;
            o["area"] = "";
            o["step"] = 0;
            o["stepId"] = "";
            File.WriteAllText(path, o.ToString());
        }
        catch (Exception ex) { LogError($"ExileCampaigns -> reset progress failed: {ex.Message}"); }
    }

    // Windows reserved device names: illegal as a filename stem even with an extension (e.g. "CON.json")
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // arbitrary name -> safe filename stem: invalid chars to '_', strip trailing dots/spaces (Windows
    // drops them), cap length, dodge reserved device names
    private static string SanitizeProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "_default";

        var invalid = Path.GetInvalidFileNameChars();   // / \ : * ? " < > | and control chars
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim()) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        var s = sb.ToString().TrimEnd('.', ' ');
        if (s.Length > 80) s = s[..80].TrimEnd('.', ' ');
        if (s.Length == 0) return "_default";

        // reserved check is on the stem before any '.' (e.g. "CON.json" -> still reserved)
        var stem = s;
        var dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        if (ReservedNames.Contains(stem)) s = "_" + s;

        return s;
    }


    // route.json runtime paths + read/write. RouteJson does the (de)serialization (Guide); this picks paths
    // and handles first-run migration from the legacy override path.
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
        return new RouteDocument(2, new List<RouteStep>());
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
        catch (Exception ex) { LogError($"ExileCampaigns -> user route.json write failed: {ex.Message}"); }
    }

    private readonly object _routeFileLock = new();
    private (bool import, string path)? _pendingRouteFile;   // dialog result waiting for the render thread
    private volatile bool _routeDialogOpen;                  // a picker is already up; block a second one

    // export/import kick off a native file picker. ShowDialog pumps its own modal message loop, so it must
    // NOT run on (or block) the render thread - that crashes ExileAPI. Fire it on a background STA thread
    // with no join, then finish the work in DrainPendingRouteFile next Tick.
    private void ExportRoute()
    {
        if (_routeStore == null) { ShowToast("No route loaded to export", ToastLevel.Warning); return; }
        ShowRouteFilePicker(import: false);
    }

    private void ImportRoute() => ShowRouteFilePicker(import: true);

    private void ShowRouteFilePicker(bool import)
    {
        if (_routeDialogOpen) return;   // one dialog at a time
        _routeDialogOpen = true;
        var th = new System.Threading.Thread(() =>
        {
            try
            {
                using System.Windows.Forms.FileDialog dlg = import
                    ? new System.Windows.Forms.OpenFileDialog { Title = "Import route", CheckFileExists = true }
                    : new System.Windows.Forms.SaveFileDialog { Title = "Export route", FileName = "route.json", DefaultExt = "json", AddExtension = true, OverwritePrompt = true };
                dlg.Filter = "Route JSON (*.json)|*.json|All files (*.*)|*.*";
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    lock (_routeFileLock) _pendingRouteFile = (import, dlg.FileName);
            }
            catch { /* dialog failed to open */ }
            finally { _routeDialogOpen = false; }
        });
        th.SetApartmentState(System.Threading.ApartmentState.STA);
        th.IsBackground = true;
        th.Start();   // no Join - the render thread must keep running
    }

    // drained each Tick: finish a picked export/import on the render thread, where touching the route +
    // toasts is safe.
    private void DrainPendingRouteFile()
    {
        (bool import, string path)? pending;
        lock (_routeFileLock) { pending = _pendingRouteFile; _pendingRouteFile = null; }
        if (pending is not { } job) return;

        if (!job.import)   // export: write the live route to the chosen path
        {
            try
            {
                if (_routeStore == null) { ShowToast("No route loaded to export", ToastLevel.Warning); return; }
                File.WriteAllText(job.path, RouteJson.Write(_routeStore.ToDocument()));
                ShowToast("Route exported", ToastLevel.Success);
            }
            catch (Exception ex) { LogError($"ExileCampaigns -> export route failed: {ex.Message}"); ShowToast("Export failed", ToastLevel.Error); }
            return;
        }

        // import: only overwrite the user copy if it parses to a non-empty route (RouteJson.Read returns 0
        // steps on junk), then reload + re-seek this character's saved step.
        try
        {
            var doc = RouteJson.Read(File.ReadAllText(job.path));
            if (doc.Steps.Count == 0) { ShowToast("Import failed: not a valid route", ToastLevel.Error); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(UserRoutePath)!);
            File.WriteAllText(UserRoutePath, RouteJson.Write(doc));   // renormalize through our writer
            LoadRoutes();
            LoadProgress();
            ShowToast($"Route imported ({doc.Steps.Count} steps)", ToastLevel.Success);
        }
        catch (Exception ex) { LogError($"ExileCampaigns -> import route failed: {ex.Message}"); ShowToast("Import failed", ToastLevel.Error); }
    }

    // drop the user route copy so the bundled default loads again, then reload + re-seek. irreversible: the
    // user's edits are gone unless they exported first (the confirm warns them).
    private void ResetRouteToDefault()
    {
        try
        {
            if (File.Exists(UserRoutePath)) File.Delete(UserRoutePath);
            LoadRoutes();
            LoadProgress();
            ShowToast("Route reset to plugin default", ToastLevel.Success);
        }
        catch (Exception ex)
        {
            LogError($"ExileCampaigns -> reset route failed: {ex.Message}");
            ShowToast("Reset failed", ToastLevel.Error);
        }
    }

}
