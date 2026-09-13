//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TarnishedTool.Control;

/// <summary>One effect as it survives a crash: what it is, and how to undo it.</summary>
public sealed class StoredEffect
{
    public string Id { get; set; }
    public string Label { get; set; }
    public string Group { get; set; }
    public List<StoredStep> Steps { get; set; } = new();
}

public sealed class StoredStep
{
    public string Op { get; set; }
    public string Args { get; set; }
}

/// <summary>
/// What is currently applied, kept on disk.
///
/// The worst outcome in this whole design is silent: a run configures
/// somebody's tool, the tool dies holding those settings, and the player
/// finds their game quietly different a week later with nothing to point
/// at. So the ledger is written on every change rather than at exit, and
/// read on the next start, where anything left in it is put back and said
/// out loud.
/// </summary>
public sealed class RestoreLog
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly string _path;

    public RestoreLog(string path) => _path = path;

    public static RestoreLog Beside(string settingsFolderName)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            settingsFolderName);
        return new RestoreLog(Path.Combine(folder, "control-restore.json"));
    }

    public void Write(IEnumerable<StoredEffect> effects)
    {
        try
        {
            var list = new List<StoredEffect>(effects);
            if (list.Count == 0)
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(_path, JsonSerializer.Serialize(list, Options));
        }
        catch (Exception ex)
        {
            Console.WriteLine(@"Could not write the control restore log: " + ex.Message);
        }
    }

    public List<StoredEffect> Read()
    {
        try
        {
            if (!File.Exists(_path)) return new List<StoredEffect>();
            var text = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(text)) return new List<StoredEffect>();
            return JsonSerializer.Deserialize<List<StoredEffect>>(text, Options) ?? new List<StoredEffect>();
        }
        catch (Exception ex)
        {
            Console.WriteLine(@"Could not read the control restore log: " + ex.Message);
            return new List<StoredEffect>();
        }
    }

    public void Clear() => Write(Array.Empty<StoredEffect>());
}
