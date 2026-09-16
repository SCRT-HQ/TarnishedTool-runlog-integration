//

namespace TarnishedTool.Interfaces;

/// <summary>A line of text on the player's screen, in the game.</summary>
public interface IGameMessageService
{
    /// <summary>Shown now, or not at all: nothing queues.</summary>
    void Show(string text);

    /// <summary>Give the game its own text back. Called as the tool closes.</summary>
    void Release();
}
