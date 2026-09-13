//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace TarnishedTool.Control;

/// <summary>
/// The control protocol, version 1.
///
/// A source outside this tool describes effects; this tool performs them.
/// Four frames in all. The tool says who it is and what it can do
/// (<c>hello</c>); the source says what to do (<c>apply</c>) and what to
/// undo (<c>revert</c>); the tool answers whether it worked
/// (<c>applied</c>).
///
/// Nothing here knows what a source is for. It carries operation names and
/// arguments, and the meaning of a name is settled by the operations this
/// build registers, not by the wire. That is deliberate: a source can be
/// changed the day someone finds a better mapping, and nobody has to
/// download a new executable.
/// </summary>
public static class Protocol
{
    public const int Version = 1;
}

/// <summary>One operation and the arguments it was given.</summary>
public sealed class OpCall
{
    public OpCall(string op, JsonElement args)
    {
        Op = op;
        Args = args;
    }

    public string Op { get; }
    public JsonElement Args { get; }
}

/// <summary>
/// Do these things, as one thing.
///
/// Several operations under one id is the ordinary case rather than a
/// batch mode: a curse is usually a status effect and a restriction
/// together, and they have to land and be taken back as one.
/// </summary>
public sealed class ApplyFrame
{
    public string Id { get; set; }
    public string Label { get; set; }
    /// <summary>Seconds it lasts, or null to hold until told otherwise.</summary>
    public int? For { get; set; }
    /// <summary>
    /// What it is filed under, where a source takes several effects back
    /// together: everything a unit of play applied, coming off when that
    /// unit closes. A source that works in seconds never sends one.
    /// </summary>
    public string Group { get; set; }
    /// <summary>
    /// Whether the operations stand or fall separately.
    /// </summary>
    /// <remarks>
    /// Off, which is the default, they are one effect: a rule that makes
    /// somebody slow and blind is one rule, and half of it is a different
    /// rule nobody wrote. On, they are a list of things to do, and one the
    /// build cannot do takes itself out and leaves the rest standing. A
    /// run's terms are a list like that: eight gifts, and a name this
    /// build has never heard of should not cost you the other seven.
    /// </remarks>
    public bool Each { get; set; }
    public List<OpCall> Ops { get; } = new();
}

/// <summary>What came off the socket, already told apart.</summary>
public sealed class Incoming
{
    public string Kind { get; set; }
    public ApplyFrame Apply { get; set; }
    public string RevertId { get; set; }
    public string RevertGroup { get; set; }
    public string Text { get; set; }
}

public static class Frames
{
    /// <summary>
    /// Reads one message. An unknown frame is not an error: a source may
    /// speak a later version of this than the build in front of it, and
    /// the right answer to a frame we have never heard of is to ignore it.
    /// </summary>
    public static Incoming Read(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new Incoming { Kind = "unreadable", Text = "not JSON" };
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new Incoming { Kind = "unreadable", Text = "not an object" };
            var kind = StringOf(root, "t");

            switch (kind)
            {
                case "apply":
                    var frame = new ApplyFrame
                    {
                        Id = StringOf(root, "id"),
                        Label = StringOf(root, "label"),
                        For = IntOf(root, "for"),
                        Group = StringOf(root, "group"),
                        Each = BoolOf(root, "each"),
                    };
                    if (string.IsNullOrEmpty(frame.Id)) return new Incoming { Kind = "unreadable", Text = "an apply with no id" };
                    if (root.TryGetProperty("ops", out var ops) && ops.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var op in ops.EnumerateArray())
                        {
                            var name = StringOf(op, "op");
                            if (string.IsNullOrEmpty(name)) continue;
                            // Cloned because the document is disposed on the way out
                            // of this method and a JsonElement does not outlive it.
                            var args = op.TryGetProperty("args", out var a) ? a.Clone() : default;
                            frame.Ops.Add(new OpCall(name, args));
                        }
                    }

                    if (frame.Ops.Count == 0) return new Incoming { Kind = "unreadable", Text = "an apply with no operations" };
                    return new Incoming { Kind = "apply", Apply = frame };

                case "revert":
                    // Three ways to be told to take something back: by
                    // name, by the group several effects were filed
                    // under, or with the reserved id "*" for everything.
                    // A source that has finished says the last of those
                    // rather than naming each effect, since only the tool
                    // knows what it is still holding.
                    var group = StringOf(root, "group");
                    if (!string.IsNullOrEmpty(group)) return new Incoming { Kind = "revert", RevertGroup = group };
                    var id = StringOf(root, "id");
                    if (string.IsNullOrEmpty(id)) return new Incoming { Kind = "unreadable", Text = "a revert with nothing to take back" };
                    return new Incoming { Kind = "revert", RevertId = id };

                case "note":
                    return new Incoming { Kind = "note", Text = StringOf(root, "text") };

                default:
                    return new Incoming { Kind = "unknown", Text = kind };
            }
        }
    }

    /// <summary>What this build is and what it can be asked for.</summary>
    public static string Hello(string version, string seat, string gameTitle, string gamePatch, IEnumerable<string> ops)
    {
        var w = new Writer();
        w.Open();
        w.Text("t", "hello");
        w.Number("protocol", Protocol.Version);
        w.Text("app", "TarnishedTool");
        w.Text("version", version);
        if (!string.IsNullOrWhiteSpace(seat)) w.Text("seat", seat);
        w.Raw("game", "{\"title\":" + Json(gameTitle) + ",\"patch\":" + Json(gamePatch) + "}");
        w.Array("ops", ops);
        w.Close();
        return w.ToString();
    }

    /// <summary>Whether an apply landed, and when it comes back off.</summary>
    public static string Applied(string id, bool ok, DateTime? until, string error)
    {
        var w = new Writer();
        w.Open();
        w.Text("t", "applied");
        w.Text("id", id);
        w.Raw("ok", ok ? "true" : "false");
        if (until.HasValue) w.Text("until", until.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(error)) w.Text("error", error);
        w.Close();
        return w.ToString();
    }

    /// <summary>Something happened in the game worth telling the source.</summary>
    public static string Event(string kind, string detail)
    {
        var w = new Writer();
        w.Open();
        w.Text("t", "event");
        w.Text("kind", kind);
        if (!string.IsNullOrEmpty(detail)) w.Text("detail", detail);
        w.Close();
        return w.ToString();
    }

    /// <summary>A flag off the wire, absent meaning false.</summary>
    private static bool BoolOf(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string StringOf(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? IntOf(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : (int?)null;

    private static string Json(string s) => JsonSerializer.Serialize(s ?? string.Empty);

    /// <summary>
    /// Enough of a JSON writer for four frames. Writing these by hand
    /// rather than reflecting over types keeps the wire readable in one
    /// place and keeps a rename in this project from changing the wire.
    /// </summary>
    private sealed class Writer
    {
        private readonly System.Text.StringBuilder _sb = new();
        private bool _first = true;

        public void Open() => _sb.Append('{');
        public void Close() => _sb.Append('}');

        public void Text(string name, string value)
        {
            Comma();
            _sb.Append(Json(name)).Append(':').Append(Json(value));
        }

        public void Number(string name, int value)
        {
            Comma();
            _sb.Append(Json(name)).Append(':').Append(value.ToString(CultureInfo.InvariantCulture));
        }

        public void Raw(string name, string value)
        {
            Comma();
            _sb.Append(Json(name)).Append(':').Append(value);
        }

        public void Array(string name, IEnumerable<string> values)
        {
            Comma();
            _sb.Append(Json(name)).Append(":[");
            var first = true;
            foreach (var v in values)
            {
                if (!first) _sb.Append(',');
                _sb.Append(Json(v));
                first = false;
            }

            _sb.Append(']');
        }

        private void Comma()
        {
            if (!_first) _sb.Append(',');
            _first = false;
        }

        public override string ToString() => _sb.ToString();
    }
}
