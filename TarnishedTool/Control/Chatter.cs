// 

namespace TarnishedTool.Control;

/// <summary>
/// How much the Control tab says about itself.
/// </summary>
/// <remarks>
/// The pane holds forty lines, so what belongs in it depends on what
/// somebody is doing. Playing, the interesting lines are the ones that
/// say a rule landed or was refused, and a line per operation would push
/// those off the end inside a minute. Setting a profile up for the first
/// time, the operation is exactly what you need to see: the one that
/// reported success and handed over nothing looks identical to the one
/// that worked, until you can read what it was asked to do.
/// </remarks>
public enum Chatter
{
    /// <summary>Only what went wrong.</summary>
    Quiet = 0,

    /// <summary>What went wrong, and what landed or came off.</summary>
    Normal = 1,

    /// <summary>All of that, and every operation as it runs, with its arguments.</summary>
    Everything = 2,
}
