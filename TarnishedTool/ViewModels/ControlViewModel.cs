//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Threading;
using TarnishedTool.Control;
using TarnishedTool.Core;
using TarnishedTool.Enums;
using TarnishedTool.Interfaces;
using TarnishedTool.Utilities;

namespace TarnishedTool.ViewModels;

/// <summary>One operation, and whether this player lets a source use it.</summary>
public sealed class ConsentRow : BaseViewModel
{
    private readonly Consent _consent;
    private bool _isAllowed;

    public ConsentRow(Consent consent, string name, bool allowed)
    {
        _consent = consent;
        Name = name;
        _isAllowed = allowed;
    }

    public string Name { get; }

    public bool IsAllowed
    {
        get => _isAllowed;
        set
        {
            if (!SetProperty(ref _isAllowed, value)) return;
            _consent.Set(Name, value);
        }
    }
}

/// <summary>
/// The Control tab.
///
/// It holds an address, a key in that address, and a switch. What it does
/// with a message is decided by the message, so nothing here knows what is
/// on the other end: it could be a run, a chat bot, a script somebody
/// wrote this afternoon. That is the point. A tab that understood one
/// service would have to be re-released whenever that service changed its
/// mind; a tab that performs described operations is finished once.
/// </summary>
public class ControlViewModel : BaseViewModel
{
    /// <summary>
    /// How long a dropped socket is given to come back before everything
    /// it applied comes off.
    ///
    /// A socket through an API gateway is closed on an idle timer as a
    /// matter of course, so a drop is usually a blip and taking a run's
    /// effects off during one would be worse than useless. Long enough to
    /// ride out a reconnection, short enough that a tool left running
    /// after everyone has gone home does not stay configured.
    /// </summary>
    private static readonly TimeSpan GraceAfterDrop = TimeSpan.FromSeconds(90);

    private readonly ControlClient _client;
    private readonly EffectRunner _runner;
    private readonly Consent _consent;
    private readonly OperationRegistry _registry;
    private readonly GameOperations _operations;
    private readonly IMemoryService _memory;
    private readonly IStateService _state;
    private readonly DispatcherTimer _dropTimer;
    private readonly DeathWatcher _deaths;
    private readonly Watches _watches;

    private bool _isLoaded;
    private string _status = "Off";
    private string _address;
    private bool _tellsOfDeath;
    private bool _connectOnStart;
    private int _liveCount;

    public ControlViewModel(
        PlayerViewModel player,
        EnemyViewModel enemies,
        UtilityViewModel utility,
        TravelViewModel travel,
        ISpEffectService spEffects,
        IPlayerService playerService,
        ITravelService travelService,
        IItemService items,
        IMemoryService memory,
        IStateService state,
        IGameTickService tick,
        HotkeyManager hotkeys,
        IEventLogReader eventLog)
    {
        _memory = memory;
        _state = state;

        _registry = new OperationRegistry();
        // What the game is asked to report on, and what happens when it
        // does: a watch firing is the game settling an objective, which
        // the run turns into the same ask a person pressing a button
        // raises. Told, not counted: the run says which in a note.
        _watches = new Watches(eventLog);
        _watches.Fired += what =>
        {
            _client.Send(Frames.Event("settled", what));
            Say("Told the run: " + what + ".");
        };
        _operations = new GameOperations(player, enemies, utility, travel, spEffects, playerService, travelService, items, hotkeys, _watches);
        _operations.RegisterOn(_registry);

        _consent = new Consent();
        _consent.Load(SettingsManager.Default.ControlAllowedOperations, _registry.Names);
        _consent.Changed += () =>
        {
            SettingsManager.Default.ControlAllowedOperations = _consent.Saved;
            SettingsManager.Default.Save();
        };

        _client = new ControlClient(() => Address, Manifest);
        _client.StatusChanged += OnStatusChanged;
        _client.Logged += Say;
        _client.Received += OnReceived;

        _runner = new EffectRunner(_registry, _consent, RestoreLog.Beside("TarnishedTool"), IsReady);
        _runner.Logged += Say;
        _runner.Changed += () => LiveCount = _runner.Live.Count;
        _runner.Answered += (id, ok, until, error) => _client.Send(Frames.Applied(id, ok, until, error));

        // Anything a previous session left applied goes back before
        // anything else runs, and before Activate on Launch has its say:
        // what this player chose for themselves wins over what a run
        // borrowed and never gave back.
        _runner.RestoreFromLastTime();

        // The other direction, on the same socket. A source may move this
        // game because somebody here said it could; this end may only
        // mention what happened, and only what is switched on below.
        _deaths = new DeathWatcher(tick, playerService, IsReady);
        _deaths.Died += () =>
        {
            // Told, which is not the same as counted: whether a run counts
            // it depends on whether that run takes asks at all, and on
            // whoever is at the table. The run says which in a note of its
            // own, a line or two after this one. Saying "said" here and
            // nothing else read as success and was not.
            _client.Send(Frames.Event("died", null));
            Say("Told the run you died.");
        };

        _address = SettingsManager.Default.ControlAddress;
        _tellsOfDeath = SettingsManager.Default.ControlTellsOfDeath;

        // Which build this is, said once, in the pane somebody is already
        // reading when they are wondering why a new operation is not
        // there. A build that cannot be copied over the running one
        // leaves the old file in place and says so only in the build
        // output, which is not where anybody is looking by then.
        try
        {
            var exe = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(exe) && System.IO.File.Exists(exe))
                Say("Tarnished Tool, built " + System.IO.File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm"), Chatter.Quiet);
        }
        catch
        {
            // Not knowing which build this is costs nothing but the line.
        }
        _chatter = (Chatter)Math.Max(0, Math.Min(2, SettingsManager.Default.ControlChatter));
        if (_tellsOfDeath) _deaths.Start();
        _connectOnStart = SettingsManager.Default.ControlConnectOnStart;

        foreach (var name in _registry.Names) Operations.Add(new ConsentRow(_consent, name, _consent.Allows(name)));

        _dropTimer = new DispatcherTimer { Interval = GraceAfterDrop };
        _dropTimer.Tick += (_, _) =>
        {
            _dropTimer.Stop();
            _runner.RevertAll("the connection did not come back");
        };

        state.Subscribe(State.Loaded, () => _isLoaded = true);
        state.Subscribe(State.NotLoaded, () => _isLoaded = false);
        // The game going away is the one state change that takes
        // everything off. A loading screen is not: an effect that survives
        // a quitout is an effect the player is still under, and taking a
        // curse back because somebody warped to a grace would be a bug
        // they would report as one.
        state.Subscribe(State.Detached, () =>
        {
            _isLoaded = false;
            _runner.RevertAll("the game closed");
        });
        state.Subscribe(State.AppClosing, () =>
        {
            _deaths.Stop();
            _runner.RevertAll("the tool is closing");
        });

        ConnectCommand = new DelegateCommand(Connect, () => !IsConnected);
        DisconnectCommand = new DelegateCommand(Disconnect, () => IsConnected);
        TakeEverythingOffCommand = new DelegateCommand(() => _runner.RevertAll("asked to, here"));

        if (_connectOnStart && !string.IsNullOrWhiteSpace(_address)) Connect();
    }

    public ObservableCollection<ConsentRow> Operations { get; } = new();

    /// <summary>The last forty lines, newest first, because that is what a person reads.</summary>
    public ObservableCollection<string> Log { get; } = new();

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand TakeEverythingOffCommand { get; }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsConnected => _client.IsRunning;

    public int LiveCount
    {
        get => _liveCount;
        private set => SetProperty(ref _liveCount, value);
    }

    /// <summary>
    /// Where to dial. A key rides in it, so it is the one thing on this
    /// tab worth keeping out of a screenshot.
    /// </summary>
    public string Address
    {
        get => _address;
        set
        {
            if (!SetProperty(ref _address, value)) return;
            SettingsManager.Default.ControlAddress = value;
            OnPropertyChanged(nameof(Seat));
            SettingsManager.Default.Save();
        }
    }

    /// <summary>
    /// Say so on the socket when the player dies. Off until somebody says
    /// otherwise, and nothing at all while nothing is connected.
    /// </summary>
    private Chatter _chatter = Chatter.Normal;

    /// <summary>How much the pane below says. Kept between sessions.</summary>
    public Chatter Chatter
    {
        get => _chatter;
        set
        {
            if (!SetProperty(ref _chatter, value)) return;
            SettingsManager.Default.ControlChatter = (int)value;
            SettingsManager.Default.Save();
        }
    }

    /// <summary>The three of them, for the menu.</summary>
    public Array Chatters { get; } = Enum.GetValues(typeof(Chatter));

    public bool TellsOfDeath
    {
        get => _tellsOfDeath;
        set
        {
            if (!SetProperty(ref _tellsOfDeath, value)) return;
            if (value) _deaths.Start();
            else _deaths.Stop();
            SettingsManager.Default.ControlTellsOfDeath = value;
            SettingsManager.Default.Save();
        }
    }

    /// <summary>
    /// Which player this is, read out of the address rather than typed.
    ///
    /// It used to be a box of its own, and the box did nothing: the far
    /// end takes the seat from `seat=` in the address, at the moment the
    /// socket opens, and never reads the one in the hello. So a person
    /// who typed a name here and pasted a line naming somebody else was
    /// told they were one player while the run believed the other, with
    /// nothing anywhere to say which had won.
    ///
    /// One address is all a person should need. This says what that
    /// address claims, so it can be read back and checked, and there is
    /// no second place for it to disagree with.
    /// </summary>
    public string Seat => SeatIn(Address);

    /// <summary>The `seat` in an address, or empty where it names none.</summary>
    public static string SeatIn(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return string.Empty;
        var q = address.IndexOf('?');
        if (q < 0) return string.Empty;
        foreach (var part in address.Substring(q + 1).Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (!string.Equals(part.Substring(0, eq), "seat", StringComparison.OrdinalIgnoreCase)) continue;
            try { return Uri.UnescapeDataString(part.Substring(eq + 1)); }
            catch { return part.Substring(eq + 1); }
        }
        return string.Empty;
    }

    public bool ConnectOnStart
    {
        get => _connectOnStart;
        set
        {
            if (!SetProperty(ref _connectOnStart, value)) return;
            SettingsManager.Default.ControlConnectOnStart = value;
            SettingsManager.Default.Save();
        }
    }

    private void Connect()
    {
        if (string.IsNullOrWhiteSpace(Address))
        {
            Say("Nothing to connect to: paste an address first.");
            return;
        }

        _dropTimer.Stop();
        _client.Start();
        Refresh();
    }

    private void Disconnect()
    {
        _dropTimer.Stop();
        _client.Stop();
        _runner.RevertAll("disconnected");
        Refresh();
    }

    /// <summary>
    /// What this build is and what it can be asked for, sent the moment a
    /// socket opens. The list of names is the whole compatibility story:
    /// a source reads it and sends only what this build can do.
    /// </summary>
    private string Manifest()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";
        return Frames.Hello(version, Seat, "ELDEN RING", _memory.IsAttached ? "attached" : "not attached", _registry.Names);
    }

    private void OnStatusChanged(string status)
    {
        Status = status;
        Refresh();

        if (string.Equals(status, "Connected", StringComparison.Ordinal))
        {
            _dropTimer.Stop();
            return;
        }

        // Dropped or reconnecting: hold what is applied for a while, since
        // a drop is ordinary, then take it all off if nothing comes back.
        if (_runner.Live.Count > 0 && !_dropTimer.IsEnabled) _dropTimer.Start();
    }

    private void OnReceived(Incoming incoming)
    {
        switch (incoming.Kind)
        {
            case "apply":
                _runner.Apply(incoming.Apply);
                break;
            case "revert":
                if (!string.IsNullOrEmpty(incoming.RevertGroup)) _runner.RevertGroup(incoming.RevertGroup);
                else _runner.Revert(incoming.RevertId);
                break;
            case "note":
                Say(incoming.Text);
                break;
            case "unreadable":
                Say("Ignored a message: " + incoming.Text);
                break;
            default:
                // A source may speak a later version of this than the
                // build in front of it. Ignoring what we do not know is
                // the whole reason it can.
                break;
        }
    }

    /// <summary>
    /// Nothing is written into the game unless the game is ready for it:
    /// attached, and past the loading screen.
    /// </summary>
    private bool IsReady() => _memory.IsAttached && _isLoaded && _state.IsLoaded();

    private void Say(string line) => Say(line, Chatter.Normal);

    /// <summary>
    /// A line for the pane, kept or dropped on how much was asked for.
    ///
    /// The pane holds forty lines. What went wrong is always worth one of
    /// them; what landed is worth one while playing; the operations
    /// themselves are worth it only while somebody is working out why a
    /// profile does not do what they wrote.
    /// </summary>
    private void Say(string line, Chatter level)
    {
        // Quiet is nought, so trouble is never filtered out.
        if (string.IsNullOrWhiteSpace(line) || level > _chatter) return;
        Log.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + line);
        while (Log.Count > 40) Log.RemoveAt(Log.Count - 1);
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsConnected));
        (ConnectCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        (DisconnectCommand as DelegateCommand)?.RaiseCanExecuteChanged();
    }
}
