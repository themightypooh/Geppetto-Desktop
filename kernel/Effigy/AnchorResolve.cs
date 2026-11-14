using System;
using System.Collections.Generic;
using System.Globalization;

namespace Effigy;

/// <summary>
/// S&amp;box-native Lane 3b: BuildScript anchor-script resolution helper.
///
/// This file parses the origin syntax Lane 3's spec assigns to BuildScript
/// and turns it into a child transform. It does NOT own the socket frames
/// themselves — those live in <see cref="RelativeAnchorSet"/> (Lane 3a,
/// <c>Effigy/RelativeAnchors.cs</c>, read but not edited here) — and it
/// does NOT wire anything into BuildScript. Both of those are separate
/// steps, owed, not applied here.
///
/// Syntax parsed (all of these are the same anchor):
///     origin=parent.socket_name + [dx, dy, dz]
///     parent.socket_name + [dx, dy, dz]
///     parent.socket_name+[dx,dy,dz]
///     parent.socket_name            (no offset: the socket origin itself)
///
/// Rules, chosen to fail loudly rather than parent to the wrong side:
/// <list type="bullet">
/// <item>Parent and socket are identifiers: a letter or underscore first,
/// then letters, digits, underscores, or hyphens. Exactly one dot separates
/// them; a second dot, a missing side, or trailing text is an error.</item>
/// <item>The offset, when present, must follow a <c>+</c> and be exactly
/// three finite numbers in brackets, comma-separated, invariant culture.
/// <c>NaN</c> and infinities are rejected, not silently carried.</item>
/// <item>Socket lookup is case-sensitive, because
/// <see cref="RelativeAnchorSet"/> matches names with
/// <see cref="StringComparer.Ordinal"/>: <c>shoulder_L</c> is not
/// <c>shoulder_l</c>. Nothing here lowercases or guesses a name.</item>
/// <item>The offset is a translation in the socket's local space, so the
/// child transform is always the spec's composition:
/// <c>T_child = T_parent_socket * T_offset</c>. An offset carries no
/// rotation or scale of its own; a scaled or posed parent goes through
/// <see cref="ResolveWithParent"/> instead, which is
/// <c>parent * T_socket * T_offset</c> and therefore scales the attachment
/// point with the parent.</item>
/// </list>
///
/// Limits, stated plainly: this helper knows nothing about which bodies
/// exist in a document. A caller that resolves by parent name must hand it
/// a parent-name-to-set map (the dictionary overloads); BuildScript wiring
/// is what will build that map from the document, and that wiring is owed.
/// </summary>
public readonly struct AnchorOrigin : IEquatable<AnchorOrigin>
{
	public readonly string Parent;
	public readonly string Socket;
	public readonly Vec3 Offset;
	public readonly bool HasOffset;

	public AnchorOrigin( string parent, string socket, Vec3 offset, bool hasOffset )
	{
		if ( string.IsNullOrWhiteSpace( parent ) )
			throw new ArgumentException( "Parent name must not be empty.", nameof( parent ) );
		if ( string.IsNullOrWhiteSpace( socket ) )
			throw new ArgumentException( "Socket name must not be empty.", nameof( socket ) );
		Parent = parent;
		Socket = socket;
		Offset = offset;
		HasOffset = hasOffset;
	}

	public AnchorOrigin( string parent, string socket )
		: this( parent, socket, Vec3.Zero, false )
	{
	}

	public AnchorOrigin( string parent, string socket, Vec3 offset )
		: this( parent, socket, offset, true )
	{
	}

	public bool Equals( AnchorOrigin o ) =>
		Parent == o.Parent && Socket == o.Socket &&
		Offset.Equals( o.Offset ) && HasOffset == o.HasOffset;

	public override bool Equals( object obj ) => obj is AnchorOrigin o && Equals( o );

	public override int GetHashCode() => HashCode.Combine( Parent, Socket, Offset, HasOffset );

	/// <summary>Canonical spelling; <see cref="AnchorResolve.Parse"/> reads it back.</summary>
	public override string ToString() =>
		HasOffset
			? $"{Parent}.{Socket} + [{Offset.x.ToString( "R", CultureInfo.InvariantCulture )}, {Offset.y.ToString( "R", CultureInfo.InvariantCulture )}, {Offset.z.ToString( "R", CultureInfo.InvariantCulture )}]"
			: $"{Parent}.{Socket}";
}

/// <summary>
/// Parses <c>origin=parent.socket + [dx, dy, dz]</c> strings and resolves
/// them through a <see cref="RelativeAnchorSet"/> (or a socket lookup
/// delegate, or a parent-name-to-set map) into a child <see cref="Xform"/>.
///
/// Every method comes in a throwing form and a <c>Try*</c> form. The
/// throwing form names the problem (bad syntax, unknown parent, unknown
/// socket); the <c>Try*</c> form returns false and never throws for a bad
/// string or an unknown name, so a script runner can report the failure in
/// its own words. A null set, map, or lookup is an argument error in the
/// throwing form and a plain false in the <c>Try*</c> form.
/// </summary>
public static class AnchorResolve
{
	// ---- Parsing ----

	/// <summary>Parses an origin string, throwing <see cref="FormatException"/> on bad syntax.</summary>
	public static AnchorOrigin Parse( string text )
	{
		if ( !TryParse( text, out var origin, out var error ) )
			throw new FormatException( error );
		return origin;
	}

	/// <summary>Parses an origin string; false on bad syntax, with no exception.</summary>
	public static bool TryParse( string text, out AnchorOrigin origin )
		=> TryParse( text, out origin, out _ );

	/// <summary>Parses an origin string; false on bad syntax, with the reason in <c>error</c>.</summary>
	public static bool TryParse( string text, out AnchorOrigin origin, out string error )
	{
		origin = default;
		error = null;

		if ( string.IsNullOrWhiteSpace( text ) )
		{
			error = "Anchor origin string is empty.";
			return false;
		}

		var s = text.Trim();

		// The "origin=" key is BuildScript's, not part of the anchor itself.
		if ( s.StartsWith( "origin=", StringComparison.OrdinalIgnoreCase ) )
			s = s.Substring( "origin=".Length ).Trim();

		if ( s.Length == 0 )
		{
			error = "Anchor origin has no parent.socket after 'origin='.";
			return false;
		}

		var i = 0;

		bool SkipSpaces()
		{
			var moved = false;
			while ( i < s.Length && char.IsWhiteSpace( s[i] ) )
			{
				i++;
				moved = true;
			}
			return moved;
		}

		bool ParseIdent( out string ident )
		{
			ident = null;
			var start = i;
			if ( i >= s.Length || !( char.IsLetter( s[i] ) || s[i] == '_' ) )
				return false;
			i++;
			while ( i < s.Length && ( char.IsLetterOrDigit( s[i] ) || s[i] == '_' || s[i] == '-' ) )
				i++;
			ident = s.Substring( start, i - start );
			return true;
		}

		bool ParseNumber( out float value )
		{
			value = 0f;
			var start = i;
			while ( i < s.Length && ( char.IsDigit( s[i] ) || s[i] == '.' || s[i] == '-' || s[i] == '+' || s[i] == 'e' || s[i] == 'E' ) )
				i++;
			if ( i == start )
				return false;
			var token = s.Substring( start, i - start );
			if ( !float.TryParse( token, NumberStyles.Float, CultureInfo.InvariantCulture, out value ) )
				return false;
			// TryParse accepts "NaN"/"Infinity" spellings via the token scan
			// above only if they were letters, which they are not — but a
			// finite check costs nothing and guards every future edit.
			return float.IsFinite( value );
		}

		if ( !ParseIdent( out var parent ) )
		{
			error = $"Anchor origin '{text}' must start with a parent name (letter or underscore first).";
			return false;
		}
		if ( i >= s.Length || s[i] != '.' )
		{
			error = $"Anchor origin '{text}' must be parent.socket — no '.' after the parent name.";
			return false;
		}
		i++; // '.'
		if ( !ParseIdent( out var socket ) )
		{
			error = $"Anchor origin '{text}' must name a socket after the '.'.";
			return false;
		}

		SkipSpaces();
		if ( i >= s.Length )
		{
			origin = new AnchorOrigin( parent, socket );
			return true;
		}

		if ( s[i] != '+' )
		{
			error = $"Anchor origin '{text}' has unexpected text after the socket name — an offset must start with '+'.";
			return false;
		}
		i++; // '+'
		SkipSpaces();
		if ( i >= s.Length || s[i] != '[' )
		{
			error = $"Anchor origin '{text}' must give its offset in brackets: + [dx, dy, dz].";
			return false;
		}
		i++; // '['

		var parts = new float[3];
		for ( var k = 0; k < 3; k++ )
		{
			SkipSpaces();
			if ( !ParseNumber( out parts[k] ) )
			{
				error = $"Anchor origin '{text}' has a bad offset component #{k + 1} — expected a finite number.";
				return false;
			}
			SkipSpaces();
			if ( k < 2 )
			{
				if ( i >= s.Length || s[i] != ',' )
				{
					error = $"Anchor origin '{text}' offset needs exactly three comma-separated numbers.";
					return false;
				}
				i++; // ','
			}
		}
		if ( i >= s.Length || s[i] != ']' )
		{
			error = $"Anchor origin '{text}' offset is missing its closing ']'.";
			return false;
		}
		i++; // ']'
		SkipSpaces();
		if ( i != s.Length )
		{
			error = $"Anchor origin '{text}' has trailing text after the offset.";
			return false;
		}

		origin = new AnchorOrigin( parent, socket, new Vec3( parts[0], parts[1], parts[2] ) );
		return true;
	}

	// ---- Resolution through one parent's socket set ----

	/// <summary>Child transform for a parsed origin against its parent's set: T_socket * T_offset.</summary>
	public static Xform Resolve( RelativeAnchorSet set, AnchorOrigin origin )
	{
		if ( set is null )
			throw new ArgumentNullException( nameof( set ) );
		return set.Resolve( origin.Socket, origin.Offset );
	}

	/// <summary>As <see cref="Resolve(RelativeAnchorSet, AnchorOrigin)"/>, false on an unknown socket.</summary>
	public static bool TryResolve( RelativeAnchorSet set, AnchorOrigin origin, out Xform child )
	{
		child = default;
		if ( set is null )
			return false;
		return set.TryResolve( origin.Socket, origin.Offset, out child );
	}

	/// <summary>Parses <c>text</c> and resolves it against one parent's set.</summary>
	public static Xform Resolve( string text, RelativeAnchorSet set )
		=> Resolve( set, Parse( text ) );

	/// <summary>Parses <c>text</c> and tries to resolve it against one parent's set.</summary>
	public static bool TryResolve( string text, RelativeAnchorSet set, out Xform child )
	{
		child = default;
		if ( !TryParse( text, out var origin ) )
			return false;
		return TryResolve( set, origin, out child );
	}

	// ---- Resolution through a parent-name-to-set map ----

	/// <summary>
	/// Child transform looked up by parent name first, then socket name.
	/// Throws <see cref="InvalidOperationException"/> for an unknown parent
	/// (and, via the set, for an unknown socket).
	/// </summary>
	public static Xform Resolve( IReadOnlyDictionary<string, RelativeAnchorSet> parents, AnchorOrigin origin )
	{
		if ( parents is null )
			throw new ArgumentNullException( nameof( parents ) );
		if ( !parents.TryGetValue( origin.Parent, out var set ) || set is null )
			throw new InvalidOperationException( $"No parent body named '{origin.Parent}' is registered." );
		return set.Resolve( origin.Socket, origin.Offset );
	}

	/// <summary>As the map overload of <see cref="Resolve(IReadOnlyDictionary{string, RelativeAnchorSet}, AnchorOrigin)"/>, false on an unknown parent or socket.</summary>
	public static bool TryResolve( IReadOnlyDictionary<string, RelativeAnchorSet> parents, AnchorOrigin origin, out Xform child )
	{
		child = default;
		if ( parents is null )
			return false;
		if ( !parents.TryGetValue( origin.Parent, out var set ) || set is null )
			return false;
		return set.TryResolve( origin.Socket, origin.Offset, out child );
	}

	/// <summary>Parses <c>text</c> and resolves it through a parent-name-to-set map.</summary>
	public static Xform Resolve( string text, IReadOnlyDictionary<string, RelativeAnchorSet> parents )
		=> Resolve( parents, Parse( text ) );

	// ---- Resolution with an explicit parent transform (scaled / posed parent) ----

	/// <summary>
	/// Child transform for a scaled or posed parent: parent * T_socket *
	/// T_offset, via <see cref="RelativeAnchorSet.ComposeWithParent"/>. Scaling
	/// the parent therefore scales the attachment point by the same factor.
	/// </summary>
	public static Xform ResolveWithParent( Xform parent, RelativeAnchorSet set, AnchorOrigin origin )
	{
		if ( set is null )
			throw new ArgumentNullException( nameof( set ) );
		return set.ComposeWithParent( parent, origin.Socket, origin.Offset );
	}

	/// <summary>As <see cref="ResolveWithParent"/>, false on an unknown socket.</summary>
	public static bool TryResolveWithParent( Xform parent, RelativeAnchorSet set, AnchorOrigin origin, out Xform child )
	{
		child = default;
		if ( set is null )
			return false;
		// ComposeWithParent throws on an unknown socket and has no Try form,
		// so resolve locally first, then multiply: parent * (T_socket *
		// T_offset) is the same composition ComposeWithParent computes.
		if ( !set.TryResolve( origin.Socket, origin.Offset, out var local ) )
			return false;
		child = parent * local;
		return true;
	}

	/// <summary>Parses <c>text</c> and resolves it with an explicit parent transform.</summary>
	public static Xform ResolveWithParent( string text, Xform parent, RelativeAnchorSet set )
		=> ResolveWithParent( parent, set, Parse( text ) );

	// ---- Resolution through a bare socket lookup delegate ----

	/// <summary>
	/// Child transform from any socket source: the delegate returns the frame
	/// for a socket name, or null when it has none. This is the seam for
	/// callers whose sockets do not live in a <see cref="RelativeAnchorSet"/>.
	/// </summary>
	public static Xform Resolve( Func<string, SocketFrame?> lookup, AnchorOrigin origin )
	{
		if ( lookup is null )
			throw new ArgumentNullException( nameof( lookup ) );
		var frame = lookup( origin.Socket );
		if ( frame is null )
			throw new InvalidOperationException( $"No socket named '{origin.Socket}' is registered." );
		return frame.Value.Compose( origin.Offset );
	}

	/// <summary>As the delegate overload of Resolve, false when the lookup has no such socket.</summary>
	public static bool TryResolve( Func<string, SocketFrame?> lookup, AnchorOrigin origin, out Xform child )
	{
		child = default;
		if ( lookup is null )
			return false;
		var frame = lookup( origin.Socket );
		if ( frame is null )
			return false;
		child = frame.Value.Compose( origin.Offset );
		return true;
	}
}
