namespace Marionette.Desktop;

/// <summary>The only thing AiLiveRun needs from its owner: resolve a run-relative path to an absolute, sandboxed one.
/// AiSuiteTools (desktop) implements it with its existing Owned(string).</summary>
public interface IRunPathOwner { string Owned( string relativePath ); }
