using System;

namespace Effigy;

public interface IPlugin
{
	string Name { get; }
	string Version { get; }
	void OnLoad();
	void OnUnload();
}