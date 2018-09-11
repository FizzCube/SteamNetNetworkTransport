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
        public float lastPing = 0;
        public float lastPong = 0;

        public SteamClient(ConnectionState state, CSteamID steamID, int connectionID)
        {
            this.state = state;
            this.steamID = steamID;
            this.connectionID = connectionID;
            this.lastPing = Time.time;
            this.lastPong = Time.time;
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

        //How to send most packets
        public static EP2PSend currentSendType = EP2PSend.k_EP2PSendReliableWithBuffering;

        private enum Channels : int
        {
            SEND_TO_CLIENT,
            SEND_TO_SERVER,
            SEND_INTERNAL
        }

        private const int PING_FREQUENCY = 5;
        private const int PONG_TIMEOUT = 30;

        private enum InternalMessages : byte
        {
            PING,
            PONG,
            DISCONNECT
        }

        private byte[] pingMsgBuffer = new byte[] { (byte)InternalMessages.PING };
        private byte[] pongMsgBuffer = new byte[] { (byte)InternalMessages.PONG };
        private byte[] disconnectMsgBuffer = new byte[] { (byte)InternalMessages.DISCONNECT };

        private enum Mode
        {
            UNDEFINED,
            CLIENT,
            SERVER
        }

        //if we are in server or client mode
        private Mode mode = Mode.UNDEFINED;

        //steam client we are connected to in client mode
        private SteamClient steamClientServer = null;
        private int nextConnectionID = 1;

        private SteamConnectionMap steamConnectionMap = new SteamConnectionMap();

        private Queue<int> steamNewConnections = new Queue<int>();
        private Queue<SteamClient> steamDisconnectedConnections = new Queue<SteamClient>();

        private byte[] serverReceiveBuffer = new byte[NetworkMessage.MaxMessageSize];
        private int maxConnections = 0;

        //These 2 variables are used in Receive if we receive a new connection And a data packet at the same time. The data is queued to be rerurned next time
        private int serverReceiveBufferPendingConnectionID = -1;
        private byte[] serverReceiveBufferPending = null;

        //this is a callback from steam that gets registered and called when the server receives new connections
        private Callback<P2PSessionRequest_t> Callback_OnNewConnection = null;
        private Callback<P2PSessionConnectFail_t> Callback_OnConnectFail = null;

        private int lastInternalMessageFrame = 0;


        public SteamworksNetworkTransport(EP2PSend sendType = EP2PSend.k_EP2PSendReliableWithBuffering)
        {
            if (LogFilter.logInfo) { Debug.Log("Created SteamworksNetworkTransport"); }

            currentSendType = sendType;
        }


        //*********************************** shared stuff


        /**
            * Send data to peer.
            * Use this to send data from one connection to another using the connectionId. Place the data to be sent in the function as a byte array.
            */
        private bool Send(SteamClient steamclient, byte[] buffer, int channel)
        {
            if (buffer == null)
            {
                throw new NullReferenceException("send buffer is not initialized");
            }

            if (steamclient.state != SteamClient.ConnectionState.CONNECTED)
            {
                //Should error
                if (LogFilter.logError) { Debug.LogError("Trying to send data on client thats not connected"); }
                return false;
            }

            if (SteamNetworking.SendP2PPacket(steamclient.steamID, buffer, (uint)buffer.Length, currentSendType, channel))
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
                if (LogFilter.logInfo) { Debug.Log("Handling a new connection from queue"); }

                connectionId = steamNewConnections.Dequeue();
                transportEvent = TransportEvent.Connected;
                return true;
            }

            //first check if we have received any new disconnects and return them as an event
            if (steamDisconnectedConnections.Count > 0)
            {
                if (LogFilter.logInfo) { Debug.Log("Handling a disconnect from queue"); }

                SteamClient steamClient = steamDisconnectedConnections.Dequeue();
                connectionId = steamClient.connectionID;
                transportEvent = TransportEvent.Disconnected;

                //remove the connection from our list
                steamConnectionMap.Remove(steamClient);

                return true;
            }

            //this is a buffer that may have been received at the same time as a new connection
            if (serverReceiveBufferPending != null)
            {
                if (LogFilter.logInfo) { Debug.Log("Handling a postponed message"); }

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
                if (packetSize > NetworkMessage.MaxMessageSize)
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

                        if (steamClient.state == SteamClient.ConnectionState.CONNECTING)
                        {
                            if (LogFilter.logInfo) { Debug.Log("Received a new connection"); }

                            if (packetSize > 0)
                            {
                                if (LogFilter.logInfo) { Debug.Log("Message received with the new connection - postponed message"); }

                                //we need to return the connection event but we also have data to return next time
                                serverReceiveBufferPendingConnectionID = steamClient.connectionID;
                                serverReceiveBufferPending = new byte[packetSize];
                                Array.Copy(serverReceiveBuffer, serverReceiveBufferPending, packetSize);
                            }


                            steamClient.state = SteamClient.ConnectionState.CONNECTED;

                            transportEvent = TransportEvent.Connected;
                            connectionId = steamClient.connectionID;
                            return true;
                        }

                        if (steamClient.state != SteamClient.ConnectionState.CONNECTED)
                        {
                            //we are not currently connected to this client - this shouldnt happen
                            Debug.LogError("Received a message for a client thats not connected");
                            connectionId = -1;
                            transportEvent = TransportEvent.Disconnected;
                            return false;
                        }
                    }
                    catch (KeyNotFoundException)
                    {
                        //This shouldnt happen
                        Debug.LogError("Totally unexpected steam ID sent a message");
                        connectionId = -1;
                        transportEvent = TransportEvent.Disconnected;
                        return false;
                    }

                    //received normal data packet
                    connectionId = steamClient.connectionID;
                    transportEvent = TransportEvent.Data;
                    //for now allocate a new buffer TODO: do we need to do this?
                    data = new byte[packetSize];
                    Array.Copy(serverReceiveBuffer, data, packetSize);

                    return true;

                }
            }

            //nothing available
            connectionId = -1;
            transportEvent = TransportEvent.Disconnected; //they havent disconnected but this is what the LLAPITransport returns here. There is not a "nothing" event
            return false;
        }

        /**
         * Check for internal networked messages like ping or disconnect request
         */
        private void handleInternalMessages()
        {
            if (lastInternalMessageFrame == Time.frameCount)
            {
                return; //already processed this frame
            }
            lastInternalMessageFrame = Time.frameCount;

            int connectionId;
            TransportEvent transportEvent;
            Byte[] data;

            while (Receive(out connectionId, out transportEvent, out data, (int)Channels.SEND_INTERNAL))
            {
                try
                {
                    SteamClient steamClient = steamConnectionMap.fromConnectionID[connectionId];

                    if (transportEvent == TransportEvent.Data && steamClient.state == SteamClient.ConnectionState.CONNECTED)
                    {
                        switch (data[0])
                        {
                            case 0x01:
                                //ping .. send a pong
                                sendInternalPong(steamClient);
                                break;

                            case 0x02:
                                //pong .. update when we last received a pong
                                steamClient.lastPong = Time.time;
                                break;

                            case 0x03:
                                if (LogFilter.logWarn) { Debug.LogWarning("Received an instruction to Disconnect"); }
                                //requested to disconnect
                                if (mode == Mode.CLIENT || mode == Mode.SERVER)
                                {
                                    //disconnect this client
                                    closeSteamConnection(steamClient);
                                }
                                break;

                        }
                    }
                }
                catch (KeyNotFoundException)
                {
                    //shouldnt happen - ignore
                }
            }

            //check all connections to check they are healthy 
            foreach (var connection in steamConnectionMap)
            {
                SteamClient steamClient = connection.Value;

                if (steamClient.state == SteamClient.ConnectionState.CONNECTED)
                {
                    if (Time.time - steamClient.lastPong > PONG_TIMEOUT)
                    {
                        //idle too long - disconnect
                        Debug.LogError("Connection " + steamClient.connectionID + " timed out - going to disconnect");
                        internalDisconnect(steamClient);
                        continue;
                    }
                    if (Time.time - steamClient.lastPing > PING_FREQUENCY)
                    {
                        //time to ping
                        sendInternalPing(steamClient);
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

            SteamNetworking.CloseP2PSessionWithUser(steamClient.steamID);

        }

        /**
         * Tell the connection that you are going to disconnect (so it can promptly disconnect you) and then close our connection
         */
        private void internalDisconnect(SteamClient steamClient)
        {
            if (steamClient.state == SteamClient.ConnectionState.CONNECTED)
            {
                Send(steamClient, disconnectMsgBuffer, (int)Channels.SEND_INTERNAL);
            }

            closeSteamConnection(steamClient);
        }

        private void sendInternalPing(SteamClient steamClient)
        {
            if (LogFilter.logInfo) { Debug.Log("Send Ping to connection " + steamClient.connectionID); }

            steamClient.lastPing = Time.time;
            Send(steamClient, pingMsgBuffer, (int)Channels.SEND_INTERNAL);
        }

        private void sendInternalPong(SteamClient steamClient)
        {
            if (LogFilter.logInfo) { Debug.Log("Send Pong to connection " + steamClient.connectionID); }

            Send(steamClient, pongMsgBuffer, (int)Channels.SEND_INTERNAL);
        }

        private void setupSteamCallbacks()
        {
            if (SteamManager.Initialized)
            {
                if (Callback_OnNewConnection == null)
                {
                    Callback_OnNewConnection = Callback<P2PSessionRequest_t>.Create(OnNewConnection);
                    Callback_OnConnectFail = Callback<P2PSessionConnectFail_t>.Create(OnConnectFail);
                }
                else
                {
                    Debug.LogError("Already listening for steam connections");
                }
            }
            else
            {
                Debug.LogError("STEAM NOT Initialized");
                return;
            }

        }

        private void OnConnectFail(P2PSessionConnectFail_t result)
        {
            if (LogFilter.logWarn) { Debug.LogWarning("Connection failed or closed Steam ID " + result.m_steamIDRemote); }

            if (mode == Mode.CLIENT)
            {
                ClientDisconnect();
            }
            else if (mode == Mode.SERVER)
            {
                //one of the clients has disconnected
                SteamClient steamClient;
                try
                {
                    steamClient = steamConnectionMap.fromSteamID[result.m_steamIDRemote];
                    closeSteamConnection(steamClient);
                }
                catch (KeyNotFoundException)
                {
                    steamClient = null;
                }
            }
        }

        private void OnNewConnection(P2PSessionRequest_t result)
        {
            //this happens when a user trys to connect to this machine and we havent agreed to accept their connection recently

            CSteamID steamID = result.m_steamIDRemote;

            //check if we have a connection already stored for this steam account
            try
            {
                SteamClient steamClient = steamConnectionMap.fromSteamID[steamID];
                if (steamClient.state == SteamClient.ConnectionState.CONNECTED || steamClient.state == SteamClient.ConnectionState.CONNECTING)
                {
                    //accept! We thought they were anyway
                    SteamNetworking.AcceptP2PSessionWithUser(steamID);
                    return;
                }
            }
            catch (KeyNotFoundException)
            {
            }

            if ((mode == Mode.SERVER && getNumberOfActiveConnection() >= maxConnections) || (mode == Mode.CLIENT && steamID != steamClientServer.steamID))
            {
                //TODO error report?
                //either too many people connecting to server Or we are a client! Dont accept

                return;
            }

            //just accept!
            SteamNetworking.AcceptP2PSessionWithUser(steamID);

            //new connection id
            int connectionId = nextConnectionID++;
            steamConnectionMap.Add(steamID, connectionId, SteamClient.ConnectionState.CONNECTING);

            //Reply with an empty packet to also confirm connection to client.. this will mean the client receives a "connected" message as this isnt TCP!
            SteamNetworking.SendP2PPacket(steamID, null, 0, EP2PSend.k_EP2PSendReliable, (int)Channels.SEND_TO_CLIENT);

            //we have to queue this connection up so we can let the ReceiveFromHost method return the new connection
            steamNewConnections.Enqueue(connectionId);
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

            setupSteamCallbacks();

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

            int connectionId = nextConnectionID++;
            steamClientServer = steamConnectionMap.Add(steamID, connectionId, SteamClient.ConnectionState.CONNECTING);

            //Send an empty message to the steam client - this requests a connection with them
            SteamNetworking.SendP2PPacket(steamID, null, 0, EP2PSend.k_EP2PSendReliable, (int)Channels.SEND_TO_SERVER);
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

            if(steamClientServer.state == SteamClient.ConnectionState.DISCONNECTING )
            {
                if (LogFilter.logInfo) { Debug.Log("We are currently trying to disconnect - so, disconnect"); }
                transportEvent = TransportEvent.Disconnected;

                //remove the connection from our list
                steamConnectionMap.Remove(steamClientServer);

                //make sure we dont communicate any more
                SteamNetworking.CloseP2PSessionWithUser(steamClientServer.steamID);

                //we are done being a client
                steamClientServer = null;
                mode = Mode.UNDEFINED;
                return true;
            }

            //if we are not connecting or connected then we shouldnt get getting messages - exit here
            if (steamClientServer.state != SteamClient.ConnectionState.CONNECTED && steamClientServer.state != SteamClient.ConnectionState.CONNECTING)
            {
                return false;
            }

            handleInternalMessages();

            int connectionId;
            return ReceiveAndProcessEvents(out connectionId, out transportEvent, out data, (int)Channels.SEND_TO_CLIENT);
        }

        public float ClientGetRTT()
        {
            return 0; //TODO
        }

        public bool ClientSend(byte[] data)
        {
            if (!ClientConnected())
            {
                return false;
            }

            return Send(steamClientServer, data, (int)Channels.SEND_TO_SERVER);
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
            setupSteamCallbacks();

        }

        private int getNumberOfActiveConnection()
        {
            int count = 0;

            foreach (var con in steamConnectionMap)
            {
                if (con.Value.state == SteamClient.ConnectionState.CONNECTED || con.Value.state == SteamClient.ConnectionState.CONNECTING)
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
            return mode == Mode.SERVER && Callback_OnNewConnection != null;
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

            return ReceiveAndProcessEvents(out connectionId, out transportEvent, out data, (int)Channels.SEND_TO_SERVER);
        }

        public bool ServerSend(int connectionId, byte[] data)
        {
            try
            {
                SteamClient steamClient = steamConnectionMap.fromConnectionID[connectionId];

                return Send(steamClient, data, (int)Channels.SEND_TO_CLIENT);
            }
            catch (KeyNotFoundException)
            {
                //we have no idea who this connection is
                Debug.LogError("Trying to send data on client thats not known");

                return false;
            }
            
        }

        public void ServerStop()
        {
            foreach (var connection in steamConnectionMap)
            {
                SteamClient steamClient = connection.Value;
                internalDisconnect(steamClient);
                
            }

            mode = Mode.UNDEFINED;
        }
        
        public void Shutdown()
        {
            if (LogFilter.logInfo) { Debug.Log("Shutdown SteamworksNetworkTransport"); }

            if (mode == Mode.SERVER)
            {
                ServerStop();
            } else if (mode == Mode.CLIENT)
            {
                ClientDisconnect();
            }
        }





    }

}
