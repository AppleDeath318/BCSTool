namespace BCSTool.Models;

/// <summary>
/// Editable values from CoopData\DedicatedServer\server-config.json.
///
/// The JSON file itself is JSON-with-comments (JSONC). CoopConfigService reads
/// it with comments/trailing commas enabled and updates only known setting
/// lines so the human-readable comments remain in the file.
/// </summary>
public sealed class DedicatedServerConfig
{
    public string SaveName { get; set; } = "saveauto1";
    public int AutosaveMinutes { get; set; } = 5;
    public string Password { get; set; } = "";
    public bool LogFile { get; set; } = true;
    public bool Steam { get; set; } = true;

    // Optional diagnostic switches documented by the server config.
    public bool TraceTick { get; set; }
    public bool TracePublish { get; set; }
    public bool TraceBandits { get; set; }
}
