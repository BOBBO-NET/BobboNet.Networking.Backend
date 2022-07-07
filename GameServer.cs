using LiteNetLib;
using LiteNetLib.Utils;
using BobboNet.Networking.Messages;
using BobboNet.Networking.Util;

namespace BobboNet.Networking.Backend;

public abstract class GameServer<SelfType, PlayerType, PlayerUpdate> : BackgroundService 
    where SelfType : GameServer<SelfType, PlayerType, PlayerUpdate>
    where PlayerType : GamePlayer<PlayerUpdate>, new()
    where PlayerUpdate : class, INetSerializable, new()
{
    //
    //  Properties
    //

    // The internal network manager, which is used to send & recieve data.
    public NetManager Network { get; private set; }

    // The logger to use when writing logs from this class.
    public ILogger<SelfType> Logger { get; private set; }

    // The port to host the server on.
    public int NetworkPort { get; set; } = 30330;

    // How many players this server can hold at once.
    public int MaxPlayers { get; set; } = 10;

    // How fast the server should update, in millisconds.
    public int TickRate { get; set; } = 15;

    //
    //  Variables
    //

    private NetPacketProcessor packetProcessor;
    private Dictionary<int, PlayerType> players;

    //
    //  Construction & Config
    //

    public GameServer(ILogger<SelfType> logger)
    {
        this.Logger = logger;
        this.Network = CreateServer();
        this.packetProcessor = CreatePacketProcessor();
        this.players = new Dictionary<int, PlayerType>(MaxPlayers);
    }

    private NetManager CreateServer()
    {
        // Create the listener, and assign all desired methods
        EventBasedNetListener listener = new EventBasedNetListener();
        listener.ConnectionRequestEvent += OnClientWantsToConnect;
        listener.PeerConnectedEvent += OnClientConnected;
        listener.PeerDisconnectedEvent += OnClientDisconnected;
        listener.NetworkReceiveEvent += OnNetworkReceive;

        // Create the server
        NetManager newServer = new NetManager(listener);
        newServer.AutoRecycle = true;
        newServer.ChannelsCount = NetworkChannelIDs.ChannelCount;

        return newServer;
    }

    // A helper function that creates a new packet processor and uses abstract methods to register
    // required data types and message hooks for the server.
    private NetPacketProcessor CreatePacketProcessor() 
    {
        NetPacketProcessor processor = new NetPacketProcessor();

        // Register nested data types
        processor.RegisterBobboNetNestedTypes();
        processor.RegisterNestedType<StandardMessages<PlayerUpdate>.SM_BatchPlayerUpdates>(() => new());
        processor.RegisterNestedType<StandardMessages<PlayerUpdate>.SM_InitialPlayerUpdates>(() => new());
        processor.RegisterNestedType<StandardMessages<PlayerUpdate>.SM_PlayerJoin>(() => new());
        processor.RegisterNestedType<StandardMessages<PlayerUpdate>.SM_PlayerLeave>(() => new());
        OnRegisterPacketTypes(processor);

        // Register message hooks
        processor.SubscribeReusable<PlayerUpdate, NetPeer>(OnMessagePlayerUpdate);
        OnRegisterPacketHooks(processor);
        
        
        return processor;
    }

    //
    //  LiteNetLib Events
    //

    // Called when a new client is requesting to connect to us
    private void OnClientWantsToConnect(ConnectionRequest request)
    {
        // If we have too many clients, REJECT and EXIT EARLY
        if(Network.ConnectedPeersCount > MaxPlayers)
        {
            request.Reject();
            Logger.LogInformation($"New client rejected because the server is full...");
            return;
        }

        // Optionally, ask the subclass if we should approve this connection too
        if(!OnConnectionRequest(request)) 
        {
            request.Reject();
            Logger.LogInformation($"New client rejected...");
            return;
        }


        // OTHERWISE... accept the connection!
        request.Accept();
        Logger.LogInformation($"New client accepted...");
    }

    // Called when a new client connects successfully
    private void OnClientConnected(NetPeer peer)
    {
        // Tell other clients about the new player.
        StandardMessages<PlayerUpdate>.SM_PlayerJoin playerJoinMessage = new StandardMessages<PlayerUpdate>.SM_PlayerJoin();
        playerJoinMessage.Id = peer.Id;
        Network.SendToAll(packetProcessor.Write(playerJoinMessage), NetworkChannelIDs.ReliableOrdered, DeliveryMethod.ReliableOrdered, peer);  // (ignore this peer)

        // Get the current entire player state
        StandardMessages<PlayerUpdate>.SM_InitialPlayerUpdates initialUpdatesMessage = new StandardMessages<PlayerUpdate>.SM_InitialPlayerUpdates();
        initialUpdatesMessage.BatchUpdates.Updates = GetEntirePlayerState();

        // Create new player info and configure it
        PlayerType newPlayer = new PlayerType();
        newPlayer.Setup(peer);
        players.Add(peer.Id, newPlayer);

        // Send the total player state to the client!
        peer.Send(packetProcessor.Write(initialUpdatesMessage), NetworkChannelIDs.ReliableOrdered, DeliveryMethod.ReliableOrdered);

        Logger.LogInformation($"Client [{peer.Id}] connected!");
    }

    // Called when an existing client disconnects
    private void OnClientDisconnected(NetPeer peer,  DisconnectInfo disconnectInfo)
    {
        // Remove the instance for this player
        players.Remove(peer.Id);

        // Tell other clients that a player has left
        StandardMessages<PlayerUpdate>.SM_PlayerLeave leaveMessage = new StandardMessages<PlayerUpdate>.SM_PlayerLeave();
        leaveMessage.Id = peer.Id;
        Network.SendToAll(packetProcessor.Write(leaveMessage), NetworkChannelIDs.ReliableOrdered, DeliveryMethod.ReliableOrdered, peer);  // (ignore this peer)

        Logger.LogInformation($"Client [{peer.Id}] disconnected (Reason: {disconnectInfo.Reason}).");
    }

    // Called when a connected client sends network data to us
    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod)
    {
        Logger.LogDebug($"Got {reader.AvailableBytes} from Client [{peer.Id}].");

        // Use the packet processor to read ALL data
        try
        {
            packetProcessor.ReadAllPackets(reader, peer);
        }
        catch(Exception exception)
        {
            Logger.LogError($"Failed to read bytes from Client [{peer.Id}]: '{exception}'");
        }
    }

    //
    //  Message Events
    //

    private void OnMessagePlayerUpdate(PlayerUpdate updateData, NetPeer peer)
    {
        // Find the player object and store it. If there's no player with this ID, exit early and return false.
        PlayerType? player;
        if(!players.TryGetValue(peer.Id, out player)) return;

        // OTHERWISE...
        // ...we've found a real player. Let's update them!
        player.ApplyPlayerUpdate(updateData);
    }

    //
    //  Event-Loop
    //

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Begin the server...
        Network.Start(NetworkPort);
        Logger.LogInformation($"Server started on port {NetworkPort}...!");

        // Loop, running the server tick
        while(!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TickRate);
            Network.PollEvents();
            RunPlayerUpdateTick();
            OnServerTick();
        }
        
        // ...and once the server should shut down...
        Network.Stop();
        Logger.LogInformation($"Server stopped.");
    }

    // Runs the logic that calls ShouldPlayerUpdate and CommitToPlayerUpdate on all player objects,
    // and sends out player updates to clients in bulk.
    private void RunPlayerUpdateTick()
    {
        foreach(PlayerType player in players.Values)
        {
            if(player.ShouldPlayerUpdate())
            {
                PlayerUpdate updateMessage = player.CommitToPlayerUpdate();
                Network.SendToAll(packetProcessor.Write<PlayerUpdate>(updateMessage), NetworkChannelIDs.Unreliable, DeliveryMethod.Unreliable, player.GetPeer());
            }
        }
    }


    //
    //  Private Methods
    //

    private PlayerUpdate[] GetEntirePlayerState()
    {
        PlayerUpdate[] updates = new PlayerUpdate[players.Count];

        // Loop through all players...
        int index = 0;
        foreach(PlayerType player in players.Values)
        {
            // ...and add them to the array above!
            updates[index++] = player.CreatePlayerUpdate();
        }

        // Once all states have been assigned, we're done!
        return updates;
    }

    //
    //  Virtual Methods
    //

    protected virtual void OnRegisterPacketTypes(NetPacketProcessor packetProcessor) {}
    protected virtual void OnRegisterPacketHooks(NetPacketProcessor packetProcessor) {}

    // Called when a new client wants to connect, after verifying that there is enough space
    // for the client. Having this return true approves the connection. Having this return false
    // rejects the connection.
    protected virtual bool OnConnectionRequest(ConnectionRequest request) { return true; }

    // Called every server tick, after player updates have been calculated & sent.
    protected virtual void OnServerTick() {}
}