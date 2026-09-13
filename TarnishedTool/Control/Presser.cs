//

using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace TarnishedTool.Control;

/// <summary>
/// Telling a source that something happened in the game.
///
/// A press, not a command: whatever is on the other end decides what to
/// do about it, and may well decide nothing. That asymmetry is the whole
/// arrangement. A source may move this game, because a person here said
/// it could and can switch it off; this end may only mention things.
///
/// One address, pasted in, with its own key already in it. A plain GET,
/// because the one thing every tool of this kind can do is fetch a URL,
/// and because the answer is one sentence written to be shown to a
/// person rather than parsed.
/// </summary>
public sealed class Presser
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// The least time between presses, whatever the game is doing.
    ///
    /// Dying twice in fifteen seconds is an ordinary evening in this
    /// game, and the far end rate-limits anyway; this is here so a bug in
    /// the watcher above cannot turn into a flood somebody else has to
    /// absorb.
    /// </summary>
    private static readonly TimeSpan Soonest = TimeSpan.FromSeconds(10);

    private readonly Func<string> _address;
    private readonly Func<string> _name;
    private DateTime _last = DateTime.MinValue;

    public Presser(Func<string> address, Func<string> name)
    {
        _address = address;
        _name = name;
    }

    public event Action<string> Logged;

    /// <summary>
    /// Mention something. Answers nothing; whatever is worth saying comes
    /// back as a line in the log.
    /// </summary>
    public void Press(string ask)
    {
        var address = (_address() ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(address)) return;
        var now = DateTime.UtcNow;
        if (now - _last < Soonest) return;
        _last = now;
        _ = SendAsync(address, ask);
    }

    private async Task SendAsync(string address, string ask)
    {
        try
        {
            var name = (_name() ?? string.Empty).Trim();
            // A reference of its own, so a press that is retried after an
            // answer nobody saw is the same press rather than a second one.
            var url = address
                      + (address.Contains("?") ? "&" : "?")
                      + "ask=" + Uri.EscapeDataString(ask)
                      + (string.IsNullOrEmpty(name) ? string.Empty : "&name=" + Uri.EscapeDataString(name))
                      + "&via=" + Uri.EscapeDataString("the game")
                      + "&ref=" + Uri.EscapeDataString(Guid.NewGuid().ToString("N"));

            var answer = await Client.GetStringAsync(url).ConfigureAwait(false);
            Say(SentenceIn(answer) ?? "Said: " + ask);
        }
        catch (Exception ex)
        {
            Say("Could not say " + ask + ": " + ex.Message);
        }
    }

    /// <summary>
    /// The one sentence worth showing, which the answer carries whether it
    /// was taken or refused.
    /// </summary>
    private static string SentenceIn(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("say", out var say) && say.ValueKind == JsonValueKind.String
                ? say.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Say(string line)
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(new Action(() => Logged?.Invoke(line)));
    }
}
