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

    private bool _isLoaded;
    private string _status = "Off";
    private string _address;
    private string _seat;
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
        HotkeyManager hotkeys)
    {
        _memory = memory;
        _state = state;

        _registry = new OperationRegistry();
        _operations = new GameOperations(player, enemies, utility, travel, spEffects, playerService, travelService, items, hotkeys);
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

        _address = SettingsManager.Default.ControlAddress;
        _seat = SettingsManager.Default.ControlSeat;
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
        state.Subscribe(State.AppClosing, () => _runner.RevertAll("the tool is closing"));

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
            SettingsManager.Default.Save();
        }
    }

    /// <summary>
    /// Which player this is, where the far end is driving more than one.
    /// Optional: a source with one player does not need it.
    /// </summary>
    public string Seat
    {
        get => _seat;
        set
        {
            if (!SetProperty(ref _seat, value)) return;
            SettingsManager.Default.ControlSeat = value;
            SettingsManager.Default.Save();
        }
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

    private void Say(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
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
