using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>What one configuration override sets.</summary>
public enum ConfigurationOverrideKind
{
	/// <summary>A feature parameter, by label, to a literal value.</summary>
	Param,

	/// <summary>A feature's <see cref="Feature.Suppressed"/> flag.</summary>
	Suppressed,

	/// <summary>A feature's material-slot parameter ("Material slot"), to a slot index.</summary>
	Material,
}

/// <summary>
/// One value a configuration names: which feature, what about it, and to what.
///
/// A configuration is a PARTIAL table: it names values for the parameters it cares about and
/// says nothing about the rest. Applying a configuration first restores every parameter an
/// earlier configuration touched to the value it had before any configuration was applied
/// (the session-local base), then sets exactly the values this configuration names — so
/// switching small → large → small restores small exactly, and a parameter no configuration
/// names keeps its base value under every configuration.
/// </summary>
public sealed class ConfigurationOverride
{
	public string FeatureId = "";
	public ConfigurationOverrideKind Kind;
	/// <summary>Param label, for <see cref="ConfigurationOverrideKind.Param"/>. Ignored otherwise.</summary>
	public string Label = "";
	/// <summary>Param: the literal text for the parameter's type. Suppressed: "0"/"1".
	/// Material: the slot index.</summary>
	public string Value = "";

	public ConfigurationOverride Clone() => new()
	{
		FeatureId = FeatureId, Kind = Kind, Label = Label, Value = Value,
	};
}

/// <summary>One named design variant: the values its overrides name.</summary>
public sealed class DesignConfiguration
{
	public string Name = "";
	public List<ConfigurationOverride> Overrides = new();

	public DesignConfiguration Clone() => new()
	{
		Name = Name,
		Overrides = Overrides.Select( o => o.Clone() ).ToList(),
	};
}

/// <summary>
/// CAD design variants (what Fusion 360 and SolidWorks call configurations): a named table of
/// values for feature parameters, suppression states and material choices, with exactly one
/// configuration active. Switching configurations rebuilds the model from the same feature
/// tree with that configuration's values; the tree itself never changes.
///
/// A configuration does NOT apply itself on rebuild. The switch is explicit —
/// <see cref="SetActive"/> / <see cref="ClearActive"/> — because the table is state the
/// feature owns, and a rebuild must not silently rewrite parameters somebody just edited.
/// <see cref="Active"/> records which configuration was last applied, so a saved document
/// reopens with its values already in the parameters.
///
/// The base a switch restores to is SESSION-LOCAL: the values the parameters held before the
/// first configuration was applied in this session. A document saved with a configuration
/// active reopens with that configuration's values as the base — switching to another
/// configuration then applies its named values over the saved state, and switching back
/// re-applies the first configuration's named values exactly. What a configuration does not
/// name, it does not touch.
///
/// Persistence: the table lives in <see cref="ConfigLines"/>, one space-free token per
/// configuration, so the existing <c>texts</c> round-trip in <see cref="StudioDocument"/>
/// carries it with no serializer change. A document holding configurations stamps format 4
/// (see <see cref="StudioDocument.Version"/>); older builds refuse it rather than opening
/// a model whose variants they would silently drop.
/// </summary>
public sealed class ConfigurationFeature : Feature
{
	public override string TypeName => "Configuration";

	/// <summary>The configuration last applied, or empty when none is. Persisted like any
	/// other parameter; the table, not this, is the source of truth for the values.</summary>
	public StringParam Active = new( "Active configuration", "" );

	/// <summary>
	/// The configuration table, one token per configuration:
	/// <c>name|featureId:param:Label=Value;featureId:suppressed:0|1;featureId:material:slot</c>.
	/// Names, labels and values are percent-encoded (see <see cref="Encode"/>) so a token never
	/// contains a space, <c>|</c>, <c>;</c>, <c>:</c> or <c>=</c> of its own — the <c>texts</c>
	/// line the document writer emits splits tokens on spaces, and a label like "Hole diameter"
	/// must survive that. A configuration with no overrides is just its name.
	/// </summary>
	public List<string> ConfigLines = new();

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Active };

	/// <summary>Session-local base values: what the parameters held before the first
	/// configuration was applied in this session. Private, so the document writer (public
	/// fields only) never persists it — see the class note on reopening.</summary>
	readonly Dictionary<string, SnapshotEntry> _snapshot = new();

	sealed class SnapshotEntry
	{
		public string ParamValue = "";
		public string ParamExpr;
		public bool Suppressed;
	}

	/// <summary>Whether the table holds at least one configuration.</summary>
	public bool HasConfigurations => ConfigLines.Count > 0;

	/// <summary>The configurations, parsed from <see cref="ConfigLines"/> in stored order.
	/// A malformed token fails closed with the token in the message rather than dropping a
	/// configuration silently.</summary>
	public IReadOnlyList<DesignConfiguration> Configurations => Parse( ConfigLines );

	public DesignConfiguration FindConfiguration( string name ) =>
		Configurations.FirstOrDefault( c => string.Equals( c.Name, name, StringComparison.Ordinal ) );

	protected override void Execute( FeatureContext ctx )
	{
		// A configuration produces no geometry: it is a table other code applies, and the
		// rebuild loop evaluating it must be a no-op, not a second application of the values.
		Note = Configurations.Count == 0
			? "No configurations yet."
			: $"Configurations: {Configurations.Count}, active '{( string.IsNullOrEmpty( Active.Value ) ? "(none)" : Active.Value )}'.";
	}

	// --- table editing ----------------------------------------------------------

	public void AddConfiguration( DesignConfiguration config )
	{
		ArgumentNullException.ThrowIfNull( config );
		if ( string.IsNullOrWhiteSpace( config.Name ) )
			throw new ArgumentException( "A configuration needs a name.", nameof( config ) );
		if ( FindConfiguration( config.Name ) is not null )
			throw new ArgumentException( $"A configuration named '{config.Name}' already exists.", nameof( config ) );
		foreach ( var o in config.Overrides )
			ValidateOverrideShape( o );
		var seen = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var o in config.Overrides )
			if ( !seen.Add( $"{o.FeatureId}|{(int)o.Kind}|{o.Label}" ) )
				throw new ArgumentException(
					$"Configuration '{config.Name}' names {o.FeatureId} / '{o.Label}' twice.", nameof( config ) );
		ConfigLines.Add( EncodeConfig( config ) );
	}

	public bool RemoveConfiguration( string name )
	{
		var index = ConfigLines.FindIndex( line =>
			string.Equals( Decode( line.Split( '|' )[0] ), name, StringComparison.Ordinal ) );
		if ( index < 0 )
			return false;
		ConfigLines.RemoveAt( index );
		if ( string.Equals( Active.Value, name, StringComparison.Ordinal ) )
			Active.Value = "";
		return true;
	}

	public void RenameConfiguration( string oldName, string newName )
	{
		if ( string.IsNullOrWhiteSpace( newName ) )
			throw new ArgumentException( "A configuration needs a name.", nameof( newName ) );
		if ( FindConfiguration( newName ) is not null )
			throw new ArgumentException( $"A configuration named '{newName}' already exists.", nameof( newName ) );
		var config = FindConfiguration( oldName )
			?? throw new ArgumentException( $"No configuration named '{oldName}'.", nameof( oldName ) );
		config.Name = newName;
		ConfigLines[ConfigLines.FindIndex( line =>
			string.Equals( Decode( line.Split( '|' )[0] ), oldName, StringComparison.Ordinal ) )] = EncodeConfig( config );
		if ( string.Equals( Active.Value, oldName, StringComparison.Ordinal ) )
			Active.Value = newName;
	}

	// --- applying ---------------------------------------------------------------

	/// <summary>
	/// Everything the named configuration needs to be true, or the errors that stop it.
	/// A configuration referencing a deleted feature, a missing parameter or an unparsable
	/// value is refused honestly: <see cref="SetActive"/> applies nothing in that case.
	/// </summary>
	public IReadOnlyList<string> Validate( PartStudio studio )
	{
		ArgumentNullException.ThrowIfNull( studio );
		var errors = new List<string>();
		foreach ( var config in Configurations )
		{
			_ = TryResolve( studio, config, out _, out var configErrors );
			errors.AddRange( configErrors );
		}
		return errors;
	}

	/// <summary>
	/// Make <paramref name="name"/> the active configuration: restore the session base,
	/// apply this configuration's values, mark the whole tree dirty and rebuild from the
	/// same feature tree. Returns the rebuild report. Throws (and applies nothing) when the
	/// configuration references a deleted feature, a missing parameter or an unparsable value.
	/// </summary>
	public RebuildReport SetActive( PartStudio studio, string name )
	{
		ArgumentNullException.ThrowIfNull( studio );
		var config = FindConfiguration( name )
			?? throw new ArgumentException( $"No configuration named '{name}'.", nameof( name ) );
		if ( !TryResolve( studio, config, out var actions, out var errors ) )
			throw new InvalidOperationException(
				$"Configuration '{name}' cannot apply: {string.Join( "; ", errors )}" );
		RestoreSnapshot( studio );
		foreach ( var action in actions )
		{
			Snapshot( action );
			action.Apply();
		}
		Active.Value = name;
		studio.MarkAllDirty();
		return studio.Rebuild();
	}

	/// <summary>
	/// Drop back to the session base: restore every value a configuration touched, clear
	/// the active name and rebuild. When nothing was ever applied in this session there is
	/// nothing to restore — the saved values stand, and only the name is cleared.
	/// </summary>
	public RebuildReport ClearActive( PartStudio studio )
	{
		ArgumentNullException.ThrowIfNull( studio );
		RestoreSnapshot( studio );
		Active.Value = "";
		studio.MarkAllDirty();
		return studio.Rebuild();
	}

	// --- resolution -------------------------------------------------------------

	sealed class ResolvedOverride
	{
		public required Feature Feature;
		public required ConfigurationOverride Override;
		public IParam Param;
		public float FloatValue;
		public int IntValue;
		public bool BoolValue;
		public string StringValue = "";
		public Vec3 VecValue;

		/// <summary>The snapshot key: feature, kind and the parameter label. For a
		/// material override the override carries no label of its own (the material
		/// parameter is found by label at resolve time), so the RESOLVED parameter's
		/// label is the key — otherwise the restore cannot find the parameter and
		/// the material slot silently keeps the configured value after a clear.</summary>
		public string Key => Override.Kind == ConfigurationOverrideKind.Suppressed
			? $"{Override.FeatureId}|{(int)Override.Kind}|"
			: $"{Override.FeatureId}|{(int)Override.Kind}|{Param?.Label ?? Override.Label}";

		public void Apply()
		{
			switch ( Override.Kind )
			{
				case ConfigurationOverrideKind.Suppressed:
					Feature.Suppressed = BoolValue;
					return;
				case ConfigurationOverrideKind.Material:
				case ConfigurationOverrideKind.Param:
					break;
				default:
					throw new InvalidOperationException( $"Unknown override kind {Override.Kind}." );
			}
			switch ( Param )
			{
				case FloatParam p:
					// An expression would re-evaluate on the next rebuild and undo the
					// configuration, so a configured dimension becomes a literal; the
					// snapshot holds the expression for the way back.
					p.Expr = null;
					p.Value = FloatValue;
					break;
				case IntParam p:
					p.Value = IntValue;
					break;
				case BoolParam p:
					p.Value = BoolValue;
					break;
				case ChoiceParam p:
					p.Index = IntValue;
					break;
				case StringParam p:
					p.Value = StringValue;
					break;
				case Vec3Param p:
					p.Value = VecValue;
					break;
				default:
					throw new InvalidOperationException(
						$"Parameter '{Param.Label}' on feature '{Feature.Id}' is a {Param.GetType().Name}, which a configuration cannot set." );
			}
		}
	}

	bool TryResolve( PartStudio studio, DesignConfiguration config,
		out List<ResolvedOverride> actions, out List<string> errors )
	{
		actions = new List<ResolvedOverride>();
		errors = new List<string>();
		foreach ( var o in config.Overrides )
		{
			var feature = studio.Features.FirstOrDefault( f => f.Id == o.FeatureId );
			if ( feature is null )
			{
				errors.Add( $"Configuration '{config.Name}' names feature '{o.FeatureId}', which is not in this document." );
				continue;
			}
			if ( ReferenceEquals( feature, this ) )
			{
				errors.Add( $"Configuration '{config.Name}' names its own table feature, which a configuration cannot set." );
				continue;
			}
			var resolved = new ResolvedOverride { Feature = feature, Override = o };
			if ( o.Kind == ConfigurationOverrideKind.Suppressed )
			{
				if ( !TryBool( o.Value, out resolved.BoolValue ) )
				{
					errors.Add( $"Configuration '{config.Name}': suppressed value '{o.Value}' for feature '{o.FeatureId}' is not 0/1." );
					continue;
				}
			}
			else if ( o.Kind == ConfigurationOverrideKind.Material )
			{
				var material = FindMaterialParam( feature );
				if ( material is null )
				{
					errors.Add( $"Configuration '{config.Name}': feature '{o.FeatureId}' has no material-slot parameter." );
					continue;
				}
				resolved.Param = material;
				if ( !int.TryParse( o.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out resolved.IntValue )
					|| resolved.IntValue < 0 || resolved.IntValue > 63 )
				{
					errors.Add( $"Configuration '{config.Name}': material slot '{o.Value}' for feature '{o.FeatureId}' is not a slot 0..63." );
					continue;
				}
			}
			else
			{
				var param = feature.Parameters.FirstOrDefault( p => string.Equals( p.Label, o.Label, StringComparison.Ordinal ) );
				if ( param is null )
				{
					errors.Add( $"Configuration '{config.Name}': feature '{o.FeatureId}' has no parameter '{o.Label}'." );
					continue;
				}
				resolved.Param = param;
				if ( !TryParseParamValue( param, o.Value, resolved, out var problem ) )
				{
					errors.Add( $"Configuration '{config.Name}': {problem}" );
					continue;
				}
			}
			actions.Add( resolved );
		}
		return errors.Count == 0;
	}

	static bool TryParseParamValue( IParam param, string value, ResolvedOverride resolved, out string problem )
	{
		problem = "";
		switch ( param )
		{
			case FloatParam:
				if ( float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out resolved.FloatValue ) )
					return true;
				problem = $"value '{value}' for parameter '{param.Label}' is not a number.";
				return false;
			case IntParam:
				if ( int.TryParse( value, NumberStyles.Integer, CultureInfo.InvariantCulture, out resolved.IntValue ) )
					return true;
				problem = $"value '{value}' for parameter '{param.Label}' is not a whole number.";
				return false;
			case BoolParam:
				if ( TryBool( value, out resolved.BoolValue ) )
					return true;
				problem = $"value '{value}' for parameter '{param.Label}' is not 0/1.";
				return false;
			case ChoiceParam choice:
				if ( int.TryParse( value, NumberStyles.Integer, CultureInfo.InvariantCulture, out resolved.IntValue )
					&& resolved.IntValue >= 0 && resolved.IntValue < choice.Options.Length )
					return true;
				var index = Array.FindIndex( choice.Options, o => string.Equals( o, value, StringComparison.OrdinalIgnoreCase ) );
				if ( index >= 0 )
				{
					resolved.IntValue = index;
					return true;
				}
				problem = $"value '{value}' for parameter '{param.Label}' is neither an option index nor an option name.";
				return false;
			case StringParam:
				resolved.StringValue = value;
				return true;
			case Vec3Param:
				var parts = value.Split( new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries );
				if ( parts.Length == 3
					&& float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x )
					&& float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y )
					&& float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z ) )
				{
					resolved.VecValue = new Vec3( x, y, z );
					return true;
				}
				problem = $"value '{value}' for parameter '{param.Label}' is not three numbers.";
				return false;
			default:
				problem = $"parameter '{param.Label}' is a {param.GetType().Name}, which a configuration cannot set.";
				return false;
		}
	}

	static IParam FindMaterialParam( Feature feature )
	{
		foreach ( var param in feature.Parameters )
		{
			if ( param.Label.Contains( "material", StringComparison.OrdinalIgnoreCase )
				&& param is IntParam or ChoiceParam )
				return param;
		}
		return null;
	}

	static bool TryBool( string value, out bool result )
	{
		switch ( value.Trim().ToLowerInvariant() )
		{
			case "1": case "true": result = true; return true;
			case "0": case "false": result = false; return true;
			default: result = false; return false;
		}
	}

	// --- snapshot ---------------------------------------------------------------

	void Snapshot( ResolvedOverride action )
	{
		if ( _snapshot.ContainsKey( action.Key ) )
			return;
		if ( action.Override.Kind == ConfigurationOverrideKind.Suppressed )
		{
			_snapshot[action.Key] = new SnapshotEntry { Suppressed = action.Feature.Suppressed };
			return;
		}
		_snapshot[action.Key] = new SnapshotEntry
		{
			ParamValue = ParamValueText( action.Param ),
			ParamExpr = ( action.Param as FloatParam )?.Expr,
		};
	}

	void RestoreSnapshot( PartStudio studio )
	{
		foreach ( var (key, entry) in _snapshot )
		{
			var parts = key.Split( '|' );
			var feature = studio.Features.FirstOrDefault( f => f.Id == parts[0] );
			if ( feature is null )
				continue;
			if ( (ConfigurationOverrideKind)int.Parse( parts[1], CultureInfo.InvariantCulture )
				== ConfigurationOverrideKind.Suppressed )
			{
				feature.Suppressed = entry.Suppressed;
				continue;
			}
			var param = feature.Parameters.FirstOrDefault( p => string.Equals( p.Label, parts[2], StringComparison.Ordinal ) );
			if ( param is null )
				continue;
			switch ( param )
			{
				case FloatParam p:
					p.Expr = entry.ParamExpr;
					if ( float.TryParse( entry.ParamValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var f ) )
						p.Value = f;
					break;
				case IntParam p:
					if ( int.TryParse( entry.ParamValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i ) )
						p.Value = i;
					break;
				case BoolParam p:
					p.Value = entry.ParamValue == "1";
					break;
				case ChoiceParam p:
					if ( int.TryParse( entry.ParamValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c ) )
						p.Index = c;
					break;
				case StringParam p:
					p.Value = entry.ParamValue;
					break;
				case Vec3Param p:
					var bits = entry.ParamValue.Split( ' ' );
					if ( bits.Length == 3
						&& float.TryParse( bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x )
						&& float.TryParse( bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y )
						&& float.TryParse( bits[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z ) )
						p.Value = new Vec3( x, y, z );
					break;
			}
		}
		_snapshot.Clear();
	}

	static string ParamValueText( IParam param ) => param switch
	{
		FloatParam p => p.Value.ToString( "R", CultureInfo.InvariantCulture ),
		IntParam p => p.Value.ToString( CultureInfo.InvariantCulture ),
		BoolParam p => p.Value ? "1" : "0",
		ChoiceParam p => p.Index.ToString( CultureInfo.InvariantCulture ),
		StringParam p => p.Value,
		Vec3Param p => $"{p.Value.x.ToString( "R", CultureInfo.InvariantCulture )} {p.Value.y.ToString( "R", CultureInfo.InvariantCulture )} {p.Value.z.ToString( "R", CultureInfo.InvariantCulture )}",
		_ => "",
	};

	// --- encoding -----------------------------------------------------------------

	static void ValidateOverrideShape( ConfigurationOverride o )
	{
		ArgumentNullException.ThrowIfNull( o );
		if ( string.IsNullOrWhiteSpace( o.FeatureId ) )
			throw new ArgumentException( "A configuration override needs a feature id." );
		if ( o.Kind == ConfigurationOverrideKind.Param && string.IsNullOrWhiteSpace( o.Label ) )
			throw new ArgumentException( "A parameter override needs a parameter label." );
		if ( string.IsNullOrEmpty( o.Value ) )
			throw new ArgumentException( "A configuration override needs a value." );
		if ( o.Kind == ConfigurationOverrideKind.Suppressed && !TryBool( o.Value, out _ ) )
			throw new ArgumentException( $"Suppressed value '{o.Value}' is not 0/1." );
		if ( o.Kind == ConfigurationOverrideKind.Material
			&& ( !int.TryParse( o.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot ) || slot < 0 || slot > 63 ) )
			throw new ArgumentException( $"Material slot '{o.Value}' is not a slot 0..63." );
	}

	static string EncodeConfig( DesignConfiguration config )
	{
		var entries = config.Overrides.Select( o => o.Kind switch
		{
			ConfigurationOverrideKind.Param => $"{o.FeatureId}:param:{Encode( o.Label )}={Encode( o.Value )}",
			ConfigurationOverrideKind.Suppressed => $"{o.FeatureId}:suppressed:{Encode( o.Value )}",
			ConfigurationOverrideKind.Material => $"{o.FeatureId}:material:{Encode( o.Value )}",
			_ => throw new InvalidOperationException( $"Unknown override kind {o.Kind}." ),
		} );
		return Encode( config.Name ) + "|" + string.Join( ";", entries );
	}

	static IReadOnlyList<DesignConfiguration> Parse( IReadOnlyList<string> lines )
	{
		var configs = new List<DesignConfiguration>();
		foreach ( var line in lines )
		{
			if ( string.IsNullOrEmpty( line ) )
				continue;
			var bar = line.IndexOf( '|' );
			var name = Decode( bar < 0 ? line : line[..bar] );
			var config = new DesignConfiguration { Name = name };
			if ( bar >= 0 && bar + 1 < line.Length )
			{
				foreach ( var entry in line[( bar + 1 )..].Split( ';', StringSplitOptions.RemoveEmptyEntries ) )
					config.Overrides.Add( ParseOverride( entry, line ) );
			}
			configs.Add( config );
		}
		return configs;
	}

	static ConfigurationOverride ParseOverride( string entry, string line )
	{
		var parts = entry.Split( ':' );
		if ( parts.Length < 3 )
			throw new FormatException( $"Malformed configuration entry '{entry}' in '{line}'." );
		var kind = parts[1] switch
		{
			"param" => ConfigurationOverrideKind.Param,
			"suppressed" => ConfigurationOverrideKind.Suppressed,
			"material" => ConfigurationOverrideKind.Material,
			_ => throw new FormatException( $"Unknown configuration override kind '{parts[1]}' in '{line}'." ),
		};
		if ( kind == ConfigurationOverrideKind.Param )
		{
			var equals = parts[2].IndexOf( '=' );
			if ( equals < 0 )
				throw new FormatException( $"Malformed parameter override '{entry}' in '{line}'." );
			return new ConfigurationOverride
			{
				FeatureId = parts[0],
				Kind = kind,
				Label = Decode( parts[2][..equals] ),
				Value = Decode( parts[2][( equals + 1 )..] ),
			};
		}
		return new ConfigurationOverride
		{
			FeatureId = parts[0],
			Kind = kind,
			Value = Decode( parts[2] ),
		};
	}

	/// <summary>Percent-encode the characters the one-token-per-configuration line cannot
	/// carry. <c>%</c> first on the way in and last on the way out, or an encoded escape
	/// would decode twice.</summary>
	static string Encode( string text ) => ( text ?? "" )
		.Replace( "%", "%25" )
		.Replace( " ", "%20" )
		.Replace( "|", "%7C" )
		.Replace( ";", "%3B" )
		.Replace( ":", "%3A" )
		.Replace( "=", "%3D" )
		.Replace( "\n", "%0A" )
		.Replace( "\r", "%0D" )
		.Replace( "\t", "%09" );

	static string Decode( string text ) => ( text ?? "" )
		.Replace( "%09", "\t" )
		.Replace( "%0D", "\r" )
		.Replace( "%0A", "\n" )
		.Replace( "%3D", "=" )
		.Replace( "%3A", ":" )
		.Replace( "%3B", ";" )
		.Replace( "%7C", "|" )
		.Replace( "%20", " " )
		.Replace( "%25", "%" );
}
