using System;
using System.Collections.Generic;

namespace Effigy;

public sealed class NetworkManager
{
	public bool IsConnected { get; private set; }
	public void Connect( string url ) { IsConnected = true; }
	public void Disconnect() { IsConnected = false; }
	public void Send( NetworkMessage msg ) { }
	public event Action<NetworkMessage>? OnMessage;
}