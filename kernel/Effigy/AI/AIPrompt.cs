using System;

namespace Effigy;

public sealed class AIPrompt
{
	public string Text { get; set; } = "";
	public string Model { get; set; } = "default";
	public float Temperature { get; set; } = 0.7f;
}