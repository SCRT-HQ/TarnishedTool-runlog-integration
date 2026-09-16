// 

using System;
using System.Collections.Generic;
using System.Windows.Threading;
using TarnishedTool.Interfaces;
using TarnishedTool.Memory;
using TarnishedTool.Models;

namespace TarnishedTool.Services;

public class EventLogReader(IMemoryService memoryService) : IEventLogReader, IDisposable
{
    private DispatcherTimer _timer;
    private int _readIndex;

    /// <summary>
    /// How many things want the log read. More than one tab reads it now,
    /// and they start and stop independently: without counting, the second
    /// Start would replace a running timer while leaving the first one
    /// ticking, and the first Stop would take the log away from whoever
    /// else was still listening.
    /// </summary>
    private int _owners;

    private nint _writeIndexAddr;
    private nint _bufferAddr;

    public event Action<List<EventLogEntry>> EntriesReceived;

    public void Start()
    {
        // Already reading: another owner, not a restart. Reading from zero
        // again here would replay everything the first owner has seen.
        if (++_owners > 1) return;
        _readIndex = 0;
        _writeIndexAddr = CodeCaveOffsets.Base + CodeCaveOffsets.EventLogWriteIndex;
        _bufferAddr = CodeCaveOffsets.Base + CodeCaveOffsets.EventLogBuffer;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += Poll;

        _timer.Start();
    }

    public void Stop()
    {
        if (_owners == 0) return;
        if (--_owners > 0) return;
        _timer?.Stop();
    }

    public void Dispose()
    {
        _owners = 0;
        _timer?.Stop();
    }
    
    private void Poll(object sender, EventArgs e)
    {
        var writeIndex = memoryService.Read<int>(_writeIndexAddr);
        if (writeIndex == _readIndex) return;

        var entries = new List<EventLogEntry>();
        
        while (_readIndex != writeIndex)
        {
            var offset = _readIndex * 5;
            var eventId = memoryService.Read<uint>(_bufferAddr + offset);
            var value = memoryService.Read<byte>(_bufferAddr + offset + 4) != 0;
            entries.Add(new EventLogEntry(eventId, value));
            
            _readIndex = (_readIndex + 1) & 511;
        }

        EntriesReceived?.Invoke(entries);
    }
   
}