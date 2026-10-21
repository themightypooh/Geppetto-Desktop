#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Effigy;

namespace Marionette.Desktop;

public sealed record RebuildResult(
	bool Success,
	long Revision,
	RebuildReport? Report = null,
	RenderSnapshot? Snapshot = null,
	bool IsStale = false,
	string? ErrorMessage = null
)
{
	public bool HasErrors => Report?.HasErrors ?? (ErrorMessage != null);
}

public interface IParameterDragSession : IDisposable
{
	void UpdatePreview( object value );
	void Commit( object? finalValue = null );
	void Cancel();
}

public sealed class ParameterDragSession<T> : IParameterDragSession
{
	readonly EditTransaction _tx;
	readonly Action<T> _setter;
	readonly T _initialValue;
	readonly Action _markDirtyAndRebuild;
	readonly PartStudio _studio;
	bool _finished;

	internal ParameterDragSession(
		EditTransaction tx,
		Action<T> setter,
		T initialValue,
		Action markDirtyAndRebuild,
		PartStudio studio )
	{
		_tx = tx ?? throw new ArgumentNullException( nameof( tx ) );
		_setter = setter ?? throw new ArgumentNullException( nameof( setter ) );
		_initialValue = initialValue;
		_markDirtyAndRebuild = markDirtyAndRebuild ?? throw new ArgumentNullException( nameof( markDirtyAndRebuild ) );
		_studio = studio ?? throw new ArgumentNullException( nameof( studio ) );
	}

	public void UpdatePreview( T value )
	{
		if ( _finished ) throw new InvalidOperationException( "Parameter drag session is already finished." );
		_tx.UpdatePreview( () =>
		{
			_setter( value );
			_studio.PreviewQuality = true;
			_markDirtyAndRebuild();
		} );
	}

	void IParameterDragSession.UpdatePreview( object value )
	{
		UpdatePreview( (T)value );
	}

	public void Commit( T? finalValue = default )
	{
		if ( _finished ) throw new InvalidOperationException( "Parameter drag session is already finished." );
		_finished = true;

		T valueToCommit = EqualityComparer<T>.Default.Equals( finalValue, default ) ? _initialValue : finalValue!;

		_tx.Commit( () =>
		{
			if ( !EqualityComparer<T>.Default.Equals( finalValue, default ) )
			{
				_setter( finalValue! );
			}
			_studio.PreviewQuality = false;
			_markDirtyAndRebuild();
		}, isNoOp: () => EqualityComparer<T>.Default.Equals( _initialValue, valueToCommit ) );
	}

	void IParameterDragSession.Commit( object? finalValue )
	{
		if ( finalValue is null ) Commit( default );
		else Commit( (T)finalValue );
	}

	public void Cancel()
	{
		if ( _finished ) return;
		_finished = true;
		_tx.Cancel();
	}

	public void Dispose()
	{
		if ( !_finished ) Cancel();
	}
}

/// <summary>
/// Undoable parametric feature editing and detached background rebuild service.
/// Operates directly on the retained PartStudio, calling real MarkDirty and Rebuild,
/// validating parameters with whitelist checks, preserving feature IDs and downstream references,
/// coalescing repeated edits via EditTransaction, and providing thread-safe detached rebuilds.
/// </summary>
public static class ParametricEditService
{
	static PartStudio RequireEffigyStudio( DocumentSession session )
	{
		ArgumentNullException.ThrowIfNull( session );
		if ( session.SourceKind != DocumentSourceKind.Effigy || session.Studio == null )
		{
			throw new InvalidOperationException( "Parametric edits require an Effigy session with a retained PartStudio." );
		}
		return session.Studio;
	}

	// --- Feature Operations -----------------------------------------------------------------

	public static void AddFeature( DocumentSession session, Feature feature, int? targetIndex = null )
	{
		var studio = RequireEffigyStudio( session );
		ArgumentNullException.ThrowIfNull( feature );

		if ( string.IsNullOrEmpty( feature.Id ) )
		{
			feature.Id = Guid.NewGuid().ToString( "N" )[..8];
		}

		int count = studio.Features.Count;
		int index = targetIndex.HasValue ? Math.Clamp( targetIndex.Value, 0, count ) : count;

		var cmd = new DelegateEditCommand(
			$"Add {feature.TypeName} feature '{feature.Name ?? feature.Id}'",
			execute: () =>
			{
				if ( index >= studio.Features.Count )
				{
					studio.Add( feature );
				}
				else
				{
					studio.Insert( index, feature );
				}
				studio.Rebuild();
			},
			undo: () =>
			{
				studio.Remove( feature );
				studio.Rebuild();
			}
		);

		session.History.Execute( cmd );
	}

	public static void RemoveFeature( DocumentSession session, string featureId )
	{
		var studio = RequireEffigyStudio( session );
		if ( string.IsNullOrWhiteSpace( featureId ) ) throw new ArgumentException( "Feature ID cannot be empty.", nameof( featureId ) );

		int index = studio.Features.FindIndex( f => f.Id == featureId );
		if ( index < 0 ) throw new KeyNotFoundException( $"Feature with ID '{featureId}' was not found in PartStudio." );

		var feature = studio.Features[index];

		var cmd = new DelegateEditCommand(
			$"Remove {feature.TypeName} feature '{feature.Name ?? feature.Id}'",
			execute: () =>
			{
				studio.Remove( feature );
				studio.Rebuild();
			},
			undo: () =>
			{
				int insertAt = Math.Clamp( index, 0, studio.Features.Count );
				studio.Insert( insertAt, feature );
				studio.Rebuild();
			}
		);

		session.History.Execute( cmd );
	}

	public static void RemoveFeature( DocumentSession session, Feature feature )
	{
		ArgumentNullException.ThrowIfNull( feature );
		RemoveFeature( session, feature.Id );
	}

	public static void MoveFeature( DocumentSession session, int fromIndex, int toIndex )
	{
		var studio = RequireEffigyStudio( session );
		if ( fromIndex < 0 || fromIndex >= studio.Features.Count )
			throw new ArgumentOutOfRangeException( nameof( fromIndex ), $"From index {fromIndex} is out of range [0, {studio.Features.Count - 1}]." );
		if ( toIndex < 0 || toIndex >= studio.Features.Count )
			throw new ArgumentOutOfRangeException( nameof( toIndex ), $"To index {toIndex} is out of range [0, {studio.Features.Count - 1}]." );

		if ( fromIndex == toIndex ) return; // No-op

		var feature = studio.Features[fromIndex];

		var cmd = new DelegateEditCommand(
			$"Move {feature.TypeName} feature '{feature.Name ?? feature.Id}' from {fromIndex} to {toIndex}",
			execute: () =>
			{
				studio.Move( fromIndex, toIndex );
				studio.Rebuild();
			},
			undo: () =>
			{
				studio.Move( toIndex, fromIndex );
				studio.Rebuild();
			}
		);

		session.History.Execute( cmd );
	}

	public static void MoveFeature( DocumentSession session, string featureId, int toIndex )
	{
		var studio = RequireEffigyStudio( session );
		if ( string.IsNullOrWhiteSpace( featureId ) ) throw new ArgumentException( "Feature ID cannot be empty.", nameof( featureId ) );

		int fromIndex = studio.Features.FindIndex( f => f.Id == featureId );
		if ( fromIndex < 0 ) throw new KeyNotFoundException( $"Feature with ID '{featureId}' was not found in PartStudio." );

		MoveFeature( session, fromIndex, toIndex );
	}

	// --- Parameter Validation and Modification ----------------------------------------------

	sealed record ParamBinding(
		string Name,
		Type ValueType,
		Func<object> Getter,
		Action<object> Setter,
		Func<object, object> Validator
	);

	static ParamBinding ResolveBinding( Feature feature, string paramLabelOrName )
	{
		ArgumentNullException.ThrowIfNull( feature );
		if ( string.IsNullOrWhiteSpace( paramLabelOrName ) )
			throw new ArgumentException( "Parameter label or name cannot be empty.", nameof( paramLabelOrName ) );

		string target = paramLabelOrName.Trim();

		// Check whitelisted feature-level properties
		if ( string.Equals( target, "Name", StringComparison.OrdinalIgnoreCase ) )
		{
			return new ParamBinding(
				"Name",
				typeof( string ),
				() => feature.Name,
				val => feature.Name = (string)val,
				val => val is string s ? s : throw new ArgumentException( "Name must be a string." )
			);
		}

		if ( string.Equals( target, "Suppressed", StringComparison.OrdinalIgnoreCase ) )
		{
			return new ParamBinding(
				"Suppressed",
				typeof( bool ),
				() => feature.Suppressed,
				val => feature.Suppressed = (bool)val,
				val => val is bool b ? b : throw new ArgumentException( "Suppressed must be a boolean." )
			);
		}

		if ( string.Equals( target, "Visible", StringComparison.OrdinalIgnoreCase ) )
		{
			return new ParamBinding(
				"Visible",
				typeof( bool ),
				() => feature.Visible,
				val => feature.Visible = (bool)val,
				val => val is bool b ? b : throw new ArgumentException( "Visible must be a boolean." )
			);
		}

		// Find IParam on the feature
		IParam? matchedParam = null;

		foreach ( var p in feature.Parameters )
		{
			if ( string.Equals( p.Label, target, StringComparison.OrdinalIgnoreCase ) )
			{
				matchedParam = p;
				break;
			}
		}

		if ( matchedParam == null )
		{
			// Check public fields on feature of type IParam
			foreach ( var field in feature.GetType().GetFields( BindingFlags.Public | BindingFlags.Instance ) )
			{
				if ( typeof( IParam ).IsAssignableFrom( field.FieldType ) )
				{
					if ( string.Equals( field.Name, target, StringComparison.OrdinalIgnoreCase ) )
					{
						matchedParam = (IParam?)field.GetValue( feature );
						break;
					}
					if ( field.GetValue( feature ) is IParam ip && string.Equals( ip.Label, target, StringComparison.OrdinalIgnoreCase ) )
					{
						matchedParam = ip;
						break;
					}
				}
			}
		}

		if ( matchedParam == null )
		{
			throw new ArgumentException( $"Parameter '{paramLabelOrName}' is not a whitelisted parameter on {feature.TypeName}." );
		}

		return matchedParam switch
		{
			FloatParam fp => new ParamBinding(
				fp.Label,
				typeof( float ),
				() => fp.Value,
				val => fp.Value = (float)val,
				val =>
				{
					float f = val switch
					{
						float floatVal => floatVal,
						double doubleVal => (float)doubleVal,
						int intVal => (float)intVal,
						_ => throw new ArgumentException( $"Cannot assign {val?.GetType().Name ?? "null"} to FloatParam '{fp.Label}'." )
					};
					if ( !float.IsFinite( f ) )
						throw new ArgumentException( $"Float parameter '{fp.Label}' must be finite." );
					if ( f < fp.Min || f > fp.Max )
						throw new ArgumentOutOfRangeException( nameof( paramLabelOrName ), f, $"Value {f} is out of range [{fp.Min}, {fp.Max}] for parameter '{fp.Label}'." );
					return f;
				}
			),
			IntParam ip => new ParamBinding(
				ip.Label,
				typeof( int ),
				() => ip.Value,
				val => ip.Value = (int)val,
				val =>
				{
					int i = val switch
					{
						int intVal => intVal,
						long longVal => checked((int)longVal),
						short shortVal => shortVal,
						_ => throw new ArgumentException( $"Cannot assign {val?.GetType().Name ?? "null"} to IntParam '{ip.Label}'." )
					};
					if ( i < ip.Min || i > ip.Max )
						throw new ArgumentOutOfRangeException( nameof( paramLabelOrName ), i, $"Value {i} is out of range [{ip.Min}, {ip.Max}] for parameter '{ip.Label}'." );
					return i;
				}
			),
			BoolParam bp => new ParamBinding(
				bp.Label,
				typeof( bool ),
				() => bp.Value,
				val => bp.Value = (bool)val,
				val => val is bool b ? b : throw new ArgumentException( $"Cannot assign {val?.GetType().Name ?? "null"} to BoolParam '{bp.Label}'." )
			),
			ChoiceParam cp => new ParamBinding(
				cp.Label,
				typeof( int ),
				() => cp.Index,
				val => cp.Index = (int)val,
				val =>
				{
					if ( val is int idx )
					{
						if ( idx < 0 || idx >= cp.Options.Length )
							throw new ArgumentOutOfRangeException( nameof( paramLabelOrName ), idx, $"Index {idx} is out of range [0, {cp.Options.Length - 1}] for ChoiceParam '{cp.Label}'." );
						return idx;
					}
					if ( val is string str )
					{
						int found = Array.FindIndex( cp.Options, o => string.Equals( o, str, StringComparison.OrdinalIgnoreCase ) );
						if ( found < 0 )
							throw new ArgumentException( $"'{str}' is not a valid option for '{cp.Label}'. Options: {string.Join( ", ", cp.Options )}." );
						return found;
					}
					throw new ArgumentException( $"Cannot assign {val?.GetType().Name ?? "null"} to ChoiceParam '{cp.Label}'." );
				}
			),
			Vec3Param vp => new ParamBinding(
				vp.Label,
				typeof( Vec3 ),
				() => vp.Value,
				val => vp.Value = (Vec3)val,
				val =>
				{
					Vec3 v = val switch
					{
						Vec3 vec => vec,
						System.Numerics.Vector3 sysV => new Vec3( sysV.X, sysV.Y, sysV.Z ),
						_ => throw new ArgumentException( $"Cannot assign {val?.GetType().Name ?? "null"} to Vec3Param '{vp.Label}'." )
					};
					if ( !float.IsFinite( v.x ) || !float.IsFinite( v.y ) || !float.IsFinite( v.z ) )
						throw new ArgumentException( $"Vec3Param '{vp.Label}' coordinates must be finite." );
					return v;
				}
			),
			StringParam sp => new ParamBinding(
				sp.Label,
				typeof( string ),
				() => sp.Value,
				val => sp.Value = (string)val,
				val => val is string s ? s : throw new ArgumentException( $"Cannot assign {val?.GetType().Name ?? "null"} to StringParam '{sp.Label}'." )
			),
			BodySelectionParam bsp => new ParamBinding(
				bsp.Label,
				typeof( List<string> ),
				() => bsp.BodyIds.ToList(),
				val =>
				{
					bsp.BodyIds.Clear();
					if ( val is IEnumerable<string> ids ) bsp.BodyIds.AddRange( ids );
				},
				val => val is IEnumerable<string> ids ? ids.ToList() : throw new ArgumentException( $"Cannot assign {val?.GetType().Name ?? "null"} to BodySelectionParam '{bsp.Label}'." )
			),
			_ => throw new ArgumentException( $"Unsupported parameter type '{matchedParam.GetType().Name}'." )
		};
	}

	static bool ValuesEqual( object? a, object? b )
	{
		if ( a is null && b is null ) return true;
		if ( a is null || b is null ) return false;
		if ( a is float fa && b is float fb ) return MathF.Abs( fa - fb ) < 1e-6f;
		if ( a is Vec3 va && b is Vec3 vb ) return va.x == vb.x && va.y == vb.y && va.z == vb.z;
		if ( a is List<string> la && b is List<string> lb ) return la.SequenceEqual( lb );
		return a.Equals( b );
	}

	public static void ModifyParameter( DocumentSession session, string featureId, string paramLabelOrName, object newValue )
	{
		var studio = RequireEffigyStudio( session );
		if ( string.IsNullOrWhiteSpace( featureId ) ) throw new ArgumentException( "Feature ID cannot be empty.", nameof( featureId ) );

		int featureIndex = studio.Features.FindIndex( f => f.Id == featureId );
		if ( featureIndex < 0 ) throw new KeyNotFoundException( $"Feature with ID '{featureId}' was not found in PartStudio." );

		var feature = studio.Features[featureIndex];
		var binding = ResolveBinding( feature, paramLabelOrName );

		object validatedNewValue = binding.Validator( newValue );
		object oldValue = binding.Getter();

		if ( ValuesEqual( oldValue, validatedNewValue ) ) return; // No-op

		var cmd = new DelegateEditCommand(
			$"Modify parameter '{binding.Name}' on {feature.TypeName} '{feature.Name ?? feature.Id}'",
			execute: () =>
			{
				binding.Setter( validatedNewValue );
				studio.MarkDirty( featureIndex );
				studio.Rebuild();
			},
			undo: () =>
			{
				binding.Setter( oldValue );
				studio.MarkDirty( featureIndex );
				studio.Rebuild();
			}
		);

		session.History.Execute( cmd );
	}

	public static void ModifyParameter<T>( DocumentSession session, string featureId, string paramLabelOrName, T newValue )
	{
		ArgumentNullException.ThrowIfNull( newValue );
		ModifyParameter( session, featureId, paramLabelOrName, (object)newValue );
	}

	public static void SetFeatureSuppressed( DocumentSession session, string featureId, bool suppressed )
	{
		ModifyParameter( session, featureId, "Suppressed", suppressed );
	}

	public static void SetFeatureVisible( DocumentSession session, string featureId, bool visible )
	{
		ModifyParameter( session, featureId, "Visible", visible );
	}

	public static void RenameFeature( DocumentSession session, string featureId, string newName )
	{
		ModifyParameter( session, featureId, "Name", newName );
	}

	// --- Drag / Coalescing ------------------------------------------------------------------

	public static ParameterDragSession<T> BeginParameterDrag<T>( DocumentSession session, string featureId, string paramLabelOrName )
	{
		var studio = RequireEffigyStudio( session );
		if ( string.IsNullOrWhiteSpace( featureId ) ) throw new ArgumentException( "Feature ID cannot be empty.", nameof( featureId ) );

		int featureIndex = studio.Features.FindIndex( f => f.Id == featureId );
		if ( featureIndex < 0 ) throw new KeyNotFoundException( $"Feature with ID '{featureId}' was not found in PartStudio." );

		var feature = studio.Features[featureIndex];
		var binding = ResolveBinding( feature, paramLabelOrName );

		T initialValue = (T)binding.Getter();

		var tx = session.History.BeginTransaction(
			$"Drag parameter '{binding.Name}' on {feature.TypeName} '{feature.Name ?? feature.Id}'",
			restoreBefore: () =>
			{
				binding.Setter( initialValue! );
				studio.PreviewQuality = false;
				studio.MarkDirty( featureIndex );
				studio.Rebuild();
			}
		);

		Action<T> setter = val =>
		{
			object validated = binding.Validator( val! );
			binding.Setter( validated );
		};

		Action markDirtyAndRebuild = () =>
		{
			studio.MarkDirty( featureIndex );
			studio.Rebuild();
		};

		return new ParameterDragSession<T>( tx, setter, initialValue, markDirtyAndRebuild, studio );
	}

	// --- Rebuild Operations -----------------------------------------------------------------

	public static RebuildReport Rebuild( DocumentSession session )
	{
		var studio = RequireEffigyStudio( session );
		var report = studio.Rebuild();
		if ( !report.HasErrors )
		{
			var snapshot = session.BuildSnapshot();
			session.TryPromoteSnapshot( snapshot );
		}
		return report;
	}

	public static async Task<RebuildResult> RebuildAsync(
		DocumentSession session,
		Func<Task>? onRebuilding = null,
		CancellationToken cancellationToken = default )
	{
		ArgumentNullException.ThrowIfNull( session );
		if ( session.SourceKind != DocumentSourceKind.Effigy || session.Studio == null )
		{
			throw new InvalidOperationException( "RebuildAsync requires an Effigy session with a retained PartStudio." );
		}

		long capturedRevision = session.Revision;
		string docId = session.DocumentId;
		string sourcePath = session.SourcePath;

		// Serialize PartStudio on caller thread to get a detached snapshot
		string serializedStudio = StudioDocument.Write( session.Studio );

		if ( cancellationToken.IsCancellationRequested )
		{
			return new RebuildResult( false, capturedRevision, ErrorMessage: "Rebuild cancelled before start." );
		}

		return await Task.Run( async () =>
		{
			if ( cancellationToken.IsCancellationRequested )
			{
				return new RebuildResult( false, capturedRevision, ErrorMessage: "Rebuild cancelled in worker." );
			}

			if ( session.Revision != capturedRevision )
			{
				return new RebuildResult( false, capturedRevision, IsStale: true, ErrorMessage: "Revision changed before rebuild." );
			}

			PartStudio detachedStudio;
			try
			{
				detachedStudio = StudioDocument.Read( serializedStudio );
			}
			catch ( Exception ex )
			{
				return new RebuildResult( false, capturedRevision, ErrorMessage: $"Failed to read detached studio: {ex.Message}" );
			}

			if ( onRebuilding != null )
			{
				await onRebuilding();
			}

			RebuildReport report;
			try
			{
				report = detachedStudio.Rebuild();
			}
			catch ( Exception ex )
			{
				return new RebuildResult( false, capturedRevision, ErrorMessage: $"Detached rebuild threw: {ex.Message}" );
			}

			if ( cancellationToken.IsCancellationRequested )
			{
				return new RebuildResult( false, capturedRevision, Report: report, ErrorMessage: "Rebuild cancelled after evaluation." );
			}

			// If the detached rebuild has errors, report failure cleanly.
			// Do not touch session or overwrite previous valid snapshot/geometry.
			if ( report.HasErrors )
			{
				return new RebuildResult(
					Success: false,
					Revision: capturedRevision,
					Report: report,
					Snapshot: null,
					IsStale: false,
					ErrorMessage: $"Rebuild produced {report.Errors.Count} error(s)."
				);
			}

			ViewportMesh mesh;
			try
			{
				mesh = ViewportMesh.FromStudio( detachedStudio, loadSeconds: 0, sourcePath );
			}
			catch ( Exception ex )
			{
				return new RebuildResult( false, capturedRevision, Report: report, ErrorMessage: $"Mesh extraction failed: {ex.Message}" );
			}

			var snapshot = new RenderSnapshot( docId, capturedRevision, mesh );

			// Check staleness before promoting
			if ( session.Revision != capturedRevision )
			{
				snapshot.Dispose();
				return new RebuildResult(
					Success: false,
					Revision: capturedRevision,
					Report: report,
					Snapshot: null,
					IsStale: true,
					ErrorMessage: "Rebuild completed but revision is stale."
				);
			}

			bool promoted = session.TryPromoteSnapshot( snapshot );
			if ( promoted )
			{
				session.Studio.Rebuild();
			}
			else
			{
				snapshot.Dispose();
				return new RebuildResult(
					Success: false,
					Revision: capturedRevision,
					Report: report,
					Snapshot: null,
					IsStale: true,
					ErrorMessage: "Stale revision rejected during snapshot promotion."
				);
			}

			return new RebuildResult(
				Success: true,
				Revision: capturedRevision,
				Report: report,
				Snapshot: snapshot,
				IsStale: false
			);
		}, cancellationToken );
	}
}
