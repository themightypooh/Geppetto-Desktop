using System;
using System.Threading.Tasks;

namespace Effigy;

public sealed class AIAssistant
{
	public Task<string> SuggestAsync( AIPrompt prompt ) => Task.FromResult( "" );
	public Task ApplySuggestionAsync( string suggestion, PolyMesh mesh ) => Task.CompletedTask;
}