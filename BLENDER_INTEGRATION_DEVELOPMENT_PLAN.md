# KeyClickOverlay — Blender Integration Development Plan

Status: Architecture/design phase  
Target: KeyClickOverlay (.NET 10 WPF) + small Blender companion add-on  
Principle: KeyClickOverlay remains a standalone Windows application. Blender integration is optional and preset-driven.

## 1. Goals

Add an optional Blender integration that lets KeyClickOverlay use the visible content region of a selected Blender editor as its available overlay area.

The integration must:

- keep all input capture and overlay rendering in KeyClickOverlay;
- use a small Blender companion add-on only to describe Blender UI geometry;
- support more than the 3D Viewport;
- react to editor resizing, side panels, workspace changes, maximized editors, Blender window moves, and DPI/multi-monitor changes;
- support multiple Blender processes and, eventually, multiple Blender windows;
- store Blender behavior per KeyClickOverlay preset;
- cleanly return to ordinary standalone behavior when Blender integration is disabled;
- fail safely during recording if Blender or the requested editor disappears.

## 2. Non-goals

The first implementation will NOT:

- recreate KeyClickOverlay as a Blender-native overlay;
- move keyboard/mouse capture into Blender;
- make KeyClickOverlay dependent on Blender for normal use;
- persistently identify one exact Blender Area across Blender restarts/workspace reconstruction;
- introduce Vulkan or replace WPF rendering as part of the Blender integration.

## 3. Recommended Architecture

### 3.1 KeyClickOverlay

KeyClickOverlay remains authoritative for:

- global keyboard and mouse capture;
- rendering;
- presets;
- overlay position and scale;
- transparent/click-through behavior;
- target selection policy;
- Windows HWND discovery;
- DPI conversion and final screen coordinates;
- connection state and fail-safe behavior.

Add a new internal abstraction for the available overlay bounds:

- Standalone/Desktop bounds provider
- Blender editor bounds provider

The rest of the overlay/layout system should consume an `available bounds` result rather than knowing why those bounds exist.

### 3.2 Blender Companion Add-on

The Blender add-on is a geometry/provider bridge only.

It should:

- discover Blender windows;
- enumerate Areas and their active Spaces;
- identify editor type/subtype;
- identify the usable `WINDOW` Region within each Area;
- report Blender-local rectangle data;
- report process/window/session identity;
- report workspace information useful for diagnostics;
- monitor changes at a lightweight interval;
- send new geometry only when the geometry/state hash changes;
- send a slower heartbeat even when nothing changes.

It should NOT:

- capture keyboard input;
- render KeyClickOverlay content;
- contain KeyClickOverlay presets;
- decide which preset is active;
- decide where within the target KeyClickOverlay should render.

## 4. IPC / Communication

### Recommended first protocol: localhost TCP + newline-delimited JSON

Reason:

- supported by Python's standard library and .NET without extra dependencies;
- easy to inspect during development;
- bidirectional if needed later;
- local-only;
- simple to version.

### Discovery

Avoid a fixed port.

When KeyClickOverlay starts:

1. bind a TCP server to `127.0.0.1` on an ephemeral port;
2. create a random session token;
3. write a small discovery file under KeyClickOverlay's local app-data folder containing:
   - protocol version;
   - KeyClickOverlay process ID;
   - localhost port;
   - session token;
4. the Blender add-on reads this discovery file and connects;
5. the add-on includes the token in its handshake.

This avoids port conflicts and lets the add-on reconnect automatically after KeyClickOverlay restarts.

### Protocol messages

Initial protocol should remain deliberately small.

`HELLO`
- protocol version
- Blender PID
- Blender version
- add-on version
- Blender session UUID

`SNAPSHOT`
- Blender PID/session
- Blender windows
- runtime window ID
- Blender window x/y/width/height
- workspace name
- matching Areas
- runtime Area ID
- area type
- editor subtype
- Area rectangle
- usable WINDOW Region rectangle

`HEARTBEAT`
- session identity
- timestamp/state

Future messages can be added without changing protocol v1 semantics.

## 5. Blender Coordinate Model

Blender exposes:

- Window x/y/width/height;
- Area x/y/width/height;
- Region x/y/width/height.

For KeyClickOverlay, the preferred target rectangle is normally the editor's `WINDOW` Region rather than its whole Area.

This is important because the Region represents the actual editor content and naturally changes when UI regions such as an N-panel consume space.

The Blender add-on should send coordinates in Blender's own window coordinate system without trying to perform Windows DPI conversion.

### Windows-side conversion

KeyClickOverlay should:

1. find the correct Blender top-level HWND using Blender PID plus window geometry;
2. find the Blender client area's screen origin using Win32;
3. convert Blender's local Region rectangle to Windows physical screen coordinates;
4. perform any required Y-axis origin conversion;
5. convert physical pixels to WPF DIPs only at the boundary where WPF needs them.

Keep all geometry internally in physical pixels as long as practical. This makes mixed-DPI multi-monitor behavior less error-prone.

## 6. Supported Editor Targets

Preset data should store stable Blender identifiers, while the UI shows friendly names.

Suggested model:

- `AreaType`
- optional `SubType`

Examples:

- 3D Viewport
  - AreaType: `VIEW_3D`

- Geometry Nodes
  - AreaType: `NODE_EDITOR`
  - SubType/tree type: Geometry Nodes

- Shader Editor
  - AreaType: `NODE_EDITOR`
  - SubType/tree type: Shader

- Compositor
  - AreaType: `NODE_EDITOR`
  - SubType/tree type: Compositor

- Image Editor
  - AreaType: `IMAGE_EDITOR`

The exact Blender subtype property/value should be verified against every supported Blender version during implementation rather than hard-coded from assumptions.

Design the data model so more editor types can be added without changing the preset schema.

## 7. Multiple Matching Editors

Do not persist a raw Blender `Area` pointer in a preset. It is a runtime identity and is not robust across workspace reconstruction/restarts.

Instead, a preset stores an editor type plus a selection strategy.

Recommended strategies:

### `LastInteracted` — recommended default

- If only one matching editor exists, use it.
- If several exist, keep following the current one.
- When the user clicks inside a different matching editor, switch to that editor.
- KeyClickOverlay can use its existing global mouse knowledge plus the reported rectangles, so Blender does not need a complex permanent modal input tracker.

This gives intuitive behavior while remaining stable when merely moving the mouse.

### `LargestMatching`

Deterministic fallback: select the matching editor with the largest usable Region.

Useful when no last-interacted target exists yet.

### Future: `PinnedRuntimeEditor`

Allow explicitly pinning one current Area for the current Blender session, with automatic fallback if it disappears.

This can be added later without changing the basic protocol.

## 8. Multiple Blender Instances / Windows

Each Blender connection reports:

- process ID;
- session UUID;
- Blender windows;
- runtime window identity and geometry.

Recommended KeyClickOverlay policy:

1. Prefer the Blender process/window that currently owns the Windows foreground window.
2. If that process contains multiple Blender windows, match the actual HWND against the geometry reported for each `bpy.types.Window`.
3. Retain the selected Blender session while it remains valid.
4. Change sessions only when the user activates/interacts with another matching Blender window or the current session disappears.

Do not add a permanent "Blender instance #1/#2" identifier to presets in v1.

A future explicit instance-lock mode can be added if real workflows show it is necessary.

## 9. Preset Data

Blender integration belongs inside each KeyClickOverlay preset.

Suggested conceptual model:

```text
BlenderIntegration
    Enabled
    ActivationMode
    Target
        AreaType
        SubType
    SelectionMode
    BoundsMode
    AutoScale
    UnavailableBehavior
```

Suggested initial values:

- Enabled: false for existing/migrated presets
- ActivationMode: TransparentOnly
- SelectionMode: LastInteracted
- BoundsMode: WindowRegion
- AutoScale: false
- UnavailableBehavior: HideOverlay

### Backward compatibility

Existing presets must deserialize exactly as before.

When the Blender section is absent:

- treat Blender integration as disabled;
- do not modify any normal KeyClickOverlay behavior.

## 10. Transparent Mode

Recommended design:

`BlenderIntegration.Enabled` and `ActivationMode` are separate concepts.

Initial activation modes:

- `TransparentOnly` — recommended default
- `Always`

With `TransparentOnly`:

- the preset remembers Blender integration and its target;
- while KeyClickOverlay is in normal/non-transparent configuration mode, it behaves as the ordinary standalone app;
- when transparent mode is enabled, it immediately starts following the selected Blender editor;
- when transparent mode is disabled, it stops constraining itself to Blender but does not forget the target.

This avoids making the normal KeyClickOverlay UI awkward to configure while preserving a complete per-preset working context.

## 11. Missing Target / Blender Not Available

For recording safety, the default should be fail-closed.

If Blender integration is active but:

- Blender is not running;
- the companion add-on is not connected;
- Blender is minimized;
- the selected Blender window is hidden;
- the requested editor does not exist;
- geometry data becomes stale;

then hide the overlay content instead of falling back to full-screen/standalone placement.

Why: silently moving the overlay to an unexpected part of the recording is worse than temporarily hiding it.

The KeyClickOverlay configuration UI can show a non-intrusive status such as:

- Blender connected
- Waiting for 3D Viewport
- Waiting for Geometry Nodes
- Blender minimized
- Blender add-on not connected

A future preset option may allow another unavailable behavior, but v1 should have one safe, predictable rule.

## 12. Positioning vs Automatic Scaling

These must be separate features.

### Positioning / containment

Blender integration defines the available target rectangle.

The overlay should position itself relative to that rectangle.

Where possible, store position as:

- anchor;
- X/Y offset from that anchor;

rather than as an absolute desktop coordinate.

This remains stable when an editor is resized or moved.

### Automatic scaling

Separate preset option:

- AutoScale OFF: retain the preset's chosen overlay/key scale and only constrain/reposition within the editor.
- AutoScale ON: scale relative to the target Region size using a well-defined baseline/reference size and min/max limits.

Do not make editor-following imply scaling.

Implement editor-following first. Add AutoScale only after coordinate behavior is proven.

## 13. Update Strategy / Performance

Use `bpy.app.timers` for geometry observation.

Initial target:

- sample geometry approximately 10–20 times per second;
- calculate a compact state hash;
- transmit only if geometry/editor/workspace state changes;
- send heartbeat about once per second.

If resize motion looks visibly stepped, test a higher/adaptive rate before redesigning the protocol.

Never access `bpy` from a background thread.

If networking is moved to a worker thread later:

- Blender main thread builds immutable serialized snapshots;
- worker thread handles only socket I/O/queues.

## 14. Vulkan / GPU Investigation

Do NOT make Vulkan part of the Blender integration.

WPF already has a GPU-accelerated hardware rendering pipeline using DirectX when supported. The coordinate bridge itself is negligible compared with rendering.

Introducing Vulkan into a WPF overlay would mean creating/maintaining a separate rendering/interoperability path and would substantially increase complexity without first proving a performance problem.

Recommended approach:

1. complete functional Blender integration;
2. measure KeyClickOverlay frame/render cost, UI-thread time, input latency, CPU/GPU utilization, and WPF rendering tier;
3. identify the real bottleneck;
4. optimize WPF layout/invalidations first;
5. only evaluate a custom GPU renderer if profiling proves the WPF renderer is the limiting factor.

Keep this as a separate future performance workstream.

## 15. UI Proposal

Keep Blender settings compact and clearly preset-specific.

Suggested section:

```text
Blender integration              [ On / Off ]

When active                      [ Transparent mode only ▼ ]
Editor                           [ Geometry Nodes ▼ ]
Multiple matching editors        [ Last interacted ▼ ]
Use editor content region        [ On ]
Automatically scale overlay      [ Off ]

Status: Connected — Geometry Nodes
```

Only show secondary controls when Blender integration is enabled.

The UI should communicate that these options are stored in the current preset.

## 16. Implementation Phases

### Phase 0 — Branch and architecture document

Create:

`feature/blender-integration`

Add this development plan before changing production code.

Commit:
`Add Blender integration development plan`

### Phase 1 — Blender geometry proof of concept

Build the smallest possible Blender script/add-on that prints/reports:

- PID;
- Blender windows;
- Areas;
- editor type/subtype;
- Area rectangle;
- WINDOW Region rectangle.

Acceptance:

- 3D Viewport identified correctly;
- Geometry Nodes identified correctly;
- N-panel changes usable Region width;
- workspace/editor maximize changes geometry;
- separate Blender windows can be enumerated.

No KeyClickOverlay changes yet.

### Phase 2 — IPC proof of concept

Implement:

- KeyClickOverlay local server;
- discovery file/session token;
- Blender reconnect logic;
- HELLO/SNAPSHOT/HEARTBEAT;
- debug logging only.

Acceptance:

- start order does not matter;
- restarting either side reconnects;
- multiple Blender processes connect independently;
- malformed/old protocol messages fail cleanly.

Still do not move the overlay.

### Phase 3 — Windows coordinate resolution

Implement Blender PID/window-to-HWND matching and Region-to-screen conversion.

Add a temporary KeyClickOverlay debug rectangle/window that visualizes the resolved Blender Region.

Acceptance:

- aligns pixel-accurately with 3D Viewport content;
- aligns with Geometry Nodes;
- survives Blender window move/resize;
- survives N-panel open/close;
- survives workspace change;
- survives editor maximize/restore;
- works on mixed-DPI/multiple monitors.

Do not integrate presets yet.

### Phase 4 — Overlay bounds provider

Add the internal `BlenderEditorBoundsProvider`.

Wire only a developer/debug toggle first.

Acceptance:

- normal standalone behavior is unchanged when provider is inactive;
- overlay uses the Blender Region as available bounds when active;
- stale/disconnected geometry hides the integrated overlay safely.

### Phase 5 — Preset schema + migration

Add optional Blender integration data to presets.

Acceptance:

- all old presets load unchanged with integration OFF;
- new settings save/load correctly;
- switching presets immediately switches Blender behavior;
- Affinity/non-Blender preset restores ordinary behavior immediately.

### Phase 6 — Transparent-only activation

Implement `TransparentOnly` and `Always`.

Acceptance:

- preset with TransparentOnly follows Blender only while transparent mode is active;
- leaving transparent mode restores ordinary app bounds;
- returning to transparent mode immediately resumes the same target.

### Phase 7 — Multiple matching editor policy

Implement:

- LastInteracted;
- LargestMatching fallback.

Acceptance:

- two 3D Viewports do not cause oscillation;
- clicking another matching editor switches intentionally;
- target remains stable while only moving the mouse.

### Phase 8 — Missing/minimized behavior + status UI

Implement recording-safe hide behavior and user-facing connection/target status.

Acceptance:

- Blender exit;
- Blender minimize;
- target editor removed;
- workspace without requested editor;
- add-on disabled/disconnected;
- stale heartbeat.

All cases recover automatically when the target returns.

### Phase 9 — Automatic scaling

Only after editor following is stable.

Implement AutoScale separately with:

- baseline target size;
- scale ratio;
- min/max clamps;
- no change to position semantics.

### Phase 10 — Performance profiling

Measure first.

Only optimize after obtaining actual data.

## 17. Testing Matrix

At minimum test:

- Blender 4.5
- Blender 5.0
- Blender 5.1
- Blender 5.2/current supported version
- one Blender process
- two Blender processes
- Blender secondary window
- 3D Viewport
- Geometry Nodes
- Shader Editor
- Compositor
- Image Editor
- N-panel open/closed
- editor split/join
- editor maximize/restore
- workspace switch
- Blender minimized/restored
- Blender moved between monitors
- mixed DPI monitors
- KeyClickOverlay launched before Blender
- Blender launched before KeyClickOverlay
- KeyClickOverlay restarted
- Blender add-on reloaded
- preset switch Blender -> Blender target
- preset switch Blender -> non-Blender
- transparent mode on/off
- target absent then restored

## 18. Key Architectural Decisions

Current recommended decisions:

1. KeyClickOverlay remains standalone and authoritative.
2. Blender add-on is a small geometry provider.
3. IPC is localhost TCP + versioned JSON.
4. Use an ephemeral port + discovery file + random session token.
5. Blender reports local geometry; KeyClickOverlay resolves final Windows screen coordinates.
6. Target the editor's WINDOW Region by default.
7. Blender settings are per preset.
8. Blender integration defaults to TransparentOnly activation.
9. Positioning and AutoScale are separate.
10. Multiple editors default to LastInteracted with LargestMatching fallback.
11. Missing target / minimized Blender hides the integrated overlay.
12. Poll Blender UI geometry with `bpy.app.timers` and send only changed snapshots.
13. Do not add Vulkan to this project phase; profile rendering separately after functionality is stable.

## 19. First Coding Checkpoint

The first code change after this document should be Blender-side only:

> Enumerate Blender windows/editors/usable WINDOW Regions and print a stable debug snapshot.

Do not start IPC or modify KeyClickOverlay until this geometry proof of concept is correct.
