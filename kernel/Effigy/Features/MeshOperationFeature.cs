using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Effigy;

/// <summary>Replays formerly transient mesh edits as saved, editable history.
/// Only local mesh operations are accepted; never files, exports or nested features.</summary>
public sealed class MeshOperationFeature : Feature
{
	public override string TypeName => "Mesh edits";
	public readonly StringParam Commands = new( "Commands", "[]" );
	public override IReadOnlyList<IParam> Parameters => new IParam[] { Commands };
	public static bool Supports( string line ) => BuildScript.VerbOf( line ) is "weight" or "fix" or "retopo" or "transfer" or "sculpt";

	protected override void Execute( FeatureContext ctx )
	{
		string[] lines;
		try { lines = JsonSerializer.Deserialize<string[]>( Commands.Value ) ?? Array.Empty<string>(); }
		catch ( JsonException e ) { Fail( "Invalid mesh-edit commands", e.Message, "Commands must be a JSON array of script lines." ); return; }
		if ( lines.Length > 256 || lines.Any( l => l is null || l.Contains( '\n' ) || l.Contains( '\r' ) || !Supports( l ) ) )
		{
			Fail( "Unsupported mesh edit", "Only weight, fix, retopo, transfer and sculpt are allowed, one command per entry (maximum 256).", "Use ordinary features for other changes." );
			return;
		}
		var input = new PartStudio { Rig = ctx.Rig };
		input.Bodies.AddRange( ctx.Bodies );
		var result = BuildScript.Run( input, string.Join( "\n", lines ) );
		if ( !result.Ok ) Fail( "Mesh edit failed", result.Error, "Edit the saved commands to fix the failing line." );
		Warning = $"{lines.Length} saved mesh edit(s), replayed on rebuild";
	}

	/// <summary>Keep consecutive operations in one feature so weight locks share a run.</summary>
	public static (bool Ok, string Log) Append( PartStudio studio, string line )
	{
		if ( !Supports( line ) ) return (false, "Not a persistent mesh operation.");
		var feature = studio.Features.LastOrDefault() as MeshOperationFeature;
		if ( feature is null ) { feature = new MeshOperationFeature { Name = "Mesh edits" }; studio.Add( feature ); }
		var lines = JsonSerializer.Deserialize<List<string>>( feature.Commands.Value ) ?? new();
		lines.Add( line );
		feature.Commands.Value = JsonSerializer.Serialize( lines );
		studio.MarkDirty( feature );
		studio.Rebuild();
		return (feature.Error is null, feature.Error ?? $"Saved mesh edit: {line}");
	}
}
