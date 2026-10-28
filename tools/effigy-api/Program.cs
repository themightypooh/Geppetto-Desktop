using System;
using System.Linq;
using System.IO;
using Effigy;
class Program {
  static int Main(string[] args) {
    if (args.Length == 0 || args[0] != "api") { Console.WriteLine("usage: effigy.sh api [Feature] | api --markdown | api --write | api --check"); return 2; }
    if (args.Length == 1) { foreach (var n in ApiCatalog.FeatureNames()) Console.WriteLine(n); Console.WriteLine("--- verbs ---"); foreach (var v in ApiCatalog.VerbNames()) Console.WriteLine(v); return 0; }
    var a = args[1];
    if (a == "--markdown") { Console.Write(ApiCatalog.GenerateMarkdown()); return 0; }
    if (a == "--write") { var md = ApiCatalog.GenerateMarkdown(); var p = "/home/poo/Desktop/geppetto-desktop/docs/EFFIGY-API.md"; File.WriteAllText(p, md); Console.WriteLine("wrote " + p + " (" + md.Length + " chars)"); return 0; }
    if (a == "--check") { var p = "/home/poo/Desktop/geppetto-desktop/docs/EFFIGY-API.md"; if (!File.Exists(p)) { Console.WriteLine("FAIL: docs/EFFIGY-API.md missing"); return 1; } var missing = ApiCatalog.VerifyDocsInSync(File.ReadAllText(p)); if (missing.Count == 0) { Console.WriteLine("OK: docs in sync"); return 0; } foreach (var m in missing) Console.WriteLine("MISSING: " + m); return 1; }
    if (a == "script") {
      if (args.Length < 3) { Console.WriteLine("usage: effigy.sh script build.txt [outdir]"); return 2; }
      var scriptPath = args[2];
      var outDir = args.Length > 3 ? args[3] : Path.GetDirectoryName(Path.GetFullPath(scriptPath)) ?? ".";
      Directory.CreateDirectory(outDir);
      var studio = new PartStudio();
      var result = BuildScript.Run(studio, File.ReadAllText(scriptPath), outDir);
      Console.WriteLine(result);
      Console.WriteLine("bodies: " + string.Join(", ", studio.Bodies.Select(b => b.Name ?? b.Id)));
      if (result.Ok) { var outPath = Path.Combine(outDir, "test.effigy"); StudioDocument.WriteFile(studio, outPath); Console.WriteLine("saved " + outPath); }
      return result.Ok ? 0 : 1;
    }
    if (a == "load") {
      if (args.Length < 3) { Console.WriteLine("usage: effigy.sh api load file.effigy"); return 2; }
      var studio = StudioDocument.ReadFile(args[2]);
      Console.WriteLine("loaded bodies: " + string.Join(", ", studio.Bodies.Select(b => b.Name ?? b.Id)));
      foreach (var f in studio.Features.OfType<SketchFeature>()) Console.WriteLine("sketch " + (f.Name ?? f.Id) + " curves: " + string.Join(", ", f.Sketch.Curves.Select(c => c.GetType().Name)));
      foreach (var img in studio.ReferenceImages) Console.WriteLine("refimage " + StudioDocument.ReferenceImageLine(img));
      foreach (var m in studio.RefMarks) Console.WriteLine("refmark " + m.View + " " + m.Name + " " + m.Px + "," + m.Py + " tag=" + (string.IsNullOrEmpty(m.Tag) ? "-" : m.Tag));
      foreach (var s in studio.RefSteps) Console.WriteLine("refstep " + s.Name + " " + s.State);
      return 0;
    }
    Console.WriteLine(ApiCatalog.DescribeFeature(a)); return 0;
  }
}
// appended script command is handled in Main via extra branch below
