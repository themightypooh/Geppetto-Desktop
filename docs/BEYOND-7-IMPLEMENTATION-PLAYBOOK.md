# Geppetto "Beyond 7" & 16-Lane Surgical Implementation Playbook

> [!IMPORTANT]
> **Target Audience**: Implementing Agent Swarm / Builders.
> **Mandate**: **85% Builder Competence / 15% Judge Backstop**. Every section below provides **exact ground-truth file paths, line numbers, root-cause diagnostics, drop-in C# / GLSL / Python code blocks, and deterministic verification commands**.
> **File-Name Ground Truth Mapping** (avoid missing-file errors from shorthand in `beyond-7-brief.md`):
> - `"PaintEditService.cs"` in `beyond-7-brief.md` $\rightarrow$ **`/home/poo/Desktop/geppetto-desktop/TexturePaintService.cs`**
> - `"AiCrewService.cs"` in `beyond-7-brief.md` $\rightarrow$ **`/home/poo/Desktop/geppetto-desktop/AiCli.cs` + `AiResearch.cs` + `ScriptLive.cs`**
> - `"EditOp.cs"` in `Effigy` $\rightarrow$ **`/home/poo/games/projects/geppetto/Effigy/Features/EditFeature.cs`** & **`/home/poo/games/projects/geppetto/Effigy/Features/ModifyFeatures.cs`**

---

## Step 1 — Look, Materials & Emission Wiring (Fastest Visible Win)

### 1.1 Root-Cause Diagnosis (Verified in Code)
1. **Live OpenGL Viewport Drops All R6 Lobes (`MaterialPalette.cs:32-42`)**:
   - [`MaterialDescriptor.cs`](file:///home/poo/Desktop/geppetto-desktop/MaterialDescriptor.cs#L133-L159), [`MaterialLobes.cs`](file:///home/poo/Desktop/geppetto-desktop/MaterialLobes.cs#L8-L63), and [`MaterialFeatureShaders.cs`](file:///home/poo/Desktop/geppetto-desktop/MaterialFeatureShaders.cs#L11-L164) **already compile and evaluate** `Transmission`, `Thickness`, `Ior`, `AttenuationColor`, `AttenuationDistance`, `Subsurface`, `SubsurfaceRadius`, `SubsurfaceScale`, `Sheen`, `SheenTint`, `SheenRoughness`, `Clearcoat`, `ClearcoatRoughness`, `Anisotropy`, and `AnisotropyRotation` in GLSL!
   - **Broken Link #1**: [`MaterialPalette.ForLook(string name, Effigy.SlotLook look)`](file:///home/poo/Desktop/geppetto-desktop/MaterialPalette.cs#L32-L42) only copies `BaseColorFactor`, `RoughnessFactor`, `MetallicFactor`, and `EmissionFactor`—leaving every advanced lobe at `0`!
   - **Broken Link #2**: [`DocumentSession.PaintFingerprint()`](file:///home/poo/Desktop/geppetto-desktop/DocumentSession.cs#L438-L444) only hashes `look.Color`, `look.Rough`, `look.Metal`, and `look.Emit`, so changing any other `SlotLook` field never invalidates the viewport `RenderSnapshot`.
   - **Broken Link #3**: [`SlotLook.Shade()`](file:///home/poo/games/projects/geppetto/Effigy/Features/SlotLook.cs#L262-L275) only copies `Color`, `Rough`, `Metal`, `Emit`, `Clearcoat`, `ClearcoatRoughness`, `Subsurface`, and `SubsurfaceColor` to `ShadeMaterial`—dropping `Toon`, `ToonBands`, `ToonRim`, `Stylized`, `OutlineWidth`, `OutlineColor`, `Opacity`, `ShadowTint`, `Iridescence`, `Anisotropy`, `Sheen`, and `Transmission`.
2. **Sprite-Sheet Baker Hardcodes `0` in `out_m.png` G Channel (`SpriteSheetBaker.cs:212-407`)**:
   - [`SpriteSheetBaker.cs`](file:///home/poo/games/projects/geppetto/Effigy/Render/SpriteSheetBaker.cs#L243) ignores `studio.MaterialLooks` and `body.Mesh.Paint`, using `Palette[bi % Palette.Length]` instead, and line 405 writes `material.Pixels[di] = Raster.Rgb(depthByte, 0, 0);`.

---

### 1.2 Exact Code Changes for [`Effigy/Features/SlotLook.cs`](file:///home/poo/games/projects/geppetto/Effigy/Features/SlotLook.cs)

#### A. Expand `SlotLook` Properties (`SlotLook.cs:27-68`)
Replace lines 27–68 in [`Effigy/Features/SlotLook.cs`](file:///home/poo/games/projects/geppetto/Effigy/Features/SlotLook.cs#L27-L68) with:
```csharp
	public Vec3 Color { get; set; } = new( 0.72f, 0.72f, 0.74f );
	public float Rough { get; set; } = 0.55f;
	public float Metal { get; set; }
	public Vec3 Emit { get; set; }
	public float EmissionStrength { get; set; } = 1.0f;

	// Clearcoat (car lacquer, wet mucus, Varnished wood, polished chitin)
	public float Clearcoat { get; set; }
	public float ClearcoatRoughness { get; set; } = 0.08f;

	// Subsurface scattering (skin, wax, gums, fins, marble, jade, foliage)
	public float Subsurface { get; set; }
	public Vec3 SubsurfaceColor { get; set; } = new( 0.9f, 0.35f, 0.25f );

	// Anisotropy (brushed steel, hair cards, silk, Damascus, carbon fiber)
	public float Anisotropy { get; set; }
	public float AnisotropyRotation { get; set; }

	// Cloth / Velvet Sheen (Charlie microfibers, felt, moss, velvet, suede)
	public float Sheen { get; set; }
	public float SheenTint { get; set; } = 0.5f;
	public float SheenRoughness { get; set; } = 0.5f;

	// Dielectric Transmission & Beer-Lambert Absorption (glass, visors, ice, water, amber)
	public float Transmission { get; set; }
	public float Ior { get; set; } = 1.5f;
	public float Thickness { get; set; } = 0.1f;
	public Vec3 AttenuationColor { get; set; } = new( 1f, 1f, 1f );
	public float AttenuationDistance { get; set; } = 1.0f;

	// Thin-Film Iridescence (fish scales, oil slick, heated titanium, beetle carapace)
	public float Iridescence { get; set; }
	public float IridescenceIor { get; set; } = 1.3f;
	public float IridescenceThickness { get; set; } = 300f;

	// Stylized / Cel / Illustrative Shading & Chromatic Shadows
	public Vec3 ShadowTint { get; set; } = Vec3.Zero;
	public float Toon { get; set; }
	public int ToonBands { get; set; } = 3;
	public float ToonRim { get; set; } = 0.25f;
	public float Stylized { get; set; }
	public float OutlineWidth { get; set; }
	public Vec3 OutlineColor { get; set; } = new( 0.06f, 0.05f, 0.08f );
	public float Opacity { get; set; } = 1.0f;
	public float NormalStrength { get; set; } = 1.0f;

	// Procedural Detail & Weathering
	public string Pattern { get; set; } = "";
	public Vec3 PatternColor { get; set; } = new( 0.35f, 0.35f, 0.38f );
	public float PatternScale { get; set; } = 6f;
	public float PatternAmount { get; set; } = 0.5f;
	public floatEdgeWear { get; set; }
	public Vec3 WearColor { get; set; } = new( 0.82f, 0.82f, 0.85f );
	public float CavityDirt { get; set; }
	public Vec3 DirtColor { get; set; } = new( 0.14f, 0.11f, 0.09f );
	public float HeightBlend { get; set; }

	// Optional explicit texture map overrides
	public string AlbedoTexture { get; set; } = "";
	public string RoughTexture { get; set; } = "";
	public string MetalTexture { get; set; } = "";
	public string NormalTexture { get; set; } = "";
	public string AoTexture { get; set; } = "";
	public string EmitTexture { get; set; } = "";
```

#### B. Expand `SlotLook.Parse` (`SlotLook.cs:73-170`) & `SlotLook.Shade()` (`SlotLook.cs:262-275`)
Add key-value parsing in `SlotLook.Parse` for:
- `shadow` / `shadow_tint` $\rightarrow$ `look.ShadowTint = HexColor(val)`
- `iridescence` $\rightarrow$ `look.Iridescence = Clamp01(F(val))`
- `iridescence_ior` $\rightarrow$ `look.IridescenceIor = Math.Clamp(F(val), 1.0f, 2.5f)`
- `iridescence_thickness` $\rightarrow$ `look.IridescenceThickness = Math.Clamp(F(val), 100f, 1200f)`
- `anisotropy` / `aniso` $\rightarrow$ `look.Anisotropy = Math.Clamp(F(val), -1f, 1f)`
- `aniso_rot` / `anisotropy_rotation` $\rightarrow$ `look.AnisotropyRotation = F(val)`
- `sheen` $\rightarrow$ `look.Sheen = Clamp01(F(val))`
- `sheen_tint` $\rightarrow$ `look.SheenTint = Clamp01(F(val))`
- `sheen_rough` $\rightarrow$ `look.SheenRoughness = Clamp01(F(val))`
- `transmission` / `trans` $\rightarrow$ `look.Transmission = Clamp01(F(val))`
- `ior` $\rightarrow$ `look.Ior = Math.Clamp(F(val), 1.0f, 3.0f)`
- `thickness` $\rightarrow$ `look.Thickness = MathF.Max(0f, F(val))`
- `attenuation_color` / `atten_color` $\rightarrow$ `look.AttenuationColor = HexColor(val)`
- `attenuation_dist` / `atten_dist` $\rightarrow$ `look.AttenuationDistance = MathF.Max(0.001f, F(val))`
- `emit_strength` $\rightarrow$ `look.EmissionStrength = MathF.Max(0f, F(val))`
- `normal_strength` $\rightarrow$ `look.NormalStrength = Math.Clamp(F(val), 0f, 4f)`
- `emit_tex` $\rightarrow$ `look.EmitTexture = val`

Update `SlotLook.Shade()` (`SlotLook.cs:262-275`) so **every field** is forwarded to `ShadeMaterial`:
```csharp
	public ShadeMaterial Shade() => new()
	{
		Albedo = Color,
		Roughness = Rough,
		Metallic = Metal,
		Emission = Emit * EmissionStrength,
		Clearcoat = Clearcoat,
		ClearcoatRoughness = ClearcoatRoughness,
		Subsurface = Subsurface,
		SubsurfaceColor = SubsurfaceColor,
		Anisotropy = Anisotropy,
		AnisotropyRotation = AnisotropyRotation,
		Sheen = Sheen,
		SheenTint = SheenTint,
		SheenRoughness = SheenRoughness,
		Transmission = Transmission,
		Ior = Ior,
		Thickness = Thickness,
		AttenuationColor = AttenuationColor,
		AttenuationDistance = AttenuationDistance,
		Iridescence = Iridescence,
		IridescenceIor = IridescenceIor,
		IridescenceThickness = IridescenceThickness,
		ShadowTint = ShadowTint,
		Toon = Toon,
		ToonBands = ToonBands,
		ToonRim = ToonRim,
		Stylized = Stylized,
		OutlineWidth = OutlineWidth,
		OutlineColor = OutlineColor,
		Opacity = Opacity,
	};
```

#### C. Add the 32 One-Word Material Presets in `SlotLook.Preset(string name)` (`SlotLook.cs:190-260`)
Ensure `SlotLook.Preset` includes all 32 presets (existing 15 + 17 new):
- `car_paint`: `Color=(0.78f,0.06f,0.08f), Rough=0.22f, Metal=0.35f, Clearcoat=1.0f, ClearcoatRoughness=0.04f, Pattern="flake", PatternScale=48f, PatternAmount=0.18f`
- `brushed_steel`: `Color=(0.76f,0.78f,0.81f), Rough=0.32f, Metal=0.95f, Anisotropy=0.78f, AnisotropyRotation=0.0f, Pattern="brushed", PatternScale=28f, PatternAmount=0.22f`
- `anodized_titanium`: `Color=(0.45f,0.52f,0.68f), Rough=0.28f, Metal=0.92f, Anisotropy=0.55f, Iridescence=0.72f, IridescenceIor=1.45f, IridescenceThickness=420f`
- `velvet`: `Color=(0.42f,0.06f,0.14f), Rough=0.88f, Metal=0.0f, Sheen=0.95f, SheenTint=0.35f, SheenRoughness=0.38f, ShadowTint=(0.12f,0.02f,0.08f)`
- `silk`: `Color=(0.86f,0.78f,0.68f), Rough=0.36f, Metal=0.0f, Anisotropy=0.65f, Sheen=0.55f, SheenTint=0.8f, SheenRoughness=0.25f`
- `clear_glass`: `Color=(0.96f,0.98f,1.0f), Rough=0.04f, Metal=0.0f, Transmission=0.95f, Ior=1.52f, Thickness=0.15f, Clearcoat=0.6f, ClearcoatRoughness=0.02f`
- `frosted_glass`: `Color=(0.92f,0.95f,0.98f), Rough=0.42f, Metal=0.0f, Transmission=0.88f, Ior=1.52f, Thickness=0.25f`
- `ice`: `Color=(0.78f,0.91f,0.98f), Rough=0.18f, Metal=0.0f, Transmission=0.82f, Ior=1.31f, Thickness=0.8f, Subsurface=0.45f, SubsurfaceColor=(0.45f,0.78f,0.95f), AttenuationColor=(0.55f,0.85f,0.98f), AttenuationDistance=1.2f`
- `eye_cornea`: `Color=(0.98f,0.98f,0.99f), Rough=0.02f, Metal=0.0f, Transmission=0.92f, Ior=1.376f, Clearcoat=1.0f, ClearcoatRoughness=0.01f`
- `fish_scale`: `Color=(0.58f,0.66f,0.72f), Rough=0.18f, Metal=0.72f, Clearcoat=0.92f, ClearcoatRoughness=0.05f, Iridescence=0.85f, IridescenceIor=1.38f, IridescenceThickness=380f, Pattern="scales", PatternScale=18f, PatternAmount=0.45f`
- `wet_mucus`: `Color=(0.52f,0.22f,0.20f), Rough=0.12f, Metal=0.0f, Clearcoat=1.0f, ClearcoatRoughness=0.03f, Subsurface=0.65f, SubsurfaceColor=(0.88f,0.22f,0.16f)`
- `chitin`: `Color=(0.16f,0.12f,0.09f), Rough=0.28f, Metal=0.08f, Clearcoat=0.75f, ClearcoatRoughness=0.12f, Iridescence=0.48f, IridescenceIor=1.42f, IridescenceThickness=340f, Subsurface=0.22f, SubsurfaceColor=(0.65f,0.28f,0.08f)`
- `jade`: `Color=(0.18f,0.58f,0.38f), Rough=0.16f, Metal=0.0f, Subsurface=0.78f, SubsurfaceColor=(0.25f,0.82f,0.52f), Clearcoat=0.65f, ClearcoatRoughness=0.06f, Transmission=0.35f, Ior=1.66f, Thickness=0.6f, AttenuationColor=(0.15f,0.68f,0.42f), AttenuationDistance=0.8f`
- `wax`: `Color=(0.92f,0.85f,0.72f), Rough=0.38f, Metal=0.0f, Subsurface=0.88f, SubsurfaceColor=(0.98f,0.58f,0.28f)`
- `foliage`: `Color=(0.24f,0.48f,0.18f), Rough=0.52f, Metal=0.0f, Subsurface=0.68f, SubsurfaceColor=(0.52f,0.78f,0.16f), Sheen=0.25f`
- `toon_hero`: `Color=(0.85f,0.32f,0.24f), Rough=0.45f, Metal=0.0f, Toon=1.0f, ToonBands=3, ToonRim=0.35f, Stylized=0.85f, ShadowTint=(0.22f,0.08f,0.28f), OutlineWidth=0.015f, OutlineColor=(0.05f,0.04f,0.08f)`
- `damaged_hull`: `Color=(0.34f,0.38f,0.42f), Rough=0.68f, Metal=0.75f, EdgeWear=0.65f, WearColor=(0.78f,0.80f,0.84f), CavityDirt=0.72f, DirtColor=(0.28f,0.14f,0.07f), Pattern="rust_streak", PatternColor=(0.42f,0.18f,0.08f), PatternScale=8f, PatternAmount=0.55f`

---

### 1.3 Exact Code Changes for [`Effigy/Render/ShadedRender.cs`](file:///home/poo/games/projects/geppetto/Effigy/Render/ShadedRender.cs)

1. **Expand `ShadeMaterial` struct (`ShadedRender.cs:40-65`)** with the new fields (`Anisotropy`, `AnisotropyRotation`, `Sheen`, `SheenTint`, `SheenRoughness`, `Transmission`, `Ior`, `Thickness`, `AttenuationColor`, `AttenuationDistance`, `Iridescence`, `IridescenceIor`, `IridescenceThickness`, `ShadowTint`, `Toon`, `ToonBands`, `ToonRim`, `Stylized`, `OutlineWidth`, `OutlineColor`, `Opacity`).
2. **Evaluate all lobes in `ShadeFragment` (`ShadedRender.cs:155-260`)**:
   - **Shadow Tint**: When `ndotl < 0.5f` and `mat.ShadowTint.LengthSquared > 1e-6f`, blend shadow hemisphere ambient/diffuse toward `mat.ShadowTint * mat.Albedo` by `SmoothStep(0.5f, 0.0f, ndotl)`.
   - **Toon / Stylized Bands**: When `mat.Toon > 0f`, quantize `ndotl` into `Math.Max(2, mat.ToonBands)` bands: `float banded = MathF.Floor(ndotl * bands) / (bands - 1); ndotl = Lerp(ndotl, banded, mat.Toon);` and add hard rim `mat.ToonRim * (rim > 0.72f ? 1f : 0f)`. When `mat.Stylized > 0f`, boost midtone saturation by `1f + 0.25f * mat.Stylized` and warm-shift lit terminator.
   - **Thin-Film Iridescence (Belcour/Barla Airy phase)**:
     ```csharp
     if ( mat.Iridescence > 0f )
     {
         float cosTheta2 = MathF.Sqrt( MathF.Max( 0f, 1f - (1f - ndotv * ndotv) / (mat.IridescenceIor * mat.IridescenceIor) ) );
         float opd = 2f * mat.IridescenceIor * mat.IridescenceThickness * cosTheta2; // nm
         Vec3 phase = new(
             0.5f + 0.5f * MathF.Cos( 2f * MathF.PI * opd / 650f ),
             0.5f + 0.5f * MathF.Cos( 2f * MathF.PI * opd / 510f ),
             0.5f + 0.5f * MathF.Cos( 2f * MathF.PI * opd / 475f ) );
         specColor = Vec3.Lerp( specColor, phase, mat.Iridescence * (1f - 0.5f * ndotv) );
     }
     ```
   - **Anisotropy, Sheen, and Transmission**:
     - Anisotropy: stretch GGX roughness along tangent `T` and bitangent `B` (`ax = rough2 * (1 + aniso)`, `ay = rough2 * (1 - aniso)`).
     - Sheen: `float sheenLobe = mat.Sheen * MathF.Pow(1f - ndotv, 4f) * (1f - 0.5f * mat.SheenRoughness); Vec3 sheenCol = Vec3.Lerp(Vec3.One, mat.Albedo, mat.SheenTint) * sheenLobe;`.
     - Transmission: Beer-Lambert absorption `Vec3 absorb = new(MathF.Exp(-MathF.Max(0f, 1f - mat.AttenuationColor.x) * mat.Thickness / mat.AttenuationDistance), ...)` blended with background/back-light by `mat.Transmission * (1f - fresnel)`.
   - **Silhouette Outline Post-Pass**: When any material has `OutlineWidth > 0f`, run a 1-ring depth-discontinuity check on `ZBuf` and paint `OutlineColor` on silhouette pixels.

---

### 1.4 Exact Code Changes for Geppetto Desktop Live Viewport (`MaterialPalette.cs` & `DocumentSession.cs`)

#### A. Fix [`MaterialPalette.ForLook`](file:///home/poo/Desktop/geppetto-desktop/MaterialPalette.cs#L32-L42)
Replace `ForLook(string name, Effigy.SlotLook look)` in `/home/poo/Desktop/geppetto-desktop/MaterialPalette.cs` (lines 32–42) with:
```csharp
	public static MaterialDescriptor ForLook( string name, Effigy.SlotLook look )
	{
		var d = ForSlot( name ).Clone();
		d.BaseColorFactor = new Vector4( look.Color.x, look.Color.y, look.Color.z, Math.Clamp( look.Opacity, 0f, 1f ) );
		d.RoughnessFactor = Math.Clamp( look.Rough, 0f, 1f );
		d.MetallicFactor = Math.Clamp( look.Metal, 0f, 1f );
		d.EmissionFactor = new Vector3( look.Emit.x, look.Emit.y, look.Emit.z );
		d.EmissionStrength = MathF.Max( 0f, look.EmissionStrength );
		d.NormalScale = Math.Clamp( look.NormalStrength, 0f, 4f );

		// R6 Extended PBR Lobes (previously dropped!)
		d.Clearcoat = Math.Clamp( look.Clearcoat, 0f, 1f );
		d.ClearcoatRoughness = Math.Clamp( look.ClearcoatRoughness, 0.045f, 1f );
		d.Subsurface = Math.Clamp( look.Subsurface, 0f, 1f );
		d.SubsurfaceRadius = new Vector3( look.SubsurfaceColor.x, look.SubsurfaceColor.y, look.SubsurfaceColor.z );
		d.SubsurfaceScale = 1.0f;
		d.Anisotropy = Math.Clamp( look.Anisotropy, -1f, 1f );
		d.AnisotropyRotation = look.AnisotropyRotation;
		d.Sheen = Math.Clamp( look.Sheen, 0f, 1f );
		d.SheenTint = Math.Clamp( look.SheenTint, 0f, 1f );
		d.SheenRoughness = Math.Clamp( look.SheenRoughness, 0.045f, 1f );
		d.Transmission = Math.Clamp( look.Transmission, 0f, 1f );
		d.Ior = Math.Clamp( look.Ior, 1.0f, 3.0f );
		d.Thickness = MathF.Max( 0f, look.Thickness );
		d.AttenuationColor = new Vector3( look.AttenuationColor.x, look.AttenuationColor.y, look.AttenuationColor.z );
		d.AttenuationDistance = MathF.Max( 0.001f, look.AttenuationDistance );
		d.AlphaMode = look.Transmission > 0.01f || look.Opacity < 0.99f ? AlphaMode.Blend : AlphaMode.Opaque;
		return d;
	}
```

#### B. Fix [`DocumentSession.PaintFingerprint()`](file:///home/poo/Desktop/geppetto-desktop/DocumentSession.cs#L438-L444)
Replace lines 438–444 in `/home/poo/Desktop/geppetto-desktop/DocumentSession.cs` with:
```csharp
		foreach ( var (slot, look) in Document.Studio.MaterialLooks )
			sb.Append( "|L:" ).Append( slot ).Append( '=' )
			  .Append( look.Describe() ).Append( ':' )
			  .Append( look.ShadowTint ).Append( ':' )
			  .Append( look.Iridescence ).Append( ':' )
			  .Append( look.Anisotropy ).Append( ':' )
			  .Append( look.Sheen ).Append( ':' )
			  .Append( look.Transmission ).Append( ':' )
			  .Append( look.Toon ).Append( ':' )
			  .Append( look.Stylized );
```

---

### 1.5 Exact Code Changes for [`Effigy/Render/SpriteSheetBaker.cs`](file:///home/poo/games/projects/geppetto/Effigy/Render/SpriteSheetBaker.cs#L212-L407)
1. In `TileBuf` (`SpriteSheetBaker.cs:212-218`), add:
   ```csharp
   public readonly byte[] Emit = new byte[size * size];
   ```
2. In `RenderTile` (`SpriteSheetBaker.cs:240-265`), replace `var rgb = Palette[bi % Palette.Length];` with lookup from `studio.MaterialLooks` (by slot name `studio.SlotName(face.Material)`) + `body.Mesh.Paint`:
   - Compute `byte emitByte = (byte)Math.Clamp(MathF.Max(look.Emit.x, MathF.Max(look.Emit.y, look.Emit.z)) * look.EmissionStrength * 255f, 0f, 255f);`.
   - If `body.Mesh.Paint?.EmissionMap` is present (`PaintCanvas` extended with `EmissionMap`), sample UV emission and take `Math.Max(emitByte, sampledEmitByte)`.
3. Pass `emitByte` into `Fill(..., byte emitByte)` (`SpriteSheetBaker.cs:340-369`) and store `buf.Emit[idx] = emitByte;`.
4. In `Blit` (`SpriteSheetBaker.cs:405`), change:
   ```csharp
   // BEFORE:
   material.Pixels[di] = Raster.Rgb( depthByte, 0, 0 );
   // AFTER:
   material.Pixels[di] = Raster.Rgb( depthByte, tile.Emit[si], 0 );
   ```

**Step 1 Verification Command**:
```bash
dotnet test /home/poo/games/projects/geppetto/Effigy.Tests/Effigy.Tests.csproj --filter "FullyQualifiedName~SlotLook|FullyQualifiedName~SpriteSheet"
dotnet build /home/poo/Desktop/geppetto-desktop/Geppetto.Desktop.csproj
```

---

## Step 2 — Paint End-to-End, Viewport Particles/Presets/DOF & Live Cabinet Sculpt

### 2.1 End-to-End Texture Paint (`TexturePaintService.cs`, `ServeMode.cs`, `DocumentSession.cs`, `FbxWriter.cs`)

1. **Add `EmissionMap` to `Effigy.PaintCanvas` (`Effigy/PaintCanvas.cs`)**:
   - Add `public byte[]? EmissionMap;` (`W * H * 3` RGB bytes), initialize lazily when an emissive brush/layer is painted, and include in `PaintCanvas.Clone()` and `BakeMaterial`.
2. **Wire `case "paint":` in [`ServeMode.cs`](file:///home/poo/Desktop/geppetto-desktop/ServeMode.cs#L952)**:
   - Add a `TexturePaintService? paintService;` field on `ServeLoop` (`ServeMode.cs:510`), lazily constructed when the active document/body has UVs (`PaintFeature.EnsureUvs(body.Mesh)`).
   - In `Apply(string line)` (`ServeMode.cs:953`), add:
     ```csharp
     case "paint":
     {
         // paint stroke X0 Y0 X1 Y1 ... [r=0.05] [s=0.8] [col=#ff8800] [rough=0.4] [metal=0.0] [emit=#000000] [sym=x]
         // OR paint fill [col=#...] | paint gradient ... | paint undo | paint redo | paint export <dir>
         Emit( HandlePaintCommand( arg ) );
         break;
     }
     ```
   - Inside `HandlePaintCommand(string arg)`:
     - Raycast screen coordinates `(x, y)` via `camera.ScreenRay(x, y, width, height)` against `sub.PickDrop(ro, rd)` to get 3D world hit + barycentric UV `(u, v)`.
     - Call `paintService.BeginStroke(settings)` $\rightarrow$ `paintService.StampStrokeSegment(uv0, uv1)` $\rightarrow$ `paintService.EndStroke()`.
     - **Durability Bridge**: If `liveDoc` is backed by a `Studio` (`Effigy` feature tree), also append/update a `PaintFeature` stroke (`PaintFeature.AddStroke(...)`) so saving the `.effigy` file persists the stroke!
     - Re-upload modified `PaintCanvas` bytes to the OpenGL texture (`RetainedBody.UpdatePaintTextures(canvas)`).
3. **Write Sidecar PNGs on Bare `FbxWriter.WriteFile` (`Effigy/FbxWriter.cs:55-67`)**:
   - In `FbxWriter.WriteFile` and `FbxWriter.WriteBinaryFile` (`Effigy/FbxWriter.cs:55-89`), check if `mesh.Paint is { Width: > 0, Height: > 0 } paint`:
     ```csharp
     if ( mesh.Paint is { Width: > 0, Height: > 0 } paint )
     {
         var dir = Path.GetDirectoryName( Path.GetFullPath( path ) ) ?? ".";
         var stem = Path.GetFileNameWithoutExtension( path );
         paint.SaveSidecarPngs( dir, stem ); // writes <stem>_color.png, <stem>_rough.png, <stem>_metal.png, <stem>_normal.png, <stem>_emit.png
     }
     ```
   - And in `WriteMaterialConnections` (`FbxWriter.cs`), emit `FileTexture` nodes referencing `<stem>_color.png`, `<stem>_normal.png`, etc.

### 2.2 Viewport Particles, Environment Presets & Camera DOF (`SceneRenderer.cs` & `ServeMode.cs`)

1. **Mount `ParticleSystem` / `GpuParticleSystem` in `SceneRenderer.RenderScene` (`SceneRenderer.cs:260-300`)**:
   - Add `public List<GpuParticleSystem> ParticleEmitters { get; } = new();` to `SceneRenderer`.
   - After rendering opaque/transparent bodies in `SceneRenderer.RenderScene`, iterate `ParticleEmitters`, call `emitter.Step(dt)` and `emitter.Render(viewProj, camera.Right, camera.Up)`.
   - Add `case "particle":` in `ServeMode.cs` (`particle add <preset|custom> bone=<name> rate=<r> ...`, `particle clear`) and `particle` command in `BuildScript.cs`.
2. **Environment Presets in `WorldEnvironment` (`WorldEnvironment.cs` & `ServeMode.cs`)**:
   - Add `WorldEnvironment.FromPreset(string preset)` supporting:
     - `studio_warm`: warm key (`#fff4e0`, 1.35), cool fill (`#8ab4f8`, 0.45), rim (`#ffffff`, 0.65), subtle bloom (`0.15`).
     - `underwater`: teal absorption fog (`#082c38`, density `0.025`), caustic zenith light (`#38b2ac`, 1.2), deep navy horizon (`#04141c`), God-ray scattering (`0.45`).
     - `horror_fog`: sickly green-grey exponential height fog (`#1a211e`, density `0.045`), harsh top spot (`#d8e2dc`, 1.6), cold rim, vignette (`0.45`).
     - `outdoor_overcast`: neutral diffuse sky dome (`#cbd5e1`, 1.1), soft ground bounce (`#475569`, 0.55), SSAO (`0.65`).
   - Wire `case "env":` in `ServeMode.cs` (`env preset <name>`) and `env` in `BuildScript.cs`.
3. **Camera Depth-of-Field (`PartTrackEvaluator` $\rightarrow$ `OrbitCamera` / `SceneRenderer`)**:
   - In `TimelineHost` / `ServeMode.cs`, when `PartTrackEvaluator` evaluates camera tracks `focal` and `aperture`, copy them to `camera.FocusDistance` and `camera.ApertureFStop`, and enable the CoC (Circle of Confusion) Bokeh pass in `PostProcessPipeline.cs`.

### 2.3 Live Cabinet Sculpt (`ServeMode.cs`, `StrokeFeature.cs`)
1. Add `case "sculpt":` in `ServeMode.cs`:
   - `sculpt brush <draw|clay_strips|crease|pinch|flatten|scrape|snake_hook|smooth|inflate|grab|mask|vdm|imm> radius=<r> strength=<s> [sym=x|y|z|radial:N] [dyntopo=0|detail] [level=L]`
   - `sculpt stroke X0 Y0 X1 Y1 ...` — raycasts screen points onto the active body (`sub.PickDrop(ro, rd)`), calls `StrokeFeature.ApplyDab` (now including `VDM`, `CurveIMM`, `Dyntopo`, `Polypaint`, and `Level` multires target), and incrementally updates the GPU vertex buffer (`RetainedBody.UpdatePositionsAndNormals`).
   - `sculpt reproject` — calls `SubdivideFeature.Reproject(coarse, detailTarget, levels)` to transfer sculpted detail across clean quad subdivisions.

---

## Step 3 — Smash the 1-Hole Limit (`Sweep`/`Loft`), Cross-DCC Modeling, Hair/26 Garments & Linework

### 3.1 Unlimited $N$-Hole `Skinner.SkinWithHoles` in [`Effigy/Features/SweepFeatures.cs`](file:///home/poo/games/projects/geppetto/Effigy/Features/SweepFeatures.cs#L146-L252)

#### Root Cause in Current Code
- `SweepFeatures.cs:464-470` (`SweepFeature`) and `SweepFeatures.cs:960-979` (`LoftFeature`) check `if (profile.Holes.Count > 1) Warning = "Only the first of ... holes was carried through..."` and call `Skinner.SkinWithHole(rings, holeRings, ...)` which hardcodes a single hole and uses a 1-to-1 quad strip for the end cap (impossible when there are $N \ge 2$ holes!).

#### Drop-In Replacement (`Skinner.SkinWithHoles` + `MultiHoleCap` via Bridge-Cut Ear-Clipping)
Add `SkinWithHoles` right below `SkinWithHole` in [`Effigy/Features/SweepFeatures.cs`](file:///home/poo/games/projects/geppetto/Effigy/Features/SweepFeatures.cs#L162):
```csharp
	/// <summary>
	/// Skins an outer ring sequence together with an arbitrary number of inner hole tunnels
	/// (<paramref name="holeTunnels"/>[h][r] is ring r of hole h). Builds each inner tunnel with
	/// inward-facing normals and caps both ends using bridge-cut polygon ear-clipping so 2, 6, or
	/// 50 holes (revolver cylinders, perforated heatshields, honeycombs, engine blocks) produce
	/// a 100% manifold, watertight solid (`BoundaryEdges == 0`).
	/// </summary>
	public static PolyMesh SkinWithHoles(
		IReadOnlyList<List<Vec3>> outerRings,
		IReadOnlyList<IReadOnlyList<List<Vec3>>> holeTunnels,
		int material,
		bool wrap = false )
	{
		if ( holeTunnels.Count == 0 )
			return SkinChecked( outerRings, !wrap, material, wrap );
		if ( holeTunnels.Count == 1 )
			return SkinWithHole( outerRings, holeTunnels[0], material, wrap );

		var mesh = BuildMultiHoled( outerRings, holeTunnels, material, wrap );
		if ( mesh.SignedVolume() < -1e-9f )
		{
			foreach ( var ring in outerRings ) ring.Reverse();
			foreach ( var tunnel in holeTunnels )
				foreach ( var ring in tunnel ) ring.Reverse();
			mesh = BuildMultiHoled( outerRings, holeTunnels, material, wrap );
		}
		return mesh;
	}

	static PolyMesh BuildMultiHoled(
		IReadOnlyList<List<Vec3>> outerRings,
		IReadOnlyList<IReadOnlyList<List<Vec3>>> holeTunnels,
		int material,
		bool wrap )
	{
		int ringCount = outerRings.Count;
		int outerPts = outerRings[0].Count;
		int holeCount = holeTunnels.Count;
		var mesh = new PolyMesh();

		var outerStarts = new int[ringCount];
		var holeStarts = new int[holeCount][];
		for ( int h = 0; h < holeCount; h++ )
			holeStarts[h] = new int[ringCount];

		for ( int r = 0; r < ringCount; r++ )
		{
			outerStarts[r] = mesh.Positions.Count;
			foreach ( var p in outerRings[r] ) mesh.AddVertex( p );

			for ( int h = 0; h < holeCount; h++ )
			{
				holeStarts[h][r] = mesh.Positions.Count;
				foreach ( var p in holeTunnels[h][r] ) mesh.AddVertex( p );
			}
		}

		int spans = wrap ? ringCount : ringCount - 1;
		var along = new float[spans + 1];
		for ( int r = 1; r <= spans; r++ )
			along[r] = along[r - 1] + (outerRings[r % ringCount][0] - outerRings[r - 1][0]).Length;
		float total = along[^1] > 0f ? along[^1] : 1f;

		void BuildTube( int[] starts, IReadOnlyList<List<Vec3>> rings, bool flip )
		{
			int count = rings[0].Count;
			var around = new float[count + 1];
			for ( int i = 0; i < count; i++ )
				around[i + 1] = around[i] + (rings[0][(i + 1) % count] - rings[0][i]).Length;
			float perimeter = around[^1] > 0f ? around[^1] : 1f;

			for ( int r = 0; r < spans; r++ )
			{
				int next = (r + 1) % ringCount;
				float v0 = along[r] / total, v1 = along[r + 1] / total;
				for ( int i = 0; i < count; i++ )
				{
					int j = (i + 1) % count;
					float u0 = around[i] / perimeter, u1 = around[i + 1] / perimeter;
					mesh.AddFace(
						flip
							? new[] { starts[r] + i, starts[next] + i, starts[next] + j, starts[r] + j }
							: new[] { starts[r] + i, starts[r] + j, starts[next] + j, starts[next] + i },
						flip
							? new[] { new Vec2( u0, v0 ), new Vec2( u0, v1 ), new Vec2( u1, v1 ), new Vec2( u1, v0 ) }
							: new[] { new Vec2( u0, v0 ), new Vec2( u1, v0 ), new Vec2( u1, v1 ), new Vec2( u0, v1 ) },
						material );
				}
			}
		}

		BuildTube( outerStarts, outerRings, flip: false );
		for ( int h = 0; h < holeCount; h++ )
			BuildTube( holeStarts[h], holeTunnels[h], flip: true );

		if ( wrap ) return mesh;

		// Cap r = 0 (start cap, flipped) and r = ringCount - 1 (end cap, unflipped)
		// using 2D plane projection + bridge-cut ear-clipping (reusing ProfileFinder/PolygonTriangulator).
		CapMultiHoleRing( mesh, outerRings, holeTunnels, outerStarts, holeStarts, ringIndex: 0, flip: true, material );
		CapMultiHoleRing( mesh, outerRings, holeTunnels, outerStarts, holeStarts, ringIndex: ringCount - 1, flip: false, material );
		return mesh;
	}
```
And in `SweepFeature` (`SweepFeatures.cs:461-471`) and `LoftFeature` (`SweepFeatures.cs:952-995`):
- Remove the `if (profile.Holes.Count > 1) Warning = ...` truncation!
- Loop over **all** `for (int h = 0; h < profile.Holes.Count; h++)`, build `holeTunnels[h]`, and call `Skinner.SkinWithHoles(rings, holeTunnels, Material.Clamped, wrap: closed)`.
- When `LoftFeature` sections have different hole topologies or $1\text{-to-}N$ outer profiles (e.g., 1 trunk splitting into 3 branches, or 1 solid section splitting into 2 holes), route to `Skinner.SkinBranchingLoft` which inserts a medial-axis saddle separator vertex at the midpoint plane and skins each sub-loop cleanly!

### 3.2 `Spin` Axis/Center Fix & `Mirror` `_L`/`_R` Bone Flip
1. **`Spin` in `Effigy/Features/ModifyFeatures.cs` (and `EditFeature.cs`)**:
   - Read `Axis` (`Vec3`, default `Vec3.UnitZ`) and `Center` (`Vec3`, default `Vec3.Zero`).
   - Build Rodrigues rotation matrix around `(Center, Axis.Normalized)` for each step `s = 1..steps` instead of hardcoded `(x*cos - y*sin, x*sin + y*cos, z)`.
2. **`MirrorFeature` in `Effigy/Features/ModifyFeatures.cs`**:
   - When mirroring a rigged `PolyMesh` across X (`plane.Normal.x`), remap each mirrored vertex's `BoneWeight` indices: if bone `b` has a counterpart `SwapLeftRightName(skeleton.Bones[b].Name)` (`_L` $\leftrightarrow$ `_R`, `.L` $\leftrightarrow$ `.R`, `left_` $\leftrightarrow$ `right_`), assign the weight to the counterpart bone index!

---

## Step 4 — Rigging, Animation, `PixelArmsStyle` First-Person Viewmodel Suite & `.vmdl_c` Probes

### 4.1 Expand [`Code/PixelArmsStyle.cs`](file:///home/poo/games/projects/geppetto/Code/PixelArmsStyle.cs#L1-L114) into a Complete First-Person Viewmodel Suite
In [`/home/poo/games/projects/geppetto/Code/PixelArmsStyle.cs`](file:///home/poo/games/projects/geppetto/Code/PixelArmsStyle.cs), add:
1. **Viewmodel Camera & Depth-Compression Settings**:
   ```csharp
   [Property, Group( "Viewmodel Camera" ), Range( 35f, 90f )]
   public float ViewmodelFov { get; set; } = 54f;

   [Property, Group( "Viewmodel Camera" )]
   public Vector3 ViewmodelOffset { get; set; } = new( 0f, 0f, 0f );

   [Property, Group( "Viewmodel Camera" ), Range( 0.1f, 1.0f )]
   public float DepthScale { get; set; } = 0.35f; // Compresses Z into near-clip band to prevent wall clipping

   [Property, Group( "Viewmodel Camera" ), Range( 0f, 1f )]
   public float AdsAlpha { get; set; } = 0f;

   [Property, Group( "Viewmodel Camera" )]
   public Vector3 AdsOffset { get; set; } = new( -4.2f, 6.5f, 1.8f );

   [Property, Group( "Stylization" )]
   public int SteppedFps { get; set; } = 0; // 0 = smooth, 12 or 24 = quantized retro/pixel-art step rate
   ```
2. **2nd-Order Critically Damped Procedural Viewmodel Spring (`StepViewmodelSprings(float dt, Angles lookDelta, Vector3 wishVelocity)`)**:
   - Evaluates mouse-look lag (`swayPos`, `swayRot`), walk-cycle Lissajous bob (`bobX = cos(phase) * ampX`, `bobZ = abs(sin(phase)) * ampZ`), jump/land impulse, and fire recoil impulse (`AddRecoilImpulse(Vector3 linearKick, Angles angularKick)`).
   - Blends `ViewmodelOffset` $\rightarrow$ `AdsOffset` by `SmoothStep(0f, 1f, AdsAlpha)` and attenuates idle sway by `1f - 0.85f * AdsAlpha`.
3. **Wire `viewmodel_cam` in `ServeMode.cs` & `BuildScriptAnim.cs`**:
   - `viewmodel_cam fov=54 offset=0,-12,4 ads=0|1` previews the exact first-person viewmodel framing inside the cabinet pane!
   - `viewmodel_grip weapon=<part> hand_l=<bone> hand_r=<bone> grip_l=<x,y,z> grip_r=<x,y,z>` constrains both hands to `weapon_bone` via 2-bone IK during `reload`, `inspect`, `fire`, and `draw_holster`.

### 4.2 Wire `MorphEditService` & Animation Callers in `ServeMode.cs`
In [`ServeMode.cs`](file:///home/poo/Desktop/geppetto-desktop/ServeMode.cs#L952), add:
- `case "morph":` $\rightarrow$ delegates to `MorphEditService` (`morph create <name>`, `morph select <name>`, `morph dab <op> <x> <y> <r> <s>`, `morph symmetrical <src_L> <dst_R>`, `morph corrective <poseName>`).
- `case "nla":` $\rightarrow$ delegates to `ClipBlendService` (`nla add <clip> <start> <end> <weight>`, `nla blend <clipA> <clipB> <alpha>`).
- `case "physics_pose":` $\rightarrow$ delegates to `EuphoriaService` / `AdvancedIkService` (`physics_pose balance`, `physics_pose ballistic <startF> <endF> <apexZ>`, `physics_pose autopose <bone> <x,y,z>`).

### 4.3 Headless `sbox-dev` `.vmdl_c` Engine Verification Script (`tools/verify-vmdl-probe.sh`)
Create `/home/poo/games/projects/geppetto/tools/verify-vmdl-probe.sh`:
```bash
#!/usr/bin/env bash
set -euo pipefail
VMDL_PATH="${1:?Usage: verify-vmdl-probe.sh <path/to/model.vmdl>}"
FBX_PATH="${VMDL_PATH%.vmdl}.fbx"

echo "[1/3] Running fbx2dmx oracle on ${FBX_PATH}..."
WINEDEBUG=-all wine ~/games/steamlibrary/steamapps/common/sbox/bin/win64/fbx2dmx.exe \
  -i "${FBX_PATH}" -o "/tmp/probe_check.dmx"

echo "[2/3] Inspecting VMDL KV3 blocks in ${VMDL_PATH}..."
for block in RenderMeshList Skeleton PhysicsShapeList HitboxSetList AttachmentList AnimConstraintList; do
  grep -q "${block}" "${VMDL_PATH}" && echo "  ✓ ${block} present" || echo "  · ${block} (optional)"
done

echo "[3/3] Verifying sidecar .vmat and .png textures..."
DIR="$(dirname "${VMDL_PATH}")"
ls -la "${DIR}"/*.vmat "${DIR}"/*.png 2>/dev/null || true
echo "PASS: Engine probe succeeded."
```

---

## Step 5 — Builder-First (85% Builder / 15% Judge) Competence Loop, Host Slots & The 4 Flagship Archetypes

### 5.1 Why Builders Fail Today & How We Fix Them (85% Effort)

1. **Compact Stage-Scoped Builder Prompts (`Effigy/ApiCatalog.cs`)**:
   - Add `ApiCatalog.CompactBuilderPrompt(BuilderStage stage)` (`Blockout`, `ProfileAndHoles`, `SurfaceAndCSG`, `DetailAndKitbash`, `LookAndPaint`, `RigAndAnim`).
   - Instead of dumping 4,000+ lines of every verb into the LLM context, each stage emits a **180-line cheat-sheet** containing only the verbs valid for that stage plus 3 golden micro-examples.
2. **Pre-Flight `PART_DIMENSIONS` Table Injection (`AiResearch.cs:115-210`)**:
   - In [`AiResearch.MeasureBrief`](file:///home/poo/Desktop/geppetto-desktop/AiResearch.cs#L115-L210), automatically run `PhotoAnalysis.ExtractPartDimensions(referenceImagePath, targetLengthInches)` before the builder writes line 1.
   - Inject a strict markdown table at the top of the builder prompt:
     ```markdown
     # LOCKED PART_DIMENSIONS (inches, X=left/right, Y=front/back, Z=bottom/top)
     | Part | X_min..X_max | Y_min..Y_max | Z_min..Z_max | Cross-Section |
     |---|---|---|---|---|
     | hull_body | -2.10..+2.10 | -18.00..+18.00 | -3.40..+3.80 | almond(0.58, 1.35) |
     ```
3. **Syntax Auto-Healer (`BuildScript.HealLine(string line)` in `Effigy/BuildScript.cs`)**:
   - Before `BuildScript.Step` parses a command line, run `HealLine`:
     - Strips markdown fences (`` ``` ``), leading bullets (`- `, `* `, `1. `), and inline `//` or `#` trailing comments.
     - Normalizes common LLM parameter aliases (`colour=` $\rightarrow$ `color=`, `roughness=` $\rightarrow$ `rough=`, `metallic=` $\rightarrow$ `metal=`, `emission=` $\rightarrow$ `emit=`, `subsurface=` $\rightarrow$ `sss=`, `radius=` $\rightarrow$ `r=`).
     - Clamps out-of-range floats instead of throwing fatal parse exceptions.
4. **Per-Part 5ms Deterministic Lint & Automatic Rollback (`ScriptLive.cs:161-241` & `AiCli.cs:320-345`)**:
   - **Eliminate `stopped.json` whole-run aborts on recoverable part errors!**
   - In [`ScriptLive.Drain`](file:///home/poo/Desktop/geppetto-desktop/ScriptLive.cs#L161-L241) and [`AiCli.cs`](file:///home/poo/Desktop/geppetto-desktop/AiCli.cs#L320-L345):
     - Snapshot `studio.CloneSnapshot()` before each `part <name>` block.
     - When the `part` block finishes, run a **5ms zero-VRAM C# check**:
       1. `mesh.SignedVolume() > 1e-6f`
       2. `TopologyReport.Analyze(mesh).NonManifoldEdges == 0`
       3. Bounding-box overlap / contact check against parent part (no floating islands $> 0.05\text{ in}$ away from parent)
       4. Symmetry check if part is on the centerline.
     - If a part fails or throws an exception: **restore `studio` from the pre-part snapshot** and return a 1-line surgical diagnostic to the builder (`"ROLLBACK part 'dorsal_fin': gap of 0.42in above 'body' Z_max=3.80; lower root Z to <= 3.75"`), allowing the builder to immediately re-emit that part without killing the run!
5. **`BuildLocal` Single-Model 8GB VRAM Runner (`AiCli.cs` & `host.py`)**:
   - On the RTX 5060 Ti (8 GB VRAM), **never load a coder model and a vision judge model in Ollama at the same time**.
   - `BuildLocal` runs **one** local coder model (`qwen2.5-coder:7b` or `14b`) with the 0-VRAM deterministic C# part linter active on every part.
   - Only after the full build script completes, if optional visual verification is requested, send `keep_alive: 0` to Ollama to unload the coder model before running a single `512x512` contact-sheet vision check.

### 5.2 Wire Leftover Host & ServeMode Slots (`ServeMode.cs` & `cabinet/host.py`)

1. **`case "ray":` in [`ServeMode.cs`](file:///home/poo/Desktop/geppetto-desktop/ServeMode.cs#L865)**:
   ```csharp
   case "ray":
   {
       var (ro, rd) = camera.ScreenRay( F( 0 ), F( 1 ), width, height );
       Emit( new JsonObject
       {
           ["ev"] = "ray",
           ["x"] = F( 0 ),
           ["y"] = F( 1 ),
           ["origin"] = new JsonArray( ro.x, ro.y, ro.z ),
           ["dir"] = new JsonArray( rd.x, rd.y, rd.z )
       } );
       return true;
   }
   ```
2. **`continueCheckpoint` & `drawOverStroke` Slots in [`cabinet/host.py`](file:///home/poo/Desktop/geppetto-desktop/cabinet/host.py#L1620)**:
   ```python
   @Slot(str, str, result="QVariantMap")
   def continueCheckpoint(self, run_dir: str, note: str = "") -> dict:
       return live_runs.continue_checkpoint(Path(run_dir), note)
   ```

---

### 5.3 Complete Build Blueprints for the 4 Flagship Archetypes

#### Archetype 1: `Barracuda v2` (`/home/poo/games/projects/fishyflop/build/barracuda/build.txt`)
Exercises: `almond` cross-sections (no cylinder tubes!), `Sweep` with operculum gill slit + recessed eye socket + 24 conical teeth, `fish_scale` Thin-Film Iridescence + `eye_cornea` Transmission + `wet_mucus` Clearcoat/SSS, and `Swim` axial wave clip for `flop-the-fish`.
```text
echo Why: Lock proportions to Great Barracuda reference (36in length, 4.2in max depth, 2.6in width, almond cross-section)
look silver_scale preset=fish_scale color=#8fa4b5 rough=0.16 metal=0.78 clearcoat=0.95 iridescence=0.88 iridescence_ior=1.38 iridescence_thickness=390
look dark_dorsal preset=fish_scale color=#1d3547 rough=0.22 metal=0.55 clearcoat=0.90 iridescence=0.72 shadow=#091520
look gum_flesh preset=wet_mucus color=#9e3b42 rough=0.14 clearcoat=0.95 sss=0.75 sss_color=#e04848
look cornea preset=eye_cornea
look tooth_enamel preset=tooth color=#f4f1e6 rough=0.18 sss=0.35

part body
  sketch s0 plane=yz origin=-14,0,0
    almond 0 0 1.1 2.0 0.55
  sketch s1 plane=yz origin=-4,0,0
    almond 0 0 1.3 2.2 0.58
  sketch s2 plane=yz origin=6,0,0
    almond 0 0 1.0 1.8 0.55
  sketch s3 plane=yz origin=14,0,-0.1
    almond 0 0 0.45 0.85 0.50
  loft s0 s1 s2 s3 smooth=true mat=silver_scale
```

#### Archetype 2: `Talon Plasma Pistol` + First-Person `PixelArmsStyle` Viewmodel (`build/talon_pistol/build.txt`)
Exercises: **Unlimited $N$-hole `Sweep`/`Loft`** (6-hole perforated barrel shroud + 4-hole ventilated cooling core in a single profile!), hard-surface `Bevel`/`Boolean`, emissive plasma coils (`out_m.png` G channel $> 200$), `anodized_titanium` + `brushed_steel`, and `PixelArmsStyle` viewmodel rig (`viewmodel_grip`, `fire`, `reload`, `inspect`, `ads_in`).
```text
echo Why: Perforated barrel shroud uses a 7-loop sketch (1 outer + 6 cooling holes) swept in one manifold pass via SkinWithHoles
look frame_metal preset=brushed_steel color=#4a525d rough=0.30 metal=0.94 aniso=0.75
look barrel_ti preset=anodized_titanium color=#5c6b73 rough=0.25 metal=0.92 iridescence=0.68
look plasma_glow color=#22e5ff rough=0.10 emit=#00f0ff emit_strength=3.5

part perforated_shroud
  sketch shroud_prof plane=yz origin=2,0,1.4
    rect -0.85 -1.10 1.70 2.20 r=0.28
    circle  0.00  0.00 0.48
    circle -0.52  0.62 0.16
    circle  0.52  0.62 0.16
    circle -0.52 -0.62 0.16
    circle  0.52 -0.62 0.16
    circle  0.00  0.78 0.14
  path shroud_axis
    pt 2.0 0 1.4
    pt 9.5 0 1.4
  sweep shroud_prof shroud_axis mat=barrel_ti
```

#### Archetype 3: `Deep-Sea Salvage Diver` (`build/salvage_diver/build.txt`)
Exercises: Full biped rig (`B-Bones` spine/arms), **Marvelous Designer 2D Pattern Garment** (`trench_coat` / diving drysuit with `PressureBalloon` inflation + `SelfCollision`), **3-Hole Copper Diving Helmet** (`Sweep`/`Loft` with front + left + right viewports in `clear_glass` Transmission), and Cascadeur ZMP-balanced heavy walk cycle.

#### Archetype 4: `Rusty Harbor Crane & Buoy` (`build/harbor_crane/build.txt`)
Exercises: Multi-hole perforated structural I-beams (`SkinWithHoles`), `damaged_hull` procedural rust-streak + cavity dirt + edge wear look, `Breakpiece` destructible crates (`PhysicsHullFromRender` + `BreakableJoint`), `ParticleSystem` sea-spray & warning beacon glow, and `underwater` / `outdoor_overcast` environment presets.
