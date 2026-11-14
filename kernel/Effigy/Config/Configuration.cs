using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Effigy;

public sealed class Configuration
{
	public Dictionary<string, object> Values { get; set; } = new();

	public T Get<T>( string key, T defaultValue = default! )
	{
		if ( Values.TryGetValue( key, out var v ) && v is JsonElement je )
		{
			try { return je.Deserialize<T>() ?? defaultValue; } catch { return defaultValue; }
		}
		if ( v is T t ) return t;
		return defaultValue;
	}

	public void Set( string key, object value ) => Values[key] = value;

	public static Configuration Load( string path )
	{
		if ( !File.Exists( path ) ) return new Configuration();
		var json = File.ReadAllText( path );
		return JsonSerializer.Deserialize<Configuration>( json ) ?? new Configuration();
	}

	public void Save( string path )
	{
		var json = JsonSerializer.Serialize( this, new JsonSerializerOptions { WriteIndented = true } );
		File.WriteAllText( path, json );
	}
}