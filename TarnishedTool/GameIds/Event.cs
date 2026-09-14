// 

namespace TarnishedTool.GameIds;

public static class Event
{
    public static readonly long[] WhetBlades = [
        65720, // Black
        65680, // Glintstone
        65610, // Iron
        65640, // Red-Hot
        65660, // Sanctified
        60130 // Whetstone Knife
    ];

    /// <summary>
    /// The three talisman pouches, by the event that awards each.
    ///
    /// Verified in game with the event logger: setting these three is
    /// the whole of it. The pouch is a key item sharing one id across
    /// all three, so spawning the item hands over one thing and the
    /// slots come from the flags.
    /// </summary>
    public static readonly long[] TalismanPouches = [
        60500, // Enia
        60510, // Margit/Morgott
        60520  // Golden Shade Godfrey
    ];

    /// <summary>
    /// What the opening hours of the game hand over, as item and event
    /// together.
    ///
    /// Both halves are needed and they do different work. The event is
    /// what stops the game awarding it a second time and what the menus
    /// read to decide a thing is available; the item is what you
    /// actually use. `UnlockWhetblades` beside this sets flags alone
    /// because a whetblade is a token and nothing holds it -- a whistle
    /// is not, and a flag without the whistle is a horse you cannot
    /// call.
    /// </summary>
    public static readonly (int Item, long Event)[] StartingGifts = [
        (0x40000082, 60100), // Spectral Steed Whistle, which is Torrent
        (0x40001FDE, 60110), // Spirit Calling Bell, which is summons
        (0x40002134, 60120), // Crafting Kit
        (0x40001FE3, 60140), // Tailoring Tools
        (0x400000FA, 60020)  // Flask of Wondrous Physick
    ];

    /// <summary>
    /// The great runes, in the form that is worth having.
    ///
    /// Each exists twice: the one a boss drops, and the one a Divine
    /// Tower gives back for it. Only the second does anything, so
    /// these are the restored ones -- a run handed the other would
    /// still owe the game a climb.
    /// </summary>
    public static readonly (int Item, long Event)[] GreatRunes = [
        (0x400000BF, 191), // Godrick
        (0x400000C0, 192), // Radahn
        (0x400000C1, 193), // Morgott
        (0x400000C2, 194), // Rykard
        (0x400000C3, 195), // Mohg
        (0x400000C4, 196), // Malenia
        (0x40002760, 197)  // Great Rune of the Unborn, which is Rennala's
    ];

    public static readonly long ClearDlc = 70;
    public static readonly long SeeUndergroundGraces = 82001;
    public static readonly long SeeDlcGraces = 82002;

    public static readonly long[] UnlockMetyr = [2050400600,2053460600,2051459226,2051459228,2051459229,2051459230,2051455023,2051459249,2051452717,
                                                 2050407000,400662,4856,4855,4854,4849,2051452718,2051459213,2051450715,9440,2051450180]; //Progress Ymir's Quest, get rid of the Invader, move chair and enable fight with metyr

    public static readonly long FightFortissax = 12032859;
    public static readonly long[] FightEldenBeast = [19002802, 19002805];

    public static readonly long SnowfieldMausoleum = 1247580400;

    public static readonly long[] LakeOfRotPlatforms = [12010590,12010591,12010592,12010593,12010594,12010595];

    //Cataclysm visuals set when the Erdtree burns / after Maliketh: main blaze, world embers, small flames
    public static readonly long[] ErdtreeAblaze = [300, 301, 302];

}
