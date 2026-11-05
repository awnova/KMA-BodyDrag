**KMA Body Drag lets you grab a dead body and drag it somewhere else.** Pull a corpse out of the line of fire, hide it around a corner, or move it before you loot it. In co-op, your teammates see you do it.

It's the same pickup, arm pose and hold that KeepMeAlive uses to drag a downed teammate, but for corpses.

> *Works with base SPT and with **Fika**. KeepMeAlive is optional. In co-op, every player needs this mod installed.*

## KMA Body Drag {.tabset}

### Overview

**Picking up a body**  
Look at a corpse and hold **Interact** (**F** by default) for a second. You grab it with both hands. A quick tap still opens the loot screen like normal. If a body is still falling, you can only loot it.

![Dragging a body](https://github.com/awnova/KMA-BodyDrag/releases/download/v1.0.0/KMA-Drag-Drag.Forge.webp)

**Sidearm**  
Your sidearm only needs your right hand, so you can draw it while you drag and keep hold of the body with your left. A primary weapon, a grenade, a knife using meds or reloading make you let go.

![Dragging a body with your sidearm out](https://github.com/awnova/KMA-BodyDrag/releases/download/v1.0.0/KMA-Drag-SideArm.Forge.webp)

**Looting while dragging**  
Press your inventory key while you're holding a body and its loot screen opens, with the body still in your hands.

![Looting a body, then putting it down](https://github.com/awnova/KMA-BodyDrag/releases/download/v1.0.0/KMA-Drag-LootDrop.Forge.webp)

**Putting it down**  
Stand up, go prone, take anything but your sidearm in your hands, or turn away to drop it. Pressing **Interact** drops it too.
**Co-op**  
Your teammates see the pickup, your arms and the drag. Only one player can hold a body at a time. If someone is looting it when you grab it, their loot screen closes.

### Features

* **Choose how you pick bodies up:** Hold Interact, or press it twice.
* **Add a modifier key:** Require Shift, Alt or Control with Interact, so plain Interact stays exactly like the base game.
* **Choose whether Interact drops the body:** Or only let go by standing, going prone, taking anything but your sidearm in your hands or turning away.
* **Play solo or co-op:** Fika isn't required.
* **Translate it:** On-screen text is localized, and you can add your own languages.

### Configuration

The config file is created the first time you start the game: `BepInEx/config/com.keepmealive.bodydrag.cfg`. There's no server config. Everything here only affects your own game.

| Setting | Default | Description |
| --- | --- | --- |
| Drag Input | Hold | **Hold** means hold Interact for the Hold Time. **DoubleTap** means press Interact twice. Without a Drag Modifier, a single tap still loots. |
| Drag Modifier | None | A key to hold with Interact when you want to drag: Shift, Alt or Control (either side). When set, Interact on its own is untouched, and the modifier plus Interact drags. When None, Interact does both, and Drag Input decides which. |
| Hold Time | 1 s | How long you hold Interact to grab a body. Only used when Drag Input is Hold. Range is 0.4 to 3 seconds. |
| Interact Drops Body | On | Pressing Interact while you're holding a body puts it down. Turn it off to drop only by standing, going prone, taking anything but your sidearm in your hands or turning away. |
| Language | Auto | Language for on-screen text. Auto follows your game language. |

To add a language, place a `<code>.json` file in `BepInEx/plugins/KeepMeAlive/Localizations`, named by the game's language code, and restart the game.

### Compatibility

* **KeepMeAlive:** Works with the Solo and Fika versions. You can run either, or neither.
* **Fika:** Optional. Without it this is a single-player mod. With it, everyone in the raid needs KMA Body Drag.
* **Rupture and Euphoria Style Physics:** Both change how corpses behave, so this mod borrows the body from them while you're holding it and hands it back when you let go.

### Installation

1. Download the release.
2. Drop the `BepInEx` folder into your SPT installation, keeping the folder structure.
3. Start the game once to create the config file.

**Requires SPT 4.1.x.** Fika and KeepMeAlive are optional.

### Credits

* Insperation to make it: **kobethuy**
* Integration with Rupture: **darkarchon**
* Integration with Euphoria: **amos**

### Support Me

I was told I should add my Ko-fi. Please don't donate money you need!

[https://ko-fi.com/awnova](https://ko-fi.com/awnova)

{.endtabset}
