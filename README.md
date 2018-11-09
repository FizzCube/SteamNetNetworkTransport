# SteamNetNetworkTransport
A Steam P2P network transport layer for Mirror ([vis2k's UNET replacement for Unity](https://github.com/vis2k/Mirror))

SteamNetNetworkTransport uses [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) wrapper for the steamworks API.

## Quick start - Demo Project

If you want to dive right in with a basic functional demo then find the example project here which includes with Mirror and Steamworks.NET

https://github.com/FizzCube/SteamNetNetworkTransport-Example

## Dependencies
SteamNetNetworkTransport relies on [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) to communicate with the [Steamworks API](https://partner.steamgames.com/doc/sdk).

SteamNetNetworkTransport is also obviously dependant on [Mirror](https://github.com/vis2k/Mirror) which is a streamline, bug fixed, maintained version of UNET for Unity.

Both of these projects need to be installed and working before you can use this transport

## How to use
1. Download and install the dependencies 
2. Download SteamworksNetworkTransport.cs and place in your Assets folder somewhere. If errors occur, open a Issue ticket.
3. In your class extending from Mirror's NetworkManager class, you would do:
```csharp
public override void InitializeTransport() {
  Transport.layer = new SteamworksNetworkTransport();
}
```
Then, to start as a host:

```csharp
SteamNetworkManager.singleton.StartHost();
```

Or, to connect to a host as a client:

```csharp
SteamNetworkManager.singleton.networkAddress = friendSteamIDs;
SteamNetworkManager.singleton.StartClient();
```
Where friendSteamIDs is a string containing the hosts Steam account numeric ID.
