// TriggersVolumes — gameplay entities: triggers, doors, buttons, movers, breakables,
// spawns, items/weapons, NPC nodes (Hammer Desktop P2: hammer-triggers-volumes-gameplay-entities).
//
// Backend / data-model / API only — no Desktop UI, no Hammer app, no shelling out to
// Hammer/Hammer++/VBSP/VVIS/VRAD. Native Geppetto: registry definitions + I/O wiring,
// not Hammer entities.
//
// Dependencies, as landed on 2026-11-13 (read, not assumed):
//   * EntityRegistry (hammer-entity-registry-fgd-json, DONE): kernel/Effigy/Map/EntityRegistry.cs
//     + kernel/Effigy/Map/registry/core-source1.json (14 Source-1 classes, incl.
//     trigger_multiple / trigger_once / trigger_hurt / func_door / func_button /
//     info_player_start / logic_*). This file adds the gameplay classes that file does
//     NOT have; it never re-emits a class core already owns, and never edits a registry
//     file itself (see GameplayEntityCatalog.ToRegistryJson).
//   * EntityIo (hammer-entity-io-logic-list-model, DONE): kernel/Effigy/Map/EntityIo.cs.
//     Row types are EntityIoOutput / EntityIoInput (EntityOutput / EntityInput already
//     belong to the registry). Wiring here validates every row against the registry:
//     the source class must declare the output event and the target class must declare
//     the input, so a wired map has valid I/O targets by construction.
//   * MapEntities (hammer-entity-place-edit-validate, DONE): kernel/Effigy/Map/MapEntities.cs.
//     All placement goes through it; this layer adds category checks on top (a trigger
//     volume must be a trigger class, a door must be a door class) and never bypasses
//     the registry's keyvalue type checks.
//   * ToolTexture (hammer-water-ladders-clip-tool-textures' sibling, landed):
//     kernel/Effigy/Map/ToolTexture.cs. A trigger volume's faces are tools/toolstrigger
//     (Trigger | NonSolid): invisible, non-solid, never a wall. The bake/BSP treatment
//     of those faces belongs to the BSP pass, not here.
//   * QuickPlay (hammer-quick-play-bsp-only-bake, landed): kernel/Effigy/Map/QuickPlay.cs.
//     It validates the spawn itself (QuickPlayError.NoPlayerStart when there is no
//     info_player_start) and uses the FIRST info_player_start. This file's validator
//     mirrors that: an info_player_start is required for a testable map, team spawns
//     (info_player_teamspawn / info_player_deathmatch) do NOT satisfy it, and more
//     than one start is a warning, not an error.
//
// Placement never invents a default or repairs a bad value: registry problems raise
// EntityRegistryException with the registry's named codes, this layer's problems raise
// TriggersVolumesException with the codes below. A trigger volume with no target
// warns (it does nothing); a hurt/push trigger with no effect configured and no
// outputs warns the same way; a button with no outputs warns. Missing-target ERRORS
// stay owned by MapEntities.Validate — this validator does not duplicate them.
//
// FGD-CONFIRM items (single strings the integrator should check against halflife2.fgd;
// everything else follows the landed core file or the logic catalog's conventions):
//   * info_player_teamspawn "TeamNum" keyvalue name (TF2/HL2DM team number).
//   * func_tracktrain has no outputs here (path_track fires OnPass); func_plat has
//     no outputs here. If the FGD lists OnStart/OnStop (tracktrain) or open/close
//     outputs (plat), add them to the catalog JSON below.
//   * func_door_rotating spawnflags 8/16 (X/Y axis; default Z) and func_tracktrain
//     spawnflags 1/2/4 (No Pitch/Yaw/Roll).
//   * Spawnflags are otherwise omitted where the FGD bit was not verified; the
//     registry worker's FGD audit owns the remaining bits.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Effigy;

namespace Effigy.Map
{
	/// <summary>
	/// Named error from the triggers/volumes/gameplay layer. <see cref="Code"/> is the
	/// stable machine-readable name; registry-driven rejections instead raise
	/// <see cref="EntityRegistryException"/> with the registry's own codes, and
	/// placement-shape problems raise <see cref="MapEntityException"/>.
	/// </summary>
	public sealed class TriggersVolumesException : Exception
	{
		public const string CodeNotTriggerClass = "NotTriggerClass";
		public const string CodeNotDoorClass = "NotDoorClass";
		public const string CodeNotButtonClass = "NotButtonClass";
		public const string CodeGameplayClassesNotMerged = "GameplayClassesNotMerged";
		public const string CodeUnknownSourceEntity = "UnknownSourceEntity";
		public const string CodeUnknownTargetEntity = "UnknownTargetEntity";
		public const string CodeUnknownOutput = "UnknownOutput";
		public const string CodeUnknownInput = "UnknownInput";
		public const string CodeInvalidOutput = "InvalidOutput";

		public string Code = "";
		public string ClassName = "";
		public string TargetName = "";

		public TriggersVolumesException( string code, string message, string className = "", string targetName = "" )
			: base( message ) { Code = code; ClassName = className; TargetName = targetName; }
	}

	/// <summary>
	/// The gameplay entity set (Source §1.2 #40/#41/#43) as DATA in the LANDED registry
	/// schema (EntityRegistry: schemaVersion 1, "classes" array; keyvalue types string /
	/// int / float / colour / angle / target / model / sound — there is NO "boolean"
	/// type, on/off keyvalues are int 0/1; targetname is implicit via baseClasses
	/// ["Targetname"], never redeclared).
	///
	/// <see cref="GameplayEntitiesJson"/> is the canonical definition document, loaded by
	/// <see cref="LoadRegistry"/>; <see cref="ToRegistryJson"/> emits it verbatim — the
	/// exact entries the registry worker merges into the registry JSON (this file never
	/// writes a registry JSON file itself). <see cref="EnsureGameplayClasses"/> merges
	/// the classes into a live registry, skipping any class it already has, so calling
	/// it twice — or after the registry file gains one of these classes — is safe.
	/// Classes core-source1.json already owns (trigger_multiple, trigger_once,
	/// trigger_hurt, func_door, func_button, info_player_start, logic_*) are NOT in
	/// this document; the category sets below still cover them, because a trigger is a
	/// trigger whichever file defined it.
	/// </summary>
	public static class GameplayEntityCatalog
	{
		/// <summary>The 42 gameplay-entity definitions. Also shipped as the test fixture gameplay-entities.json.</summary>
		public const string GameplayEntitiesJson = """
{
  "schemaVersion": 1,
  "name": "source1-gameplay",
  "description": "Source-1 gameplay entity set for Geppetto Desktop (hammer-triggers-volumes-gameplay-entities): triggers, doors, buttons, movers, breakables, spawns, items/weapons, NPC nodes. Additive to registry/core-source1.json — none of these classes is in core. Every class implicitly accepts the base keyvalues classname (string), targetname (string), parentname (target), angles (angle) and spawnflags (int); they are not repeated per class.",
  "classes": [
    {
      "name": "trigger_push",
      "kind": "brush",
      "description": "Brush volume that pushes entities touching it in a direction.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "pushdir", "type": "angle", "description": "Direction to push (pitch yaw roll)."},
        {"name": "speed", "type": "float", "default": "100", "description": "Push speed in units per second."},
        {"name": "filtername", "type": "target", "description": "Filter entity that decides which entities are pushed."},
        {"name": "StartDisabled", "type": "int", "default": "0", "description": "Start disabled. 0 = off, 1 = on."}
      ],
      "spawnflags": [
        {"value": 1, "name": "Clients", "description": "Trigger on players."},
        {"value": 2, "name": "NPCs", "description": "Trigger on NPCs."},
        {"value": 4, "name": "Pushables", "description": "Trigger on physics props."},
        {"value": 8, "name": "Physics Objects", "description": "Trigger on physics objects."}
      ],
      "inputs": [
        {"name": "Enable", "argumentType": "void", "description": "Enable the trigger."},
        {"name": "Disable", "argumentType": "void", "description": "Disable the trigger."},
        {"name": "Toggle", "argumentType": "void", "description": "Toggle the trigger."}
      ],
      "outputs": [
        {"name": "OnStartTouch", "argumentType": "void", "description": "Fired when an entity enters the volume."},
        {"name": "OnEndTouch", "argumentType": "void", "description": "Fired when an entity leaves the volume."},
        {"name": "OnTrigger", "argumentType": "void", "description": "Fired when the trigger fires."}
      ],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "trigger_teleport",
      "kind": "brush",
      "description": "Brush volume that teleports entities touching it to a destination.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "target", "type": "target", "description": "Teleport destination (info_target or info_teleport_destination)."},
        {"name": "filtername", "type": "target", "description": "Filter entity that decides which entities are teleported."},
        {"name": "StartDisabled", "type": "int", "default": "0", "description": "Start disabled. 0 = off, 1 = on."}
      ],
      "spawnflags": [
        {"value": 1, "name": "Clients", "description": "Trigger on players."},
        {"value": 2, "name": "NPCs", "description": "Trigger on NPCs."},
        {"value": 4, "name": "Pushables", "description": "Trigger on physics props."},
        {"value": 8, "name": "Physics Objects", "description": "Trigger on physics objects."}
      ],
      "inputs": [
        {"name": "Enable", "argumentType": "void", "description": "Enable the trigger."},
        {"name": "Disable", "argumentType": "void", "description": "Disable the trigger."},
        {"name": "Toggle", "argumentType": "void", "description": "Toggle the trigger."}
      ],
      "outputs": [
        {"name": "OnStartTouch", "argumentType": "void", "description": "Fired when an entity enters the volume."},
        {"name": "OnTrigger", "argumentType": "void", "description": "Fired when the trigger fires."}
      ],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "func_door_rotating",
      "kind": "brush",
      "description": "Rotating door. Rotates around the axis set by spawnflags (default Z).",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "speed", "type": "float", "default": "100", "description": "Rotation speed in degrees per second."},
        {"name": "wait", "type": "float", "default": "4", "description": "Seconds before the door closes again. -1 = stay open."},
        {"name": "distance", "type": "float", "default": "90", "description": "Rotation distance in degrees."},
        {"name": "dmg", "type": "int", "default": "0", "description": "Damage dealt to entities blocking the door."},
        {"name": "health", "type": "int", "default": "0", "description": "Health. 0 = unbreakable; breaking it opens the door."},
        {"name": "locked", "type": "int", "default": "0", "description": "Starts locked. 0 = off, 1 = on."},
        {"name": "noise1", "type": "sound", "description": "Sound played while the door moves."},
        {"name": "noise2", "type": "sound", "description": "Sound played when the door stops moving."}
      ],
      "spawnflags": [
        {"value": 1, "name": "Starts Open", "description": "The door starts in the open position."},
        {"value": 2, "name": "Reverse Dir", "description": "The door rotates in the opposite direction."},
        {"value": 8, "name": "X Axis", "description": "Rotate around the X axis."},
        {"value": 16, "name": "Y Axis", "description": "Rotate around the Y axis."}
      ],
      "inputs": [
        {"name": "Open", "argumentType": "void", "description": "Open the door."},
        {"name": "Close", "argumentType": "void", "description": "Close the door."},
        {"name": "Toggle", "argumentType": "void", "description": "Toggle the door."},
        {"name": "Lock", "argumentType": "void", "description": "Lock the door."},
        {"name": "Unlock", "argumentType": "void", "description": "Unlock the door."}
      ],
      "outputs": [
        {"name": "OnFullyOpen", "argumentType": "void", "description": "Fired when the door finishes opening."},
        {"name": "OnFullyClosed", "argumentType": "void", "description": "Fired when the door finishes closing."},
        {"name": "OnLockedUse", "argumentType": "void", "description": "Fired when a locked door is used."},
        {"name": "OnOpen", "argumentType": "void", "description": "Fired when the door starts opening."},
        {"name": "OnClose", "argumentType": "void", "description": "Fired when the door starts closing."}
      ],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "func_rot_button",
      "kind": "brush",
      "description": "Brush button that rotates when pressed and fires outputs.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "speed", "type": "float", "default": "100", "description": "Rotation speed in degrees per second."},
        {"name": "distance", "type": "float", "default": "90", "description": "Rotation distance in degrees."},
        {"name": "wait", "type": "float", "default": "1", "description": "Seconds before the button resets. -1 = never reset."},
        {"name": "health", "type": "int", "default": "0", "description": "Health. 0 = unbreakable; breaking it presses the button."}
      ],
      "inputs": [
        {"name": "Press", "argumentType": "void", "description": "Press the button."},
        {"name": "Lock", "argumentType": "void", "description": "Lock the button."},
        {"name": "Unlock", "argumentType": "void", "description": "Unlock the button."}
      ],
      "outputs": [
        {"name": "OnPressed", "argumentType": "void", "description": "Fired when the button is pressed."},
        {"name": "OnDamaged", "argumentType": "void", "description": "Fired when the button takes damage."},
        {"name": "OnIn", "argumentType": "void", "description": "Fired when the button reaches its pressed position."},
        {"name": "OnOut", "argumentType": "void", "description": "Fired when the button returns to its rest position."}
      ],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "func_movelinear",
      "kind": "brush",
      "description": "Brush that slides along a direction between two positions.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "movedir", "type": "angle", "description": "Direction of travel (pitch yaw roll)."},
        {"name": "movedistance", "type": "float", "default": "0", "description": "Distance to move in units."},
        {"name": "speed", "type": "float", "default": "100", "description": "Movement speed in units per second."},
        {"name": "startposition", "type": "float", "default": "0", "description": "Starting position as a fraction 0..1 along the travel."},
        {"name": "StartDisabled", "type": "int", "default": "0", "description": "Start disabled. 0 = off, 1 = on."}
      ],
      "inputs": [
        {"name": "Open", "argumentType": "void", "description": "Move to the open (far) position."},
        {"name": "Close", "argumentType": "void", "description": "Move to the closed (near) position."},
        {"name": "SetPosition", "argumentType": "float", "description": "Move to a position (0..1)."},
        {"name": "SetSpeed", "argumentType": "float", "description": "Set the movement speed."}
      ],
      "outputs": [
        {"name": "OnFullyOpen", "argumentType": "void", "description": "Fired when the mover reaches the open position."},
        {"name": "OnFullyClosed", "argumentType": "void", "description": "Fired when the mover reaches the closed position."}
      ],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "func_tracktrain",
      "kind": "brush",
      "description": "Brush train that follows a path_track route. The train itself fires no outputs; path_tracks fire OnPass as it passes them.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "target", "type": "target", "description": "First path_track of the route."},
        {"name": "speed", "type": "float", "default": "0", "description": "Speed in units per second."},
        {"name": "startspeed", "type": "float", "default": "0", "description": "Speed when the train starts."},
        {"name": "StartDisabled", "type": "int", "default": "0", "description": "Start disabled. 0 = off, 1 = on."}
      ],
      "spawnflags": [
        {"value": 1, "name": "No Pitch (X-rot)", "description": "The train does not pitch."},
        {"value": 2, "name": "No Yaw (Y-rot)", "description": "The train does not yaw."},
        {"value": 4, "name": "No Roll (Z-rot)", "description": "The train does not roll."}
      ],
      "inputs": [
        {"name": "StartForward", "argumentType": "void", "description": "Start moving forward along the route."},
        {"name": "StartBackward", "argumentType": "void", "description": "Start moving backward along the route."},
        {"name": "Stop", "argumentType": "void", "description": "Stop the train."},
        {"name": "Toggle", "argumentType": "void", "description": "Toggle the train."},
        {"name": "SetSpeed", "argumentType": "float", "description": "Set the train speed."},
        {"name": "Reverse", "argumentType": "void", "description": "Reverse the train direction."},
        {"name": "TeleportToPathNode", "argumentType": "string", "description": "Teleport the train to a path_track."}
      ],
      "outputs": [],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "func_plat",
      "kind": "brush",
      "description": "Elevator platform: a brush that moves up and down by its height.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "height", "type": "float", "default": "0", "description": "Travel height in units."},
        {"name": "speed", "type": "float", "default": "150", "description": "Movement speed in units per second."}
      ],
      "inputs": [
        {"name": "GoUp", "argumentType": "void", "description": "Move the platform up."},
        {"name": "GoDown", "argumentType": "void", "description": "Move the platform down."},
        {"name": "Toggle", "argumentType": "void", "description": "Toggle the platform."}
      ],
      "outputs": [],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "func_breakable",
      "kind": "brush",
      "description": "Breakable brush: breaks when its health reaches zero, firing OnBreak.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "health", "type": "int", "default": "0", "description": "Health. 0 = unbreakable."},
        {"name": "material", "type": "int", "default": "0", "description": "Material type: 0 = glass, 1 = wood, 2 = metal, 3 = flesh, 4 = cinder block, 5 = ceiling tile, 6 = computer, 7 = unbreakable glass, 8 = rock."},
        {"name": "explodemagnitude", "type": "int", "default": "0", "description": "Explosion magnitude when broken. 0 = no explosion."}
      ],
      "inputs": [
        {"name": "Break", "argumentType": "void", "description": "Break the brush."},
        {"name": "SetHealth", "argumentType": "int", "description": "Set the health."}
      ],
      "outputs": [
        {"name": "OnBreak", "argumentType": "void", "description": "Fired when the brush breaks."},
        {"name": "OnHealthChanged", "argumentType": "float", "description": "Fired when health changes; passes the new health."}
      ],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "func_pushable",
      "kind": "brush",
      "description": "Pushable brush: a physics brush the player can push around.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "health", "type": "int", "default": "0", "description": "Health. 0 = unbreakable."},
        {"name": "friction", "type": "float", "default": "0", "description": "Friction against the ground. 0 = default."}
      ],
      "inputs": [
        {"name": "Break", "argumentType": "void", "description": "Break the brush."}
      ],
      "outputs": [
        {"name": "OnBreak", "argumentType": "void", "description": "Fired when the brush breaks."},
        {"name": "OnHealthChanged", "argumentType": "float", "description": "Fired when health changes; passes the new health."}
      ],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "info_player_teamspawn",
      "kind": "point",
      "description": "Team spawn point: a player spawn for one team.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "TeamNum", "type": "int", "default": "0", "description": "Team number. 0 = any team."}
      ],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "info_player_deathmatch",
      "kind": "point",
      "description": "Deathmatch spawn point.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "info_node",
      "kind": "point",
      "description": "AI navigation node: a point NPCs can path through.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "info_node_hint",
      "kind": "point",
      "description": "AI node hint: tells NPCs how to use a nearby node (cover, sniper spot, guard post).",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "hint", "type": "string", "description": "Hint type, e.g. Crouch Cover, Guard Position, Sniper, Watch Position (see the FGD HintType list)."},
        {"name": "nodeid", "type": "int", "default": "0", "description": "Node ID this hint belongs to. 0 = auto-assign at compile."}
      ],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "info_target",
      "kind": "point",
      "description": "Named target point: an aim or destination marker other entities can point at.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}]
    },
    {
      "name": "info_teleport_destination",
      "kind": "point",
      "description": "Teleport destination: where a trigger_teleport sends entities.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "path_track",
      "kind": "point",
      "description": "Path node for a func_tracktrain route. Fires OnPass when a train passes it.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "target", "type": "target", "description": "Next path_track on the route."},
        {"name": "speed", "type": "float", "default": "0", "description": "Train speed at this track. 0 = keep current speed."},
        {"name": "orientationtype", "type": "int", "default": "0", "description": "Train orientation: 0 = no change, 1 = face direction of travel, 2 = face this track's angles."}
      ],
      "inputs": [
        {"name": "ToggleAlternatePath", "argumentType": "void", "description": "Toggle the alternate path."},
        {"name": "EnableAlternatePath", "argumentType": "void", "description": "Enable the alternate path."},
        {"name": "DisableAlternatePath", "argumentType": "void", "description": "Disable the alternate path."}
      ],
      "outputs": [
        {"name": "OnPass", "argumentType": "void", "description": "Fired when a train passes this track."}
      ],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "item_healthkit",
      "kind": "point",
      "description": "Health kit pickup: restores health on touch.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "item_healthvial",
      "kind": "point",
      "description": "Health vial pickup: restores a small amount of health on touch.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "item_battery",
      "kind": "point",
      "description": "Suit battery pickup: recharges suit power on touch.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "item_suit",
      "kind": "point",
      "description": "HEV suit pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "item_ammo_pistol",
      "kind": "point",
      "description": "Pistol ammo pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "item_ammo_smg1",
      "kind": "point",
      "description": "SMG ammo pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "item_ammo_ar2",
      "kind": "point",
      "description": "Pulse-rifle ammo pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "item_ammo_357",
      "kind": "point",
      "description": "Magnum ammo pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "item_ammo_crossbow",
      "kind": "point",
      "description": "Crossbow bolt pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "item_box_buckshot",
      "kind": "point",
      "description": "Shotgun shell box pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "item_rpg_round",
      "kind": "point",
      "description": "RPG rocket pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "weapon_crowbar",
      "kind": "point",
      "description": "Crowbar weapon pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "weapon_pistol",
      "kind": "point",
      "description": "Pistol weapon pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "weapon_smg1",
      "kind": "point",
      "description": "SMG weapon pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "weapon_ar2",
      "kind": "point",
      "description": "Pulse-rifle weapon pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "weapon_shotgun",
      "kind": "point",
      "description": "Shotgun weapon pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "weapon_357",
      "kind": "point",
      "description": "Magnum weapon pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "weapon_crossbow",
      "kind": "point",
      "description": "Crossbow weapon pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "weapon_rpg",
      "kind": "point",
      "description": "RPG weapon pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "weapon_frag",
      "kind": "point",
      "description": "Frag grenade weapon pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "weapon_physcannon",
      "kind": "point",
      "description": "Gravity gun weapon pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "weapon_stunstick",
      "kind": "point",
      "description": "Stunstick weapon pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "weapon_slam",
      "kind": "point",
      "description": "S.L.A.M. weapon pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "weapon_bugbait",
      "kind": "point",
      "description": "Bugbait weapon pickup.",
      "baseClasses": ["Targetname"],
      "keyvalues": [],
      "inputs": [],
      "outputs": [],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "npc_citizen",
      "kind": "point",
      "description": "Citizen NPC: a human ally that follows squad orders and takes cover.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "squadname", "type": "string", "description": "Squad the NPC belongs to."},
        {"name": "hintgroup", "type": "string", "description": "Hint group the NPC is limited to."},
        {"name": "sleepstate", "type": "int", "default": "0", "description": "Sleep state: 0 = none, 1 = waiting for threat, 2 = waiting for PVS, 3 = waiting for input."},
        {"name": "wakeradius", "type": "float", "default": "0", "description": "Radius in which the NPC wakes. 0 = default."},
        {"name": "wakesquad", "type": "int", "default": "0", "description": "Wake the whole squad when woken. 0 = no, 1 = yes."},
        {"name": "physdamagescale", "type": "float", "default": "1", "description": "Scale for physics damage taken."}
      ],
      "inputs": [
        {"name": "Wake", "argumentType": "void", "description": "Wake the NPC."},
        {"name": "Sleep", "argumentType": "void", "description": "Put the NPC to sleep."},
        {"name": "SetRelationship", "argumentType": "string", "description": "Set a relationship to another entity."},
        {"name": "SetSquad", "argumentType": "string", "description": "Set the squad the NPC belongs to."},
        {"name": "IgnoreDangerSounds", "argumentType": "float", "description": "Ignore danger sounds for this many seconds."}
      ],
      "outputs": [
        {"name": "OnDamaged", "argumentType": "void", "description": "Fired when the NPC takes damage."},
        {"name": "OnDeath", "argumentType": "void", "description": "Fired when the NPC dies."},
        {"name": "OnHalfHealth", "argumentType": "void", "description": "Fired when the NPC reaches half health."},
        {"name": "OnFoundEnemy", "argumentType": "void", "description": "Fired when the NPC finds an enemy."},
        {"name": "OnLostEnemy", "argumentType": "void", "description": "Fired when the NPC loses its enemy."},
        {"name": "OnSleep", "argumentType": "void", "description": "Fired when the NPC goes to sleep."},
        {"name": "OnWake", "argumentType": "void", "description": "Fired when the NPC wakes."}
      ],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    },
    {
      "name": "npc_combine_s",
      "kind": "point",
      "description": "Combine soldier NPC: an enemy soldier that patrols, takes cover and fights in squads.",
      "baseClasses": ["Targetname"],
      "keyvalues": [
        {"name": "squadname", "type": "string", "description": "Squad the NPC belongs to."},
        {"name": "hintgroup", "type": "string", "description": "Hint group the NPC is limited to."},
        {"name": "sleepstate", "type": "int", "default": "0", "description": "Sleep state: 0 = none, 1 = waiting for threat, 2 = waiting for PVS, 3 = waiting for input."},
        {"name": "wakeradius", "type": "float", "default": "0", "description": "Radius in which the NPC wakes. 0 = default."},
        {"name": "wakesquad", "type": "int", "default": "0", "description": "Wake the whole squad when woken. 0 = no, 1 = yes."},
        {"name": "physdamagescale", "type": "float", "default": "1", "description": "Scale for physics damage taken."}
      ],
      "inputs": [
        {"name": "Wake", "argumentType": "void", "description": "Wake the NPC."},
        {"name": "Sleep", "argumentType": "void", "description": "Put the NPC to sleep."},
        {"name": "SetRelationship", "argumentType": "string", "description": "Set a relationship to another entity."},
        {"name": "SetSquad", "argumentType": "string", "description": "Set the squad the NPC belongs to."},
        {"name": "IgnoreDangerSounds", "argumentType": "float", "description": "Ignore danger sounds for this many seconds."}
      ],
      "outputs": [
        {"name": "OnDamaged", "argumentType": "void", "description": "Fired when the NPC takes damage."},
        {"name": "OnDeath", "argumentType": "void", "description": "Fired when the NPC dies."},
        {"name": "OnHalfHealth", "argumentType": "void", "description": "Fired when the NPC reaches half health."},
        {"name": "OnFoundEnemy", "argumentType": "void", "description": "Fired when the NPC finds an enemy."},
        {"name": "OnLostEnemy", "argumentType": "void", "description": "Fired when the NPC loses its enemy."},
        {"name": "OnSleep", "argumentType": "void", "description": "Fired when the NPC goes to sleep."},
        {"name": "OnWake", "argumentType": "void", "description": "Fired when the NPC wakes."}
      ],
      "helpers": [{"kind": "origin"}, {"kind": "angles"}]
    }
  ]
}
""";

		static readonly EntityRegistry _catalog = EntityRegistry.LoadFromJson( GameplayEntitiesJson, "source1-gameplay" );

		/// <summary>All 42 gameplay classes, sorted by name (as <see cref="EntityRegistry.Classes"/> orders them).</summary>
		public static IReadOnlyList<EntityClass> Classes => _catalog.Classes;
		/// <summary>Number of gameplay classes in the catalog (42).</summary>
		public static int Count => _catalog.Count;
		/// <summary>All gameplay class names.</summary>
		public static IReadOnlyList<string> AllClassNames => Classes.Select( c => c.Name ).ToArray();

		/// <summary>Load a standalone registry holding only the gameplay classes.</summary>
		public static EntityRegistry LoadRegistry() => EntityRegistry.LoadFromJson( GameplayEntitiesJson, "source1-gameplay" );

		/// <summary>
		/// Emits the canonical definitions as registry JSON (schemaVersion 1, name
		/// "source1-gameplay") — the exact entries for the registry JSON. This is
		/// <see cref="GameplayEntitiesJson"/> verbatim; the registry worker merges it
		/// into the registry file (EntityRegistry.Merge rejects duplicates, so classes
		/// core already owns must not be re-added — none of these 42 is in core).
		/// </summary>
		public static string ToRegistryJson() => GameplayEntitiesJson;

		/// <summary>Gameplay class by name (case-insensitive), or null when it is not one of the 42.</summary>
		public static EntityClass Find( string className ) => _catalog.Find( className );

		/// <summary>True when the class is one of the 42 gameplay classes in this catalog.</summary>
		public static bool Contains( string className ) => _catalog.Contains( className );

		/// <summary>
		/// Merge the gameplay classes into a live registry (typically one loaded from
		/// core-source1.json). Classes the registry already has are skipped, never
		/// re-added, so calling this twice — or after the registry file gains one of
		/// these classes — is safe. Returns the names that were actually added, in
		/// catalog order.
		/// </summary>
		public static IReadOnlyList<string> EnsureGameplayClasses( EntityRegistry registry )
		{
			if ( registry is null ) throw new ArgumentNullException( nameof( registry ) );
			var added = new List<string>();
			foreach ( var cls in _catalog.Classes )
			{
				if ( registry.Find( cls.Name ) is not null ) continue;
				registry.Add( cls );
				added.Add( cls.Name );
			}
			return added;
		}

		// ---- Categories. These cover the core classes too (a trigger is a trigger
		// whichever registry file defined it), so Is* answers about the class, not
		// about which file it came from. ----

		static readonly HashSet<string> TriggerClasses = new( StringComparer.OrdinalIgnoreCase )
			{ "trigger_multiple", "trigger_once", "trigger_hurt", "trigger_push", "trigger_teleport" };
		static readonly HashSet<string> DoorClasses = new( StringComparer.OrdinalIgnoreCase )
			{ "func_door", "func_door_rotating" };
		static readonly HashSet<string> ButtonClasses = new( StringComparer.OrdinalIgnoreCase )
			{ "func_button", "func_rot_button" };
		static readonly HashSet<string> MoverClasses = new( StringComparer.OrdinalIgnoreCase )
			{ "func_movelinear", "func_tracktrain", "func_plat" };
		static readonly HashSet<string> BreakableClasses = new( StringComparer.OrdinalIgnoreCase )
			{ "func_breakable", "func_pushable" };
		static readonly HashSet<string> SpawnClasses = new( StringComparer.OrdinalIgnoreCase )
			{ "info_player_start", "info_player_teamspawn", "info_player_deathmatch" };
		static readonly HashSet<string> ItemClasses = new( StringComparer.OrdinalIgnoreCase )
			{ "item_healthkit", "item_healthvial", "item_battery", "item_suit",
				"item_ammo_pistol", "item_ammo_smg1", "item_ammo_ar2", "item_ammo_357",
				"item_ammo_crossbow", "item_box_buckshot", "item_rpg_round" };
		static readonly HashSet<string> WeaponClasses = new( StringComparer.OrdinalIgnoreCase )
			{ "weapon_crowbar", "weapon_pistol", "weapon_smg1", "weapon_ar2", "weapon_shotgun",
				"weapon_357", "weapon_crossbow", "weapon_rpg", "weapon_frag", "weapon_physcannon",
				"weapon_stunstick", "weapon_slam", "weapon_bugbait" };
		static readonly HashSet<string> NpcClasses = new( StringComparer.OrdinalIgnoreCase )
			{ "npc_citizen", "npc_combine_s" };
		static readonly HashSet<string> NodeClasses = new( StringComparer.OrdinalIgnoreCase )
			{ "info_node", "info_node_hint" };
		static readonly HashSet<string> TargetClasses = new( StringComparer.OrdinalIgnoreCase )
			{ "info_target", "info_teleport_destination" };
		static readonly HashSet<string> PathClasses = new( StringComparer.OrdinalIgnoreCase )
			{ "path_track" };

		/// <summary>Trigger class names (core + catalog).</summary>
		public static IReadOnlyCollection<string> TriggerClassNames => TriggerClasses;
		/// <summary>Door class names (core + catalog).</summary>
		public static IReadOnlyCollection<string> DoorClassNames => DoorClasses;
		/// <summary>Button class names (core + catalog).</summary>
		public static IReadOnlyCollection<string> ButtonClassNames => ButtonClasses;
		/// <summary>Mover class names.</summary>
		public static IReadOnlyCollection<string> MoverClassNames => MoverClasses;
		/// <summary>Breakable class names.</summary>
		public static IReadOnlyCollection<string> BreakableClassNames => BreakableClasses;
		/// <summary>Spawn class names (core + catalog).</summary>
		public static IReadOnlyCollection<string> SpawnClassNames => SpawnClasses;
		/// <summary>Item class names.</summary>
		public static IReadOnlyCollection<string> ItemClassNames => ItemClasses;
		/// <summary>Weapon class names.</summary>
		public static IReadOnlyCollection<string> WeaponClassNames => WeaponClasses;
		/// <summary>NPC class names.</summary>
		public static IReadOnlyCollection<string> NpcClassNames => NpcClasses;
		/// <summary>Node/hint class names.</summary>
		public static IReadOnlyCollection<string> NodeClassNames => NodeClasses;

		/// <summary>True for trigger_multiple / trigger_once / trigger_hurt / trigger_push / trigger_teleport.</summary>
		public static bool IsTrigger( string className ) => !string.IsNullOrWhiteSpace( className ) && TriggerClasses.Contains( className.Trim() );
		/// <summary>True for func_door / func_door_rotating.</summary>
		public static bool IsDoor( string className ) => !string.IsNullOrWhiteSpace( className ) && DoorClasses.Contains( className.Trim() );
		/// <summary>True for func_button / func_rot_button.</summary>
		public static bool IsButton( string className ) => !string.IsNullOrWhiteSpace( className ) && ButtonClasses.Contains( className.Trim() );
		/// <summary>True for func_movelinear / func_tracktrain / func_plat.</summary>
		public static bool IsMover( string className ) => !string.IsNullOrWhiteSpace( className ) && MoverClasses.Contains( className.Trim() );
		/// <summary>True for func_breakable / func_pushable.</summary>
		public static bool IsBreakable( string className ) => !string.IsNullOrWhiteSpace( className ) && BreakableClasses.Contains( className.Trim() );
		/// <summary>True for info_player_start / info_player_teamspawn / info_player_deathmatch.</summary>
		public static bool IsSpawn( string className ) => !string.IsNullOrWhiteSpace( className ) && SpawnClasses.Contains( className.Trim() );
		/// <summary>True for the item_* pickup classes.</summary>
		public static bool IsItem( string className ) => !string.IsNullOrWhiteSpace( className ) && ItemClasses.Contains( className.Trim() );
		/// <summary>True for the weapon_* pickup classes.</summary>
		public static bool IsWeapon( string className ) => !string.IsNullOrWhiteSpace( className ) && WeaponClasses.Contains( className.Trim() );
		/// <summary>True for any item_* or weapon_* pickup class.</summary>
		public static bool IsItemOrWeapon( string className ) => IsItem( className ) || IsWeapon( className );
		/// <summary>True for npc_citizen / npc_combine_s.</summary>
		public static bool IsNpc( string className ) => !string.IsNullOrWhiteSpace( className ) && NpcClasses.Contains( className.Trim() );
		/// <summary>True for info_node / info_node_hint.</summary>
		public static bool IsNodeOrHint( string className ) => !string.IsNullOrWhiteSpace( className ) && NodeClasses.Contains( className.Trim() );
		/// <summary>True for info_target / info_teleport_destination.</summary>
		public static bool IsTarget( string className ) => !string.IsNullOrWhiteSpace( className ) && TargetClasses.Contains( className.Trim() );
		/// <summary>True for path_track.</summary>
		public static bool IsPathTrack( string className ) => !string.IsNullOrWhiteSpace( className ) && PathClasses.Contains( className.Trim() );

		/// <summary>True when the class is any gameplay class: one of the 42 in this catalog, or a core gameplay class (trigger_multiple, func_door, info_player_start, ...).</summary>
		public static bool IsGameplayEntity( string className )
		{
			if ( string.IsNullOrWhiteSpace( className ) ) return false;
			return Contains( className )
				|| TriggerClasses.Contains( className ) || DoorClasses.Contains( className )
				|| ButtonClasses.Contains( className ) || MoverClasses.Contains( className )
				|| BreakableClasses.Contains( className ) || SpawnClasses.Contains( className )
				|| ItemClasses.Contains( className ) || WeaponClasses.Contains( className )
				|| NpcClasses.Contains( className ) || NodeClasses.Contains( className )
				|| TargetClasses.Contains( className ) || PathClasses.Contains( className );
		}
	}

	/// <summary>
	/// One placed trigger volume: a brush entity of a trigger class whose faces are
	/// the tools/toolstrigger tool texture. The volume is invisible and non-solid BY
	/// CONSTRUCTION — the face contract comes from the landed <see cref="ToolTexture"/>
	/// registry (Trigger | NonSolid), not from a flag stored here, so a change to the
	/// tool texture's flags is visible here with no code change. The bake/BSP pass
	/// decides how trigger faces are treated at compile time; this type only states
	/// the volume contract.
	/// </summary>
	public sealed class TriggerVolume
	{
		public TriggerVolume( MapEntity entity )
		{
			Entity = entity ?? throw new ArgumentNullException( nameof( entity ) );
			if ( !GameplayEntityCatalog.IsTrigger( entity.ClassName ) )
				throw new TriggersVolumesException( TriggersVolumesException.CodeNotTriggerClass,
					$"Entity #{entity.Id} ('{entity.ClassName}') is not a trigger volume: it is not a trigger class.", entity.ClassName );
		}

		/// <summary>The placed brush entity behind this volume.</summary>
		public MapEntity Entity { get; }
		public int Id => Entity.Id;
		public string ClassName => Entity.ClassName;
		public string TargetName => Entity.TargetName;
		public Vec3 Origin => Entity.Origin;
		/// <summary>Tied brushes in tie order. Empty is impossible — placement requires at least one brush.</summary>
		public int BrushCount => Entity.Brushes.Count;
		/// <summary>The tool material a trigger volume's faces carry: tools/toolstrigger.</summary>
		public string FaceMaterial => GameplayEntities.TriggerFaceMaterial;
		/// <summary>The landed tool-texture entry for the face material, or null when the tool registry does not know it.</summary>
		public ToolTexture FaceTexture => ToolTexture.Lookup( GameplayEntities.TriggerFaceMaterial );
		/// <summary>True when the face material carries the Trigger compile flag.</summary>
		public bool HasTriggerFlag => FaceTexture is not null && ( FaceTexture.CompileFlags & ToolCompileFlags.Trigger ) != 0;
		/// <summary>True when the face material carries the NonSolid compile flag: the volume never blocks movement.</summary>
		public bool IsNonSolid => FaceTexture is not null && ( FaceTexture.CompileFlags & ToolCompileFlags.NonSolid ) != 0;
		/// <summary>True when the face material is a trigger tool texture: the volume is never drawn as a world surface.</summary>
		public bool IsInvisible => FaceTexture is not null && FaceTexture.Kind == ToolTextureKind.Trigger;
		/// <summary>True when the volume contract holds end to end: trigger-flagged, non-solid, invisible.</summary>
		public bool IsValidVolume => HasTriggerFlag && IsNonSolid && IsInvisible && BrushCount > 0;

		public override string ToString() => $"#{Id} {ClassName} '{TargetName}' ({BrushCount} brush(es), {FaceMaterial})";
	}

	/// <summary>
	/// Gameplay placement + I/O wiring on top of <see cref="MapEntities"/> and
	/// <see cref="EntityIoList"/>. Every method takes the caller's live
	/// <see cref="MapEntities"/> (whose registry must already hold the gameplay
	/// classes — merge them first with
	/// <see cref="GameplayEntityCatalog.EnsureGameplayClasses"/>; forgetting that
	/// step is a named <see cref="TriggersVolumesException"/> with code
	/// GameplayClassesNotMerged, never a silent no-op) and delegates the actual
	/// placement to MapEntities, so the registry's keyvalue type checks still apply.
	/// </summary>
	public static class GameplayEntities
	{
		/// <summary>The tool material a trigger volume's faces carry.</summary>
		public const string TriggerFaceMaterial = "tools/toolstrigger";

		/// <summary>The compile flags a trigger volume's faces carry: Trigger | NonSolid.</summary>
		public static ToolCompileFlags TriggerFaceFlags => ToolCompileFlags.Trigger | ToolCompileFlags.NonSolid;

		// ---- Brush-entity placement ----

		/// <summary>
		/// Place a trigger volume (trigger_multiple / trigger_once / trigger_hurt /
		/// trigger_push / trigger_teleport): the class must be a trigger class and the
		/// registry must hold it. Brushes are stored by reference and never modified
		/// (see <see cref="MapEntities.PlaceBrushEntity{TBrush}"/>). Returns the
		/// volume descriptor; the placed entity is <see cref="TriggerVolume.Entity"/>.
		/// </summary>
		public static TriggerVolume PlaceTrigger<TBrush>( MapEntities entities, string className, IReadOnlyList<TBrush> brushes, IReadOnlyDictionary<string, string> keyvalues = null, Vec3? origin = null )
		{
			RequirePlaceable( entities, className, GameplayEntityCatalog.IsTrigger, TriggersVolumesException.CodeNotTriggerClass, "trigger" );
			return new TriggerVolume( entities.PlaceBrushEntity( className, brushes, keyvalues, origin ) );
		}

		/// <summary>Describe an already-placed entity as a trigger volume. Throws NotTriggerClass when its class is not a trigger class.</summary>
		public static TriggerVolume DescribeTrigger( MapEntity entity ) => new TriggerVolume( entity );

		/// <summary>Place a door (func_door / func_door_rotating): the class must be a door class and the registry must hold it.</summary>
		public static MapEntity PlaceDoor<TBrush>( MapEntities entities, string className, IReadOnlyList<TBrush> brushes, IReadOnlyDictionary<string, string> keyvalues = null, Vec3? origin = null )
		{
			RequirePlaceable( entities, className, GameplayEntityCatalog.IsDoor, TriggersVolumesException.CodeNotDoorClass, "door" );
			return entities.PlaceBrushEntity( className, brushes, keyvalues, origin );
		}

		/// <summary>Place a button (func_button / func_rot_button): the class must be a button class and the registry must hold it.</summary>
		public static MapEntity PlaceButton<TBrush>( MapEntities entities, string className, IReadOnlyList<TBrush> brushes, IReadOnlyDictionary<string, string> keyvalues = null, Vec3? origin = null )
		{
			RequirePlaceable( entities, className, GameplayEntityCatalog.IsButton, TriggersVolumesException.CodeNotButtonClass, "button" );
			return entities.PlaceBrushEntity( className, brushes, keyvalues, origin );
		}

		// ---- Point-entity placement (nodes / hints / targets) ----

		/// <summary>Place an AI navigation node (info_node) at a position.</summary>
		public static MapEntity PlaceNode( MapEntities entities, Vec3 position, IReadOnlyDictionary<string, string> keyvalues = null, Vec3? angles = null )
		{
			RequireMerged( entities, "info_node" );
			return entities.PlacePointEntity( "info_node", position, keyvalues, angles );
		}

		/// <summary>
		/// Place an AI node hint (info_node_hint) at a position. <paramref name="hint"/>
		/// is stored in keyvalue "hint" unless the caller already supplied one; pass ""
		/// for a hint whose type is set later. <paramref name="nodeId"/> (0 = auto)
		/// is stored in keyvalue "nodeid" unless the caller already supplied one.
		/// </summary>
		public static MapEntity PlaceNodeHint( MapEntities entities, Vec3 position, string hint, int nodeId = 0, IReadOnlyDictionary<string, string> keyvalues = null, Vec3? angles = null )
		{
			RequireMerged( entities, "info_node_hint" );
			var merged = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
			if ( keyvalues is not null )
				foreach ( var pair in keyvalues ) merged[pair.Key] = pair.Value;
			if ( !string.IsNullOrEmpty( hint ) && !merged.ContainsKey( "hint" ) ) merged["hint"] = hint;
			if ( nodeId != 0 && !merged.ContainsKey( "nodeid" ) ) merged["nodeid"] = nodeId.ToString( CultureInfo.InvariantCulture );
			return entities.PlacePointEntity( "info_node_hint", position, merged, angles );
		}

		/// <summary>Place a named target point (info_target) at a position.</summary>
		public static MapEntity PlaceTarget( MapEntities entities, Vec3 position, IReadOnlyDictionary<string, string> keyvalues = null, Vec3? angles = null )
		{
			RequireMerged( entities, "info_target" );
			return entities.PlacePointEntity( "info_target", position, keyvalues, angles );
		}

		/// <summary>Place a teleport destination (info_teleport_destination) at a position.</summary>
		public static MapEntity PlaceTeleportDestination( MapEntities entities, Vec3 position, IReadOnlyDictionary<string, string> keyvalues = null, Vec3? angles = null )
		{
			RequireMerged( entities, "info_teleport_destination" );
			return entities.PlacePointEntity( "info_teleport_destination", position, keyvalues, angles );
		}

		static void RequirePlaceable( MapEntities entities, string className, Func<string, bool> isCategory, string notCategoryCode, string categoryName )
		{
			if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
			if ( !isCategory( className ) )
				throw new TriggersVolumesException( notCategoryCode,
					$"Cannot place '{className}': it is not a {categoryName} class.", className ?? "" );
			RequireMerged( entities, className );
		}

		static void RequireMerged( MapEntities entities, string className )
		{
			if ( GameplayEntityCatalog.Contains( className ) && entities.Registry.Find( className ) is null )
				throw new TriggersVolumesException( TriggersVolumesException.CodeGameplayClassesNotMerged,
					$"The registry has no '{className}': merge the gameplay catalog first with GameplayEntityCatalog.EnsureGameplayClasses(registry).", className );
		}

		// ---- I/O wiring (validated: source output + target input must exist) ----

		/// <summary>
		/// Wire one output row: "when <paramref name="event"/> fires on the entity named
		/// <paramref name="sourceTargetName"/>, send <paramref name="input"/> to the
		/// entity named <paramref name="targetName"/>". Validated against the registry
		/// before anything is added: an unknown source or target name, an output the
		/// source class does not declare, or an input the target class does not declare
		/// each raise <see cref="TriggersVolumesException"/> with a named code and the
		/// list is left unchanged. Engine special targets ("!self", "!activator",
		/// "!player", ...) always resolve, mirroring <see cref="EntityIoList"/> — they
		/// have no class to check the input against, so the input is not validated
		/// for them.
		/// </summary>
		public static EntityIoOutput Wire( MapEntities entities, EntityIoList io, string sourceTargetName, string @event, string targetName, string input, double delaySeconds = 0, int maxFires = -1, string parameter = "" )
		{
			if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
			if ( io is null ) throw new ArgumentNullException( nameof( io ) );
			var source = entities.FindByTargetName( sourceTargetName ).FirstOrDefault();
			if ( source is null )
				throw new TriggersVolumesException( TriggersVolumesException.CodeUnknownSourceEntity,
					$"Cannot wire '{@event}': no entity is named '{sourceTargetName}'.", "", sourceTargetName ?? "" );
			var sourceClass = entities.Registry.Get( source.ClassName );
			if ( sourceClass.FindOutput( @event ) is null )
				throw new TriggersVolumesException( TriggersVolumesException.CodeUnknownOutput,
					$"Cannot wire '{@event}': '{source.ClassName}' ('{source.TargetName}') has no output '{@event}'.", source.ClassName, sourceTargetName ?? "" );
			if ( !EntityIoList.IsSpecialTarget( targetName ) )
			{
				var target = entities.FindByTargetName( targetName ).FirstOrDefault();
				if ( target is null )
					throw new TriggersVolumesException( TriggersVolumesException.CodeUnknownTargetEntity,
						$"Cannot wire '{@event}': no entity is named '{targetName}'.", "", targetName ?? "" );
				var targetClass = entities.Registry.Get( target.ClassName );
				if ( targetClass.FindInput( input ) is null )
					throw new TriggersVolumesException( TriggersVolumesException.CodeUnknownInput,
						$"Cannot wire '{@event}': '{target.ClassName}' ('{target.TargetName}') has no input '{input}'.", target.ClassName, targetName ?? "" );
			}
			var output = new EntityIoOutput( source.TargetName, @event, targetName, input, delaySeconds, maxFires, parameter ?? "" );
			string error = io.Add( output );
			if ( error is not null )
				throw new TriggersVolumesException( TriggersVolumesException.CodeInvalidOutput, error, source.ClassName, targetName ?? "" );
			return output;
		}

		/// <summary>The door/button I/O default: a button's OnPressed opens the door (OnPressed -> Open).</summary>
		public static EntityIoOutput WireButtonToDoor( MapEntities entities, EntityIoList io, string buttonTargetName, string doorTargetName, double delaySeconds = 0 )
			=> Wire( entities, io, buttonTargetName, "OnPressed", doorTargetName, "Open", delaySeconds );

		/// <summary>A trigger's OnStartTouch opens the door (OnStartTouch -> Open).</summary>
		public static EntityIoOutput WireTriggerToDoor( MapEntities entities, EntityIoList io, string triggerTargetName, string doorTargetName, double delaySeconds = 0 )
			=> Wire( entities, io, triggerTargetName, "OnStartTouch", doorTargetName, "Open", delaySeconds );

		// ---- Node listing ----

		/// <summary>
		/// All placed NPC nodes and hints, in placement order: info_node entries (Hint "")
		/// and info_node_hint entries (Hint = keyvalue "hint", "" when unset). NPCs
		/// themselves are not nodes — use <see cref="MapEntities.FindByClass"/> for those.
		/// </summary>
		public static IReadOnlyList<GameplayNode> ListNodes( MapEntities entities )
		{
			if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
			var nodes = new List<GameplayNode>();
			foreach ( var entity in entities.Entities )
			{
				if ( !GameplayEntityCatalog.IsNodeOrHint( entity.ClassName ) ) continue;
				entity.Keyvalues.TryGetValue( "hint", out string hint );
				nodes.Add( new GameplayNode { EntityId = entity.Id, ClassName = entity.ClassName, Origin = entity.Origin, Hint = hint ?? "" } );
			}
			return nodes;
		}
	}

	/// <summary>One placed NPC node or hint, as listed by <see cref="GameplayEntities.ListNodes"/>.</summary>
	public sealed class GameplayNode
	{
		public int EntityId;
		public string ClassName = "";
		public Vec3 Origin;
		/// <summary>Hint type for info_node_hint (keyvalue "hint"); "" for plain info_node entries and for hints with no type set.</summary>
		public string Hint = "";
		/// <summary>True for info_node_hint entries.</summary>
		public bool IsHint => string.Equals( ClassName, "info_node_hint", StringComparison.OrdinalIgnoreCase );

		public override string ToString() => IsHint
			? $"#{EntityId} {ClassName} '{Hint}' @ {MapEntities.FormatOrigin( Origin )}"
			: $"#{EntityId} {ClassName} @ {MapEntities.FormatOrigin( Origin )}";
	}

	/// <summary>
	/// One gameplay validation finding from <see cref="GameplayMapValidator.Validate"/>:
	/// a missing player start (error — Quick Play cannot validate the spawn) or a
	/// gameplay entity that does nothing (warnings). <see cref="EntityId"/> is the
	/// placed entity's stable id, or 0 when the finding is about the map as a whole.
	/// Missing-target ERRORS are owned by <see cref="MapEntities.Validate"/> and are
	/// not duplicated here.
	/// </summary>
	public sealed class GameplayIssue
	{
		public const string CodeNoPlayerStart = "NoPlayerStart";
		public const string CodeMultiplePlayerStarts = "MultiplePlayerStarts";
		public const string CodeTriggerWithoutTarget = "TriggerWithoutTarget";
		public const string CodeTriggerWithoutEffect = "TriggerWithoutEffect";
		public const string CodeButtonWithoutTarget = "ButtonWithoutTarget";
		public const string CodeMoverWithoutRoute = "MoverWithoutRoute";

		public MapEntityIssueSeverity Severity;
		public string Code = "";
		public string Message = "";
		public int EntityId;
		public string Key = "";

		public GameplayIssue() { }
		public GameplayIssue( MapEntityIssueSeverity severity, string code, string message, int entityId = 0, string key = "" )
		{
			Severity = severity; Code = code; Message = message; EntityId = entityId; Key = key;
		}

		public override string ToString() => string.IsNullOrEmpty( Key )
			? $"[{Severity} {Code}] {Message}"
			: $"[{Severity} {Code}] {Message} (entity #{EntityId}, key '{Key}')";
	}

	/// <summary>
	/// Gameplay validation for one map: the spawn rule Quick Play needs plus the
	/// "does nothing" warnings. A map is testable when there are no errors
	/// (<see cref="IsTestable"/>); warnings never block a test run, they name
	/// entities a mapper should look at.
	///
	/// Rules, in check order:
	///   * NoPlayerStart (Error) — no info_player_start. Team spawns do NOT count:
	///     Quick Play looks for info_player_start only and throws no-player-start
	///     without one. MultiplePlayerStarts (Warning) — Quick Play uses the first.
	///   * TriggerWithoutTarget (Warning) — a trigger_multiple / trigger_once with no
	///     output rows and no target-typed keyvalue, or a trigger_teleport with no
	///     "target" keyvalue and no output rows: the volume fires nothing.
	///   * TriggerWithoutEffect (Warning) — a trigger_hurt whose effective damage
	///     (stored keyvalue, else the registry default) is 0 with no outputs, or a
	///     trigger_push whose effective speed is 0 with no outputs: touching it
	///     does nothing.
	///   * ButtonWithoutTarget (Warning) — a func_button / func_rot_button with no
	///     output rows: pressing it fires nothing. (Doors need no warning: a door
	///     opens on touch/use by default.)
	///   * MoverWithoutRoute (Warning) — a func_tracktrain with no "target" keyvalue:
	///     the train has no route to follow.
	///
	/// An output row belongs to an entity when its SourceEntity is the entity's
	/// targetname (case-insensitive) or "#id" for an unnamed entity — the same
	/// convention EntityIo documents for SourceEntity.
	/// </summary>
	public static class GameplayMapValidator
	{
		public static IReadOnlyList<GameplayIssue> Validate( MapEntities entities, EntityIoList io )
		{
			if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
			if ( io is null ) throw new ArgumentNullException( nameof( io ) );
			var issues = new List<GameplayIssue>();

			var starts = entities.FindByClass( "info_player_start" );
			if ( starts.Count == 0 )
				issues.Add( new GameplayIssue( MapEntityIssueSeverity.Error, GameplayIssue.CodeNoPlayerStart,
					"The map has no info_player_start: Quick Play cannot validate a spawn.", 0, "" ) );
			else if ( starts.Count > 1 )
				issues.Add( new GameplayIssue( MapEntityIssueSeverity.Warning, GameplayIssue.CodeMultiplePlayerStarts,
					$"The map has {starts.Count} info_player_start entities; Quick Play uses the first.", starts[1].Id, "targetname" ) );

			var sources = new HashSet<string>( io.Outputs.Select( o => o.SourceEntity ), StringComparer.OrdinalIgnoreCase );
			foreach ( var entity in entities.Entities )
			{
				bool hasOutputs = ( !string.IsNullOrEmpty( entity.TargetName ) && sources.Contains( entity.TargetName ) )
					|| sources.Contains( "#" + entity.Id.ToString( CultureInfo.InvariantCulture ) );
				if ( GameplayEntityCatalog.IsTrigger( entity.ClassName ) ) CheckTrigger( entities, entity, hasOutputs, issues );
				else if ( GameplayEntityCatalog.IsButton( entity.ClassName ) )
				{
					if ( !hasOutputs )
						issues.Add( new GameplayIssue( MapEntityIssueSeverity.Warning, GameplayIssue.CodeButtonWithoutTarget,
							$"Entity #{entity.Id} ('{entity.ClassName}') has no outputs: pressing it fires nothing.", entity.Id, "" ) );
				}
				else if ( string.Equals( entity.ClassName, "func_tracktrain", StringComparison.OrdinalIgnoreCase ) )
				{
					if ( !HasKeyvalue( entity, "target" ) )
						issues.Add( new GameplayIssue( MapEntityIssueSeverity.Warning, GameplayIssue.CodeMoverWithoutRoute,
							$"Entity #{entity.Id} ('{entity.ClassName}') has no 'target' keyvalue: the train has no route to follow.", entity.Id, "target" ) );
				}
			}
			return issues;
		}

		/// <summary>True when validation finds no errors (warnings do not block a test run).</summary>
		public static bool IsTestable( MapEntities entities, EntityIoList io ) =>
			Validate( entities, io ).All( i => i.Severity != MapEntityIssueSeverity.Error );

		static void CheckTrigger( MapEntities entities, MapEntity entity, bool hasOutputs, List<GameplayIssue> issues )
		{
			if ( string.Equals( entity.ClassName, "trigger_multiple", StringComparison.OrdinalIgnoreCase )
				|| string.Equals( entity.ClassName, "trigger_once", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( !hasOutputs && !HasTargetKeyvalue( entities, entity ) )
					issues.Add( new GameplayIssue( MapEntityIssueSeverity.Warning, GameplayIssue.CodeTriggerWithoutTarget,
						$"Entity #{entity.Id} ('{entity.ClassName}') has no outputs and no target keyvalue: the volume fires nothing.", entity.Id, "" ) );
			}
			else if ( string.Equals( entity.ClassName, "trigger_teleport", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( !hasOutputs && !HasKeyvalue( entity, "target" ) )
					issues.Add( new GameplayIssue( MapEntityIssueSeverity.Warning, GameplayIssue.CodeTriggerWithoutTarget,
						$"Entity #{entity.Id} ('{entity.ClassName}') has no teleport target and no outputs: the volume fires nothing.", entity.Id, "target" ) );
			}
			else if ( string.Equals( entity.ClassName, "trigger_hurt", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( !hasOutputs && EffectiveFloat( entities, entity, "damage" ) <= 0 )
					issues.Add( new GameplayIssue( MapEntityIssueSeverity.Warning, GameplayIssue.CodeTriggerWithoutEffect,
						$"Entity #{entity.Id} ('{entity.ClassName}') has damage 0 and no outputs: touching it does nothing.", entity.Id, "damage" ) );
			}
			else if ( string.Equals( entity.ClassName, "trigger_push", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( !hasOutputs && EffectiveFloat( entities, entity, "speed" ) <= 0 )
					issues.Add( new GameplayIssue( MapEntityIssueSeverity.Warning, GameplayIssue.CodeTriggerWithoutEffect,
						$"Entity #{entity.Id} ('{entity.ClassName}') has speed 0 and no outputs: touching it does nothing.", entity.Id, "speed" ) );
			}
		}

		static bool HasKeyvalue( MapEntity entity, string key ) =>
			entity.Keyvalues.TryGetValue( key, out string value ) && !string.IsNullOrWhiteSpace( value );

		/// <summary>True when the entity has a non-empty keyvalue whose declared registry type is Target (e.g. trigger_teleport "target"). A "filtername" does NOT count — a filter is not a destination: a trigger with only a filter and no outputs still fires nothing.</summary>
		static bool HasTargetKeyvalue( MapEntities entities, MapEntity entity )
		{
			var cls = entities.Registry.Find( entity.ClassName );
			if ( cls is null ) return false;
			foreach ( var pair in entity.Keyvalues )
			{
				if ( string.IsNullOrEmpty( pair.Value ) ) continue;
				if ( string.Equals( pair.Key, "filtername", StringComparison.OrdinalIgnoreCase ) ) continue;
				if ( cls.FindKeyValue( pair.Key )?.Type == KeyValueType.Target ) return true;
			}
			return false;
		}

		/// <summary>
		/// Effective numeric keyvalue: the stored value when present, else the registry
		/// default for that keyvalue (e.g. trigger_hurt damage defaults to 10), else 0.
		/// A stored value that does not parse is 0 — the registry type check at
		/// placement time rejects mistyped values, so this only sees valid numbers.
		/// </summary>
		static float EffectiveFloat( MapEntities entities, MapEntity entity, string key )
		{
			if ( entity.Keyvalues.TryGetValue( key, out string stored )
				&& float.TryParse( stored, NumberStyles.Float, CultureInfo.InvariantCulture, out float storedValue ) )
				return storedValue;
			var cls = entities.Registry.Find( entity.ClassName );
			string fallback = cls?.FindKeyValue( key )?.DefaultValue ?? "";
			if ( float.TryParse( fallback, NumberStyles.Float, CultureInfo.InvariantCulture, out float defaultValue ) )
				return defaultValue;
			return 0;
		}
	}
}
