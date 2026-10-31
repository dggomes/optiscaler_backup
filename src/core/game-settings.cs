using System.Globalization;
using System.Text;

namespace OptiScalerBackup.Core;

public sealed record SettingObservation(string File, string Layer, string Section, string Key, string RawValue, int Line, string Setting, string Value, string Confidence, string Source)
{
    public string Scope => GameSettingsReader.IsOptiScaler(File) ? "OptiScaler" : "Game";
}
public sealed record GameSettingsReport(List<SettingObservation> Observations, List<string> Warnings)
{
    public string Summary
    {
        get
        {
            if (Observations.Count == 0) return "No recognized settings recorded";
            var values = Observations.Where(o => o.Setting is "Resolution width" or "Resolution height" or "Window mode" or "FSR quality preference" || o.Setting.StartsWith("Game upscaling:") || o.Setting.StartsWith("Game frame generation:") || o.Setting.StartsWith("OptiScaler "))
                .Select(o => o.Setting + ": " + o.Value).Distinct().ToList();
            return values.Count == 0 ? $"{Observations.Count} saved graphics values" : string.Join(" · ", values);
        }
    }
}

public static partial class GameSettingsReader
{
    public const int MaxBytes = 2 * 1024 * 1024;
    private const string Epic = "https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/Engine/UGameUserSettings";
    private const string CVars = "https://dev.epicgames.com/documentation/en-us/unreal-engine/unreal-engine-console-variables-reference";
    private const string Fsr = "https://gpuopen.com/learn/ue-fsr/";
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    { "GameUserSettings.ini", "Engine.ini", "Scalability.ini", "DefaultGameUserSettings.ini", "DefaultEngine.ini", "DefaultScalability.ini", "OptiScaler.ini" };
    public static bool IsOptiScaler(string path) => path.Replace('\\', '/').Split('/').Last().Split(':').Last().Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase);
    public static bool IsSettings(string path) => Names.Contains(Path.GetFileName(path.Replace('\\', '/')));

    public static GameSettingsReport Snapshot(Store store, Snapshot snapshot)
    {
        var observations = new List<SettingObservation>(); var warnings = new List<string>();
        foreach (var state in snapshot.Files.Where(f => f.Hash != null && IsSettings(f.Target.Path)).Take(1000))
        {
            try
            {
                if (state.Size > MaxBytes) throw new IOException("Settings file exceeds 2 MiB.");
                store.Verify(state);
                var report = Parse(Read(store.Payload(state)), state.Target.Root + ":" + state.Target.Path);
                observations.AddRange(report.Observations); warnings.AddRange(report.Warnings);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or DecoderFallbackException)
            { warnings.Add(state.Target.Path + ": " + error.Message); }
        }
        if (observations.Count == 0) warnings.Add("No readable supported graphics settings were captured. Inventory hashes cannot reconstruct missing INI contents.");
        return Finish(observations, warnings);
    }
    public static GameSettingsReport Current(Game game)
    {
        var observations = new List<SettingObservation>(); var warnings = new List<string>();
        // External roots are read only after the existing managed-file approval flow.
        foreach (var target in game.Managed.Where(t => IsSettings(t.Path)).Take(1000))
        {
            try
            {
                var path = SafePaths.Resolve(game, target);
                if (!SafePaths.FileExists(path)) { warnings.Add(target.Path + ": file is absent."); continue; }
                var report = Parse(Read(path), target.Root + ":" + target.Path);
                observations.AddRange(report.Observations); warnings.AddRange(report.Warnings);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or DecoderFallbackException)
            { warnings.Add(target.Path + ": " + error.Message); }
        }
        if (observations.Count == 0) warnings.Add("Use Review / add files to approve game settings INIs or OptiScaler.ini. External settings require path approval; custom fields may need a game-specific rule.");
        return Finish(observations, warnings);
    }
    private static string Read(string file)
    {
        SafePaths.NoLinks(file);
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes) throw new IOException("Settings file exceeds 2 MiB.");
        var length = checked((int)stream.Length); var bytes = new byte[length]; stream.ReadExactly(bytes);
        if (stream.Length != length) throw new IOException("Settings file changed while reading; retry.");
        using var memory = new MemoryStream(bytes);
        using var reader = new StreamReader(memory, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        if (text.Length > MaxBytes || text.Contains('\0')) throw new IOException("Invalid settings text.");
        return text;
    }
    public static GameSettingsReport Parse(string text, string file)
    {
        if (text.Length > MaxBytes || text.Contains('\0')) throw new IOException("Invalid or oversized settings text.");
        var observations = new List<SettingObservation>(); var warnings = new List<string>(); var section = ""; var number = 0;
        var layer = IsOptiScaler(file) ? "OptiScaler configuration" : file.Replace('\\', '/').Split('/').Last().Split(':').Last().StartsWith("Default", StringComparison.OrdinalIgnoreCase) ? "Project default" : "Saved/config candidate";
        using var input = new StringReader(text);
        while (input.ReadLine() is { } line)
        {
            number++; if (line.Length > 16_384) { warnings.Add(file + ": oversized INI line " + number); continue; }
            var value = line.Trim(); if (value.Length == 0 || value[0] is ';' or '#') continue;
            if (value.StartsWith('[') && value.EndsWith(']')) { section = value[1..^1].Trim(); continue; }
            var equals = value.IndexOf('='); if (equals < 1 || section.Length == 0) continue;
            var key = value[..equals].Trim(); var raw = StripComment(value[(equals + 1)..]).Trim().Trim('"');
            if (key[0] is '+' or '-' or '.' or '!') continue; // Unreal array operators are not scalar assignments.
            var mapped = IsOptiScaler(file) ? MapOptiScaler(section, key, raw) : Map(section, key, raw);
            if (mapped != null) observations.Add(new(file, layer, section, key, raw, number, mapped.Value.Name, mapped.Value.Value, mapped.Value.Confidence, mapped.Value.Source));
        }
        return Finish(observations, warnings);
    }
    private static string StripComment(string text)
    {
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"' && (i == 0 || text[i - 1] != '\\')) quoted = !quoted;
            if (!quoted && text[i] == ';') return text[..i];
        }
        return text;
    }
    private static (string Name, string Value, string Confidence, string Source)? Map(string section, string key, string raw)
    {
        var k = key.ToLowerInvariant(); var s = section.ToLowerInvariant();
        var user = s.Contains("usersettings") || s is "graphics" or "display";
        var cvars = s is "systemsettings" or "consolevariables" or "/script/engine.renderersettings";
        string Boolean() => raw.ToLowerInvariant() switch { "true" or "1" => "On", "false" or "0" => "Off", _ => "Unmapped: " + raw };
        string Number(string suffix) => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? raw + suffix : "Unmapped: " + raw;
        var zero = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric) && numeric == 0;
        var confidence = "Explicit saved preference";
        if (user)
        {
            var standard = k switch
            {
                "resolutionsizex" => ("Resolution width", Number(" px")),
                "resolutionsizey" => ("Resolution height", Number(" px")),
                "lastuserconfirmedresolutionsizex" => ("Last confirmed width", Number(" px")),
                "lastuserconfirmedresolutionsizey" => ("Last confirmed height", Number(" px")),
                "fullscreenmode" => ("Window mode", raw switch { "0" => "Fullscreen", "1" => "Borderless fullscreen", "2" => "Windowed", _ => "Unmapped: " + raw }),
                "lastconfirmedfullscreenmode" => ("Last confirmed window mode", raw switch { "0" => "Fullscreen", "1" => "Borderless fullscreen", "2" => "Windowed", _ => "Unmapped: " + raw }),
                "busevsync" => ("VSync", Boolean()),
                "busedynamicresolution" => ("Dynamic resolution", Boolean()),
                "frameratelimit" => ("Frame limit", zero ? "Uncapped (saved 0)" : Number(" FPS")),
                "busehdrdisplayoutput" => ("HDR", Boolean()),
                "hdrdisplayoutputnits" => ("HDR luminance", Number(" nits")),
                _ => ("", "")
            };
            if (standard.Item1.Length > 0)
                return s is "graphics" or "display" ? ("Game-specific " + key, raw, "Raw game value; game-specific mapping required", Epic)
                    : (standard.Item1, standard.Item2, confidence, Epic);
            if (new[] { "upscaler", "upscalingmethod", "upscalingmode", "upscalingquality", "dlssmode", "dlssquality", "fsrmode", "fsrquality", "xessmode", "xessquality", "antialiasingmethod", "upscaling", "upscalingtype", "upscalertype", "framegeneration", "framegenerationtype", "framegenerationmode", "framegenerationenabled", "bframegenerationenabled", "buseframegeneration", "framegen", "framegenmode", "dlssframegeneration", "busedlssframegeneration", "fsrframegeneration", "xessframegeneration" }.Contains(k))
                return ("Game-specific " + key, raw, "Raw game value; game-specific mapping required", Epic);
        }
        if (s == "scalabilitygroups" && k == "sg.resolutionquality")
            return ("Scalability render scale", zero ? "Engine default (saved 0)" : Number("%"), "Saved scale; does not establish Native AA or DLAA", CVars);
        if (!cvars) return null;
        if (k == "r.screenpercentage") return ("Render scale override", double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale <= 0 ? "Engine/default policy (saved " + raw + ")" : Number("%"), "Saved CVar; defaults/plugins/dynamic resolution can override", CVars);
        if (k == "r.antialiasingmethod") return ("AA method preference", raw switch { "0" => "Off", "1" => "FXAA", "2" => "TAA", "3" => "MSAA", "4" => "TSR / temporal plugin hook", _ => "Unmapped: " + raw }, "Engine setting; a plugin can replace the temporal upscaler", CVars);
        if (k == "r.temporalaa.upsampling") return ("Temporal upsampling", Boolean(), confidence, CVars);
        if (k == "r.dynamicres.operationmode") return ("Dynamic resolution policy", raw switch { "0" => "Disabled", "1" => "Use game setting", "2" => "Forced on", _ => "Unmapped: " + raw }, confidence, CVars);
        if (k == "r.fullscreenmode") return ("Fullscreen request policy", raw switch { "0" => "Fullscreen", "1" => "Borderless fullscreen", _ => "Unmapped: " + raw }, "Engine CVar policy; separate from GameUserSettings.FullscreenMode", CVars);
        if (k == "r.vsync") return ("VSync override", Boolean(), confidence, CVars);
        if (new[] { "r.fidelityfx.fsr.", "r.fidelityfx.fsr3." }.Any(prefix => k == prefix + "qualitymode"))
            return ("FSR quality preference", raw switch { "0" => "Native AA", "1" => "Quality", "2" => "Balanced", "3" => "Performance", "4" => "Ultra Performance", _ => "Unmapped: " + raw }, "Documented FSR plugin enum; preference alone does not prove plugin is enabled", Fsr);
        if (new[] { "r.fidelityfx.fsr.", "r.fidelityfx.fsr2.", "r.fidelityfx.fsr3." }.Any(prefix => k == prefix + "enabled"))
            return ("Game upscaling: FSR", Boolean(), "Saved enable preference; runtime selection is not measured", Fsr);
        var plugin = MapGamePlugin(k, raw);
        if (plugin != null) return plugin;
        if (k.StartsWith("r.fidelityfx.fsr") && (k.EndsWith("qualitymode") || k.EndsWith("enabled")))
            return (key, raw, "Unmapped plugin family/version; raw saved value", Fsr);
        if (k.StartsWith("r.ngx.dlss.") || k.StartsWith("r.ngx.dlaa.") || k.StartsWith("r.streamline.dlssg."))
            if (k.EndsWith("enable") || k.EndsWith("quality")) return (key, raw, "Raw plugin-specific value; no universal enum assumed", "https://developer.nvidia.com/rtx/dlss");
        return null;
    }
    private static GameSettingsReport Finish(List<SettingObservation> observations, List<string> warnings)
    {
        foreach (var group in observations.GroupBy(o => o.Setting).Where(g => g.Select(o => o.RawValue).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            warnings.Add("Multiple saved values for " + group.Key + "; file layers/runtime precedence were not guessed.");
        if (observations.Any(o => o.Setting.Contains("Render scale", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("Render scale alone cannot distinguish native rendering, Native AA or DLAA. Saved settings may differ from runtime state.");
        return new(observations, warnings.Distinct().ToList());
    }
}
