// 

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TarnishedTool.Memory;

public readonly ref struct MemoryBlock(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;

    public T Get<T>(int offset) where T : unmanaged 
        => MemoryMarshal.Read<T>(_data.Slice(offset));
    
    public string GetString(int offset, int maxBytes)
    {
        var slice = _data.Slice(offset, maxBytes);

        int terminatorIndex = -1;
        for (int i = 0; i < slice.Length - 1; i += 2)
        {
            if (slice[i] == 0 && slice[i + 1] == 0)
            {
                terminatorIndex = i;
                break;
            }
        }

        int length = terminatorIndex >= 0 ? terminatorIndex : slice.Length - slice.Length % 2;
        return Encoding.Unicode.GetString(slice.Slice(0, length).ToArray());
    }
}