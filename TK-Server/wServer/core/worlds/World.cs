﻿using common.database;
using common.resources;
using dungeonGen;
using dungeonGen.templates;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using wServer.core.objects;
using wServer.core.objects.vendors;
using wServer.core.terrain;
using wServer.core.worlds.impl;
using wServer.core.worlds.logic;
using wServer.networking;
using wServer.networking.packets.outgoing;

namespace wServer.core.worlds
{
    public class World
    {
        public const int ClothBazaar = -10;
        public const int GuildHall = -7;
        public const int MarketPlace = -15;
        public const int Nexus = -2;
        public const int NexusExplanation = -3;
        public const int Realm = 1;
        public const int Test = -6;
        public const int Tutorial = -1;
        public const int Vault = -4;
        public const int Poseidon = -420;

        protected static readonly Random Rand = new Random((int)DateTime.Now.Ticks);

        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static int _entityInc;

        private long _elapsedTime;
        private CoreServerManager _manager;
        private int _totalConnects;

        public int Id { get; }
        public string IdName { get; set; }
        public string DisplayName { get; set; }
        public WorldResourceInstanceType InstanceType { get; private set; }
        public bool Persist { get; private set; }
        public int MaxPlayers { get; private set; }

        public bool IsNotCombatMapArea => Id == Nexus || Id == Vault || Id == GuildHall || Id == NexusExplanation;
        public bool IsRealm { get; set; }
        public bool AllowTeleport { get; protected set; }
        public int Background { get; protected set; }
        public byte Blocking { get; protected set; }
        public string Music { get; set; }
        public bool Closed { get; set; }
        public int Difficulty { get; protected set; }
        public bool Deleted { get; protected set; }
        public ConcurrentDictionary<int, Container> Containers { get; private set; }
        public ConcurrentDictionary<int, Enemy> Enemies { get; private set; }

        public CollisionMap<Entity> EnemiesCollision { get; private set; }

        public CoreServerManager Manager { get; set; }

        public Wmap Map { get; private set; }
        
        
        public ConcurrentDictionary<int, Pet> Pets { get; private set; }
        public ConcurrentDictionary<int, Player> Players { get; private set; }
        public CollisionMap<Entity> PlayersCollision { get; private set; }
        public ConcurrentDictionary<Tuple<int, byte>, Projectile> Projectiles { get; private set; }
        public ConcurrentDictionary<int, Enemy> Quests { get; private set; }
        public bool ShowDisplays { get; protected set; }
        public ConcurrentDictionary<int, Enemy> SpecialEnemies { get; private set; }
        public ConcurrentDictionary<int, StaticObject> StaticObjects { get; private set; }
        public List<WorldTimer> Timers { get; private set; }
        public int TotalConnects { get { return _totalConnects; } }

        public readonly WorldBranch WorldBranch;

        public World(int id, WorldResource resource)
        {
            Setup();
            Id = id;
            IdName = resource.DisplayName;
            DisplayName = resource.DisplayName;
            Difficulty = resource.Difficulty;
            Background = resource.Background;
            MaxPlayers = resource.Capacity;
            InstanceType = resource.Instance;
            Persist = resource.Persists;
            AllowTeleport = true;// !resource.restrictTp;
            ShowDisplays = Id == -2;// resource.showDisplays;
            Blocking = resource.VisibilityType;

            IsRealm = false;

            var rnd = new Random();
            if (resource.Music.Count > 0)
                Music = resource.Music[rnd.Next(0, resource.Music.Count)];
            else
                Music = "sorc";

            WorldBranch = new WorldBranch(this);
        }

        public virtual bool AllowedAccess(Client client) => !Closed || client.Account.Admin;

        public void Broadcast(OutgoingMessage outgoingMessage, PacketPriority priority = PacketPriority.Normal)
            => PlayersBroadcastAsParallel(_ => _.Client.SendPacket(outgoingMessage, priority));

        public void BroadcastIfVisible(OutgoingMessage outgoingMessage, Position worldPosData, PacketPriority priority = PacketPriority.Normal)
            => PlayersBroadcastAsParallel(_ =>
            {
                if (_.Dist(worldPosData) <= 15d)
                    _.Client.SendPacket(outgoingMessage, priority);
            });

        public void BroadcastIfVisible(OutgoingMessage outgoingMessage, Entity broadcaster, PacketPriority priority = PacketPriority.Normal)
            => PlayersBroadcastAsParallel(_ =>
            {
                if (_.Dist(broadcaster) <= 15d)
                    _.Client.SendPacket(outgoingMessage, priority);
            });

        public void BroadcastIfVisibleExclude(OutgoingMessage outgoingMessage, Entity broadcaster, Entity exclude, PacketPriority priority = PacketPriority.Normal)
            => PlayersBroadcastAsParallel(_ =>
            {
                if (_.Id != exclude.Id && _.Dist(broadcaster) <= 15d)
                    _.Client.SendPacket(outgoingMessage, priority);
            });

        public void BroadcastToPlayer(OutgoingMessage outgoingMessage, int playerId, PacketPriority priority = PacketPriority.Normal)
            => PlayersBroadcastAsParallel(_ =>
            {
                if (_.Id == playerId)
                    _.Client.SendPacket(outgoingMessage, priority);
            });

        public void BroadcastToPlayers(OutgoingMessage outgoingMessage, List<int> playerIds, PacketPriority priority = PacketPriority.Normal)
            => PlayersBroadcastAsParallel(_ =>
            {
                if (playerIds.Contains(_.Id))
                    _.Client.SendPacket(outgoingMessage, priority);
            });

        public void ChatReceived(Player player, string text)
        {
            foreach (var en in Enemies)
                en.Value.OnChatTextReceived(player, text);

            foreach (var en in StaticObjects)
                en.Value.OnChatTextReceived(player, text);
        }

        public virtual int EnterWorld(Entity entity)
        {
            if (entity is Player)
            {
                entity.Id = GetNextEntityId();
                entity.Init(this);

                Players.TryAdd(entity.Id, entity as Player);
                PlayersCollision.Insert(entity);

                Interlocked.Increment(ref _totalConnects);
            }
            else if (entity is Enemy)
            {
                entity.Id = GetNextEntityId();
                entity.Init(this);

                Enemies.TryAdd(entity.Id, entity as Enemy);
                EnemiesCollision.Insert(entity);

                if (entity.ObjectDesc.SpecialEnemy)
                    SpecialEnemies.TryAdd(entity.Id, entity as Enemy);

                if (entity.ObjectDesc.Quest)
                    Quests.TryAdd(entity.Id, entity as Enemy);
            }
            else if (entity is Projectile)
            {
                entity.Init(this);

                var prj = entity as Projectile;

                Projectiles[new Tuple<int, byte>(prj.ProjectileOwner.Self.Id, prj.ProjectileId)] = prj;
            }
            else if (entity is Container)
            {
                entity.Id = GetNextEntityId();
                entity.Init(this);

                Containers.TryAdd(entity.Id, entity as Container);
            }
            else if (entity is StaticObject)
            {
                entity.Id = GetNextEntityId();
                entity.Init(this);

                StaticObjects.TryAdd(entity.Id, entity as StaticObject);

                if (entity is Decoy)
                    PlayersCollision.Insert(entity);
                else
                    EnemiesCollision.Insert(entity);
            }
            else if (entity is Pet)
            {
                entity.Id = GetNextEntityId();
                entity.Init(this);

                Pets.TryAdd(entity.Id, entity as Pet);

                PlayersCollision.Insert(entity);
            }

            return entity.Id;
        }

        public long GetAge() => _elapsedTime;

        public string GetDisplayName() => DisplayName != null && DisplayName.Length > 0 ? DisplayName : IdName;

        public Entity GetEntity(int id)
        {
            if (Players.TryGetValue(id, out var ret1))
                return ret1;

            if (Enemies.TryGetValue(id, out var ret2))
                return ret2;

            if (StaticObjects.TryGetValue(id, out var ret3))
                return ret3;

            if (Containers.TryGetValue(id, out var ret4))
                return ret4;

            return null;
        }

        public virtual World CreateInstance(Client client)
        {
            return null;

            //DynamicWorld.TryGetWorld(_manager.Resources.Worlds[Name], client, out World world);

            //if (world == null)
            //    world = new World(_manager.Resources.Worlds[Name]);


            //return Manager.WorldManager.CreateNewWorld(world);
        }

        public int GetNextEntityId() => Interlocked.Increment(ref _entityInc);

        public IEnumerable<Player> GetPlayers()
        {
            foreach (var player in Players)
                if (player.Value != null)
                    yield return player.Value;
        }

        public Projectile GetProjectile(int objectId, int bulletId)
        {
            var entity = GetEntity(objectId);

            return entity != null ? ((IProjectileOwner)entity).Projectiles[bulletId] : Projectiles.SingleOrDefault(p => p.Value.ProjectileOwner.Self.Id == objectId && p.Value.ProjectileId == bulletId).Value;
        }

        public Position? GetRegionPosition(TileRegion region)
        {
            if (Map.Regions.All(t => t.Value != region))
                return null;

            var reg = Map.Regions.Single(t => t.Value == region);

            return new Position() { X = reg.Key.X, Y = reg.Key.Y };
        }

        public virtual KeyValuePair<IntPoint, TileRegion>[] GetSpawnPoints() => Map.Regions.Where(t => t.Value == TileRegion.Spawn).ToArray();

        public Player GetUniqueNamedPlayer(string name)
        {
            if (Database.GuestNames.Contains(name))
                return null;

            foreach (var i in Players.Values)
                if (i.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase))
                {
                    if (!i.NameChosen && !(this is TestWorld))
                        Manager.Database.ReloadAccount(i.Client.Account);

                    if (i.Client.Account.NameChosen)
                        return i;
                    break;
                }

            return null;
        }

        public bool IsPassable(double x, double y, bool spawning = false)
        {
            var x_ = (int)x;
            var y_ = (int)y;

            if (!Map.Contains(x_, y_))
                return false;

            var tile = Map[x_, y_];

            if (tile.TileDesc.NoWalk)
                return false;

            if (tile.ObjType != 0 && tile.ObjDesc != null)
                if (tile.ObjDesc.FullOccupy || tile.ObjDesc.EnemyOccupySquare || (spawning && tile.ObjDesc.OccupySquare))
                    return false;

            return true;
        }

        public bool IsPlayersMax() => Players.Count >= MaxPlayers;

        public virtual void LeaveWorld(Entity entity)
        {
            if (entity is Player)
            {
                Players.TryRemove(entity.Id, out Player dummy);
                PlayersCollision.Remove(entity);

                // if in trade, cancel it...
                if (dummy != null && dummy.tradeTarget != null)
                    dummy.CancelTrade();

                if (dummy != null && dummy.Pet != null)
                    LeaveWorld(dummy.Pet);
            }
            else if (entity is Enemy)
            {
                Enemies.TryRemove(entity.Id, out Enemy dummy);
                EnemiesCollision.Remove(entity);

                if (entity.ObjectDesc.SpecialEnemy)
                    SpecialEnemies.TryRemove(entity.Id, out dummy);
                if (entity.ObjectDesc.Quest)
                    Quests.TryRemove(entity.Id, out dummy);
            }
            else if (entity is Projectile)
            {
                var p = entity as Projectile;

                Projectiles.TryRemove(new Tuple<int, byte>(p.ProjectileOwner.Self.Id, p.ProjectileId), out p);
            }
            else if (entity is Container)
                Containers.TryRemove(entity.Id, out Container dummy);
            else if (entity is StaticObject)
            {
                StaticObjects.TryRemove(entity.Id, out StaticObject dummy);

                if (entity is Decoy)
                    PlayersCollision.Remove(entity);
                else
                    EnemiesCollision.Remove(entity);
            }
            else if (entity is Pet)
            {
                Pets.TryRemove(entity.Id, out Pet dummy);

                PlayersCollision.Remove(entity);
            }

            entity.Destroy();
        }

        public void PlayersBroadcastAsParallel(Action<Player> action)
        {
            var players = GetPlayers();
            players.AsParallel().Select(_ =>
            {
                action.Invoke(_);
                return _;
            }).ToArray();
        }

        public void QuakeToWorld(World newWorld)
        {
            if (!Persist || this is RealmWorld)
                Closed = true;

            Broadcast(new ShowEffect()
            {
                EffectType = EffectType.Earthquake
            }, PacketPriority.Low);
            Timers.Add(new WorldTimer(8000, (w, t) =>
            {
                var rcpNotPaused = new Reconnect()
                {
                    Host = "",
                    Port = Manager.ServerConfig.serverInfo.port,
                    GameId = newWorld.Id,
                    Name = newWorld.DisplayName
                };

                var rcpPaused = new Reconnect()
                {
                    Host = "",
                    Port = Manager.ServerConfig.serverInfo.port,
                    GameId = Nexus,
                    Name = "Nexus"
                };

                w.PlayersBroadcastAsParallel(_ =>
                    _.Client.Reconnect(
                        _.HasConditionEffect(ConditionEffects.Paused)
                            ? rcpPaused
                            : rcpNotPaused
                    )
                );
            }));

            if (!Persist)
                Timers.Add(new WorldTimer(20000, (w2, t2) =>
                {
                    // to ensure people get kicked out of world
                    w2.PlayersBroadcastAsParallel(_ =>
                        _.Client.Disconnect("QuakeToWorld")
                    );
                }));
        }

        public void WorldAnnouncement(string msg)
        {
            var announcement = string.Concat("<ANNOUNCMENT> ", msg);

            foreach (var i in Players)
                i.Value.SendInfo(announcement);
        }

        protected void FromDungeonGen(int seed, DungeonTemplate template)
        {
            var gen = new Generator(seed, template);
            gen.Generate();

            var ras = new Rasterizer(seed, gen.ExportGraph());
            ras.Rasterize();

            var dTiles = ras.ExportMap();

            if (Map == null)
            {
                Map = new Wmap(Manager.Resources.GameData);
                Interlocked.Add(ref _entityInc, Map.Load(dTiles, _entityInc));
            }
            else
                Map.ResetTiles();

            InitMap();
        }

        protected void FromWorldMap(Stream dat)
        {
            if (Map == null)
            {
                Map = new Wmap(Manager.Resources.GameData);
                Interlocked.Add(ref _entityInc, Map.Load(dat, _entityInc));
            }
            else
                Map.ResetTiles();

            InitMap();
        }


        private Random Random = new Random();
        public bool LoadMapFromData(WorldResource worldResource)
        {
            var data = Manager.Resources.GameData.GetWorldData(worldResource.MapJM[Random.Next(0, worldResource.MapJM.Count)]);
            if (data == null)
                return false;
            FromWorldMap(new MemoryStream(data));
            return true;
        }

        public virtual void Init()
        {
            // initialize shops
            foreach (var shop in MerchantLists.Shops)
            {
                if (shop.Value.Item1 == null)
                    continue;

                var shopItems = new List<ISellableItem>(shop.Value.Item1);
                var mLocations = Map.Regions.Where(r => shop.Key == r.Value).Select(r => r.Key).ToArray();

                if (shopItems.Count <= 0 || shopItems.All(i => i.ItemId == ushort.MaxValue))
                    continue;

                var rotate = shopItems.Count > mLocations.Length;
                var reloadOffset = 0;

                foreach (var loc in mLocations)
                {
                    var shopItem = shopItems[0];
                    shopItems.RemoveAt(0);

                    while (shopItem.ItemId == ushort.MaxValue)
                    {
                        if (shopItems.Count <= 0)
                            shopItems.AddRange(shop.Value.Item1);

                        shopItem = shopItems[0];
                        shopItems.RemoveAt(0);
                    }

                    reloadOffset += 500;

                    var m = new WorldMerchant(Manager, 0x01ca)
                    {
                        ShopItem = shopItem,
                        Item = shopItem.ItemId,
                        Price = shopItem.Price,
                        Count = shopItem.Count,
                        Currency = shop.Value.Item2,
                        RankReq = shop.Value.Item3,
                        ItemList = shop.Value.Item1,
                        TimeLeft = -1,
                        ReloadOffset = reloadOffset,
                        Rotate = rotate
                    };
                    m.Move(loc.X + .5f, loc.Y + .5f);

                    EnterWorld(m);

                    if (shopItems.Count <= 0)
                        shopItems.AddRange(shop.Value.Item1);
                }
            }
        }

        private void InitMap()
        {
            var w = Map.Width;
            var h = Map.Height;

            EnemiesCollision = new CollisionMap<Entity>(0, w, h);
            PlayersCollision = new CollisionMap<Entity>(1, w, h);
            Projectiles.Clear();
            StaticObjects.Clear();
            Containers.Clear();
            Enemies.Clear();
            Players.Clear();
            Quests.Clear();
            SpecialEnemies.Clear();
            Timers.Clear();

            foreach (var i in Map.InstantiateEntities(Manager))
                EnterWorld(i);
        }

        private void Setup()
        {
            Players = new ConcurrentDictionary<int, Player>();
            Enemies = new ConcurrentDictionary<int, Enemy>();
            Quests = new ConcurrentDictionary<int, Enemy>();
            SpecialEnemies = new ConcurrentDictionary<int, Enemy>();
            Pets = new ConcurrentDictionary<int, Pet>();
            Projectiles = new ConcurrentDictionary<Tuple<int, byte>, Projectile>();
            StaticObjects = new ConcurrentDictionary<int, StaticObject>();
            Containers = new ConcurrentDictionary<int, Container>();
            Timers = new List<WorldTimer>();
            AllowTeleport = true;
            ShowDisplays = true;
            Persist = false; // if false, attempts to delete world with 0 players
            Blocking = 0; // toggles sight block (0 disables sight block)
        }

        public bool Update(ref TickTime time)
        {
            _elapsedTime += time.ElaspedMsDelta;

            WorldBranch.Update(ref time);

            if (IsPastLifetime(ref time))
                return true;
            UpdateLogic(ref time);
            return false;
        }
        
        protected virtual void UpdateLogic(ref TickTime time)
        {
            try
            {
                foreach (var stat in StaticObjects.Values)
                    stat.Tick(time);

                foreach (var container in Containers.Values)
                    container.Tick(time);
                
                foreach (var pet in Pets.Values)
                    pet.Tick(time);

                foreach (var i in Projectiles)
                    i.Value.Tick(time);

                if (Players.Values.Count > 0)
                {
                    foreach (var player in Players.Values)
                    {
                        player.HandleIO();
                        player.DoUpdate(time);
                        player.HandlePendingActions(time);
                        player.Tick(time);
                    }

                    if (EnemiesCollision != null)
                    {
                        foreach (var i in EnemiesCollision.GetActiveChunks(PlayersCollision))
                            i.Tick(time);

                        //foreach (var i in StaticObjects.Where(x => x.Value != null && x.Value is Decoy))
                        //    i.Value.Tick(time);
                    }
                    else
                    {
                        foreach (var i in Enemies)
                            i.Value.Tick(time);

                        //foreach (var i in StaticObjects)
                        //    i.Value.Tick(time);
                    }
                }

                for (var i = Timers.Count - 1; i >= 0; i--)
                    try
                    {
                        if (Timers[i].Tick(this, time))
                            Timers.RemoveAt(i);
                    }
                    catch (Exception e)
                    {
                        var msg = e.Message + "\n" + e.StackTrace;
                        Log.Error(msg);
                        Timers.RemoveAt(i);
                    }
            }
            catch (Exception e)
            {
                Log.Error($"Unknown Error with TickLogic {e}");
            }
        }

        public void FlagForClose() 
        {
            ForceLifetimeExpire = true;
        }

        private bool ForceLifetimeExpire = false;

        private bool IsPastLifetime(ref TickTime time)
        {
            if (WorldBranch.HasBranches())
                return false;

            if (ForceLifetimeExpire)
                return true;

            if (Players.Count > 0)
                return false;

            if (Persist)
                return false;

            if (Deleted)
                return false;

            if (_elapsedTime >= 60000)
            {
                Console.WriteLine($"[{IdName} {Id}] Has Expired");
                return true;
            }

            return false;
        }
    }
}
