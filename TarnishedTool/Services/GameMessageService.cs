//

using System;
using TarnishedTool.Enums;
using TarnishedTool.Interfaces;
using TarnishedTool.Memory;
using static TarnishedTool.GameIds.Emevd;

namespace TarnishedTool.Services;

/// <summary>
/// A line of text on the player's screen, in the game's own status
/// message: the small one at the top that says a map was found.
///
/// The game shows a message by id, from its text tables, and never by
/// string, so the text has to be in a table first. Once per attach, one
/// entry of the table status messages are drawn from is pointed at a
/// buffer of this tool's own, and every message after that is a write
/// into the buffer and a show of that entry's id. The loading-screen
/// reminder writes over an entry's text where it lies, which limits it to
/// the length of what was there; this borrows the entry's pointer
/// instead, and gives it back on release.
/// </summary>
public class GameMessageService : IGameMessageService
{
    /// <summary>The table status messages are drawn from, by the game's numbering.</summary>
    private const uint EventTextForMap = 34;

    /// <summary>In UTF-16 characters, terminator included. A status line is short or unreadable.</summary>
    private const int Capacity = 128;

    private readonly IMemoryService _memory;
    private readonly IEmevdService _emevd;

    private nint _buffer;
    /// <summary>Where the borrowed entry's offset lives, and what it said before.</summary>
    private nint _slot;
    private long _wasOffset;
    private int _messageId;

    public GameMessageService(IMemoryService memory, IEmevdService emevd, IStateService state)
    {
        _memory = memory;
        _emevd = emevd;
        // The game took its memory with it, so there is nothing to give back.
        state.Subscribe(State.Detached, Forget);
    }

    public void Show(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!Borrow()) return;

        var line = text.Trim();
        if (line.Length >= Capacity) line = line.Substring(0, Capacity - 1);
        // Zero-padded to the buffer's length, so the terminator is written with the text.
        _memory.WriteString(_buffer, line, Capacity * 2);
        _emevd.ExecuteEmevdCommand(EmevdCommands.DisplayStatusMessage(_messageId));
    }

    public void Release()
    {
        if (_slot != 0 && _memory.IsAttached)
        {
            _memory.Write(_slot, _wasOffset);
            _memory.FreeMem(_buffer);
        }

        Forget();
    }

    private void Forget()
    {
        _buffer = 0;
        _slot = 0;
        _wasOffset = 0;
        _messageId = 0;
    }

    /// <summary>
    /// Point one entry of the table at a buffer of our own. The last entry
    /// of the last range, since the highest id in the table is the one the
    /// game is least likely to be about to say for itself.
    /// </summary>
    private bool Borrow()
    {
        if (_buffer != 0) return true;

        var (fmg, stringTable, rangeCount) = Table(0, EventTextForMap);
        if (fmg == 0 || stringTable == 0 || rangeCount <= 0) return false;

        var last = fmg + 0x28 + (rangeCount - 1) * 0x10;
        var baseIndex = _memory.Read<uint>(last);
        var firstId = _memory.Read<uint>(last + 0x4);
        var lastId = _memory.Read<uint>(last + 0x8);
        if (lastId < firstId) return false;

        var index = baseIndex + (lastId - firstId);
        var slot = stringTable + (nint)(index * 8);

        var buffer = _memory.AllocateMem(Capacity * 2);
        if (buffer == 0) return false;

        _wasOffset = _memory.Read<long>(slot);
        _memory.Write(slot, (long)(buffer - fmg));

        _buffer = buffer;
        _slot = slot;
        _messageId = (int)lastId;
        return true;
    }

    /// <summary>One table of the message repository, laid out as the reminder reads it.</summary>
    private (nint fmg, nint stringTable, int rangeCount) Table(uint version, uint category)
    {
        var msgRepo = _memory.Read<nint>(Offsets.MsgRepository.Base);
        if (msgRepo == 0) return (0, 0, 0);

        var versionCount = _memory.Read<uint>(msgRepo + 0x10);
        var categoryCount = _memory.Read<uint>(msgRepo + 0x14);
        if (version >= versionCount || category >= categoryCount) return (0, 0, 0);

        var versionsArray = _memory.Read<nint>(msgRepo + 0x8);
        var versionPtr = _memory.Read<nint>((IntPtr)(versionsArray + version * 8));
        if (versionPtr == 0) return (0, 0, 0);

        var fmg = _memory.Read<nint>((IntPtr)(versionPtr + category * 8));
        if (fmg == 0) return (0, 0, 0);

        var stringTable = _memory.Read<nint>(fmg + 0x18);
        var rangeCount = _memory.Read<int>(fmg + 0x0C);
        return (fmg, stringTable, rangeCount);
    }
}
