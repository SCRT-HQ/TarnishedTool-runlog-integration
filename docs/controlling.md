# Driving this tool from something else

The Control tab holds a WebSocket address and dials it. What is on the other end is not its business: it says what it can do, performs what it is asked for, and puts everything back afterwards. Runlog is one thing that can sit there. So is a chat bot, a stream deck, or a script you wrote this afternoon.

This is what the other end has to do.

**This is for offline use only. Everything here edits the memory of a running game, which violates the Terms of Service and will most likely lead to a ban if you do it online.** The game goes offline; the machine it runs on does not have to, and a controller reaching it over a socket is not what gets anybody banned.

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
| `label` | What a person sees in the tool's log. |
| `for` | Seconds. Leave it out and it holds until you say otherwise. |
| `group` | Optional. Several effects can share one, and a `revert` naming the group takes all of them back together. |
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
{ "t": "revert", "id": "*" }
```

`*` means everything in force. Send it when whatever you are doing is over, since the tool is the one that knows what it is still holding.

### note

Anything you send with `t: "note"` and a `text` is written into the tool's log. Useful for saying why nothing is happening.

## What it will and will not do

Every operation has to be one the person allowed. The Control tab lists them with a checkbox each, and warps, items and button presses start switched off. One that is switched off is refused by name, the same as one that does not exist.

Nothing is written unless the game is attached, loaded and past the fade-in. An apply that arrives during a loading screen waits up to forty-five seconds, and is dropped with an answer if it waits longer. A warp refuses immediately instead, because arriving somewhere unexpected four minutes late is worse than not arriving.

Everything comes back off: when its time runs out, when its group is reverted, when you say so, when the connection stays gone, when the game closes, when the tool closes. What it restores is what the player had, not a switch flipped the other way, so a setting somebody turned on by hand survives your borrowing it.

You cannot read the game. There is no request for the player's health or position, and the only thing travelling the other way is what the tool volunteers, below.

## What the game tells you

One frame, on the same socket, off by default:

```json
{ "t": "event", "kind": "died" }
```

It is a mention, not a command. Whatever is listening decides what it means and whether to do anything, including nothing. Runlog turns it into an ask, which the table still has to accept; a script would probably just log it.

A kind you do not know is ignored, so a later build saying more than `died` will not break an older controller.

Switched on by **Say when I die** in the Control tab. There is no second address and no second key: if something is connected, it hears this, and if nothing is, nothing is sent.

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
| `player.drop` | `height` | Straight up from wherever they are, then gravity. Needs no map. One way, off by default. |
| `item.give` | `id`, `quantity`, `ashOfWar` | By the game's own id. One way, off by default. |
| `item.named` | `name`, `quantity` | By name, from the tool's own lists: consumables, materials, tears, talismans, arrows, spells, and the key items that are simply given. One way, off by default. |
| `value.add` | `name`, `by` | Moves a number rather than setting it. Reverts to what it was, not to what it became. |
| `action.invoke` | `action` | Fifteen of the tool's own buttons. Off by default. |

The names for `flag.set` and `value.set` are listed in `TarnishedTool/Control/GameOperations.cs`, which is the one file in the control layer that knows what game this is. The names `warp.grace` takes are the game's own, as the tool's grace list spells them; the Travel tab shows the same list.

Two of these exist because a controller cannot know where a player is standing. A source that wants somebody moved cannot say "the nearest cave" and cannot honestly ship coordinates either, since those belong to one machine and one patch. It can name a place, which stays true, or it can ask for straight up, which needs nothing at all.
