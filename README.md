# SteamNetNetworkTransport
A Steamworks.Net transport layer for Mirror

Uses https://github.com/rlabrecque/Steamworks.NET as a transport layer for https://github.com/vis2k/Mirror

Currently in initial development


Needs both Mirror and Steamworks.NET installed and working in Unity


Quick example how to start a host:

Transport.layer = new SteamworksNetworkTransport();
NetworkManager.singleton.StartHost();


Quick example how to connect to a host as a client:

Transport.layer = new SteamworksNetworkTransport();
NetworkManager.singleton.networkAddress = "11111111111111111"; //Where this number is the 64bit Steam ID of the user you are connecting to
NetworkManager.singleton.StartClient();
