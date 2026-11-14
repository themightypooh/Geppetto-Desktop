using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Effigy;

public sealed class PluginManager
{
	private readonly Dictionary<string, IPlugin> _loaded = new();
	public IReadOnlyDictionary<string, IPlugin> Loaded => _loaded;

	public void ScanDirectory( string directory )
	{
		if ( !Directory.Exists( directory ) ) return;
		foreach ( var file in Directory.GetFiles( directory, "*.dll" ) )
		{
			try { LoadAssembly( file ); } catch (System.Exception soft) { Effigy.KernelLog.Soft( "PluginManager.ScanDirectory", soft ); }
		}
	}

	private void LoadAssembly( string path )
	{
		var asm = System.Reflection.Assembly.LoadFrom( path );
		foreach ( var t in asm.GetTypes() )
		{
			if ( !typeof( IPlugin ).IsAssignableFrom( t ) || t.IsAbstract ) continue;
			var attr = t.GetCustomAttribute<PluginAttribute>();
			if ( attr is null ) continue;
			if ( Activator.CreateInstance( t ) is IPlugin plugin )
				Load( plugin );
		}
	}

	public void Load( IPlugin plugin )
	{
		if ( plugin is null ) throw new ArgumentNullException( nameof( plugin ) );
		if ( _loaded.ContainsKey( plugin.Name ) ) return;
		plugin.OnLoad();
		_loaded[plugin.Name] = plugin;
	}

	public void Unload( string name )
	{
		if ( _loaded.TryGetValue( name, out var p ) )
		{
			p.OnUnload();
			_loaded.Remove( name );
		}
	}

	public void UnloadAll()
	{
		foreach ( var p in _loaded.Values.ToList() ) p.OnUnload();
		_loaded.Clear();
	}
}