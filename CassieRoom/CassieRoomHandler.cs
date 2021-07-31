// -----------------------------------------------------------------------
// <copyright file="CassieRoomHandler.cs" company="Mistaken">
// Copyright (c) Mistaken. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using Exiled.API.Extensions;
using Exiled.API.Features;
using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using MEC;
using Mirror;
using Mistaken.API;
using Mistaken.API.Components;
using Mistaken.API.Diagnostics;
using Mistaken.API.Extensions;
using Mistaken.API.GUI;
using Mistaken.CustomItems;
using Mistaken.RoundLogger;
using UnityEngine;
using static Mistaken.API.Extensions.DoorUtils;

namespace Mistaken.CassieRoom
{
    internal class CassieRoomHandler : API.Diagnostics.Module
    {
        public CassieRoomHandler(PluginHandler plugin)
            : base(plugin)
        {
            this.RunCoroutine(this.Loop(), "Loop");
            this.RunCoroutine(this.UpdateSNav(), "UpdateSNav");
            API.Utilities.Map.OnTeslaModeChange += (newValue) =>
            {
                if (this.teslaToggleButton == null)
                    return;
                TeslaIndicator.NetworkTargetState = newValue != API.Utilities.TeslaMode.ENABLED;
            };
        }

        public override string Name => "CassieRoom";

        public override void OnEnable()
        {
            if (!PluginHandler.Instance.Config.SpawnCassieRoom)
            {
                this.Enabled = false;
                return;
            }

            Exiled.Events.Handlers.Server.WaitingForPlayers += this.Handle(() => this.Server_WaitingForPlayers(), "WaitingForPlayers");
            Exiled.Events.Handlers.Player.InteractingDoor += this.Handle<Exiled.Events.EventArgs.InteractingDoorEventArgs>((ev) => this.Player_InteractingDoor(ev));
            Exiled.Events.Handlers.Warhead.Starting += this.Handle<Exiled.Events.EventArgs.StartingEventArgs>((ev) => this.Warhead_Starting(ev));
            Exiled.Events.Handlers.Warhead.Stopping += this.Handle<Exiled.Events.EventArgs.StoppingEventArgs>((ev) => this.Warhead_Stopping(ev));
            Exiled.Events.Handlers.Player.Verified += this.Handle<Exiled.Events.EventArgs.VerifiedEventArgs>((ev) => this.Player_Verified(ev));
        }

        public override void OnDisable()
        {
            Exiled.Events.Handlers.Server.WaitingForPlayers -= this.Handle(() => this.Server_WaitingForPlayers(), "WaitingForPlayers");
            Exiled.Events.Handlers.Player.InteractingDoor -= this.Handle<Exiled.Events.EventArgs.InteractingDoorEventArgs>((ev) => this.Player_InteractingDoor(ev));
            Exiled.Events.Handlers.Warhead.Starting -= this.Handle<Exiled.Events.EventArgs.StartingEventArgs>((ev) => this.Warhead_Starting(ev));
            Exiled.Events.Handlers.Warhead.Stopping -= this.Handle<Exiled.Events.EventArgs.StoppingEventArgs>((ev) => this.Warhead_Stopping(ev));
            Exiled.Events.Handlers.Player.Verified -= this.Handle<Exiled.Events.EventArgs.VerifiedEventArgs>((ev) => this.Player_Verified(ev));
        }

        internal static DoorVariant TeslaIndicator { get; set; }

        private static readonly List<Mirror.NetworkIdentity> NetworkIdentities = new List<Mirror.NetworkIdentity>();
        private static readonly List<(Vector3 Pos, Vector3 Size, Vector3 Rot)> Doors = new List<(Vector3 Pos, Vector3 Size, Vector3 Rot)>()
        {
            (new Vector3(183.4f, 998.5f, -73), new Vector3(6, 1, 1), Vector3.forward * 90),
            (new Vector3(185f, 998.5f, -73), new Vector3(6, 1, 1), Vector3.forward * 90),
            (new Vector3(195f, 998.5f, -73), new Vector3(6, 1, 1), Vector3.forward * 90),
            (new Vector3(194.3f, 998.5f, -73), new Vector3(6, 1, 1), Vector3.forward * 90),
            (new Vector3(188f, 998.5f, -73), new Vector3(6, 1, 1), Vector3.forward * 90),
            (new Vector3(189f, 998.5f, -73), new Vector3(6, 1, 1), Vector3.forward * 90),
        };

        private static readonly Dictionary<DoorVariant, Func<Exiled.Events.EventArgs.InteractingDoorEventArgs, bool>> DoorCallbacks = new Dictionary<DoorVariant, Func<Exiled.Events.EventArgs.InteractingDoorEventArgs, bool>>();
        private static readonly HashSet<Player> LoadedAll = new HashSet<Player>();
        private static InRangeBall sNavSurface;
        private static InRangeBall sNavEZHCZ;
        private static MethodInfo removeFromVisList = null;

        private static DoorVariant SpawnIndicator(Vector3 pos, Vector3 buttonOffset, Vector3 rotation)
        {
            var door = DoorUtils.SpawnDoor(DoorUtils.DoorType.HCZ_BREAKABLE, pos + buttonOffset, rotation, Vector3.one);
            (door as BreakableDoor)._brokenPrefab = null;
            door.NetworkTargetState = false;
            DoorCallbacks[door] = (ev) => false;
            API.Patches.DoorPatch.IgnoredDoor.Add(door);
            return door;
        }

        private static DoorVariant SpawnButton(Vector3 pos, Vector3 buttonOffset, Vector3 rotation, string name, Func<Exiled.Events.EventArgs.InteractingDoorEventArgs, bool> onCall, Vector3 size = default)
        {
            var door = DoorUtils.SpawnDoor(DoorUtils.DoorType.HCZ_BREAKABLE, pos + buttonOffset, rotation, size == default ? Vector3.one : size);
            (door as BreakableDoor)._brokenPrefab = null;
            door.NetworkTargetState = true;
            DoorCallbacks[door] = onCall;
            API.Patches.DoorPatch.IgnoredDoor.Add(door);
            InRange.Spawn(
                pos,
                Vector3.one * 2,
                (p) =>
                {
                    p.SetGUI("cassie_room_display", PseudoGUIPosition.MIDDLE, name);
                },
                (p) =>
                {
                    p.SetGUI("cassie_room_display", PseudoGUIPosition.MIDDLE, null);
                });
            return door;
        }

        private static DoorVariant SpawnDoor(string name, Vector3 pos, Vector3 rotation, Vector3 size)
        {
            var door = DoorUtils.SpawnDoor(DoorUtils.DoorType.HCZ_BREAKABLE, pos, rotation, size, name: name);
            door.NetworkActiveLocks |= (ushort)DoorLockReason.AdminCommand;
            (door as BreakableDoor)._brokenPrefab = null;
            API.Patches.DoorPatch.IgnoredDoor.Add(door);
            NetworkIdentities.Add(door.netIdentity);
            return door;
        }

        private static void SpawnItem(ItemType type, Vector3 pos, Vector3 rot, Vector3 size, bool collide = false)
        {
            var gameObject = UnityEngine.Object.Instantiate<GameObject>(Server.Host.Inventory.pickupPrefab);
            gameObject.transform.position = pos;
            gameObject.transform.localScale = size;
            gameObject.transform.rotation = Quaternion.Euler(rot);
            gameObject.GetComponent<Rigidbody>().constraints = RigidbodyConstraints.FreezeAll;
            if (collide)
            {
                gameObject.AddComponent<BoxCollider>();
                gameObject.layer = LayerMask.GetMask("Default");
            }

            var pickup = gameObject.GetComponent<Pickup>();
            pickup.SetupPickup(type, 78253f, Server.Host.Inventory.gameObject, new Pickup.WeaponModifiers(true, 0, 0, 4), gameObject.transform.position, gameObject.transform.rotation);
            pickup.Locked = true;
            foreach (var c in pickup.model.GetComponents<Component>())
                GameObject.Destroy(c.gameObject);
            NetworkIdentities.Add(pickup.netIdentity);
            Pickup.Instances.Remove(pickup);
            GameObject.Destroy(pickup);
            foreach (var item in gameObject.GetComponents<Collider>())
                GameObject.Destroy(item);
            foreach (var item in gameObject.GetComponents<MeshRenderer>())
                GameObject.Destroy(item);
            Mirror.NetworkServer.Spawn(gameObject);
            gameObject.SetActive(false);
        }

        private bool unlocked = false;
        private DoorVariant warheadStartButton;
        private DoorVariant warheadStopButton;
        private DoorVariant warheadLockButton;
        private DoorVariant teslaToggleButton;
        private DoorVariant cassieRoomOpenButton;
        private DoorVariant mainDoor;

        private void SyncFor(Player player)
        {
            MethodInfo sendSpawnMessage = Server.SendSpawnMessage;
            if (sendSpawnMessage != null)
            {
                if (player.ReferenceHub.networkIdentity.connectionToClient == null)
                    return;
                this.Log.Debug($"Syncing cards for {player.Nickname}", PluginHandler.Instance.Config.VerbouseOutput);
                foreach (var netid in NetworkIdentities)
                    sendSpawnMessage.Invoke(null, new object[] { netid, player.Connection });
            }
        }

        private void DesyncFor(Player player)
        {
            if (removeFromVisList == null)
                removeFromVisList = typeof(NetworkConnection).GetMethod("RemoveFromVisList", BindingFlags.NonPublic | BindingFlags.Instance);
            if (player.ReferenceHub.networkIdentity.connectionToClient == null)
                return;
            this.Log.Debug($"DeSyncing cards for {player.Nickname}", PluginHandler.Instance.Config.VerbouseOutput);
            foreach (var netid in NetworkIdentities)
            {
                ObjectDestroyMessage msg = new ObjectDestroyMessage
                {
                    netId = netid.netId,
                };
                NetworkServer.SendToClientOfPlayer<ObjectDestroyMessage>(player.ReferenceHub.networkIdentity, msg);
                if (netid.observers.ContainsKey(player.Connection.connectionId))
                {
                    netid.observers.Remove(player.Connection.connectionId);
                    removeFromVisList?.Invoke(player.Connection, new object[] { netid, true });
                }
            }
        }

        private IEnumerator<float> Loop()
        {
            while (true)
            {
                try
                {
                    var start = DateTime.Now;
                    foreach (var player in RealPlayers.List)
                    {
                        if (player.ReferenceHub.networkIdentity.connectionToClient == null)
                            continue;
                        if (player.Position.y > 900 || player.Role == RoleType.Spectator || player.Role == RoleType.Scp079)
                        {
                            if (LoadedAll.Contains(player))
                                continue;
                            LoadedAll.Add(player);
                            this.SyncFor(player);
                        }
                        else if (LoadedAll.Contains(player))
                        {
                            this.DesyncFor(player);
                            LoadedAll.Remove(player);
                        }
                    }

                    MasterHandler.LogTime("CassieRoom", "Loop", start, DateTime.Now);
                }
                catch (System.Exception ex)
                {
                    this.Log.Error(ex.Message);
                    this.Log.Error(ex.StackTrace);
                    MasterHandler.LogError(ex, this, "Loop");
                }

                yield return Timing.WaitForSeconds(1);
            }
        }

        private IEnumerator<float> UpdateSNav()
        {
            yield return Timing.WaitForSeconds(5);
            while (true)
            {
                try
                {
                    var start = DateTime.Now;

                    this.UpdateSNavSurface();
                    this.UpdateSNavEZHCZ();

                    MasterHandler.LogTime("CassieRoom", "UpdateSNav", start, DateTime.Now);
                }
                catch (System.Exception ex)
                {
                    this.Log.Error(ex.Message);
                    this.Log.Error(ex.StackTrace);
                    MasterHandler.LogError(ex, this, "UpdateSNav");
                }

                yield return Timing.WaitForSeconds(5);
            }
        }

        private void UpdateSNavSurface()
        {
            if (sNavSurface == null)
                return;
            if (sNavSurface.ColliderInArea.Count > 0)
            {
                string[] toWrite = SNav.SNavHandler.GenerateSurfaceSNav(true);
                var list = NorthwoodLib.Pools.ListPool<string>.Shared.Rent(toWrite);
                list.RemoveAll(i => string.IsNullOrWhiteSpace(i));
                while (list.Count < 65)
                    list.Insert(0, string.Empty);
                string msg = "<voffset=25em><color=yellow>SURFACE</color><color=green><size=25%><align=left><mspace=0.5em>" + string.Join("<br>", list) + "</mspace></align></size></color></voffset>";
                foreach (var gameObject in sNavSurface.ColliderInArea)
                {
                    if (gameObject == null)
                        continue;
                    var player = Player.Get(gameObject);
                    if (player.ReferenceHub.networkIdentity.connectionToClient == null)
                        continue;
                    player.ShowHint(msg, 6);
                }
            }
        }

        private void UpdateSNavEZHCZ()
        {
            if (sNavEZHCZ == null)
                return;
            if (sNavEZHCZ.ColliderInArea.Count > 0)
            {
                string[] toWrite = SNav.SNavHandler.GenerateEZ_HCZSNav(null, true);
                var list = NorthwoodLib.Pools.ListPool<string>.Shared.Rent(toWrite);
                list.RemoveAll(i => string.IsNullOrWhiteSpace(i));
                while (list.Count < 65)
                    list.Insert(0, string.Empty);
                string msg = "<voffset=25em><color=yellow>Entrance Zone</color> & <color=yellow>Heavy Containment Zone</color><color=green><size=25%><align=left><mspace=0.5em>" + string.Join("<br>", list) + "</mspace></align></size></color></voffset>";
                foreach (var gameObject in sNavEZHCZ.ColliderInArea)
                {
                    if (gameObject == null)
                        continue;
                    var player = Player.Get(gameObject);
                    if (player.ReferenceHub.networkIdentity.connectionToClient == null)
                        continue;
                    player.ShowHint(msg, 6);
                }
            }
        }

        private void Player_InteractingDoor(Exiled.Events.EventArgs.InteractingDoorEventArgs ev)
        {
            if (ev.IsAllowed && DoorCallbacks.TryGetValue(ev.Door, out var callback))
            {
                if (ev.Player.Role == RoleType.Scp106)
                {
                    ev.IsAllowed = false;
                    return;
                }

                ev.IsAllowed = callback(ev);
            }
            else if (ev.Door == this.mainDoor)
            {
                if (ev.Player.CurrentItemIndex == -1 || !ev.Player.CurrentItem.id.IsKeycard() || ev.Player.CurrentItem.id == ItemType.KeycardChaosInsurgency)
                {
                    ev.IsAllowed = false;
                    RLogger.Log("CASSIE ROOM", "ACCESS", "Access denied, wrong keycard or none");
                }
                else
                {
                    if (!this.unlocked)
                    {
                        var citem = CustomItemsHandler.GetCustomItem(ev.Player.CurrentItem);
                        if (citem == null)
                        {
                            ev.IsAllowed = false;
                            RLogger.Log("CASSIE ROOM", "ACCESS", "Access denied, not custom item");
                        }
                        else
                        {
                            switch (citem.SessionVarType)
                            {
                                case SessionVarType.CC_DEPUTY_FACILITY_MANAGER_KEYCARD:
                                    this.unlocked = true;
                                    RLogger.Log("CASSIE ROOM", "ACCESS", "Access granted, facility manager keycard");
                                    break;
                                case SessionVarType.CI_GUARD_COMMANDER_KEYCARD:
                                    if (!ev.Player.GetSessionVar<bool>(SessionVarType.CI_GUARD_COMMANDER_KEYCARD_OWNER) && !ev.Player.GetSessionVar<bool>(SessionVarType.CC_GUARD_COMMANDER))
                                    {
                                        ev.IsAllowed = false;
                                        RLogger.Log("CASSIE ROOM", "ACCESS", "Access denied, not working guard commander keycard");
                                    }
                                    else
                                    {
                                        this.unlocked = true;
                                        RLogger.Log("CASSIE ROOM", "ACCESS", "Access granted, guard commander keycard");
                                    }

                                    break;
                                default:
                                    ev.IsAllowed = false;
                                    break;
                            }
                        }
                    }

                    if (!ev.IsAllowed)
                        return;
                    RLogger.Log("CASSIE ROOM", "ACCESS", "Access granted");
                    this.mainDoor.ServerChangeLock(PluginDoorLockReason.COOLDOWN, true);
                    MEC.Timing.CallDelayed(5f, () =>
                    {
                        this.mainDoor.ServerChangeLock(PluginDoorLockReason.COOLDOWN, false);
                    });
                }
            }
        }

        private void Server_WaitingForPlayers()
        {
            this.unlocked = false;
            DoorCallbacks.Clear();
            NetworkIdentities.Clear();
            #region Helipad
            {
                SpawnItem(ItemType.KeycardSeniorGuard, new Vector3(181f, 992.460f, -58.3f + 0.1f), new Vector3(90, 90, 0), new Vector3(18, 40, 0.05f));
                SpawnItem(ItemType.KeycardSeniorGuard, new Vector3(181f, 992.461f, -58.3f - 2f + 0.1f), new Vector3(90, 0, 0), new Vector3(30, 40, 0.05f));
                SpawnItem(ItemType.KeycardSeniorGuard, new Vector3(181f, 992.461f, -58.3f + 2f + 0.1f), new Vector3(90, 0, 0), new Vector3(30, 40, 0.05f));
                SpawnItem(ItemType.KeycardGuard, new Vector3(181f, 992.45f, -58.3f + 0.1f), new Vector3(90, 0, 0), new Vector3(26.25f, 1600, 0.05f));
                SpawnItem(ItemType.KeycardGuard, new Vector3(181f, 992.451f, -58.3f + 0.1f), new Vector3(90, 45, 0), new Vector3(26.25f, 1600, 0.05f));
                SpawnItem(ItemType.KeycardGuard, new Vector3(181f, 992.452f, -58.3f + 0.1f), new Vector3(90, 90, 0), new Vector3(26.25f, 1600, 0.05f));
                SpawnItem(ItemType.KeycardGuard, new Vector3(181f, 992.453f, -58.3f + 0.1f), new Vector3(90, 135, 0), new Vector3(26.25f, 1600, 0.05f));

                SpawnItem(ItemType.KeycardFacilityManager, new Vector3(187.446f, 992.453f, -58.3f + 0.1f), new Vector3(90, 90, 0), new Vector3(24, 35, 0.05f));
                SpawnItem(ItemType.KeycardFacilityManager, new Vector3(174.56f, 992.453f, -58.3f + 0.1f), new Vector3(90, 90, 0), new Vector3(24, 35, 0.05f));
                SpawnItem(ItemType.KeycardFacilityManager, new Vector3(181f, 992.453f, -58.3f + 0.1f - 6.45f), new Vector3(90, 0, 0), new Vector3(24, 35, 0.05f));
                SpawnItem(ItemType.KeycardFacilityManager, new Vector3(181f, 992.453f, -58.3f + 0.1f + 6.45f), new Vector3(90, 0, 0), new Vector3(24, 35, 0.05f));
                SpawnItem(ItemType.KeycardFacilityManager, new Vector3(187.446f - 1.86f, 992.453f, -58.3f + 0.1f + 4.5f), new Vector3(90, 45, 0), new Vector3(24, 35, 0.05f));
                SpawnItem(ItemType.KeycardFacilityManager, new Vector3(174.56f + 1.86f, 992.453f, -58.3f + 0.1f - 4.5f), new Vector3(90, 45, 0), new Vector3(24, 35, 0.05f));
                SpawnItem(ItemType.KeycardFacilityManager, new Vector3(187.446f - 1.86f, 992.453f, -58.3f + 0.1f - 4.5f), new Vector3(90, -45, 0), new Vector3(24, 35, 0.05f));
                SpawnItem(ItemType.KeycardFacilityManager, new Vector3(174.56f + 1.86f, 992.453f, -58.3f + 0.1f + 4.5f), new Vector3(90, -45, 0), new Vector3(24, 35, 0.05f));
            }
            #endregion
            #region Lights
            {
                SpawnItem(ItemType.Flashlight, new Vector3(179f, 992.75f, -68.2f), new Vector3(0, -90, 180), new Vector3(2, 2, 2));
                SpawnItem(ItemType.Flashlight, new Vector3(176f, 992.75f, -68.2f), new Vector3(0, -90, 180), new Vector3(2, 2, 2));
                SpawnItem(ItemType.Flashlight, new Vector3(182f, 992.75f, -68.2f), new Vector3(0, -90, 180), new Vector3(2, 2, 2));
                SpawnItem(ItemType.Flashlight, new Vector3(184.5f, 992.75f, -68.2f), new Vector3(0, -90, 180), new Vector3(2, 2, 2));

                SpawnItem(ItemType.Flashlight, new Vector3(179f, 992.75f, -51.5f), new Vector3(0, 90, 180), new Vector3(2, 2, 2));
                SpawnItem(ItemType.Flashlight, new Vector3(176f, 992.75f, -51.5f), new Vector3(0, 90, 180), new Vector3(2, 2, 2));
                SpawnItem(ItemType.Flashlight, new Vector3(182f, 992.75f, -51.5f), new Vector3(0, 90, 180), new Vector3(2, 2, 2));
                SpawnItem(ItemType.Flashlight, new Vector3(184.5f, 992.75f, -51.5f), new Vector3(0, 90, 180), new Vector3(2, 2, 2));

                // 150 1010 -45 -40 0 -90 1 1 1
                for (int i = 0; i < 22; i++)
                {
                    SpawnItem(ItemType.GunE11SR, new Vector3(150 + (i * 2), 1010, -45), new Vector3(-40, 0, -90), new Vector3(.01f, .01f, .01f));
                    if (i > 17)
                        SpawnItem(ItemType.GunE11SR, new Vector3(150 + (i * 2), 1010, -45), new Vector3(-35, 0, -90), new Vector3(.01f, .01f, .01f));
                }

                // 180 1006 -75 -90 0 90 1 1 1
                for (int i = 0; i < 4; i++)
                {
                    for (int j = 0; j < 5; j++)
                    {
                        SpawnItem(ItemType.GunE11SR, new Vector3(180 + (i * 4), 1005, -75 - (j * 4)), new Vector3(-90, 0, 90), new Vector3(.01f, .01f, .01f));
                    }
                }
            }
            #endregion
            #region Structure
            {
                ItemType keycardType = ItemType.KeycardContainmentEngineer;

                // MainDoor
                this.mainDoor = DoorUtils.SpawnDoor(DoorUtils.DoorType.EZ_BREAKABLE, new Vector3(190f, 992.5f, -73), Vector3.zero, Vector3.one);
                (this.mainDoor as BreakableDoor)._brokenPrefab = null;
                this.mainDoor.ServerChangeLock(PluginDoorLockReason.REQUIREMENTS_NOT_MET, true);
                API.Patches.DoorPatch.IgnoredDoor.Add(this.mainDoor);

                // UpperDoor
                SpawnDoor(null, new Vector3(190f, 995.75f, -73), Vector3.zero, new Vector3(1, 1, 0.1f));
                SpawnDoor(null, new Vector3(190f, 995.75f + 3.25f, -73), Vector3.zero, new Vector3(1, 1, 0.1f));
                SpawnDoor(null, new Vector3(190f, 995.75f + 3.25f + 3.25f, -73), Vector3.zero, new Vector3(1, 1, 0.1f));
                SpawnItem(keycardType, new Vector3(190f, 999.95f, -73f), new Vector3(0, 0, 0), new Vector3(20, 1020, 2));
                SpawnItem(keycardType, new Vector3(188f, 1005f, -73f), new Vector3(0, 0, 0), new Vector3(70, 500, 2));
                SpawnItem(keycardType, new Vector3(189f, 1005f, -84.5f), new Vector3(90, 90, 0), new Vector3(100, 2500, 2));

                foreach (var (pos, size, rot) in Doors)
                {
                    this.Log.Debug("Spawning Door", PluginHandler.Instance.Config.VerbouseOutput);

                    // Door
                    SpawnDoor(null, pos, rot, size);

                    // Card
                    SpawnItem(keycardType, pos - new Vector3(1.65f, 0, 0), rot, new Vector3(size.x * 9, size.y * 410, size.z * 2));
                    this.Log.Debug("Spawned Door", PluginHandler.Instance.Config.VerbouseOutput);
                }

                var obj = new GameObject();
                var collider = obj.AddComponent<BoxCollider>();
                obj.transform.position = new Vector3(187f, 1005, -83f);
                collider.size = new Vector3(20, 2, 20);
            }
            #endregion
            #region Functionality
            {
                // Test Button
                /*SpawnButton(new Vector3(181, 994, -75), new Vector3(-1.5f, 2, -2), new Vector3(0, 90, 90), "<size=150%><color=yellow>Test</color> button</size>", (ev) =>
                {
                    Cassie.Message(".g4 .g4 CASSIE ROOM OVERRIDE .g4 .g4 . This is a test message", false, false);
                    ev.Door.ServerChangeLock(PluginDoorLockReason.COOLDOWN, true);
                    this.CallDelayed(5, () =>
                    {
                        ev.Door.ServerChangeLock(PluginDoorLockReason.COOLDOWN, false);
                    }, "TestButtonCooldown");
                    return false;
                });*/
                // Warhead Start
                this.warheadStartButton = SpawnButton(new Vector3(181, 994, -79), new Vector3(-1.5f, 2, -2), new Vector3(0, 90, 90), "<size=150%><color=yellow>Start</color> a Warhead</size>", (ev) =>
                {
                    Cassie.Message(".g4 .g4 CASSIE ROOM OVERRIDE .g4 .g4 . Warhead engaged", false, false);
                    ev.Door.ServerChangeLock(PluginDoorLockReason.COOLDOWN, true);
                    this.CallDelayed(
                        5,
                        () =>
                        {
                            Warhead.Start();
                            this.CallDelayed(
                                2 * 60,
                                () =>
                                {
                                    ev.Door.ServerChangeLock(PluginDoorLockReason.COOLDOWN, false);
                                },
                                "WarheadStartCooldown");
                        },
                        "DelayWarheadStart");
                    return false;
                });

                // Warhead Stop
                this.warheadStopButton = SpawnButton(new Vector3(181, 994, -83), new Vector3(-1.5f, 2, -2), new Vector3(0, 90, 90), "<size=150%><color=yellow>Stop</color> a Warhead</size>", (ev) =>
                {
                    if (Warhead.DetonationTimer < 10 + 5)
                        return false;
                    Cassie.Message(".g4 .g4 CASSIE ROOM OVERRIDE .g4 .g4 . Warhead disengaged", false, false);
                    ev.Door.ServerChangeLock(PluginDoorLockReason.COOLDOWN, true);
                    this.CallDelayed(
                        5,
                        () =>
                        {
                            if (BetterWarheadHandler.Warhead.StopLock)
                                return;
                            Warhead.Stop();
                            this.CallDelayed(
                                2 * 60,
                                () =>
                                {
                                    ev.Door.ServerChangeLock(PluginDoorLockReason.COOLDOWN, false);
                                },
                                "WarheadStopCooldown");
                        },
                        "DelayWarheadStop");
                    return false;
                });
                this.warheadStopButton.ServerChangeLock(PluginDoorLockReason.REQUIREMENTS_NOT_MET, true);

                // Disable warhead
                this.warheadLockButton = SpawnButton(new Vector3(181, 994, -87), new Vector3(-1.5f, 2, -2), new Vector3(0, 90, 90), "<size=150%><color=yellow>Disable</color> Warhead</size>", (ev) =>
                {
                    BetterWarheadHandler.Warhead.StartLock = true;
                    Cassie.Message(".g4 .g4 CASSIE ROOM OVERRIDE .g4 .g4 . warhead is now in permanent lockdown");
                    this.warheadLockButton.ServerChangeLock(PluginDoorLockReason.REQUIREMENTS_NOT_MET, true);
                    return false;
                });
                TeslaIndicator = SpawnIndicator(new Vector3(181, 994.5f, -91), new Vector3(-1.5f, 2, -2), new Vector3(0, 90, 90));
                this.teslaToggleButton = SpawnButton(new Vector3(181, 994, -91), new Vector3(-1.5f, 2, -2), new Vector3(0, 90, 90), "<size=150%><color=yellow>Toggles</color> all Tesla gates</size><br><color=blue>Enabled</color> | <color=green>Disabled</color>", (ev) =>
                {
                    ev.Door.ServerChangeLock(PluginDoorLockReason.COOLDOWN, true);
                    if (API.Utilities.Map.TeslaMode == API.Utilities.TeslaMode.ENABLED)
                    {
                        API.Utilities.Map.TeslaMode = API.Utilities.TeslaMode.DISABLED;
                        TeslaIndicator.NetworkTargetState = true;
                        Cassie.Message(".g4 .g4 CASSIE ROOM OVERRIDE .g4 .g4 . Tesla gates are now deactivated");
                    }
                    else
                    {
                        API.Utilities.Map.TeslaMode = API.Utilities.TeslaMode.ENABLED;
                        TeslaIndicator.NetworkTargetState = false;
                        Cassie.Message(".g4 .g4 CASSIE ROOM OVERRIDE .g4 .g4 . Tesla gates are now activated");
                    }

                    MEC.Timing.CallDelayed(2 * 60, () =>
                    {
                        ev.Door.ServerChangeLock(PluginDoorLockReason.COOLDOWN, false);
                    });
                    return false;
                });

                this.cassieRoomOpenButton = SpawnButton(
                    new Vector3(-16.3f, 1020, -48.7f),
                    Vector3.zero,
                    new Vector3(0, 90, 90),
                    string.Empty,
                    (ev) =>
                    {
                        if (!Map.IsLCZDecontaminated)
                            return false;
                        /*if(!unlocked)
                        {
                            var citem = CustomItemsHandler.GetCustomItem(ev.Player.CurrentItem);
                            if (citem == null)
                            {
                                Gamer.RoundLoggerSystem.RoundLogger.Log("CASSIE ROOM", "TOWER ACCESS", "Access denied, not custom item");
                                return false;
                            }
                            switch(citem.SessionVarType)
                            {
                                case Main.SessionVarType.CC_DEPUTY_FACILITY_MANAGER_KEYCARD:
                                    Gamer.RoundLoggerSystem.RoundLogger.Log("CASSIE ROOM", "TOWER ACCESS", "Access granted, facility manager keycard");
                                    break;
                                case Main.SessionVarType.CI_GUARD_COMMANDER_KEYCARD:
                                    if (!ev.Player.GetSessionVar<bool>(Main.SessionVarType.CI_GUARD_COMMANDER_KEYCARD_OWNER) && !ev.Player.GetSessionVar<bool>(Main.SessionVarType.CC_GUARD_COMMANDER))
                                    {
                                        Gamer.RoundLoggerSystem.RoundLogger.Log("CASSIE ROOM", "TOWER ACCESS", "Access denied, not working guard commander keycard");
                                        return false;
                                    }
                                    Gamer.RoundLoggerSystem.RoundLogger.Log("CASSIE ROOM", "TOWER ACCESS", "Access granted, guard commander keycard");
                                    break;
                                default:
                                    Gamer.RoundLoggerSystem.RoundLogger.Log("CASSIE ROOM", "TOWER ACCESS", "Access denied, wrong custom item");
                                    return false;
                            }
                        }*/
                        RLogger.Log("CASSIE ROOM", "TOWER ACCESS", "Access granted");
                        this.mainDoor.ServerChangeLock(PluginDoorLockReason.REQUIREMENTS_NOT_MET, false);
                        this.cassieRoomOpenButton.ServerChangeLock(PluginDoorLockReason.COOLDOWN, true);
                        this.cassieRoomOpenButton.NetworkTargetState = true;
                        MEC.Timing.CallDelayed(3f, () =>
                        {
                            this.cassieRoomOpenButton.NetworkTargetState = false;
                            this.mainDoor.ServerChangeLock(PluginDoorLockReason.REQUIREMENTS_NOT_MET, true);
                            this.cassieRoomOpenButton.ServerChangeLock(PluginDoorLockReason.COOLDOWN, false);
                            if (((PluginDoorLockReason)this.mainDoor.NetworkActiveLocks & PluginDoorLockReason.BLOCKED_BY_SOMETHING) == 0)
                                this.mainDoor.NetworkTargetState = false;
                        });
                        return false;
                    },
                    new Vector3(0.5f, 0.5f, 0.5f));
                this.cassieRoomOpenButton.NetworkTargetState = false;

                // 188 992.46 -91 180 0 0 10 0.001 10
                SpawnItem(ItemType.SCP018, new Vector3(188, 992.46f, -91), new Vector3(180, 0, 0), new Vector3(10, 0.001f, 10));
                sNavSurface = InRangeBall.Spawn(
                    new Vector3(188, 993, -91),
                    1,
                    1,
                    (player) =>
                    {
                        PseudoGUIHandler.Ignore(player);
                        this.UpdateSNavSurface();
                    },
                    (player) => PseudoGUIHandler.StopIgnore(player));
                SpawnItem(ItemType.SCP018, new Vector3(192, 992.46f, -91), new Vector3(180, 0, 0), new Vector3(10, 0.001f, 10));
                sNavEZHCZ = InRangeBall.Spawn(
                    new Vector3(192, 993, -91),
                    1,
                    1,
                    (player) =>
                    {
                        PseudoGUIHandler.Ignore(player);
                        this.UpdateSNavEZHCZ();
                    },
                    (player) => PseudoGUIHandler.StopIgnore(player));
                InRange isSomeoneInside = null;
                isSomeoneInside = InRange.Spawn(
                    new Vector3(188, 993f, -85),
                    new Vector3(23, 10, 23),
                    (player) =>
                    {
                        if (player.Role == RoleType.Tutorial || player.Role == RoleType.Scp106)
                            return;
                        this.mainDoor.NetworkTargetState = true;
                        this.mainDoor.ServerChangeLock(PluginDoorLockReason.BLOCKED_BY_SOMETHING, true);
                    },
                    (player) =>
                    {
                        if (isSomeoneInside.ColliderInArea.Count == 0)
                        {
                            this.CallDelayed(
                                5,
                                () =>
                                {
                                    if (isSomeoneInside.ColliderInArea.Count == 0)
                                    {
                                        this.mainDoor.ServerChangeLock(PluginDoorLockReason.BLOCKED_BY_SOMETHING, false);
                                        if (((PluginDoorLockReason)this.mainDoor.NetworkActiveLocks & PluginDoorLockReason.REQUIREMENTS_NOT_MET) != 0)
                                            this.mainDoor.NetworkTargetState = false;
                                    }
                                },
                                "Unlock doors");
                        }
                    });
                var obj = new GameObject();
                obj.transform.position = new Vector3(188, 1004, -85);
                obj.AddComponent<BoxCollider>().size = new Vector3(23, 1, 23);
            }
            #endregion
        }

        private void Player_Verified(Exiled.Events.EventArgs.VerifiedEventArgs ev)
        {
            this.DesyncFor(ev.Player);
        }

        private void Warhead_Starting(Exiled.Events.EventArgs.StartingEventArgs ev)
        {
            this.warheadStartButton.ServerChangeLock(PluginDoorLockReason.REQUIREMENTS_NOT_MET, true);
            this.warheadStopButton.ServerChangeLock(PluginDoorLockReason.REQUIREMENTS_NOT_MET, false);
        }

        private void Warhead_Stopping(Exiled.Events.EventArgs.StoppingEventArgs ev)
        {
            this.warheadStartButton.ServerChangeLock(PluginDoorLockReason.REQUIREMENTS_NOT_MET, false);
            this.warheadStopButton.ServerChangeLock(PluginDoorLockReason.REQUIREMENTS_NOT_MET, true);
        }
    }
}
