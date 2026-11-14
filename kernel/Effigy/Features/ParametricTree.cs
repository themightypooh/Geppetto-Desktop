using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Tree-level operations on a PartStudio's parametric history: rename a variable everywhere it is
/// used, rename a feature, suppress or unsuppress a feature.
///
/// WHY A SEPARATE SERVICE, AND WHY IT ONLY USES PUBLIC API. The pieces a rename touches already
/// exist — <see cref="Feature.RenameVariableReferences"/> rewrites a feature's own parameter
/// expressions and <see cref="VariableResolver.RenameReferences"/> rewrites one expression — but
/// nothing called them, and neither reaches a sketch's constraint expressions or one variable's
/// expression referring to another. A rename that only rewrites half the references is worse than
/// no rename at all: the dimension silently freezes at its old value. So this service is the one
/// place that walks the whole tree, and it does it through public API only, so the tree's own
/// dirty-marking rules stay the ones that run.
///
/// ORDER OF OPERATIONS. Rebuild() evaluates variables before any feature runs, and a feature reads
/// its parameters at run time, so a rename only has to make every text agree before the next
/// rebuild. MarkAllDirty after the rename is what guarantees that rebuild happens.
/// </summary>
public static class ParametricTree
{
	/// <summary>
	/// Rename a variable everywhere it is referred to: its own table entry, every feature's
	/// parameter expressions, every sketch constraint expression, and every other variable's
	/// expression. Returns false with an error when the old name does not exist, the new name is
	/// illegal, or the new name is already taken. Renaming to the same name (case-insensitive —
	/// names are case-insensitive, see SetVariable) is a no-op success.
	/// <paramref name="referencesChanged"/> counts the expressions rewritten, not the variable
	/// entry itself.
	/// </summary>
	public static bool RenameVariable( PartStudio studio, string oldName, string newName, out string error, out int referencesChanged )
	{
		error = null;
		referencesChanged = 0;

		if ( studio is null ) { error = "No studio."; return false; }
		if ( string.IsNullOrWhiteSpace( oldName ) ) { error = "No variable name given."; return false; }
		if ( string.IsNullOrWhiteSpace( newName ) ) { error = "No new name given."; return false; }
		if ( !VariableResolver.IsLegalName( newName ) ) { error = $"\"{newName}\" is not a legal variable name."; return false; }
		if ( string.Equals( oldName, newName, StringComparison.OrdinalIgnoreCase ) ) return true;

		StudioVariable target = null;

		foreach ( var v in studio.Variables )
		{
			if ( v?.Name is null ) continue;

			if ( string.Equals( v.Name, newName, StringComparison.OrdinalIgnoreCase ) )
			{
				error = $"There is already a variable called \"{newName}\".";
				return false;
			}

			if ( string.Equals( v.Name, oldName, StringComparison.OrdinalIgnoreCase ) )
				target = v;
		}

		if ( target is null )
		{
			error = $"No variable called \"{oldName}\".";
			return false;
		}

		target.Name = newName;

		foreach ( var feature in studio.Features )
		{
			if ( feature is null ) continue;

			referencesChanged += feature.RenameVariableReferences( oldName, newName );

			if ( feature is SketchFeature sketchFeature )
			{
				foreach ( var c in sketchFeature.Sketch.Constraints )
				{
					if ( string.IsNullOrWhiteSpace( c.Expr ) ) continue;

					var rewritten = VariableResolver.RenameReferences( c.Expr, oldName, newName );

					if ( rewritten == c.Expr ) continue;

					c.Expr = rewritten;
					referencesChanged++;
				}
			}
		}

		foreach ( var v in studio.Variables )
		{
			if ( v is null || v == target || string.IsNullOrWhiteSpace( v.Expr ) ) continue;

			var rewritten = VariableResolver.RenameReferences( v.Expr, oldName, newName );

			if ( rewritten == v.Expr ) continue;

			v.Expr = rewritten;
			referencesChanged++;
		}

		studio.MarkAllDirty();
		return true;
	}

	/// <summary>
	/// Rename a feature. Names are unique in the tree (see DefaultName), so a rename onto an
	/// existing feature's name is refused rather than creating two features the user cannot tell
	/// apart. Renaming to the feature's own current name is a no-op success.
	/// </summary>
	public static bool RenameFeature( PartStudio studio, Feature feature, string name, out string error )
	{
		error = null;

		if ( studio is null ) { error = "No studio."; return false; }
		if ( feature is null ) { error = "No feature given."; return false; }
		if ( !studio.Features.Contains( feature ) ) { error = "That feature is not in this studio."; return false; }
		if ( string.IsNullOrWhiteSpace( name ) ) { error = "No name given."; return false; }
		if ( name == feature.Name ) return true;

		foreach ( var f in studio.Features )
		{
			if ( f is not null && f != feature && f.Name == name )
			{
				error = $"There is already a feature called \"{name}\".";
				return false;
			}
		}

		feature.Name = name;
		studio.MarkDirty( feature );
		return true;
	}

	/// <summary>
	/// Suppress or unsuppress a feature and dirty it, so the next rebuild either skips it or
	/// builds it again. Returns false when the studio or feature is null, or the feature is not
	/// in this studio.
	/// </summary>
	public static bool SetSuppressed( PartStudio studio, Feature feature, bool suppressed )
	{
		if ( studio is null || feature is null || !studio.Features.Contains( feature ) ) return false;
		if ( feature.Suppressed == suppressed ) return true;

		feature.Suppressed = suppressed;
		studio.MarkDirty( feature );
		return true;
	}
}
