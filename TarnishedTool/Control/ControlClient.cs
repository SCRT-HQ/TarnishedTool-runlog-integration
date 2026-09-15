//

using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TarnishedTool.Control;

/// <summary>
/// The socket to whatever is driving this tool.
///
/// It dials out rather than listening. A tool that listens only works when
/// the thing driving it is on the same machine, which is not how anyone
/// plays with a second screen or streams, and a listening socket inside a
/// program that edits another program's memory is a thing worth not
/// building.
///
/// Everything it raises is raised on the UI thread, because everything
/// downstream of it touches view models.
/// </summary>
public sealed class ControlClient
{
    private readonly Func<string> _address;
    private readonly Func<string> _manifest;
    private CancellationTokenSource _cancel;
    private ClientWebSocket _socket;
    private Task _loop;

    public ControlClient(Func<string> address, Func<string> manifest)
    {
        _address = address;
        _manifest = manifest;
    }

    /// <summary>Connected, reconnecting, or off.</summary>
    public string Status { get; private set; } = "Off";

    public event Action<string> StatusChanged;
    public event Action<Incoming> Received;
    public event Action<string, Chatter> Logged;

    public bool IsRunning => _loop != null && !_loop.IsCompleted;

    public void Start()
    {
        if (IsRunning) return;
        _cancel = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cancel.Token));
    }

    public void Stop()
    {
        try
        {
            _cancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped; nothing to do.
        }

        Set("Off");
    }

    public void Send(string json)
    {
        var socket = _socket;
        if (socket == null || socket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        // Fire and forget: nothing downstream waits on an answer, and a
        // send that fails means the socket is gone, which the receive loop
        // is already about to notice.
        _ = socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>
    /// Connect, read until the socket dies, wait, connect again.
    ///
    /// A socket through an API gateway is closed on an idle timer and
    /// again at a hard limit, so dropping is ordinary rather than
    /// exceptional and the backoff exists to be polite about it, not to
    /// recover from a fault. It is capped low because the case that
    /// matters is a run already in progress.
    /// </summary>
    private async Task RunAsync(CancellationToken token)
    {
        var backoff = TimeSpan.FromSeconds(1);
        var buffer = new byte[16 * 1024];

        while (!token.IsCancellationRequested)
        {
            var address = _address();
            if (string.IsNullOrWhiteSpace(address))
            {
                Set("No address");
                return;
            }

            try
            {
                Set("Connecting");
                _socket = new ClientWebSocket();
                await _socket.ConnectAsync(new Uri(address), token).ConfigureAwait(false);
                Set("Connected");
                backoff = TimeSpan.FromSeconds(1);
                Send(_manifest());

                var message = new StringBuilder();
                while (_socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
                        break;
                    }

                    message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;
                    var json = message.ToString();
                    message.Clear();
                    Raise(Frames.Read(json));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("Connection lost: " + ex.Message);
            }
            finally
            {
                try
                {
                    _socket?.Dispose();
                }
                catch (Exception)
                {
                    // A disposed socket that objects to being disposed is
                    // not news.
                }

                _socket = null;
            }

            if (token.IsCancellationRequested) break;
            Set("Reconnecting");
            try
            {
                await Task.Delay(backoff, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
        }

        Set("Off");
    }

    private void Set(string status)
    {
        Status = status;
        OnUi(() => StatusChanged?.Invoke(status));
    }

    private void Log(string line) => OnUi(() => Logged?.Invoke(line, Chatter.Quiet));

    private void Raise(Incoming incoming) => OnUi(() => Received?.Invoke(incoming));

    private static void OnUi(Action action)
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(action);
    }
}
