# Driving this tool from something else

The Control tab connects Tarnished Tool to a WebSocket server you name: Streamer.bot, a Runlog run, or anything else that speaks the protocol below. Once it is connected, whatever is on that server can change your game while you are playing it. Chat redeeming channel points, a Runlog run drawing a curse at the table, a button on a stream deck: any of them can take your dodge away for ninety seconds, drop you on the other side of the map, or hand you a Golden Seed.

What is on the other end is not the tab's business, and that is the point of it. The tab says what it can do, performs what comes back, and afterwards puts back what can be put back. A controller nobody has written yet will work with it without a new build of this.

This is what that other end has to do.

Offline only, as the [readme](../README.md) says. Worth one line here because this document is about connecting to a network and could be read the wrong way round: it is the game that goes offline, not the machine, and a controller reaching this tool over a socket is not the part that gets anybody banned.

## The transport

A WebSocket, and nothing else. `ws://` or `wss://`, whatever you paste in the Address box; there is no HTTP fallback, no long poll, and no other scheme.

| | |
| --- | --- |
| Direction | The tool is always the client. It dials out and never listens, so there is no port to open on the machine running the game. |
| Frames | Text, UTF-8, one JSON object per message. Binary frames are ignored. |
| Subprotocol | None requested. Do not require one. |
| Headers | None set. Anything a server needs to authenticate goes in the query string, which is why the whole address is one field. |
| Size | Nothing the tool sends is more than a few hundred bytes. What it reads is reassembled across frames, so a large message is fine. |
| Keep-alive | .NET's default, a ping every thirty seconds. The protocol has no heartbeat of its own; do not write one. |
| Reconnecting | On its own, backing off one second, then two, four, and so on to thirty. A drop is ordinary and the tool treats it that way. |
| Closing | A normal close is answered and the loop reconnects. Whatever was applied stays for ninety seconds, then comes off. |

Nothing the tool sends is a request. There is no reply to wait for, no correlation id, and no ordering requirement beyond the obvious: an `apply` has to arrive before the `revert` that takes it back.

## The shape of it

The tool connects, speaks first, and answers what it is told.

```
tool  → hello    what I am, and every operation I can perform
      ← apply    do these things, under this id, for this long
tool  → applied  whether it worked, and when it comes off
      ← revert   take that back
tool  → event    something happened in the game
```

Every message is one JSON object on its own frame. Anything a side does not recognize is ignored rather than treated as an error. That is what lets one end speak a later version of this than the other.

### hello

Sent the moment the socket opens.

```json
{
  "t": "hello",
  "protocol": 1,
  "app": "TarnishedTool",
  "version": "1.4.2",
  "seat": "Mira",
  "game": { "title": "ELDEN RING", "patch": "attached" },
  "ops": ["flag.set", "value.set", "speffect.apply", "warp.position"]
}
```

Send only the operations `ops` lists. Anything else is refused by name and nothing happens. `seat` is what the person typed in the Player box, for a controller driving more than one machine.

### apply

```json
{
  "t": "apply",
  "id": "curse-4",
  "label": "Scarlet Rot",
  "for": 90,
  "ops": [
    { "op": "flag.set", "args": { "name": "player.noRoll", "value": true } },
    { "op": "value.set", "args": { "name": "player.speed", "value": 0.8 } }
  ]
}
```

| Field | What it does |
| --- | --- |
| `id` | Yours, and how you take it back. Sending the same id twice replaces the first rather than stacking a second. |
| `label` | What a person sees in the tool's log, and on their screen in the game (see below). Write it for the player: `Slowed to a third` reads better than `curse-4`. |
| `for` | Seconds. Leave it out and it holds until you say otherwise. |
| `group` | Optional. Several effects can share one, and a `revert` naming the group takes all of them back together. |
| `each` | Optional, and off unless said. Off, the operations are one effect: all of them land or none of them do, because a rule that makes somebody slow and blind is one rule and half of it is a different rule nobody wrote. On, they are a list of separate things, and one this build has no name for, or one switched off, or one that fails, takes itself out and leaves the rest standing. A run's terms are sent this way; a rule's operations are not. |
| `ops` | One or more, applied in order, as one thing. If any fails, the ones that landed are taken back and the whole apply is refused. |

### applied

```json
{ "t": "applied", "id": "curse-4", "ok": true, "until": "2026-09-12T21:41:07Z" }
{ "t": "applied", "id": "curse-4", "ok": false, "error": "no such flag: player.flies" }
```

### revert

```json
{ "t": "revert", "id": "curse-4" }
{ "t": "revert", "group": "round-3" }
{ "t": "revert", "id": "o4#*" }
{ "t": "revert", "id": "*" }
```

`*` means everything in force. Send it when whatever you are doing is over, since the tool is the one that knows what it is still holding.

An id ending in `*` means everything filed under what comes before it. One result can match several rules and so apply several effects, each under its own id; a source taking that result back knows the result and not how many rules it happened to match. Runlog sends this when somebody undoes the move that drew a result.

### note

Anything you send with `t: "note"` and a `text` is written into the tool's log. Useful for saying why nothing is happening.

## What it will and will not do

Every operation has to be one the person allowed. The Control tab lists them with a checkbox each, and warps, items and button presses start switched off. One that is switched off is refused by name, the same as one that does not exist.

Nothing is written unless the game is attached, loaded and past the fade-in. An apply that arrives during a loading screen waits up to forty-five seconds, and is dropped with an answer if it waits longer. A warp refuses immediately instead, because arriving somewhere unexpected four minutes late is worse than not arriving.

Everything that holds something in force comes back off: when its time runs out, when its group is reverted, when you say so, when the connection stays gone, when the game closes, when the tool closes. What it restores is what the player had, not a switch flipped the other way, so a setting somebody turned on by hand survives your borrowing it. The one-way operations, marked so in the table below, are not undone by any of these: an item given stays given, and a warp is not walked back.

You cannot read the game. There is no request for the player's health or position, and the only thing travelling the other way is what the tool volunteers, below.

## What it may do

Everything, until you say otherwise. The list on the tab is how a person switches something off, and what is kept is what they switched off rather than what they left on. The difference shows the first time a build learns a new operation: kept the other way round, anything added since somebody last touched that list arrives switched off, and says so only when a profile is refused by name.

Switching all of it off is a thing somebody can mean, and it is remembered as itself.

## Watching it work

The pane at the foot of the tab says what happened, newest first, forty lines deep. How much it says is up to the box beside it.

**Trouble** is what went wrong and nothing else: a refusal, a connection lost, an operation that could not be put back. **The usual** adds what landed and what came off, which is what you want while playing. **Everything** adds a line per operation as it runs, with its arguments, which is what you want while working out why a profile does something other than what you wrote: an operation that reports success and changes nothing looks exactly like one that worked until you can read what it was asked to do.

The first line says which build this is, by the time its file was written. Worth a glance when an operation you just added is refused by name: a build that cannot be copied over a running copy of itself leaves the old one in place, and says so only in the build output.

## What the player sees

An effect they would feel and not be told of is said on their screen, in the game's own status line, as it lands and as it lifts: the label on the way in, and the label with `lifted` on the way out. Several lifting together take one line. That covers `flag.set`, `value.set`, `value.add` and `speffect.apply`, the operations that hold something in force. A gift, a warp, a fall or a button press shows itself, so an apply made only of those says nothing. Nothing is said while the game is loading.

Switched off by **Show server effect notifications** in the Control tab. It is the player's screen, so it is the player's switch; a source cannot ask for it.

## What the game tells you

One frame, on the same socket, off by default:

```json
{ "t": "event", "kind": "died" }
```

It is a mention, not a command. Whatever is listening decides what it means and whether to do anything, including nothing. Runlog turns it into an ask, which the table still has to accept; a script would probably just log it.

A kind you do not know is ignored, so a later build saying more than `died` will not break an older controller.

Switched on by **Relay deaths to server** in the Control tab. There is no second address and no second key: if something is connected, it hears this, and if nothing is, nothing is sent.

## A controller, in full

Node, with `ws`. Run it, put `ws://127.0.0.1:8787` in the Control tab, press Connect.

```js
import { WebSocketServer } from "ws";

const server = new WebSocketServer({ port: 8787 });

server.on("connection", (socket) => {
  socket.on("message", (raw) => {
    const message = JSON.parse(String(raw));
    console.log("←", message);

    if (message.t === "hello") {
      console.log(`${message.app} ${message.version} can do: ${message.ops.join(", ")}`);

      // Ninety seconds of no dodging.
      socket.send(
        JSON.stringify({
          t: "apply",
          id: "demo",
          label: "No dodging",
          for: 90,
          ops: [{ op: "flag.set", args: { name: "player.noRoll", value: true } }],
        }),
      );

      // Or take it back early:
      // socket.send(JSON.stringify({ t: "revert", id: "demo" }));
    }
  });

  socket.on("close", () => console.log("the tool went away"));
});

console.log("waiting on ws://127.0.0.1:8787");
```

Everything else is deciding *when* to send one.

## Streamer.bot

Streamer.bot is the obvious controller for a stream: it already knows about channel points, bits, subscriptions, chat commands and a stream deck, and any of those could apply an effect.

It runs a WebSocket server of its own, the right shape for this, since the tool is a client and wants something to dial. The address is the one in Streamer.bot under **Servers/Clients → WebSocket Server**, usually `ws://127.0.0.1:8080/`.

Two things have to be true for this to work, and **neither has been tested against a real instance yet**:

1. A client that is not a Streamer.bot client may connect. Its WebSocket server speaks its own protocol, in which a client subscribes to events and makes requests. This tool speaks neither: it sends one `hello` and waits. Streamer.bot has to tolerate a client that says something it does not recognize rather than closing the socket on it.
2. An action can push arbitrary JSON to a connected client. The C# sub-action API has a broadcast for the WebSocket server, and that is what an action fired by a redeem would use to send an `apply`. Whether the broadcast reaches a client that never subscribed to anything is the thing to check.

If both hold, a channel-point reward becomes one C# sub-action that broadcasts an `apply`, and nothing else is needed. If the first fails, the answer is a small relay: the controller above, listening for this tool on one port and for Streamer.bot's client API on another.

Anyone who tests this against a real Streamer.bot, please open an issue saying which of the two held, and with which version. Documentation that cannot be followed is worse than none, and this section is a hypothesis until somebody has run it.

### An importable action

The intention is to ship a Streamer.bot import string here: the actions, the broadcast, and a reward or two already wired. It is not written yet, for the reason above. It will be built by hand in a real instance and exported, rather than generated, because their import format is opaque and has broken compatibility before; and it will say which release it was built against.

## Runlog

The controller this tab was written against, and the one that is known to work. Runlog is a dice engine for challenge runs: a pack says what can happen, the run rolls it, and a **control profile** says what each result means to a tool. The profile lives on Runlog's side rather than in a file here, so a mapping somebody got wrong is fixed in a browser instead of in a new build of this.

Two things are being set up and they have different lifetimes, which is worth knowing before the steps: the address and the ticked operations belong to this machine and are remembered, and the profile belongs to one run. Both are reached from inside a run, because that is where the panel lives, but only one of them is per-run.

**On this machine, once.** In Runlog, open any run and go to **Settings → Stream → Control**. Press **Make a watch key**; the address completes itself with the key in it, `wss://runlog.scrthq.com/ws?k=…&as=control`, and you copy it. The key is shown that once, so if the address is lost, make another and the old one stops working. Paste it into **Address** on the Control tab, press **Connect**, and tick what you are willing to let a run do under **Source actions**. Switching settings and numbers is on already; anything that moves you, hands you an item or presses a button is off until you say so. The tool remembers all of it, so this does not come round again.

**In each run.** Under the same **Settings → Stream → Control**, pick the profile. A pack that ships one offers it in a press: *Use the one that ships with Elden Ring: Interference*. Otherwise choose from the list of built-in profiles, or **Import** a file somebody sent you. The panel says if a rule can never fire against the pack you are playing, which is the usual sign that a profile and a pack have drifted apart. Then play: a result lands, the tool performs it, and the log says what it did and for how long.

A profile names the tool it was written for. If it says `TarnishedTool` and something else connects, the run sends it nothing and says so in the tool's log rather than leaving it sitting there looking connected and idle.

### More than one player

Everyone else at the table attaches on the run's **live link** instead of a watch key, which the host shares with them anyway, and adds their own name:

```
wss://runlog.scrthq.com/ws?run=<runId>&t=<token>&as=control&seat=Mira
```

Put the same name in the **Player** box. A rule that names a seat then reaches that person only; a rule that names nobody reaches everyone, which is how one curse lands on four machines at the same moment. A tool that never said which player it is hears only the ones meant for everybody.

### Telling the run you died

Tick **Relay deaths to server**. It travels on the socket already open, so there is nothing else to set up, and it arrives as an ask in the run's tray for the host to accept. The run has to be taking asks, under **Settings → Stream → Chat**, since attaching a tool is not the same as being allowed to move somebody's run.

### When nothing happens

The log on the Control tab is the first place to look, and it says which of these it is.

| It says | What it means |
| --- | --- |
| Nothing at all | Nothing is connected, or the run has no profile. Check the status, then check that Import actually loaded one. |
| `This run is set up for …` | The profile names a different tool, so nothing will be sent at all. |
| `Refused …: … is switched off here` | The operation is unticked in the list above the log. |
| `Waiting for the game: …` | The game is not attached, or is on a loading screen. It waits forty-five seconds and then gives up. |
| `Refused …: the game is not ready to be moved` | A warp arrived while the game could not take one. Warps refuse rather than queue. |
| `Refused …: this build has no …` | The profile names an operation this build does not have. |
| `Refused …: no such flag` or `no such value` | The profile names a setting this build does not have, usually a typo. |

## The operations this build offers

The authoritative list is the `ops` in `hello`, and the Control tab shows the same list with what is switched on. As of this build:

| Operation | Arguments | Notes |
| --- | --- | --- |
| `flag.set` | `name`, `value` | 34 named toggles. Restored to what it was. |
| `value.set` | `name`, `value` | 15 named numbers, each with its own range. Restored to what it was. |
| `speffect.apply` | `id` | A special effect by the game's own id. Taken off on revert. |
| `speffect.remove` | `id` | One way. |
| `warp.position` | `block`, `x`, `y`, `z`, `angle` | One way, and off by default. |
| `warp.grace` | `name`, `area` | Somewhere by name, from the tool's own list of every grace, including ones the player has never found. One way, off by default. |
| `warp.boss` | `name`, `area` | In front of a boss by name, from the tool's own list of every arena it can reach. Same shape as `warp.grace`, and the area is needed for the same reason: several of these are the same fight in two places. One way, off by default. |
| `player.drop` | `height` | Straight up from wherever they are, then gravity. Needs no map. One way, off by default. |
| `item.give` | `id`, `quantity`, `ashOfWar` | By the game's own id. One way, off by default. |
| `item.named` | `name`, `quantity` | By name, from the tool's own lists: consumables, materials, tears, talismans, armor, arrows, spells, and the key items that are simply given. One way, off by default. |
| `weapon.named` | `name`, `upgrade`, `ash`, `affinity`, `count` | A weapon, at a level. A weapon is not an item with a count: its id carries how far it has been reinforced, so the level is part of naming it. Ordinary weapons reinforce to +25 and somber ones to +10, and a level past a weapon's own ceiling is held there rather than refused. Omit it for the weapon as found. An `ash` is applied where the weapon takes one and the ash goes on that kind of weapon, and is refused by name where either is untrue rather than quietly dropped. The `affinity` is part of the weapon rather than the ash, and an ash allows only some of them: unasked it is the ordinary one, or the ash's own first choice where ordinary is not allowed. A weapon does not stack, so `count` is how a source hands somebody a pair. One way, off by default. |
| `value.add` | `name`, `by` | Moves a number rather than setting it. Reverts to what it was, not to what it became. |
| `runes.give` | `amount` | Runes, given. Adds and cannot set: nothing in the game says how many somebody is carrying, so there is no number to set one to, and `player.runes` was never one of the values above however much it looked like one. A negative amount is a toll. One way, off by default. |
| `action.invoke` | `action` | Fifteen of the tool's own buttons. Off by default. |

The names for `flag.set` and `value.set` are listed in `TarnishedTool/Control/GameOperations.cs`, which is the one file in the control layer that knows what game this is. The names `warp.grace` takes are the game's own, as the tool's grace list spells them; the Travel tab shows the same list.

Two of these exist because a controller cannot know where a player is standing. A source that wants somebody moved cannot say "the nearest cave" and cannot honestly ship coordinates either, since those belong to one machine and one patch. It can name a place, which stays true, or it can ask for straight up, which needs nothing at all.
