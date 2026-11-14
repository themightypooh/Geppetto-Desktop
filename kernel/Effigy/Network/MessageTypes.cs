using System;

namespace Effigy;

public enum NetworkMessageType { MeshUpdate, SelectionUpdate, CursorUpdate, Chat }

[Serializable]
public sealed class NetworkMessage
{
	public NetworkMessageType Type { get; set; }
	public string Payload { get; set; } = "";
	public string Sender { get; set; } = "";
}