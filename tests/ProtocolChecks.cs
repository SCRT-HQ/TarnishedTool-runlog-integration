using System;
using System.Collections.Generic;
using System.Linq;
using TarnishedTool.Control;

static class Check
{
    static int failures;

    static void Is(string what, object got, object want)
    {
        var ok = Equals(Convert.ToString(got), Convert.ToString(want));
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what + (ok ? "" : "  got <" + got + "> want <" + want + ">"));
        if (!ok) failures++;
    }

    static int Main()
    {
        // A well formed apply, with several operations under one id.
        var m = Frames.Read("{\"t\":\"apply\",\"id\":\"a91f\",\"label\":\"Scarlet Rot\",\"for\":90,\"ops\":[{\"op\":\"speffect.apply\",\"args\":{\"id\":6900}},{\"op\":\"flag.set\",\"args\":{\"name\":\"player.noRoll\",\"value\":true}}]}");
        Is("kind", m.Kind, "apply");
        Is("id", m.Apply.Id, "a91f");
        Is("label", m.Apply.Label, "Scarlet Rot");
        Is("for", m.Apply.For, 90);
        Is("op count", m.Apply.Ops.Count, 2);
        Is("first op", m.Apply.Ops[0].Op, "speffect.apply");

        // Arguments survive the document being disposed: the bug this
        // clone exists to prevent would only show up here.
        Is("cloned arg", new Args(m.Apply.Ops[0].Args).Unsigned("id"), 6900u);
        Is("cloned flag", new Args(m.Apply.Ops[1].Args).Flag("value"), true);
        Is("missing arg", new Args(m.Apply.Ops[1].Args).Whole("nope") == null, true);

        // An effect that lasts a unit of play is filed under a group,
        // and several come off together when the source says so.
        var g = Frames.Read("{\"t\":\"apply\",\"id\":\"o4#0\",\"group\":\"unit:2\",\"ops\":[{\"op\":\"x\"}]}");
        Is("group", g.Apply.Group, "unit:2");
        Is("no group", Frames.Read("{\"t\":\"apply\",\"id\":\"a\",\"ops\":[{\"op\":\"x\"}]}").Apply.Group == null, true);
        // A frame of separate things says so. Absent means one effect,
        // which is what every rule sends and what the older builds that
        // never heard of this will go on assuming.
        var each = Frames.Read("{\"t\":\"apply\",\"id\":\"setup\",\"each\":true,\"ops\":[{\"op\":\"x\"}]}");
        Is("each", each.Apply.Each, true);
        Is("no each", Frames.Read("{\"t\":\"apply\",\"id\":\"a\",\"ops\":[{\"op\":\"x\"}]}").Apply.Each, false);
        Is("each false", Frames.Read("{\"t\":\"apply\",\"id\":\"a\",\"each\":false,\"ops\":[{\"op\":\"x\"}]}").Apply.Each, false);

        var rg = Frames.Read("{\"t\":\"revert\",\"group\":\"unit:2\"}");
        Is("revert a group", rg.RevertGroup, "unit:2");
        Is("a group revert names no id", rg.RevertId == null, true);
        Is("revert with nothing to take back", Frames.Read("{\"t\":\"revert\"}").Kind, "unreadable");

        // A lifetime is optional.
        var none = Frames.Read("{\"t\":\"apply\",\"id\":\"b\",\"ops\":[{\"op\":\"x\"}]}");
        Is("no lifetime", none.Apply.For == null, true);

        // Everything malformed is refused rather than half read.
        Is("no id", Frames.Read("{\"t\":\"apply\",\"ops\":[{\"op\":\"x\"}]}").Kind, "unreadable");
        Is("no ops", Frames.Read("{\"t\":\"apply\",\"id\":\"a\",\"ops\":[]}").Kind, "unreadable");
        Is("not json", Frames.Read("{oh dear").Kind, "unreadable");
        Is("not an object", Frames.Read("[1,2]").Kind, "unreadable");
        Is("revert", Frames.Read("{\"t\":\"revert\",\"id\":\"a91f\"}").RevertId, "a91f");
        Is("revert with no id", Frames.Read("{\"t\":\"revert\"}").Kind, "unreadable");

        // A later version of the protocol is ignored, not fatal.
        Is("unknown frame", Frames.Read("{\"t\":\"teleport\",\"whither\":\"elsewhere\"}").Kind, "unknown");

        // What we send.
        var hello = Frames.Hello("1.4.2", "ELDEN RING", "attached", new[] { "flag.set", "speffect.apply" });
        Is("hello", hello, "{\"t\":\"hello\",\"protocol\":1,\"app\":\"TarnishedTool\",\"version\":\"1.4.2\",\"game\":{\"title\":\"ELDEN RING\",\"patch\":\"attached\"},\"ops\":[\"flag.set\",\"speffect.apply\"]}");
        // The seat is the far end's word, taken from the address; the hello never carries one.
        Is("hello carries no seat", hello.Contains("seat"), false);
        Is("applied", Frames.Applied("a91f", true, new DateTime(2026, 9, 12, 21, 41, 7, DateTimeKind.Utc), null), "{\"t\":\"applied\",\"id\":\"a91f\",\"ok\":true,\"until\":\"2026-09-12T21:41:07Z\"}");
        Is("refused", Frames.Applied("a91f", false, null, "no such flag"), "{\"t\":\"applied\",\"id\":\"a91f\",\"ok\":false,\"error\":\"no such flag\"}");

        // A revert is an operation call, so it can be written to disk and
        // read back through the same registry.
        var step = RevertStep.Of("flag.set", "name", "player.noRoll", "value", false);
        Is("revert step", step.Args, "{\"name\":\"player.noRoll\",\"value\":false}");
        Is("revert reads back", Args.Parse(step.Args).Flag("value"), false);
        var real = RevertStep.Of("value.set", "name", "player.speed", "value", 1.25f);
        Is("float round trip", Args.Parse(real.Args).Real("value"), 1.25f);
        Is("quotes survive", Args.Parse(RevertStep.Of("x", "name", "a\"b").Args).Text("name"), "a\"b");

        // The registry answers for what it has and refuses what it does not.
        var registry = new OperationRegistry();
        var seen = new List<string>();
        registry.Register("flag.set", a => { seen.Add(a.Text("name")); return new[] { RevertStep.Of("flag.set", "name", a.Text("name"), "value", false) }; });
        Is("has", registry.Has("flag.set"), true);
        Is("has not", registry.Has("nope"), false);
        Is("invoke returns a revert", registry.Invoke("flag.set", Args.Parse("{\"name\":\"player.noRoll\"}")).Length, 1);
        Is("invoked with", seen.Single(), "player.noRoll");
        try
        {
            registry.Invoke("nope", Args.Parse("{}"));
            Is("refuses the unknown", "no throw", "OperationRefused");
        }
        catch (OperationRefused e)
        {
            Is("refuses the unknown", e.Message, "this build has no nope");
        }

        // The examples in docs/controlling.md, read by the same parser the
        // tool uses. Documentation that does not parse is worse than none.
        var doc = Frames.Read("{\"t\":\"apply\",\"id\":\"curse-4\",\"label\":\"Scarlet Rot\",\"for\":90,\"ops\":[{\"op\":\"flag.set\",\"args\":{\"name\":\"player.noRoll\",\"value\":true}},{\"op\":\"value.set\",\"args\":{\"name\":\"player.speed\",\"value\":0.8}}]}");
        Is("the doc's apply", doc.Apply.Ops.Count, 2);
        Is("the doc's apply lasts", doc.Apply.For, 90);
        Is("the doc's second arg", new Args(doc.Apply.Ops[1].Args).Real("value"), 0.8f);
        Is("the doc's group revert", Frames.Read("{\"t\":\"revert\",\"group\":\"round-3\"}").RevertGroup, "round-3");
        Is("the doc's everything revert", Frames.Read("{\"t\":\"revert\",\"id\":\"*\"}").RevertId, "*");
        Is("the doc's note", Frames.Read("{\"t\":\"note\",\"text\":\"hello\"}").Text, "hello");
        Is("the controller example", Frames.Read("{\"t\":\"apply\",\"id\":\"demo\",\"label\":\"No dodging\",\"for\":90,\"ops\":[{\"op\":\"flag.set\",\"args\":{\"name\":\"player.noRoll\",\"value\":true}}]}").Apply.Label, "No dodging");

        // ---- what a player has agreed a source may do ----------------
        //
        // Kept as what was switched off. The other way round, every
        // operation a later build learns arrives switched off for
        // anybody who had ever touched this list, and says so only when
        // a profile is refused by name.
        var ops3 = new[] { "flag.set", "item.named", "warp.grace" };

        var fresh = new Consent();
        fresh.Load("", ops3);
        Is("everything on to begin with", ops3.All(fresh.Allows), true);

        var curated = new Consent();
        curated.Load("", ops3);
        curated.Set("warp.grace", false);
        Is("keeps what was switched off", curated.Saved, "!warp.grace");

        // The build after this one knows an operation the last did not.
        var later = new Consent();
        later.Load(curated.Saved, new[] { "flag.set", "item.named", "warp.grace", "weapon.named" });
        Is("a new operation arrives on", later.Allows("weapon.named"), true);
        Is("and the one switched off stays off", later.Allows("warp.grace"), false);

        // Somebody who switched everything off meant it.
        var noneAllowed = new Consent();
        noneAllowed.Load("", ops3);
        foreach (var op in ops3) noneAllowed.Set(op, false);
        Is("nothing allowed reads back as nothing", noneAllowed.Saved, "-");
        var stillNone = new Consent();
        stillNone.Load("-", ops3);
        Is("and stays nothing", ops3.Any(stillNone.Allows), false);

        // A list written before this change is read as it was meant, so
        // nobody's choices turn themselves back on.
        var older = new Consent();
        older.Load("flag.set,item.named", ops3);
        Is("an older list still means what it said", older.Allows("flag.set") && !older.Allows("warp.grace"), true);

        Console.WriteLine(failures == 0 ? "\nall good" : "\n" + failures + " failed");
        return failures == 0 ? 0 : 1;
    }
}
