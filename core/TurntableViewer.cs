#nullable enable
using System;
using System.IO;
using System.Net;
using System.Text.Json;

namespace Marionette.Desktop;

/// <summary>
/// Godlike Wave 3 P2 — WebGL turntable viewer for review ("send to reviewer").
///
/// Generates a viewer.html next to an exported .glb: three.js (pinned
/// 0.160.0, CDN import map) loads the model and turns it on a turntable
/// (auto-rotate) with orbit controls, so a reviewer can inspect a model
/// without Geppetto installed. No server in v1: the page is static HTML.
///
/// One honest limitation, stated in the generated page's comment: browsers
/// block fetch() of local files over file://, so the reviewer serves the
/// folder with any static server (for example `python3 -m http.server`) or
/// opens it through the Desktop review link. The generator refuses absolute
/// paths and directory escapes in the GLB name, so a generated page never
/// leaks a local filesystem path.
/// </summary>
public static class TurntableViewer
{
	public const string FileName = "viewer.html";
	public const string ThreeVersion = "0.160.0";

	/// <summary>Turntable page HTML. glbFileName must be a bare file name
	/// (no directories, no absolute path); title is HTML-encoded.</summary>
	public static string GenerateHtml( string glbFileName, string title )
	{
		if ( string.IsNullOrWhiteSpace( glbFileName ) )
			throw new ArgumentException( "A GLB file name is required.", nameof( glbFileName ) );
		if ( glbFileName.IndexOfAny( new[] { '/', '\\' } ) >= 0 || Path.IsPathRooted( glbFileName ) )
			throw new ArgumentException( "The GLB name must be a bare file name, not a path.", nameof( glbFileName ) );
		var safeTitle = WebUtility.HtmlEncode( string.IsNullOrWhiteSpace( title ) ? "Geppetto turntable" : title );
		var glbLiteral = JsonSerializer.Serialize( glbFileName );
		return
			"<!doctype html>\n" +
			"<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n" +
			"<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n" +
			$"<title>{safeTitle}</title>\n" +
			"<style>html,body{margin:0;height:100%;overflow:hidden;background:#181818}" +
			"#app{position:fixed;inset:0}" +
			"#hint{position:fixed;left:12px;bottom:10px;color:#bbb;font:13px system-ui,sans-serif}" +
			"#err{position:fixed;left:12px;top:10px;color:#f66;font:13px system-ui,sans-serif;display:none}" +
			"</style>\n" +
			"<!-- Geppetto turntable review page. Serve this folder over HTTP " +
			"(e.g. python3 -m http.server): browsers block fetch() of local files over file://. -->\n" +
			"<script type=\"importmap\">\n" +
			"{\"imports\":{" +
			$"\"three\":\"https://unpkg.com/three@{ThreeVersion}/build/three.module.js\"," +
			$"\"three/addons/\":\"https://unpkg.com/three@{ThreeVersion}/examples/jsm/\"}}}}\n" +
			"</script>\n</head>\n<body>\n<div id=\"app\"></div>\n" +
			"<div id=\"err\"></div>\n" +
			"<div id=\"hint\">Drag to orbit &middot; scroll to zoom &middot; turntable auto-rotates</div>\n" +
			"<script type=\"module\">\n" +
			"import * as THREE from 'three';\n" +
			"import { OrbitControls } from 'three/addons/controls/OrbitControls.js';\n" +
			"import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';\n" +
			"const app = document.getElementById('app');\n" +
			"const renderer = new THREE.WebGLRenderer({ antialias: true });\n" +
			"renderer.setPixelRatio(window.devicePixelRatio);\n" +
			"renderer.setSize(window.innerWidth, window.innerHeight);\n" +
			"app.appendChild(renderer.domElement);\n" +
			"const scene = new THREE.Scene();\n" +
			"scene.background = new THREE.Color(0x181818);\n" +
			"const camera = new THREE.PerspectiveCamera(45, window.innerWidth / window.innerHeight, 0.01, 1000);\n" +
			"camera.position.set(3, 2, 4);\n" +
			"const controls = new OrbitControls(camera, renderer.domElement);\n" +
			"controls.autoRotate = true;\n" +
			"controls.autoRotateSpeed = 2.0;\n" +
			"controls.enableDamping = true;\n" +
			"scene.add(new THREE.HemisphereLight(0xffffff, 0x444455, 1.2));\n" +
			"const key = new THREE.DirectionalLight(0xffffff, 2.0);\n" +
			"key.position.set(4, 6, 3);\n" +
			"scene.add(key);\n" +
			$"new GLTFLoader().load({glbLiteral}, (gltf) => {{\n" +
			"  const model = gltf.scene;\n" +
			"  const box = new THREE.Box3().setFromObject(model);\n" +
			"  const center = box.getCenter(new THREE.Vector3());\n" +
			"  model.position.sub(center);\n" +
			"  const size = box.getSize(new THREE.Vector3()).length();\n" +
			"  camera.position.setLength(Math.max(size * 1.6, 0.5));\n" +
			"  scene.add(model);\n" +
			"  const tick = () => { controls.update(); renderer.render(scene, camera); requestAnimationFrame(tick); };\n" +
			"  tick();\n" +
			"}, undefined, (err) => {\n" +
			"  const el = document.getElementById('err');\n" +
			"  el.textContent = 'Could not load the model: ' + (err && err.message ? err.message : err);\n" +
			"  el.style.display = 'block';\n" +
			"});\n" +
			"window.addEventListener('resize', () => {\n" +
			"  camera.aspect = window.innerWidth / window.innerHeight;\n" +
			"  camera.updateProjectionMatrix();\n" +
			"  renderer.setSize(window.innerWidth, window.innerHeight);\n" +
			"});\n" +
			"</script>\n</body>\n</html>\n";
	}

	/// <summary>Write viewer.html next to the GLB in outputDir. The GLB must
	/// already exist there. Returns the viewer path.</summary>
	public static string WriteViewer( string outputDir, string glbFileName, string title )
	{
		ArgumentNullException.ThrowIfNull( outputDir );
		var glbPath = Path.Combine( outputDir, glbFileName );
		if ( !File.Exists( glbPath ) )
			throw new FileNotFoundException( $"GLB '{glbFileName}' does not exist in '{outputDir}'.", glbPath );
		var html = GenerateHtml( glbFileName, title );
		var viewerPath = Path.Combine( outputDir, FileName );
		File.WriteAllText( viewerPath, html );
		return viewerPath;
	}
}
