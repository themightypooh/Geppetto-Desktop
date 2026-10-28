# Geppetto addon for Poohdot

Imports Effigy `.effigy` models into Poohdot (Godot 4.7.2 .NET). Each model becomes a scene
with its mesh, materials, `Skeleton3D` + `Skin`, and an `AnimationPlayer` holding any
`<model>*.riganim` clips that sit next to it. There is no panel UI yet.

## Install into a Poohdot project

1. Copy this folder to `res://addons/geppetto/` in your project.
2. Make the project a C# project (Project > Tools > C# > Create C# solution), then add the
   Effigy kernel and Geppetto core to its `.csproj`:

   ```xml
   <ItemGroup>
     <ProjectReference Include="/home/poo/Desktop/geppetto-desktop/kernel/Effigy.Kernel.csproj" />
     <ProjectReference Include="/home/poo/Desktop/geppetto-desktop/core/Geppetto.Core.csproj" />
   </ItemGroup>
   ```

3. Put a `nuget.config` next to the `.csproj` so the build uses Poohdot's own Godot packages:

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <configuration><packageSources><clear /><add key="poohdot" value="/home/poo/src/poohdot/nupkgs" /></packageSources></configuration>
   ```

4. Build once (`dotnet build`, or the Build button in Poohdot), then turn on
   Project > Project Settings > Plugins > Geppetto.
5. Drop `.effigy` files (and their `.riganim` clips) into the project. They import on their own.

Clips are only added when the model has a rig.

## Check it headless

`res://addons/geppetto/tools/harness.gd` imports files through this addon without opening a window and
prints a JSON report (import time, surfaces, vertices, triangles, bones, clips, materials,
warnings). Exit code 1 means a file failed.

```
env -u DISPLAY -u WAYLAND_DISPLAY ~/src/poohdot/godot/bin/godot.linuxbsd.editor.x86_64.mono \
  --headless --path . -s res://addons/geppetto/tools/harness.gd -- --out report.json models/thing.effigy
```

Set `G1_PROBE=1` to also run the old G1 kernel load/rebuild probe when the plugin starts.
