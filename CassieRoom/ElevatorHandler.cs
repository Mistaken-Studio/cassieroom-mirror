// -----------------------------------------------------------------------
// <copyright file="ElevatorHandler.cs" company="Mistaken">
// Copyright (c) Mistaken. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
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
using Mistaken.RoundLogger;
using UnityEngine;

namespace Mistaken.CassieRoom
{
    internal class ElevatorHandler : API.Diagnostics.Module
    {
        public ElevatorHandler(PluginHandler plugin)
            : base(plugin)
        {
            Log = base.Log;
            this.RunCoroutine(this.Loop(), "Loop");
        }

        public override string Name => "Elevator";

        public override void OnDisable()
        {
            Exiled.Events.Handlers.Server.WaitingForPlayers -= this.Handle(() => this.Server_WaitingForPlayers(), "WaitingForPlayers");
            Exiled.Events.Handlers.Player.InteractingDoor -= this.Handle<Exiled.Events.EventArgs.InteractingDoorEventArgs>((ev) => this.Player_InteractingDoor(ev));
            Exiled.Events.Handlers.Player.Verified -= this.Handle<Exiled.Events.EventArgs.VerifiedEventArgs>((ev) => this.Player_Verified(ev));
            Exiled.Events.Handlers.Server.RoundStarted -= this.Handle(() => this.Server_RoundStarted(), "RoundStart");
            Exiled.Events.Handlers.Player.ChangingRole -= this.Handle<Exiled.Events.EventArgs.ChangingRoleEventArgs>((ev) => this.Player_ChangingRole(ev));
            Exiled.Events.Handlers.Player.Died -= this.Handle<Exiled.Events.EventArgs.DiedEventArgs>((ev) => this.Player_Died(ev));
        }

        public override void OnEnable()
        {
            Exiled.Events.Handlers.Server.WaitingForPlayers += this.Handle(() => this.Server_WaitingForPlayers(), "WaitingForPlayers");
            Exiled.Events.Handlers.Player.InteractingDoor += this.Handle<Exiled.Events.EventArgs.InteractingDoorEventArgs>((ev) => this.Player_InteractingDoor(ev));
            Exiled.Events.Handlers.Player.Verified += this.Handle<Exiled.Events.EventArgs.VerifiedEventArgs>((ev) => this.Player_Verified(ev));
            Exiled.Events.Handlers.Server.RoundStarted += this.Handle(() => this.Server_RoundStarted(), "RoundStart");
            Exiled.Events.Handlers.Player.ChangingRole += this.Handle<Exiled.Events.EventArgs.ChangingRoleEventArgs>((ev) => this.Player_ChangingRole(ev));
            Exiled.Events.Handlers.Player.Died += this.Handle<Exiled.Events.EventArgs.DiedEventArgs>((ev) => this.Player_Died(ev));
        }

        internal static readonly HashSet<Player> LoadedAll = new HashSet<Player>();

        internal void SyncFor(Player player)
        {
            MethodInfo sendSpawnMessage = Server.SendSpawnMessage;
            if (sendSpawnMessage != null)
            {
                if (player.ReferenceHub.networkIdentity.connectionToClient == null)
                    return;
                Log.Debug($"Syncing cards for {player.Nickname}", PluginHandler.Instance.Config.VerbouseOutput);
                foreach (var netid in NetworkIdentities)
                {
                    if (netid == null)
                        continue;
                    sendSpawnMessage.Invoke(null, new object[] { netid, player.Connection, });
                }
            }
        }

        internal void DesyncFor(Player player)
        {
            if (removeFromVisList == null)
                removeFromVisList = typeof(NetworkConnection).GetMethod("RemoveFromVisList", BindingFlags.NonPublic | BindingFlags.Instance);
            if (player.ReferenceHub.networkIdentity.connectionToClient == null)
                return;
            Log.Debug($"DeSyncing cards for {player.Nickname}", PluginHandler.Instance.Config.VerbouseOutput);
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

        private static readonly List<(Vector3 Pos, Vector3 Size, Vector3 Rot)> ElevatorDoors = new List<(Vector3 Pos, Vector3 Size, Vector3 Rot)>()
        {
            // -15 1001 -42.5 0 0 90 2 1 1
            (new Vector3(-15f, 1001.34f, -43.1f), new Vector3(2, 1, 1), Vector3.forward * 90),
        };

        private static readonly Vector3 Offset = new Vector3(-8.77f, 18.56f, 0.9f);
        private static readonly List<Mirror.NetworkIdentity> NetworkIdentities = new List<Mirror.NetworkIdentity>();

        private static MethodInfo removeFromVisList = null;
        private static Vector3 bottomMiddle = new Vector3(-16.58f, 1002f, -41f);
        private static bool elevatorUp = true;
        private static bool moving = false;
        private static DoorVariant doorUp;
        private static DoorVariant doorDown;
        private static InRange upTrigger;
        private static InRange downTrigger;

        private static new ModuleLogger Log { get; set; }

        private static (DoorVariant door, InRange trigger) SpawnElevator(Vector3 offset)
        {
            ItemType keycardType = ItemType.KeycardNTFLieutenant;

            // Elevator
            // -15 1000 -41 0 90 0 1 1 1
            var elevatorDoor = DoorUtils.SpawnDoor(DoorUtils.DoorType.HCZ_BREAKABLE, new Vector3(-15f, 1000f, -41.2f) + offset, Vector3.up * 90, Vector3.one);

            // elevatorDoor.RequiredPermissions.RequiredPermissions = KeycardPermissions.ContainmentLevelThree | KeycardPermissions.ArmoryLevelThree | KeycardPermissions.AlphaWarhead;
            (elevatorDoor as BreakableDoor)._brokenPrefab = null;
            API.Patches.DoorPatch.IgnoredDoor.Add(elevatorDoor);

            // networkIdentities.Add(elevatorDoor.netIdentity);
            DoorVariant door;
            foreach (var (pos, size, rot) in ElevatorDoors)
            {
                Log.Debug("Spawning Door", PluginHandler.Instance.Config.VerbouseOutput);

                // Door
                door = DoorUtils.SpawnDoor(DoorUtils.DoorType.HCZ_BREAKABLE, pos + offset, rot, size);
                door.NetworkActiveLocks |= (ushort)DoorLockReason.AdminCommand;
                (door as BreakableDoor)._brokenPrefab = null;
                API.Patches.DoorPatch.IgnoredDoor.Add(door);
                NetworkIdentities.Add(door.netIdentity);

                // Card
                SpawnItem(keycardType, pos - new Vector3(1.65f, 0, 0) + offset, rot, new Vector3(size.x * 9, size.y * 410, size.z * 2));
                Log.Debug("Spawned Door", PluginHandler.Instance.Config.VerbouseOutput);
            }

            // spawn3 -15 1001.5 -42.1 0 90 0 1 400 1
            SpawnItem(keycardType, new Vector3(-15f, 1001.5f, -42.7f) + offset, new Vector3(0, 90, 0), new Vector3(4.75f, 450, 2), true);

            // -15 1001.5 -39.9 0 90 0 3 450 2
            SpawnItem(keycardType, new Vector3(-15f, 1001.5f, -39.9f) + offset, new Vector3(0, 90, 0), new Vector3(3, 450, 2), true);

            // spawn7 -16.5 1001.5 -43.1 3 4 0.1
            API.Extensions.Extensions.SpawnBoxCollider(new Vector3(-16.5f, 1001.5f, -43.1f), new Vector3(3, 4, 0.1f));

            // spawn7 -15 1001.5 -39.95 0.1 4 0.7
            API.Extensions.Extensions.SpawnBoxCollider(new Vector3(-15, 1001.5f, -39.95f), new Vector3(0.1f, 4, 0.7f));

            // spawn7 -15 1001.5 -42.65 0.1 4 1.1
            API.Extensions.Extensions.SpawnBoxCollider(new Vector3(-15, 1001.5f, -42.65f), new Vector3(0.1f, 4, 1.1f));

            // spawn7 -15 1003.4 -41.35 0.1 0.5 3.6
            API.Extensions.Extensions.SpawnBoxCollider(new Vector3(-15, 1003.4f, -41.35f), new Vector3(0.1f, 0.5f, 3.6f));

            // -15 1001.34 -43 0 90 90 2 0.25 1
            door = DoorUtils.SpawnDoor(DoorUtils.DoorType.HCZ_BREAKABLE, new Vector3(-15, 1001.34f, -43) + offset, new Vector3(0, 90, 90), new Vector3(2, 0.25f, 1));
            door.NetworkActiveLocks |= (ushort)DoorLockReason.AdminCommand;
            (door as BreakableDoor)._brokenPrefab = null;
            API.Patches.DoorPatch.IgnoredDoor.Add(door);
            NetworkIdentities.Add(door.netIdentity);

            // -15 1001.9 -40 0 0 90 1.4 1 1
            door = DoorUtils.SpawnDoor(DoorUtils.DoorType.HCZ_BREAKABLE, new Vector3(-15, 1001.9f, -39.5f) + offset, new Vector3(0, 0, 90), new Vector3(1.4f, 1, 1));
            door.NetworkActiveLocks |= (ushort)DoorLockReason.AdminCommand;
            (door as BreakableDoor)._brokenPrefab = null;
            API.Patches.DoorPatch.IgnoredDoor.Add(door);
            NetworkIdentities.Add(door.netIdentity);

            // -16.58 1003.7 -41 90 180 0 15 550 6
            SpawnItem(keycardType, new Vector3(-16.58f, 1003.7f, -41f) + offset, new Vector3(90, 180, 0), new Vector3(15, 550, 6), true); // Up

            // -16.58 1001.87 -43 0 0 0 15 550 6
            SpawnItem(keycardType, new Vector3(-16.58f, 1001.87f, -43f) + offset, new Vector3(0, 0, 0), new Vector3(15, 550, 6), true); // Up-Left

            // -16.58 1001.87 -39.5 0 0 0 15 550 6
            SpawnItem(keycardType, new Vector3(-16.58f, 1001.87f, -39.5f) + offset, new Vector3(0, 0, 0), new Vector3(15, 550, 6), true); // Right

            // -18.08 1001.87 -41 0 90 0 15 550 6
            SpawnItem(keycardType, new Vector3(-18.08f, 1001.87f, -41f) + offset, new Vector3(0, 90, 0), new Vector3(15, 550, 6), true); // Back

            // -17.78 1001.47 -42.5 0 90 90 1.5 1.2 1 //Back
            door = DoorUtils.SpawnDoor(DoorUtils.DoorType.HCZ_BREAKABLE, new Vector3(-17.78f, 1001.47f, -42.5f) + offset, new Vector3(0, 90, 90), new Vector3(1.5f, 1.1f, 1));
            door.NetworkActiveLocks |= (ushort)DoorLockReason.AdminCommand;
            (door as BreakableDoor)._brokenPrefab = null;
            API.Patches.DoorPatch.IgnoredDoor.Add(door);
            NetworkIdentities.Add(door.netIdentity);
            var obj = new GameObject();
            var collider = obj.AddComponent<BoxCollider>();
            obj.transform.position = new Vector3(-16.58f, 1004f, -41f) + offset;
            collider.size = new Vector3(4, 2, 5);
            var elevatorTrigger = InRange.Spawn(
                new Vector3(-16.58f, 1002.7f, -41f) + offset,
                new Vector3(3f, 4, 4f),
                (p) =>
                {
                    Log.Debug($"{p.Nickname} entered", PluginHandler.Instance.Config.VerbouseOutput);
                },
                (p) =>
                {
                    Log.Debug($"{p.Nickname} exited", PluginHandler.Instance.Config.VerbouseOutput);
                });

            // elevatorTrigger.DEBUG = true;
            return (elevatorDoor, elevatorTrigger);
        }

        private static DoorVariant Spawn1499ContainmentChamber()
        {
            ItemType keycardType = ItemType.KeycardNTFLieutenant;

            // -23.7 1018.6 -43.5 0 90 0 1 1 1
            var mainDoor = DoorUtils.SpawnDoor(DoorUtils.DoorType.HCZ_BREAKABLE, new Vector3(-23.8f, 1018.6f, -43.5f), Vector3.up * 90, Vector3.one, name: "SCP1499Chamber");
            mainDoor.RequiredPermissions.RequiredPermissions = KeycardPermissions.ContainmentLevelThree;
            (mainDoor as BreakableDoor)._brokenPrefab = null;

            // networkIdentities.Add(mainDoor.netIdentity);
            // Systems.Patches.DoorPatch.IgnoredDoor.Add(mainDoor);

            // -23.7 1022.35 -43.5 0 90 0 10 110 1
            SpawnItem(keycardType, new Vector3(-23.8f, 1022.33f, -43.5f), new Vector3(0, 90, 0), new Vector3(10, 110, 2), true);

            // -23.8 1020.35 -41.92 0 90 0 5.5 610 2
            SpawnItem(keycardType, new Vector3(-23.8f, 1020.35f, -41.92f), new Vector3(0, 90, 0), new Vector3(5.5f, 610, 2), true);

            // -23.8 1020.35 -45.1 0 90 0 5.5 610 2
            SpawnItem(keycardType, new Vector3(-23.8f, 1020.35f, -45.1f), new Vector3(0, 90, 0), new Vector3(5.5f, 610, 2), true);

            // -23.8 1020.35 -45.7 0 90 90 2.2 0.37 1
            SpawnDoor(new Vector3(-23.8f, 1020.35f, -45.7f), new Vector3(0, 90, 90), new Vector3(2.2f, 0.37f, 1));

            // -23.8 1020.35 -45.55 0 0 90 2.2 1 1
            SpawnDoor(new Vector3(-23.8f, 1020.35f, -45.55f), new Vector3(0, 0, 90), new Vector3(2.2f, 1, 1));

            // -25.5 1020.35 -45.55 0 0 0 15 650 2.5
            SpawnItem(keycardType, new Vector3(-25.5f, 1020.35f, -45.55f), new Vector3(0, 0, 0), new Vector3(15f, 650, 2.5f), true);

            // door = DoorUtils.SpawnDoor(DoorUtils.DoorType.HCZ_BREAKABLE, null, new Vector3(-17.78f, 1001.47f, -42.5f), new Vector3(0, 90, 90), new Vector3(1.5f, 1.1f, 1));
            // door.NetworkActiveLocks |= (ushort)DoorLockReason.AdminCommand;
            // (door as BreakableDoor)._brokenPrefab = null;
            // Systems.Patches.DoorPatch.IgnoredDoor.Add(door);
            return mainDoor;
        }

        private static IEnumerator<float> MoveElevator()
        {
            if (moving)
                yield break;
            elevatorUp = !elevatorUp;
            moving = true;
            doorUp.NetworkTargetState = false;
            doorDown.NetworkTargetState = false;
            doorDown.ServerChangeLock(DoorLockReason.AdminCommand, true);
            doorUp.ServerChangeLock(DoorLockReason.AdminCommand, true);
            yield return Timing.WaitForSeconds(3);
            Log.Debug($"Colliders in up trigger: {upTrigger.ColliderInArea.Count} and in down trigger: {downTrigger.ColliderInArea.Count}", PluginHandler.Instance.Config.VerbouseOutput);
            if (elevatorUp)
            {
                foreach (var item in downTrigger.ColliderInArea.ToArray())
                {
                    try
                    {
                        var ply = Player.Get(item);
                        if (ply.IsConnected && ply.IsAlive && ply.Position.y < 1010)
                        {
                            ply.Position += Offset;
                            RLogger.Log("ELEVATOR", "TELEPORT", $"Teleported {ply.Nickname} Up ({ply.Position})");
                        }
                        else
                            RLogger.Log("ELEVATOR", "DENY", $"Denied teleporting {ply.Nickname} Up ({ply.Position})");
                    }
                    catch (System.Exception ex)
                    {
                        Log.Error(ex.Message);
                        Log.Error(ex.StackTrace);
                    }
                }

                foreach (var item in Pickup.Instances)
                {
                    if (item.durability == 78253f)
                        continue;
                    if (Vector3.Distance(item.position, bottomMiddle) < 3)
                        item.transform.position += Offset;
                }

                foreach (var item in GameObject.FindObjectsOfType<Grenades.Grenade>())
                {
                    if (item.thrower == null)
                        continue;
                    if (Vector3.Distance(item.transform.position, bottomMiddle) < 3)
                        item.transform.position += Offset;
                }

                yield return Timing.WaitForSeconds(2);
                doorDown.NetworkTargetState = false;
                doorUp.NetworkTargetState = true;
            }
            else
            {
                foreach (var item in upTrigger.ColliderInArea.ToArray())
                {
                    try
                    {
                        var ply = Player.Get(item);
                        if (ply.IsConnected && ply.IsAlive && ply.Position.y > 1010)
                        {
                            ply.Position -= Offset;
                            RLogger.Log("ELEVATOR", "TELEPORT", $"Teleported {ply.Nickname} Down ({ply.Position})");
                        }
                        else
                            RLogger.Log("ELEVATOR", "DENY", $"Denied teleporting {ply.Nickname} Down ({ply.Position})");
                    }
                    catch (System.Exception ex)
                    {
                        Log.Error(ex.Message);
                        Log.Error(ex.StackTrace);
                    }
                }

                foreach (var item in Pickup.Instances)
                {
                    if (item.durability == 78253f)
                        continue;
                    if (Vector3.Distance(item.position, bottomMiddle + Offset) < 3)
                        item.transform.position -= Offset;
                }

                foreach (var item in GameObject.FindObjectsOfType<Grenades.Grenade>())
                {
                    if (item.thrower == null)
                        continue;
                    if (Vector3.Distance(item.transform.position, bottomMiddle + Offset) < 3)
                        item.transform.position -= Offset;
                }

                yield return Timing.WaitForSeconds(2);
                doorDown.NetworkTargetState = true;
                doorUp.NetworkTargetState = false;
            }

            yield return Timing.WaitForSeconds(2);
            doorDown.ServerChangeLock(DoorLockReason.AdminCommand, false);
            doorUp.ServerChangeLock(DoorLockReason.AdminCommand, false);
            moving = false;
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

        private static DoorVariant SpawnDoor(Vector3 pos, Vector3 rot, Vector3 size)
        {
            var door = DoorUtils.SpawnDoor(DoorUtils.DoorType.HCZ_BREAKABLE, pos, rot, size);
            door.NetworkActiveLocks |= (ushort)DoorLockReason.AdminCommand;
            (door as BreakableDoor)._brokenPrefab = null;
            API.Patches.DoorPatch.IgnoredDoor.Add(door);
            NetworkIdentities.Add(door.netIdentity);
            return door;
        }

        private readonly Dictionary<Player, int> camperPoints = new Dictionary<Player, int>();
        private readonly Dictionary<Player, HashSet<Type>> camperEffects = new Dictionary<Player, HashSet<Type>>();
        private InRange inRange;

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
                    Log.Error(ex.Message);
                    Log.Error(ex.StackTrace);
                }

                yield return Timing.WaitForSeconds(1);
            }
        }

        private void Player_Died(Exiled.Events.EventArgs.DiedEventArgs ev)
        {
            this.camperPoints[ev.Target] = 0;
            if (this.camperEffects.ContainsKey(ev.Target))
                this.camperEffects[ev.Target].Clear();
        }

        private void Player_ChangingRole(Exiled.Events.EventArgs.ChangingRoleEventArgs ev)
        {
            this.camperPoints[ev.Player] = 0;
            if (this.camperEffects.ContainsKey(ev.Player))
                this.camperEffects[ev.Player].Clear();
        }

        private void Server_RoundStarted()
        {
            this.RunCoroutine(this.DoRoundLoop(), "RoundLoop");
        }

        private void Player_Verified(Exiled.Events.EventArgs.VerifiedEventArgs ev)
        {
            this.DesyncFor(ev.Player);
        }

        private void Player_InteractingDoor(Exiled.Events.EventArgs.InteractingDoorEventArgs ev)
        {
            if (ev.Door == doorUp || ev.Door == doorDown)
            {
                ev.IsAllowed = false;
                this.RunCoroutine(MoveElevator(), "MoveElevator");
            }
        }

        private void Server_WaitingForPlayers()
        {
            moving = false;
            NetworkIdentities.Clear();
            elevatorUp = true;

            (doorDown, downTrigger) = SpawnElevator(Vector3.zero);
            (doorUp, upTrigger) = SpawnElevator(Offset);
            doorUp.NetworkTargetState = true;
            if (PluginHandler.Instance.Config.SpawnSCP1499Chamber)
                Spawn1499ContainmentChamber();

            // -26.65 1019.5 -46.5 80 90 90 1 1 1
            GameObject gameObject = UnityEngine.Object.Instantiate(Server.Host.Inventory.pickupPrefab);
            gameObject.transform.position = new Vector3(-26.65f, 1019.5f, -46.5f);
            gameObject.transform.localScale = Vector3.one;
            gameObject.transform.rotation = Quaternion.Euler(new Vector3(80, 90, 90));
            gameObject.GetComponent<Rigidbody>().constraints = RigidbodyConstraints.FreezeAll;
            Mirror.NetworkServer.Spawn(gameObject);
            gameObject.GetComponent<Pickup>().SetupPickup(ItemType.GunE11SR, 40, Server.Host.Inventory.gameObject, new Pickup.WeaponModifiers(true, 1, 4, 4), gameObject.transform.position, gameObject.transform.rotation);

            gameObject = UnityEngine.Object.Instantiate(Server.Host.Inventory.pickupPrefab);
            gameObject.transform.position = new Vector3(-26.65f, 1019.5f, -47f);
            gameObject.transform.localScale = Vector3.one;
            gameObject.transform.rotation = Quaternion.Euler(new Vector3(80, 90, 90));
            gameObject.GetComponent<Rigidbody>().constraints = RigidbodyConstraints.FreezeAll;
            Mirror.NetworkServer.Spawn(gameObject);
            gameObject.GetComponent<Pickup>().SetupPickup(ItemType.GunE11SR, 40, Server.Host.Inventory.gameObject, new Pickup.WeaponModifiers(true, 1, 4, 3), gameObject.transform.position, gameObject.transform.rotation);

            gameObject = UnityEngine.Object.Instantiate(Server.Host.Inventory.pickupPrefab);
            gameObject.transform.position = new Vector3(-26.65f, 1019.5f, -47.5f);
            gameObject.transform.localScale = Vector3.one;
            gameObject.transform.rotation = Quaternion.Euler(new Vector3(80, 90, 90));
            gameObject.GetComponent<Rigidbody>().constraints = RigidbodyConstraints.FreezeAll;
            Mirror.NetworkServer.Spawn(gameObject);
            gameObject.GetComponent<Pickup>().SetupPickup(ItemType.GunE11SR, 40, Server.Host.Inventory.gameObject, new Pickup.WeaponModifiers(true, 4, 3, 4), gameObject.transform.position, gameObject.transform.rotation);

            gameObject = UnityEngine.Object.Instantiate(Server.Host.Inventory.pickupPrefab);
            gameObject.transform.position = new Vector3(-26.65f, 1019.5f, -48f);
            gameObject.transform.localScale = Vector3.one;
            gameObject.transform.rotation = Quaternion.Euler(new Vector3(80, 90, 90));
            gameObject.GetComponent<Rigidbody>().constraints = RigidbodyConstraints.FreezeAll;
            Mirror.NetworkServer.Spawn(gameObject);
            gameObject.GetComponent<Pickup>().SetupPickup(ItemType.GunE11SR, 40, Server.Host.Inventory.gameObject, new Pickup.WeaponModifiers(true, 4, 3, 4), gameObject.transform.position, gameObject.transform.rotation);

            // Spawn Killer
            this.inRange = InRange.Spawn(new Vector3(-20, 1019, -43), new Vector3(20, 5, 20), null, null);
        }

        private IEnumerator<float> DoRoundLoop()
        {
            yield return Timing.WaitForSeconds(1);
            while (Round.IsStarted)
            {
                yield return Timing.WaitForSeconds(5);
                foreach (var player in RealPlayers.List)
                {
                    if (player.IsDead)
                        continue;
                    if (!this.camperPoints.TryGetValue(player, out int value))
                    {
                        this.camperPoints[player] = 0;
                        value = 0;
                    }

                    if (this.inRange.ColliderInArea.Contains(player.GameObject))
                    {
                        if (!this.camperEffects.TryGetValue(player, out var effects))
                        {
                            effects = new HashSet<Type>();
                            this.camperEffects[player] = effects;
                        }

                        this.camperPoints[player] += 2 * 5;
                        value += 2 * 5;

                        // 1 Min
                        if (value >= 120)
                        {
                            if (!player.GetEffectActive<CustomPlayerEffects.Deafened>())
                            {
                                effects.Add(typeof(CustomPlayerEffects.Deafened));
                                player.EnableEffect<CustomPlayerEffects.Deafened>();
                            }

                            if (!player.GetEffectActive<CustomPlayerEffects.Disabled>())
                            {
                                effects.Add(typeof(CustomPlayerEffects.Disabled));
                                player.EnableEffect<CustomPlayerEffects.Disabled>();

                                player.SetGUI("Tower_Bad", PseudoGUIPosition.MIDDLE, "Nie czuję się za dobrze.", 5);
                            }

                            // 1.5 Min
                            if (value >= 180)
                            {
                                if (!player.GetEffectActive<CustomPlayerEffects.Concussed>())
                                {
                                    effects.Add(typeof(CustomPlayerEffects.Concussed));
                                    player.EnableEffect<CustomPlayerEffects.Concussed>();

                                    player.SetGUI("Tower_Bad", PseudoGUIPosition.MIDDLE, "Zaczyna mnie boleć głowa.", 5);
                                }

                                // 2 Min
                                if (value >= 240)
                                {
                                    if (!player.GetEffectActive<CustomPlayerEffects.Blinded>())
                                    {
                                        effects.Add(typeof(CustomPlayerEffects.Blinded));
                                        player.EnableEffect<CustomPlayerEffects.Blinded>();
                                    }

                                    if (!player.GetEffectActive<CustomPlayerEffects.Exhausted>())
                                    {
                                        effects.Add(typeof(CustomPlayerEffects.Exhausted));
                                        player.EnableEffect<CustomPlayerEffects.Exhausted>();

                                        player.SetGUI("Tower_Bad", PseudoGUIPosition.MIDDLE, "Jestem taki zmęczony.", 5);
                                    }

                                    // 3 Min
                                    if (value >= 360)
                                    {
                                        if (!player.GetEffectActive<CustomPlayerEffects.Hemorrhage>())
                                        {
                                            effects.Add(typeof(CustomPlayerEffects.Hemorrhage));
                                            player.EnableEffect<CustomPlayerEffects.Hemorrhage>();
                                        }

                                        if (!player.GetEffectActive<CustomPlayerEffects.Asphyxiated>())
                                        {
                                            effects.Add(typeof(CustomPlayerEffects.Asphyxiated));
                                            player.EnableEffect<CustomPlayerEffects.Asphyxiated>();
                                        }

                                        if (!player.GetEffectActive<CustomPlayerEffects.Amnesia>())
                                        {
                                            effects.Add(typeof(CustomPlayerEffects.Amnesia));
                                            player.EnableEffect<CustomPlayerEffects.Amnesia>();
                                        }

                                        if (!player.GetEffectActive<CustomPlayerEffects.Bleeding>())
                                        {
                                            effects.Add(typeof(CustomPlayerEffects.Bleeding));
                                            player.EnableEffect<CustomPlayerEffects.Bleeding>();

                                            player.SetGUI("Tower_Bad", PseudoGUIPosition.MIDDLE, "Tracę czucie w nogach.", 5);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        if (value > 0)
                        {
                            this.camperPoints[player] -= 5;
                            value -= 1 * 5;

                            // player.SetGUI("Test", PseudoGUIHandler.Position.TOP, "Value: " + value);
                            if (!this.camperEffects.TryGetValue(player, out var effects))
                                continue;

                            // 3 Min
                            if (value < 360)
                            {
                                if (effects.Contains(typeof(CustomPlayerEffects.Hemorrhage)))
                                {
                                    effects.Remove(typeof(CustomPlayerEffects.Hemorrhage));
                                    player.DisableEffect<CustomPlayerEffects.Hemorrhage>();
                                }

                                if (effects.Contains(typeof(CustomPlayerEffects.Asphyxiated)))
                                {
                                    effects.Remove(typeof(CustomPlayerEffects.Asphyxiated));
                                    player.DisableEffect<CustomPlayerEffects.Asphyxiated>();
                                }

                                if (effects.Contains(typeof(CustomPlayerEffects.Amnesia)))
                                {
                                    effects.Remove(typeof(CustomPlayerEffects.Amnesia));
                                    player.DisableEffect<CustomPlayerEffects.Amnesia>();
                                }

                                if (effects.Contains(typeof(CustomPlayerEffects.Bleeding)))
                                {
                                    effects.Remove(typeof(CustomPlayerEffects.Bleeding));
                                    player.DisableEffect<CustomPlayerEffects.Bleeding>();
                                }

                                // 2 Min
                                if (value < 240)
                                {
                                    if (effects.Contains(typeof(CustomPlayerEffects.Blinded)))
                                    {
                                        effects.Remove(typeof(CustomPlayerEffects.Blinded));
                                        player.DisableEffect<CustomPlayerEffects.Blinded>();
                                    }

                                    if (effects.Contains(typeof(CustomPlayerEffects.Exhausted)))
                                    {
                                        effects.Remove(typeof(CustomPlayerEffects.Exhausted));
                                        player.DisableEffect<CustomPlayerEffects.Exhausted>();
                                    }

                                    // 1.5 Min
                                    if (value < 180)
                                    {
                                        if (effects.Contains(typeof(CustomPlayerEffects.Concussed)))
                                        {
                                            effects.Remove(typeof(CustomPlayerEffects.Concussed));
                                            player.DisableEffect<CustomPlayerEffects.Concussed>();
                                        }

                                        // 1 Min
                                        if (value < 120)
                                        {
                                            if (effects.Contains(typeof(CustomPlayerEffects.Deafened)))
                                            {
                                                effects.Remove(typeof(CustomPlayerEffects.Deafened));
                                                player.DisableEffect<CustomPlayerEffects.Deafened>();
                                            }

                                            if (effects.Contains(typeof(CustomPlayerEffects.Disabled)))
                                            {
                                                effects.Remove(typeof(CustomPlayerEffects.Disabled));
                                                player.DisableEffect<CustomPlayerEffects.Disabled>();
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
