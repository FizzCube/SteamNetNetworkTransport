using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using Steamworks;

namespace Mirror
{

    internal class SteamClient
    {
        public enum ConnectionState
        {
            CONNECTING,
            CONNECTED,
            DISCONNECTING,
        }

        public CSteamID steamID;
        public ConnectionState state;
        public int connectionID;
        public float timeIdle = 0;

        public SteamClient(ConnectionState state, CSteamID steamID, int connectionID)
        {
            this.state = state;
            this.steamID = steamID;
            this.connectionID = connectionID;
            this.timeIdle = 0;
        }
    }

    internal class SteamConnectionMap : IEnumerable<KeyValuePair<int, SteamClient>>
    {
        public readonly Dictionary<CSteamID, SteamClient> fromSteamID = new Dictionary<CSteamID, SteamClient>();
        public readonly Dictionary<int, SteamClient> fromConnectionID = new Dictionary<int, SteamClient>();

        public SteamConnectionMap()
        {
        }

        public SteamClient Add(CSteamID steamID, int connectionID, SteamClient.ConnectionState state)
        {
            var newClient = new SteamClient(state, steamID, connectionID);
            fromSteamID.Add(steamID, newClient);
            fromConnectionID.Add(connectionID, newClient);

            return newClient;
        }

        public void Remove(SteamClient steamClient)
        {
            fromSteamID.Remove(steamClient.steamID);
            fromConnectionID.Remove(steamClient.connectionID);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public IEnumerator<KeyValuePair<int, SteamClient>> GetEnumerator()
        {
            return fromConnectionID.GetEnumerator();
        }
    }

    public class SteamworksNetworkTransport : TransportLayer
    {

        private enum SteamChannels : int
        {
            SEND_TO_CLIENT,
            SEND_TO_SERVER,
            SEND_INTERNAL
        }

        EP2PSend[] sendMethods =
        {
            EP2PSend.k_EP2PSendReliableWithBuffering,
            EP2PSend.k_EP2PSendUnreliable
        };

        private enum InternalMessages : byte
        {
            CONNECT,
            ACCEPT_CONNECT,
            DISCONNECT
        }

        private byte[] connectMsgBuffer = new byte[] { (byte)InternalMessages.CONNECT };
        private byte[] acceptConnectMsgBuffer = new byte[] { (byte)InternalMessages.ACCEPT_CONNECT };
        private byte[] disconnectMsgBuffer = new byte[] { (byte)InternalMessages.DISCONNECT };

        private enum Mode
        {
            UNDEFINED,
            CLIENT,
            SERVER
        }

        //The amount of seconds After NetworkTime.PingFrequency before we deam a connection as timed out. We should receive messages at least as oftern as NetworkTime.PingFrequency! Default up to 1 second late
        public static float connectedTimeoutBuffer = 5.0f;

        private float clientConnectStarted;
        //how long to wait before connect timeout
        public static float clientConnectTimeout = 25.0f;

        public static bool debug = false;

        //if we are in server or client mode
        private Mode mode = Mode.UNDEFINED;

        //steam client we are connected to in client mode
        private SteamClient steamClientServer;
        private int nextConnectionID;

        private SteamConnectionMap steamConnectionMap;

        private Queue<int> steamNewConnections;
        private Queue<SteamClient> steamDisconnectedConnections;

        private byte[] serverReceiveBuffer;
        private int maxConnections = 0;

        //These 2 variables are used in Receive if we receive a new connection And a data packet at the same time. The data is queued to be rerurned next time
        private int serverReceiveBufferPendingConnectionID = -1;
        private byte[] serverReceiveBufferPending = null;

        //this is a callback from steam that gets registered and called when the server receives new connections
        private Callback<P2PSessionRequest_t> callback_OnNewConnection = null;
        //this is a callback from steam that gets registered and called when the ClientConnect fails
        private Callback<P2PSessionConnectFail_t> callback_OnConnectFail = null;

        private int lastInternalMessageFrame = 0;


        public int GetMaxPacketSize(int channelId = Channels.DefaultReliable)
        {
            switch (channelId)
            {
                case Channels.DefaultUnreliable:
                    return 1200; //UDP like - MTU size.

                case Channels.DefaultReliable:
                    return 1048576; //Reliable message send. Can send up to 1MB of data in a single message.

                default:
                    Debug.LogError("Unknown channel so uknown max size");
                    return 0;
            }

        }


        //*********************************** shared stuff


        void initialise()
        {
            nextConnectionID = 1;

            steamConnectionMap = new SteamConnectionMap();

            steamNewConnections = new Queue<int>();
            steamDisconnectedConnections = new Queue<SteamClient>();

            serverReceiveBuffer = new byte[GetMaxPacketSize()];

            serverReceiveBufferPendingConnectionID = -1;
            serverReceiveBufferPending = null;

            setupSteamCallbacks();
        }

        /**
            * Send data to peer.
            * Use this to send data from one connection to another using the connectionId. Place the data to be sent in the function as a byte array.
            */
        private bool Send(SteamClient steamclient, byte[] buffer, int steamChannel, int sendType)
        {
            if (buffer == null)
            {
                throw new NullReferenceException("send buffer is not initialized");
            }
            if (sendType >= sendMethods.Length)
            {
                Debug.LogError("Trying to use an unknown method to send data");
                return false;
            }

            if (steamclient.state != SteamClient.ConnectionState.CONNECTED)
            {
                Debug.LogError("Trying to send data on client thats not connected. Current State: " + steamclient.state);
                return false;
            }

            if (SteamNetworking.SendP2PPacket(steamclient.steamID, buffer, (uint)buffer.Length, sendMethods[sendType], steamChannel))
            {
                return true;
            }
            else
            {
                Debug.LogError("Error sending data over steam on connection " + steamclient.connectionID);
                return false;
            }

        }

        /*
         * Check for messages and also deal with new connections and disconnects etc
         */
        private bool ReceiveAndProcessEvents(out int connectionId, out TransportEvent transportEvent, out byte[] data, int chan)
        {
            data = null;

            //first check if we have received any new connections and return them as an event
            if (steamNewConnections.Count > 0)
            {
                if (LogFilter.Debug || debug) { Debug.Log("Handling a new connection from queue"); }

                connectionId = steamNewConnections.Dequeue();

                try
                {
                    SteamClient steamClient = steamConnectionMap.fromConnectionID[connectionId];

                    steamClient.timeIdle = 0;

                    if (steamClient.state == SteamClient.ConnectionState.CONNECTING)
                    {
                        if (LogFilter.Debug || debug) { Debug.Log("Set connection state to connected"); }
                        steamClient.state = SteamClient.ConnectionState.CONNECTED;
                    }
                }
                catch (KeyNotFoundException)
                {
                    //shouldnt happen - ignore
                }

                transportEvent = TransportEvent.Connected;
                return true;
            }

            //first check if we have received any new disconnects and return them as an event
            if (steamDisconnectedConnections.Count > 0)
            {
                if (LogFilter.Debug || debug) { Debug.Log("Handling a disconnect from queue"); }

                SteamClient steamClient = steamDisconnectedConnections.Dequeue();
                connectionId = steamClient.connectionID;
                transportEvent = TransportEvent.Disconnected;

                //at this point we have completly disconnected from the client so stop accepting further messages from this client
                SteamNetworking.CloseP2PSessionWithUser(steamClient.steamID);

                return true;
            }

            //this is a buffer that may have been received at the same time as a new connection
            if (serverReceiveBufferPending != null)
            {
                if (LogFilter.Debug || debug) { Debug.Log("Handling a postponed message"); }

                //we have a packet received and already in the buffer waiting to be returned
                connectionId = serverReceiveBufferPendingConnectionID;
                transportEvent = TransportEvent.Data;
                data = serverReceiveBufferPending;

                //clear our buffered variables
                serverReceiveBufferPendingConnectionID = -1;
                serverReceiveBufferPending = null;
                return true;
            }

            //finally look for new packets

            return Receive(out connectionId, out transportEvent, out data, chan);
        }

        /**
         * Check for new network messages
         */
        private bool Receive(out int connectionId, out TransportEvent transportEvent, out byte[] data, int chan)
        {
            data = null;


            //finally look for new packets

            uint packetSize;
            CSteamID clientSteamID;

            if (SteamNetworking.IsP2PPacketAvailable(out packetSize, chan))
            {
                //check we have enough room for this packet
                if (packetSize > GetMaxPacketSize())
                {
                    //cant read .. too big! should error here
                    Debug.LogError("Available message is too large");
                    connectionId = -1;
                    transportEvent = TransportEvent.Disconnected;
                    return false;
                }

                if (SteamNetworking.ReadP2PPacket(serverReceiveBuffer, packetSize, out packetSize /*actual size read*/, out clientSteamID, chan))
                {
                    SteamClient steamClient;
                    try
                    {
                        //check we have a record for this connection
                        steamClient = steamConnectionMap.fromSteamID[clientSteamID];

                        if (steamClient.state != SteamClient.ConnectionState.CONNECTED)
                        {
                            //we are not currently connected to this client - this shouldnt happen
                            Debug.LogError("Received a message for a client thats not connected");
                            connectionId = -1;
                            transportEvent = TransportEvent.Disconnected;
                            return false;
                        }

                        steamClient.timeIdle = 0;
                    }
                    catch (KeyNotFoundException)
                    {
                        if (packetSize == 0)
                        {
                            Debug.LogWarning("Unexpected packet from steam client");

                            //ok so this happens when steam has previously accepted message from this client but they are not on our current list. This could be after quickly disconnecting a host and then starting it back up again for example.
                            //It should be perfectly ok for this to happen in these types of circumstances
                            HandleNewConnection(clientSteamID);
                        }
                        else
                        {
                            //This shouldnt happen
                            Debug.LogError("Totally unexpected steam ID " + clientSteamID + " sent a message size: " + packetSize + " / byte: " + serverReceiveBuffer[0]);
                        }
                        connectionId = -1;
                        transportEvent = TransportEvent.Disconnected;
                        return false;
                    }

                    //received normal data packet
                    if (packetSize == 0)
                    {
                        //ok so, sometimes in steam p2p we send a blank packet just to handshake.. this should just be around connect, but we seem to be already connected.
                        //best thing we can do is call another Receive
                        return Receive(out connectionId, out transportEvent, out data, chan);
                    }
                    else
                    {
                        connectionId = steamClient.connectionID;
                        transportEvent = TransportEvent.Data;
                        //for now allocate a new buffer TODO: do we need to do this?
                        data = new byte[packetSize];
                        Array.Copy(serverReceiveBuffer, data, packetSize);

                        return true;
                    }

                }
            }

            //nothing available
            connectionId = -1;
            transportEvent = TransportEvent.Disconnected; //they havent disconnected but this is what the LLAPITransport returns here. There is not a "nothing" event
            return false;
        }

        /**
         * Check for new network messages
         */
        private bool ReceiveInternal(out int connectionId, out byte[] data)
        {
            data = null;


            //finally look for new packets

            uint packetSize;
            CSteamID clientSteamID;

            if (SteamNetworking.IsP2PPacketAvailable(out packetSize, (int)SteamChannels.SEND_INTERNAL))
            {
                //check we have enough room for this packet
                if (packetSize > GetMaxPacketSize())
                {
                    //cant read .. too big! should error here
                    Debug.LogError("Available message is too large");
                    connectionId = -1;
                    return false;
                }

                if (SteamNetworking.ReadP2PPacket(serverReceiveBuffer, packetSize, out packetSize /*actual size read*/, out clientSteamID, (int)SteamChannels.SEND_INTERNAL))
                {
                    SteamClient steamClient;
                    try
                    {
                        //check we have a record for this connection
                        steamClient = steamConnectionMap.fromSteamID[clientSteamID];

                    }
                    catch (KeyNotFoundException)
                    {
                        //There was no existing connection found.. initialise it
                        HandleNewConnection(clientSteamID);

                        //now check its there and get it
                        try
                        {
                            //check we have a record for this connection
                            steamClient = steamConnectionMap.fromSteamID[clientSteamID];

                        }
                        catch (KeyNotFoundException)
                        {
                            HandleNewConnection(clientSteamID);

                            connectionId = -1;
                            return false;
                        }
                    }

                    if (packetSize != 0)
                    {
                        connectionId = steamClient.connectionID;
                        //for now allocate a new buffer TODO: do we need to do this?
                        data = new byte[packetSize];
                        Array.Copy(serverReceiveBuffer, data, packetSize);

                        return true;
                    }

                }
            }

            //nothing available
            connectionId = -1;
            return false;
        }

        /**
         * Check for internal networked messages like disconnect request
         */
        private void handleInternalMessages()
        {
            if (lastInternalMessageFrame == Time.frameCount)
            {
                return; //already processed this frame
            }
            lastInternalMessageFrame = Time.frameCount;

            int connectionId;
            Byte[] data;

            while (ReceiveInternal(out connectionId, out data))
            {
                if (LogFilter.Debug || debug) { Debug.Log("handleInternalMessages Got a message"); }
                try
                {

                    SteamClient steamClient = steamConnectionMap.fromConnectionID[connectionId];

                    if (steamClient.state == SteamClient.ConnectionState.CONNECTED || steamClient.state == SteamClient.ConnectionState.CONNECTING)
                    {
                        NetworkReader reader = new NetworkReader(data);
                        byte message = reader.ReadByte();
                        switch (message)
                        {
                            case (byte)InternalMessages.CONNECT:
                                if (LogFilter.Debug || debug) { Debug.LogWarning("Received a request to Connect from " + steamClient.steamID); }

                                //if we are a server and have spaces then accept the request to connect, else reject
                                if (mode == Mode.SERVER && getNumberOfActiveConnection() < maxConnections)
                                {
                                    //we have to queue this connection up so we can let the ReceiveFromHost method return the new connection
                                    steamNewConnections.Enqueue(connectionId);
                                    //send a message back to the client to confirm they have connected successfully
                                    SteamNetworking.SendP2PPacket(steamClient.steamID, acceptConnectMsgBuffer, (uint)acceptConnectMsgBuffer.Length, sendMethods[Channels.DefaultReliable], (int)SteamChannels.SEND_INTERNAL);
                                }
                                else
                                {
                                    if (LogFilter.Debug || debug) { Debug.LogWarning("Rejected connect from " + steamClient.steamID + ". Active Connections: " + getNumberOfActiveConnection() + ", maxConnections: " + maxConnections); }
                                    SteamNetworking.CloseP2PSessionWithUser(steamClient.steamID);
                                }

                                steamClient.timeIdle = 0;

                                break;

                            case (byte)InternalMessages.ACCEPT_CONNECT:
                                if (LogFilter.Debug || debug) { Debug.LogWarning("Received an accept to connect to " + steamClient.steamID); }

                                //if we are a client and received this message from the server we are trying to connect to then connect, else reject
                                if (mode == Mode.CLIENT && steamClient.steamID == steamClientServer.steamID)
                                {
                                    //we have to queue this connection up so we can let the ReceiveFromHost method return the new connection
                                    steamNewConnections.Enqueue(connectionId);
                                }
                                else
                                {
                                    Debug.LogError("Received an ACCEPT_CONNECT from wrong person");
                                    SteamNetworking.CloseP2PSessionWithUser(steamClient.steamID);
                                }

                                steamClient.timeIdle = 0;

                                break;

                            case (byte)InternalMessages.DISCONNECT:
                                if (LogFilter.Debug || debug) { Debug.LogWarning("Received an instruction to Disconnect from " + steamClient.steamID); }

                                //requested to disconnect
                                if (mode == Mode.CLIENT || mode == Mode.SERVER)
                                {
                                    //disconnect this client
                                    closeSteamConnection(steamClient);
                                }
                                break;

                        }
                    }
                    else
                    {
                        Debug.LogWarning("Received message for non connected client ?");
                    }
                }
                catch (KeyNotFoundException)
                {
                    //shouldnt happen - ignore
                    Debug.LogError("Received internal message from unknown.. connect?");
                }
            }

            //check all connections to check they are healthy 
            foreach (var connection in steamConnectionMap)
            {
                SteamClient steamClient = connection.Value;

                if (steamClient.state == SteamClient.ConnectionState.CONNECTED)
                {
                    if (steamClient.timeIdle > (NetworkTime.PingFrequency + connectedTimeoutBuffer))
                    {
                        //idle too long - disconnect
                        Debug.LogWarning("Connection " + steamClient.connectionID + " timed out - going to disconnect");
                        internalDisconnect(steamClient);
                        continue;
                    }

                    //If client then this will Not be called if the scene is loading. We dont want to count idle time if loading the scene for this reason
                    //If Server then this will be called so we need to not count idle time for clients who are not "ready" as they are loading the scene and unable to communicate. The connect timeout will catch people that disconnect during scene load
                    if (mode == Mode.CLIENT || (mode == Mode.SERVER && (!NetworkServer.connections.ContainsKey(steamClient.connectionID) || NetworkServer.connections[steamClient.connectionID].isReady)))
                    {
                        steamClient.timeIdle += Time.deltaTime;
                    }

                }
            }

        }

        /**
         * This closes the P2P connection and if its connected or connecting adds it to a queue to return the disconnect event
         */
        private void closeSteamConnection(SteamClient steamClient)
        {
            if (steamClient.state == SteamClient.ConnectionState.CONNECTED || steamClient.state == SteamClient.ConnectionState.CONNECTING)
            {
                steamClient.state = SteamClient.ConnectionState.DISCONNECTING;

                steamDisconnectedConnections.Enqueue(steamClient);
            }

        }

        /**
         * Tell the connection that you are going to disconnect (so it can promptly disconnect you) and then close our connection
         */
        private void internalDisconnect(SteamClient steamClient)
        {
            if (steamClient.state == SteamClient.ConnectionState.CONNECTED)
            {
                if (LogFilter.Debug || debug) { Debug.Log("Send internal disconnect message to " + steamClient); }
                Send(steamClient, disconnectMsgBuffer, (int)SteamChannels.SEND_INTERNAL, Channels.DefaultReliable);
            }

            closeSteamConnection(steamClient);
        }

        private void setupSteamCallbacks()
        {
            if (SteamManager.Initialized)
            {
                if (callback_OnNewConnection == null)
                {
                    callback_OnNewConnection = Callback<P2PSessionRequest_t>.Create(OnNewConnection);
                }
                if (callback_OnConnectFail == null)
                {
                    callback_OnConnectFail = Callback<P2PSessionConnectFail_t>.Create(OnConnectFail);
                }
            }
            else
            {
                Debug.LogError("STEAM NOT Initialized so couldnt integrate with P2P");
                return;
            }

        }

        //note: can be called on the server too probably on its first message to the client. In any case, just try to disconnect
        private void OnConnectFail(P2PSessionConnectFail_t result)
        {
            Debug.LogWarning("Connection failed or closed Steam ID: " + result.m_steamIDRemote + " / eP2PSessionError : " + result.m_eP2PSessionError);

            if (mode == Mode.CLIENT)
            {
                ClientDisconnect();
            }
            else if (mode == Mode.SERVER)
            {
                try
                {
                    ServerDisconnect(steamConnectionMap.fromSteamID[result.m_steamIDRemote].connectionID);
                }
                catch (KeyNotFoundException)
                {
                }
            }
        }

        private void OnNewConnection(P2PSessionRequest_t result)
        {
            //this happens when a user trys to connect to this machine and we havent agreed to accept their connection recently

            HandleNewConnection(result.m_steamIDRemote);

        }

        private void HandleNewConnection(CSteamID steamID)
        {
            if (LogFilter.Debug || debug) { Debug.LogError("HandleNewConnection " + steamID); }

            //check if we have a connection already stored for this steam account
            try
            {
                SteamClient steamClient = steamConnectionMap.fromSteamID[steamID];
                if (steamClient.state == SteamClient.ConnectionState.CONNECTED || steamClient.state == SteamClient.ConnectionState.CONNECTING)
                {
                    if (LogFilter.Debug || debug) { Debug.Log("HandleNewConnection ACCEPT"); }
                    //accept! We thought they were anyway
                    SteamNetworking.AcceptP2PSessionWithUser(steamID);
                    return;
                }

                if (LogFilter.Debug || debug) { Debug.Log("HandleNewConnection REJECT - disconnecting"); }
                //currently in disconnecting state. Dont accept another connection
                SteamNetworking.CloseP2PSessionWithUser(steamID);
                return;
            }
            catch (KeyNotFoundException)
            {
            }

            if (LogFilter.Debug || debug) { Debug.Log("HandleNewConnection New"); }

            if ((mode == Mode.SERVER && getNumberOfActiveConnection() >= maxConnections) || (mode == Mode.CLIENT && steamID != steamClientServer.steamID))
            {
                Debug.LogError("HandleNewConnection Reject, full or unexpected");

                //either too many people connecting to server Or we are a client and someone else is tryign to connect! Dont accept
                SteamNetworking.CloseP2PSessionWithUser(steamID);
                return;
            }

            if (LogFilter.Debug || debug) { Debug.Log("HandleNewConnection ACCEPT NEW"); }
            //just accept! so we can start communicating
            SteamNetworking.AcceptP2PSessionWithUser(steamID);

            //new connection id
            int connectionId = nextConnectionID++;
            steamConnectionMap.Add(steamID, connectionId, SteamClient.ConnectionState.CONNECTING);
        }

        /*********************************** implement client stuff */

        public void ClientConnect(string address, int port)
        {
            if (mode == Mode.CLIENT)
            {
                Debug.LogError("Cant connect, already client");
                return;
            }
            if (mode == Mode.SERVER)
            {
                Debug.LogError("Cant connect, already server");
                return;
            }

            initialise();

            CSteamID steamID;

            try
            {
                steamID = new CSteamID(Convert.ToUInt64(address));
            }
            catch (FormatException)
            {
                Debug.LogError("*** ERROR passing steam ID address");
                return;
            }

            mode = Mode.CLIENT;

            clientConnectStarted = Time.time;

            int connectionId = nextConnectionID++;
            steamClientServer = steamConnectionMap.Add(steamID, connectionId, SteamClient.ConnectionState.CONNECTING);

            if (LogFilter.Debug || debug) { Debug.Log("Sending connect request to " + steamID); }

            //Send a connect message to the steam client - this requests a connection with them
            SteamNetworking.SendP2PPacket(steamID, connectMsgBuffer, (uint)connectMsgBuffer.Length, sendMethods[Channels.DefaultReliable], (int)SteamChannels.SEND_INTERNAL);
        }

        public bool ClientConnected()
        {
            //basic - Steam P2P doesnt have a real connection state
            if (steamClientServer == null || mode != Mode.CLIENT)
            {
                return false;
            }

            return steamClientServer.state == SteamClient.ConnectionState.CONNECTED;
        }

        public void ClientDisconnect()
        {
            if (steamClientServer == null || mode != Mode.CLIENT)
            {
                return;
            }

            if (steamClientServer.state == SteamClient.ConnectionState.CONNECTED || steamClientServer.state == SteamClient.ConnectionState.CONNECTING)
            {
                internalDisconnect(steamClientServer);
            }

            mode = Mode.UNDEFINED;
        }

        public bool ClientGetNextMessage(out TransportEvent transportEvent, out byte[] data)
        {
            transportEvent = TransportEvent.Disconnected;
            data = null;

            //basic - Steam P2P doesnt have a real connection state
            if (steamClientServer == null || mode != Mode.CLIENT)
            {
                return false;
            }

            if (steamClientServer.state == SteamClient.ConnectionState.DISCONNECTING)
            {
                if (LogFilter.Debug || debug) { Debug.Log("We are currently trying to disconnect - so, disconnect"); }
                transportEvent = TransportEvent.Disconnected;

                //make sure we dont communicate any more
                SteamNetworking.CloseP2PSessionWithUser(steamClientServer.steamID);

                //we are done being a client
                steamClientServer = null;
                mode = Mode.UNDEFINED;
                return true;
            }

            if (steamClientServer.state == SteamClient.ConnectionState.CONNECTING && Time.time - clientConnectStarted > clientConnectTimeout)
            {
                Debug.LogWarning("Timeout while connecting..");

                ClientDisconnect();
                return false;
            }

            //if we are not connecting or connected then we shouldnt get getting messages - exit here
            if (steamClientServer.state != SteamClient.ConnectionState.CONNECTED && steamClientServer.state != SteamClient.ConnectionState.CONNECTING)
            {
                return false;
            }

            handleInternalMessages();

            int connectionId;
            return ReceiveAndProcessEvents(out connectionId, out transportEvent, out data, (int)SteamChannels.SEND_TO_CLIENT);
        }

        public float ClientGetRTT()
        {
            return (float)NetworkTime.rtt;
        }

        public bool ClientSend(int sendType, byte[] data)
        {
            if (!ClientConnected())
            {
                return false;
            }

            return Send(steamClientServer, data, (int)SteamChannels.SEND_TO_SERVER, sendType);
        }


        public bool GetConnectionInfo(int connectionId, out string address)
        {
            try
            {
                CSteamID steamID = steamConnectionMap.fromConnectionID[connectionId].steamID;

                address = Convert.ToString(steamID);
                return true;
            }
            catch (KeyNotFoundException)
            {
                address = "";
                return false;
            }
        }

        /*********************************** implement server stuff */

        public void ServerStart(string address, int port, int maxConnections)
        {
            if (mode == Mode.CLIENT)
            {
                Debug.LogError("Cant start server, already client");
                return;
            }
            if (mode == Mode.SERVER)
            {
                Debug.LogError("Cant start server, already server");
                return;
            }

            this.maxConnections = maxConnections;
            mode = Mode.SERVER;
            initialise();

        }

        private int getNumberOfActiveConnection()
        {
            int count = 0;

            foreach (var con in steamConnectionMap)
            {
                if (con.Value.state == SteamClient.ConnectionState.CONNECTED)
                {
                    count++;
                }
            }

            return count;
        }

        public void ServerStartWebsockets(string address, int port, int maxConnections)
        {
            Debug.LogError("Websockets not supported with steam transport"); //duh!
        }

        public bool ServerActive()
        {
            return mode == Mode.SERVER && callback_OnNewConnection != null;
        }

        public bool ServerGetNextMessage(out int connectionId, out TransportEvent transportEvent, out byte[] data)
        {
            if (!ServerActive())
            {
                connectionId = -1;
                transportEvent = TransportEvent.Disconnected;
                data = null;
                return false;
            }

            handleInternalMessages();

            return ReceiveAndProcessEvents(out connectionId, out transportEvent, out data, (int)SteamChannels.SEND_TO_SERVER);
        }

        public bool ServerSend(int connectionId, int sendType, byte[] data)
        {
            try
            {
                SteamClient steamClient = steamConnectionMap.fromConnectionID[connectionId];

                return Send(steamClient, data, (int)SteamChannels.SEND_TO_CLIENT, sendType);
            }
            catch (KeyNotFoundException)
            {
                //we have no idea who this connection is
                Debug.LogError("Trying to send data on client thats not known " + connectionId);

                return false;
            }

        }

        public bool ServerDisconnect(int connectionId)
        {
            if (LogFilter.Debug || debug) { Debug.Log("ServerDisconnect SteamworksNetworkTransport on ID " + connectionId); }

            if (connectionId == 0)
            {
                //this is the ID used for internal connections - ignore this
                return true;
            }
            if (!ServerActive())
            {
                return false;
            }

            try
            {
                if (LogFilter.Debug || debug) { Debug.Log("Attempting to disconnect SteamID:" + connectionId); }

                SteamClient steamClient = steamConnectionMap.fromConnectionID[connectionId];

                //remove the connection from our list
                steamConnectionMap.Remove(steamClient);

                if (steamClient.state == SteamClient.ConnectionState.CONNECTED || steamClient.state == SteamClient.ConnectionState.CONNECTING)
                {
                    internalDisconnect(steamClient);
                    if (LogFilter.Debug || debug) { Debug.Log("Server Disconnected"); }
                    return true;
                }

            }
            catch (KeyNotFoundException)
            {
                //we have no idea who this connection is
                Debug.LogError("Trying to disconnect a client thats not known " + connectionId);
            }

            return false;
        }

        public void ServerStop()
        {
            if (LogFilter.Debug || debug) { Debug.Log("Stop SteamworksNetworkTransport"); }

            foreach (var connection in steamConnectionMap)
            {
                SteamClient steamClient = connection.Value;
                internalDisconnect(steamClient);

            }

            mode = Mode.UNDEFINED;
        }

        public void Shutdown()
        {
            if (LogFilter.Debug || debug) { Debug.Log("Shutdown SteamworksNetworkTransport"); }

            if (mode == Mode.SERVER)
            {
                ServerStop();
            }
            else if (mode == Mode.CLIENT)
            {
                ClientDisconnect();
            }
        }


    }

}