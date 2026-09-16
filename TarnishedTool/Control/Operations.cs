//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace TarnishedTool.Control;

/// <summary>
/// How to put one operation back.
///
/// A revert is expressed as another operation rather than as a closure,
/// for two reasons. It can be written to disk, which is what lets the tool
/// put things back after a crash rather than leaving somebody's game
/// configured by a run that ended three days ago. And it goes through the
/// same registry as everything else, so there is one path into the game
/// rather than two.
/// </summary>
public sealed class RevertStep
{
    public RevertStep(string op, string args)
    {
        Op = op;
        Args = args;
    }

    public string Op { get; }
    /// <summary>The arguments, as JSON text.</summary>
    public string Args { get; }

    public static RevertStep Of(string op, params object[] pairs)
    {
        var parts = new List<string>();
        for (var i = 0; i + 1 < pairs.Length; i += 2)
        {
            parts.Add(JsonSerializer.Serialize(Convert.ToString(pairs[i], CultureInfo.InvariantCulture)) + ":" + Literal(pairs[i + 1]));
        }

        return new RevertStep(op, "{" + string.Join(",", parts) + "}");
    }

    private static string Literal(object value)
    {
        switch (value)
        {
            case null: return "null";
            case bool b: return b ? "true" : "false";
            case string s: return JsonSerializer.Serialize(s);
            case float f: return f.ToString("R", CultureInfo.InvariantCulture);
            case double d: return d.ToString("R", CultureInfo.InvariantCulture);
            default: return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>Reading an operation's arguments without trusting any of them.</summary>
public sealed class Args
{
    private readonly JsonElement _element;

    public Args(JsonElement element) => _element = element;

    public static Args Parse(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return new Args(doc.RootElement.Clone());
    }

    public string Text(string name)
    {
        return Find(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public bool? Flag(string name)
    {
        if (!Find(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        return null;
    }

    public int? Whole(string name)
    {
        return Find(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : (int?)null;
    }

    public uint? Unsigned(string name)
    {
        return Find(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var i) ? i : (uint?)null;
    }

    public float? Real(string name)
    {
        return Find(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out var f) ? f : (float?)null;
    }

    private bool Find(string name, out JsonElement value)
    {
        value = default;
        return _element.ValueKind == JsonValueKind.Object && _element.TryGetProperty(name, out value);
    }
}

/// <summary>An operation refused for a reason worth telling the source.</summary>
public sealed class OperationRefused : Exception
{
    public OperationRefused(string message) : base(message)
    {
    }
}

/// <summary>
/// Every operation this build can perform, by name.
///
/// The names are the whole compatibility story: a source reads them on
/// connect and sends only what this build said it could do, so a run whose
/// mapping is ahead of somebody's executable degrades instead of failing.
/// </summary>
public sealed class OperationRegistry
{
    private readonly Dictionary<string, Func<Args, IEnumerable<RevertStep>>> _ops =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _felt = new(StringComparer.Ordinal);

    /// <summary>
    /// One that holds something in force until it is put back. Felt unless
    /// said otherwise: a player who is slower, or cannot roll, or takes
    /// double damage, is told nothing by the game, so the tool says it.
    /// A watch is held the same way but happens to the run, not to them.
    /// </summary>
    public void Register(string name, Func<Args, IEnumerable<RevertStep>> handler, bool felt = true)
    {
        _ops[name] = handler;
        if (felt) _felt.Add(name);
        else _felt.Remove(name);
    }

    /// <summary>
    /// One that changes nothing that could be put back. Never felt: a
    /// gift, a warp, or a fall announces itself.
    /// </summary>
    public void RegisterOneShot(string name, Action<Args> handler)
    {
        _ops[name] = args =>
        {
            handler(args);
            return Array.Empty<RevertStep>();
        };
        _felt.Remove(name);
    }

    public bool Has(string name) => _ops.ContainsKey(name);

    /// <summary>Whether the player would notice this without being told.</summary>
    public bool Felt(string name) => _felt.Contains(name);

    public IEnumerable<string> Names => _ops.Keys.OrderBy(n => n, StringComparer.Ordinal);

    public RevertStep[] Invoke(string name, Args args)
    {
        if (!_ops.TryGetValue(name, out var handler)) throw new OperationRefused("this build has no " + name);
        var steps = handler(args);
        return steps == null ? Array.Empty<RevertStep>() : steps.ToArray();
    }

    public RevertStep[] Invoke(RevertStep step) => Invoke(step.Op, Args.Parse(step.Args));
}
