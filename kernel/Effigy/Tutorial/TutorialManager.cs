using System.Collections.Generic;

namespace Marionette.EditorTools;

public class TutorialManager
{
	private readonly List<TutorialStep> _steps = new();
	private int _index;
	public IReadOnlyList<TutorialStep> Steps => _steps;
	public int CurrentIndex => _index;
	public TutorialStep Current => ( _index >= 0 && _index < _steps.Count ) ? _steps[_index] : null;
	public void Add( TutorialStep s ) => _steps.Add( s );
	public void Next() { if ( _index < _steps.Count - 1 ) _index++; }
	public void Prev() { if ( _index > 0 ) _index--; }
	public void Reset() => _index = 0;
}
