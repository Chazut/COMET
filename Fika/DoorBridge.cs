using System;
using System.Collections.Generic;
using BepInEx;
using Comet.Api;
using Fika.Core.Main.Players;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace Comet.Fika;

[BepInPlugin("com.chazut.comet.fika","COMET Fika door transport","0.1.0")]
[BepInDependency("com.chazut.comet")]
[BepInDependency("com.fika.core")]
public sealed class DoorBridge : BaseUnityPlugin
{
    private static DoorBridge? instance;
    private IFikaNetworkManager? network;
    private bool host;
    private string session="";
    private long revision;
    private float helloAt;
    private float applyAt;
    private readonly HashSet<NetPeer> peers=new(),ready=new();
    private readonly Dictionary<string,DoorPacket> latest=new(),queued=new();
    private readonly Dictionary<string,long> received=new();
    private readonly Dictionary<string,float> expires=new();
    private readonly List<string> consumed=new();
    private Harmony? harmony;
    private long generation;
    private Func<bool>? readyCallback;
    private Func<EFT.Player,Vector3,bool>? teleportCallback;
    private void Awake()
    {
        instance=this;FikaEventDispatcher.OnFikaEvent+=OnEvent;
        DoorMotionNetwork.Changed+=Changed;DoorMotionNetwork.RaidEnded+=EndRaid;
        DoorMotionNetwork.Ready=readyCallback=()=>network!=null&&host&&peers.SetEquals(ready);
        MovementRecoveryNetwork.Teleport=teleportCallback=Relocate;
        harmony=new Harmony("com.chazut.comet.fika");
        harmony.Patch(AccessTools.DeclaredMethod(typeof(FikaPlayer),nameof(FikaPlayer.ExecuteInteraction)),
            postfix:new HarmonyMethod(typeof(DoorBridge),nameof(AfterInteraction)));
    }
    private void OnEvent(FikaEvent e)
    {
        switch(e)
        {
            case FikaNetworkManagerCreatedEvent created:
                Reset();network=created.Manager;host=network is FikaServer;session=host?Guid.NewGuid().ToString("N"):"";
                var epoch=generation;
                network.RegisterPacket<DoorPacket,NetPeer>((packet,peer)=>{if(epoch==generation)Receive(packet,peer);});break;
            case FikaNetworkManagerDestroyedEvent destroyed when destroyed.Manager==network:Reset();break;
            case PeerConnectedEvent connected when connected.NetworkManager==network:
                peers.Add(connected.Peer);helloAt=0;break;
            case PeerDisconnectedEvent disconnected when disconnected.NetworkManager==network:
                peers.Remove(disconnected.Peer);ready.Remove(disconnected.Peer);
                if(!host){session="";received.Clear();queued.Clear();expires.Clear();}break;
        }
    }
    private void Reset()
    {
        var previous=network;network=null;generation++;
        session="";revision=0;helloAt=0;host=false;
        peers.Clear();ready.Clear();latest.Clear();queued.Clear();received.Clear();expires.Clear();consumed.Clear();applyAt=0;
        previous?.UnregisterPacket<DoorPacket>();
    }
    private void EndRaid()=>Reset();
    private bool Relocate(EFT.Player player,Vector3 target)
    {
        if(!host||network==null||!peers.SetEquals(ready)||player is not FikaBot bot||bot.BotPacketSender==null
            ||!bot.BotPacketSender.SendState||!bot.BotPacketSender.isActiveAndEnabled||bot.HealthController?.IsAlive!=true
            ||!float.IsFinite(target.x)||!float.IsFinite(target.y)||!float.IsFinite(target.z)||Vector3.Distance(bot.Position,target)>2)return false;
        bot.Teleport(target);
        // Fika's authoritative bot snapshot serializes Player.Position. Observed clients apply it through
        // their normal snapshot/interpolation pipeline; never issue a second recovery decision there.
        // Do not use ClearSnapshotterPacket: it also clears the remote health bar's effects.
        bot.BotPacketSender.SendPlayerState();return true;
    }
    private void Changed(string door,int state,float angle)
    {
        if(!host||network==null)return;
        if(latest.Count>=512&&!latest.ContainsKey(door))return;
        var packet=new DoorPacket{Kind=2,Session=session,Door=door,Revision=++revision,State=state,Angle=angle};
        latest[door]=queued[door]=packet;
    }
    private static void AfterInteraction(WorldInteractiveObject door)
    {
        var p=instance;if(p==null||door==null||p.network==null)return;
        if(p.host)
        {
            p.latest.Remove(door.Id);
            p.queued[door.Id]=new(){Kind=3,Session=p.session,Door=door.Id,Revision=++p.revision};
        }
        else {p.queued.Remove(door.Id);p.expires.Remove(door.Id);}
    }
    private void Receive(DoorPacket packet,NetPeer peer)
    {
        if(network==null)return;
        if(host)
        {
            // Clients can request a snapshot, never author a door state.
            if(packet.Kind!=0||!peers.Contains(peer))return;
            ready.Add(peer);
            var welcome=new DoorPacket{Kind=1,Session=session};network.SendDataToPeer(ref welcome,DeliveryMethod.ReliableOrdered,peer);
            foreach(var state in latest.Values){var copy=state;network.SendDataToPeer(ref copy,DeliveryMethod.ReliableOrdered,peer);}
            return;
        }
        if(network is not FikaClient client||peer!=client.ServerConnection)return;
        if(packet.Kind==1)
        {
            if(packet.Session.Length!=32||session.Length>0&&packet.Session!=session)return;
            if(packet.Session!=session){session=packet.Session;received.Clear();queued.Clear();expires.Clear();}
            return;
        }
        if(packet.Session!=session||session.Length==0||packet.Kind is not (2 or 3)||string.IsNullOrEmpty(packet.Door)
            ||packet.Revision<=0||received.TryGetValue(packet.Door,out var previous)&&packet.Revision<=previous)return;
        if(received.Count>=512&&!received.ContainsKey(packet.Door))return;
        received[packet.Door]=packet.Revision;
        if(packet.Kind==3){queued.Remove(packet.Door);expires.Remove(packet.Door);return;}
        queued[packet.Door]=packet;
        expires[packet.Door]=Time.unscaledTime+120;applyAt=0;
    }
    private void Update()
    {
        if(network==null)return;
        try
        {
            if(!host&&session.Length==0&&Time.unscaledTime>=helloAt)
            {var hello=new DoorPacket{Kind=0};network.SendData(ref hello,DeliveryMethod.ReliableOrdered);helloAt=Time.unscaledTime+1;}
            if(queued.Count==0)return;
            if(!host&&Time.unscaledTime<applyAt)return;
            consumed.Clear();
            // Defer host sends until after Fika's native ExecuteInteraction packet on the same ordered channel.
            foreach(var value in queued.Values)
            {
                if(host){foreach(var peer in ready){var copy=value;network.SendDataToPeer(ref copy,DeliveryMethod.ReliableOrdered,peer);}}
                else
                {
                    if(!DoorMotionNetwork.DoorAvailable(value.Door)&&Time.unscaledTime<expires[value.Door])continue;
                    // Once a door exists, a rejected local interaction wins; do not retry over the player.
                    DoorMotionNetwork.Apply(value.Door,value.State,value.Angle);
                }
                consumed.Add(value.Door);
            }
            foreach(var door in consumed){queued.Remove(door);expires.Remove(door);}consumed.Clear();
            applyAt=Time.unscaledTime+.1f;
        }
        catch(Exception ex){queued.Clear();expires.Clear();Logger.LogWarning("COMET door sync: "+ex.Message);}
    }
    private void OnDestroy()
    {
        FikaEventDispatcher.OnFikaEvent-=OnEvent;DoorMotionNetwork.Changed-=Changed;DoorMotionNetwork.RaidEnded-=EndRaid;
        if(ReferenceEquals(DoorMotionNetwork.Ready,readyCallback))DoorMotionNetwork.Ready=null;
        if(ReferenceEquals(MovementRecoveryNetwork.Teleport,teleportCallback))MovementRecoveryNetwork.Teleport=null;
        readyCallback=null;teleportCallback=null;
        try{Reset();}finally{try{harmony?.UnpatchSelf();}finally{if(instance==this)instance=null;}}
    }
}
