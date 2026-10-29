#if TOOLS
#nullable enable
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Geppetto;

// The mode frame and bar over each 3D viewport (GeppettoModeBar), and the model dimmed behind a sketch.
// The dim is a RenderingServer overlay on the character's mesh instances: nothing on the nodes changes,
// so it can't be saved into the scene, and taking it off puts back exactly what was there.
public partial class GeppettoPlugin
{
	readonly List<GeppettoModeBar> _modeBars = new();
	readonly Dictionary<ulong, MeshInstance3D> _dimmed = new();
	StandardMaterial3D? _dimMaterial;

	void SetupModeBars()
	{
		for ( int i = 0; i < 4; i++ )
		{
			var surface = ViewportSurface( i );
			if ( surface == null ) continue;
			var bar = new GeppettoModeBar { Name = "GeppettoModeBar" };
			surface.AddChild( bar );
			_modeBars.Add( bar );
		}
	}

	void TeardownModeBars()
	{
		Undim();
		foreach ( var b in _modeBars ) if ( IsInstanceValid( b ) ) b.QueueFree();
		_modeBars.Clear();
	}

	bool _syncQueued;

	// Deferred and folded into one per frame: one edit fires several document changes, and a bar button's
	// own press rebuilds the bar.
	void SyncModeBars()
	{
		if ( _syncQueued ) return;
		_syncQueued = true;
		Callable.From( SyncModeBarsNow ).CallDeferred();
	}

	void SyncModeBarsNow()
	{
		_syncQueued = false;
		foreach ( var b in _modeBars ) if ( IsInstanceValid( b ) ) b.Sync();
		if ( GeppettoTools.Active is CadSketch { InMode: true } ) Dim();
		else Undim();
	}

	/// <summary>True while the model is dimmed behind a sketch (the window runs check it).</summary>
	public bool ModelDimmed => _dimmed.Count > 0;

	void Dim()
	{
		if ( _character?.Root is not { } root || !IsInstanceValid( root ) ) return;
		_dimMaterial ??= new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			AlbedoColor = new Color( 0.06f, 0.07f, 0.09f, 0.6f ),
		};
		// A rebuild swaps the mesh instances, so new ones are picked up on the next redraw.
		foreach ( var mi in root.FindChildren( "*", nameof( MeshInstance3D ), true, false ).OfType<MeshInstance3D>().Prepend( root as MeshInstance3D ) )
		{
			if ( mi == null || _dimmed.ContainsKey( mi.GetInstanceId() ) ) continue;
			RenderingServer.InstanceGeometrySetMaterialOverlay( mi.GetInstance(), _dimMaterial.GetRid() );
			_dimmed[mi.GetInstanceId()] = mi;
		}
	}

	void Undim()
	{
		foreach ( var mi in _dimmed.Values )
			if ( IsInstanceValid( mi ) )
				RenderingServer.InstanceGeometrySetMaterialOverlay( mi.GetInstance(), mi.MaterialOverlay?.GetRid() ?? new Rid() );
		_dimmed.Clear();
	}
}
#endif
