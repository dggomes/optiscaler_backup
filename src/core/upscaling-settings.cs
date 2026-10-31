namespace OptiScalerBackup.Core;

public static partial class GameSettingsReader
{
    private const string OptiSource = "https://github.com/optiscaler/OptiScaler/blob/v0.9.4/OptiScaler.ini";
    private const string OptiLegacy = "https://github.com/optiscaler/OptiScaler/blob/v0.7.9/OptiScaler.ini";
    private const string OptiCurrent = "https://github.com/optiscaler/OptiScaler/blob/master/OptiScaler.ini";
    private const string IntelSource = "https://github.com/GameTechDev/XeSSUnrealPlugin/blob/main/README.md";
    private const string NvidiaSource = "https://d29g4g2dyqv443.cloudfront.net/sites/default/files/DLSS_Frame_Generation_Quick_Start_Guide.pdf";
    private static string SavedToggle(string raw) => raw.ToLowerInvariant() switch
    {
        "1" or "true" => "On",
        "0" or "false" => "Off",
        "auto" => "Automatic / unspecified",
        _ => "Unmapped: " + raw
    };

    private static (string Name, string Value, string Confidence, string Source)? MapGamePlugin(string key, string raw)
    {
        var mapping = key switch
        {
            "r.ngx.dlss.enable" => ("Game upscaling: DLSS", "https://developer.nvidia.com/rtx/dlss"),
            "r.ngx.dlaa.enable" => ("Game AA: DLAA", "https://developer.nvidia.com/rtx/dlss"),
            "r.streamline.dlssg.enable" => ("Game frame generation: DLSS", NvidiaSource),
            "r.fidelityfx.fi.enabled" => ("Game frame generation: FSR", Fsr),
            "r.xess.enabled" => ("Game upscaling: XeSS", IntelSource),
            "r.xefg.enabled" => ("Game frame generation: XeSS", IntelSource),
            _ => ("", "")
        };
        if (mapping.Item1.Length > 0)
            return (mapping.Item1, SavedToggle(raw), "Saved plugin preference; runtime activation is not measured", mapping.Item2);
        if (key == "r.xess.quality") return ("XeSS quality preference", raw, "Raw plugin-version-specific mode; no universal enum assumed", IntelSource);
        return null;
    }

    private static (string Name, string Value, string Confidence, string Source)? MapOptiScaler(string section, string key, string raw)
    {
        var s = section.ToLowerInvariant(); var k = key.ToLowerInvariant(); var value = raw.ToLowerInvariant();
        const string confidence = "Saved OptiScaler preference; auto/defaults, hardware and API can change runtime selection";
        if (s == "upscalers" && k is "dx11upscaler" or "dx12upscaler" or "vulkanupscaler")
        {
            var api = k == "dx11upscaler" ? "DX11" : k == "dx12upscaler" ? "DX12" : "Vulkan";
            var translated = value switch
            {
                "auto" => "Automatic / unspecified",
                "dlss" => "DLSS",
                "xess" => "XeSS",
                "xess_12" => "XeSS (DX12 translation)",
                "fsr21" => "FSR 2.1",
                "fsr22" => "FSR 2.2",
                "fsr31" => "FSR 3.1 / FSR 4-capable backend",
                "fsr21_12" => "FSR 2.1 (DX12 translation)",
                "fsr22_12" => "FSR 2.2 (DX12 translation)",
                "fsr31_12" => "FSR 3.1 / FSR 4-capable backend (DX12 translation)",
                "ffx" => "FidelityFX backend (FSR version unresolved)",
                "ffx_12" => "FidelityFX backend (DX12 translation; FSR version unresolved)",
                _ => "Custom / unmapped: " + raw
            };
            return ("OptiScaler upscaling: " + api, translated, confidence, value.StartsWith("ffx") ? OptiCurrent : OptiSource);
        }
        if (s == "framegen")
        {
            if (k == "enabled") return ("OptiScaler frame generation", SavedToggle(raw), confidence, OptiSource);
            if (k is "fginput" or "fgoutput" or "fgtype")
            {
                var translated = value switch
                {
                    "auto" => "Automatic / unspecified",
                    "nofg" => "No frame generation",
                    "dlssg" => "DLSS frame generation (Streamline)",
                    "nvngxfg" => "DLSS frame generation (NVNGX input)",
                    "fsrfg" => "FSR frame generation",
                    "fsrfg30" => "FSR 3.0 frame generation",
                    "xefg" => "XeSS frame generation",
                    "upscaler" => "Upscaler-driven frame generation input",
                    "optifg" => "OptiFG (FSR frame generation)",
                    "nukems" => "Nukem DLSS-to-FSR frame generation",
                    _ => "Custom / unmapped: " + raw
                };
                return ("OptiScaler FG " + (k == "fginput" ? "input" : k == "fgoutput" ? "output" : "legacy type"), translated, confidence,
                    k == "fgtype" ? OptiLegacy : value == "nvngxfg" ? OptiCurrent : OptiSource);
            }
            if (k == "fgnvngxreplacement") return ("OptiScaler FG NVNGX replacement", value switch
            {
                "auto" => "Automatic / unspecified",
                "none" => "No replacement",
                "nukems" => "Nukem (FSR 3 FG)",
                "arturs" => "DLSS Enabler (FSR multi-frame generation)",
                "ffx" => "FidelityFX frame generation",
                "combo" => "FidelityFX + DLSS Enabler",
                _ => "Custom / unmapped: " + raw
            }, confidence, OptiCurrent);
        }
        if (s == "optifg" && k == "enabled") return ("OptiScaler OptiFG enable preference", SavedToggle(raw), confidence, OptiLegacy);
        return null;
    }
}
