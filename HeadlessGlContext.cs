using System;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// Linux EGL device/pbuffer context. No SDL, window, X11 or Wayland connection; never
/// falls back to a visible app when EGL is unavailable.
/// </summary>
public sealed class HeadlessGlContext : IDisposable
{
	const int None = 0x3038;
	nint _display, _surface, _context;
	public GL Gl { get; private set; }

	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	delegate uint QueryDevices( int capacity, [Out] nint[] devices, out int count );
	[UnmanagedFunctionPointer( CallingConvention.Cdecl )]
	delegate nint QueryDeviceString( nint device, int name );

	public HeadlessGlContext()
	{
		if ( !OperatingSystem.IsLinux() ) throw new PlatformNotSupportedException( "Headless GPU rendering currently requires Linux EGL." );
		nint query = eglGetProcAddress( "eglQueryDevicesEXT" );
		if ( query == 0 ) throw new NotSupportedException( "EGL device enumeration unavailable. No window will be opened." );
		var devices = new nint[16];
		if ( Marshal.GetDelegateForFunctionPointer<QueryDevices>( query )( devices.Length, devices, out int count ) == 0 )
			throw new InvalidOperationException( "EGL device enumeration failed." );
		var stringAddress = eglGetProcAddress( "eglQueryDeviceStringEXT" );
		var deviceString = stringAddress == 0 ? null : Marshal.GetDelegateForFunctionPointer<QueryDeviceString>( stringAddress );
		for ( int i = 0; i < count; i++ )
		{
			string extensions = deviceString is null ? "" : Marshal.PtrToStringAnsi( deviceString( devices[i], 0x3055 ) ) ?? "";
			if ( extensions.Contains( "EGL_MESA_device_software", StringComparison.Ordinal ) ) continue;
			_display = eglGetPlatformDisplay( 0x313F, devices[i], 0 ); // EGL_PLATFORM_DEVICE_EXT
			if ( _display == 0 || eglInitialize( _display, out _, out _ ) == 0 ) { Release(); continue; }
			int[] configAttributes = { 0x3033, 1, 0x3040, 8, 0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3025, 24, None };
			var configs = new nint[1];
			if ( eglChooseConfig( _display, configAttributes, configs, 1, out int configsCount ) == 0 || configsCount == 0 || eglBindAPI( 0x30A2 ) == 0 )
			{ Release(); continue; }
			_context = eglCreateContext( _display, configs[0], 0, new[] { 0x3098, 4, 0x30FB, 6, 0x30FD, 1, None } );
			_surface = eglCreatePbufferSurface( _display, configs[0], new[] { 0x3057, 1, 0x3056, 1, None } );
			if ( _context == 0 || _surface == 0 || eglMakeCurrent( _display, _surface, _surface, _context ) == 0 ) { Release(); continue; }
			Gl = GL.GetApi( eglGetProcAddress );
			Console.WriteLine( $"headless : EGL device/pbuffer; no window or display-server connection" );
			Console.WriteLine( $"renderer : {Gl.GetStringS( StringName.Renderer )}" );
			Console.WriteLine( $"GL       : {Gl.GetStringS( StringName.Version )}" );
			return;
		}
		throw new NotSupportedException( $"No hardware EGL OpenGL 4.6 context available (EGL error 0x{eglGetError():X}). No window will be opened." );
	}

	void Release()
	{
		if ( _display != 0 )
		{
			eglMakeCurrent( _display, 0, 0, 0 );
			if ( _context != 0 ) eglDestroyContext( _display, _context );
			if ( _surface != 0 ) eglDestroySurface( _display, _surface );
			eglTerminate( _display );
		}
		_context = _surface = _display = 0;
	}
	public void Dispose() { Gl?.Dispose(); Release(); }

	[DllImport( "libEGL.so.1" )] static extern nint eglGetProcAddress( [MarshalAs( UnmanagedType.LPStr )] string name );
	[DllImport( "libEGL.so.1" )] static extern nint eglGetPlatformDisplay( uint platform, nint device, nint attributes );
	[DllImport( "libEGL.so.1" )] static extern uint eglInitialize( nint display, out int major, out int minor );
	[DllImport( "libEGL.so.1" )] static extern uint eglChooseConfig( nint display, int[] attributes, [Out] nint[] configs, int capacity, out int count );
	[DllImport( "libEGL.so.1" )] static extern uint eglBindAPI( uint api );
	[DllImport( "libEGL.so.1" )] static extern nint eglCreateContext( nint display, nint config, nint share, int[] attributes );
	[DllImport( "libEGL.so.1" )] static extern nint eglCreatePbufferSurface( nint display, nint config, int[] attributes );
	[DllImport( "libEGL.so.1" )] static extern uint eglMakeCurrent( nint display, nint draw, nint read, nint context );
	[DllImport( "libEGL.so.1" )] static extern uint eglDestroyContext( nint display, nint context );
	[DllImport( "libEGL.so.1" )] static extern uint eglDestroySurface( nint display, nint surface );
	[DllImport( "libEGL.so.1" )] static extern uint eglTerminate( nint display );
	[DllImport( "libEGL.so.1" )] static extern uint eglGetError();
}
