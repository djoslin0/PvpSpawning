using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using SpaceEngineers.Game.ModAPI;
using Sandbox.ModAPI;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.Components;
using VRage.Utils;
using VRageMath;
using VRage.ModAPI;
using VRage.Game.ModAPI.Interfaces;

namespace PvpSpawning
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class Main : MySessionComponentBase
    {
        double spawnDistanceMean = 150000;
        double spawnDistanceVariance = 40000;
        TimeSpan checkRespawnShipLength = new TimeSpan(0, 1, 0);

        private HashSet<long> respawning = new HashSet<long>();
        private Dictionary<long, DateTime> respawnShipPlayers = new Dictionary<long, DateTime>();
        private int skipUpdate = 0;
        Random rnd = new Random();

        public override void Init(MyObjectBuilder_SessionComponent sessionComponent)
        {
            base.Init(sessionComponent);

            /* Add Listener */
            MyVisualScriptLogicProvider.PlayerSpawned += PlayerSpawned;
            MyVisualScriptLogicProvider.PlayerRespawnRequest += PlayerRespawnRequest;
            MyVisualScriptLogicProvider.PlayerEnteredCockpit += PlayerEnteredCockpit;
            MyVisualScriptLogicProvider.PlayerDisconnected += PlayerDisconnected;
        }

        public override void UpdateBeforeSimulation()
        {
            // only check once every 100 ticks
            if (skipUpdate > 0) { skipUpdate--; return; }
            skipUpdate = 100;

            List<long> expiredPlayers = new List<long>();

            foreach (long id in respawnShipPlayers.Keys)
            {
                // only check on respawn ship players for a minute or so
                if ((DateTime.Now - respawnShipPlayers[id]) > checkRespawnShipLength)
                {
                    expiredPlayers.Add(id);
                    continue;
                }

                if (MoveRespawnShip(id))
                {
                    expiredPlayers.Add(id);
                }
            }

            // clean up expired respawn ship players
            foreach (long id in expiredPlayers)
            {
                respawnShipPlayers.Remove(id);
            }
        }

        private bool MoveRespawnShip(long playerId)
        {
            // make sure we actually are in control of something
            IMyPlayer player = GetPlayer(playerId);
            if (player == null) { return false; }
            if (player.Controller == null) { return false; }
            if (player.Controller.ControlledEntity == null) { return false; }
            if (player.Controller.ControlledEntity.Entity == null) { return false; }
            if (player.Controller.ControlledEntity.Entity.Parent == null) { return false; }

            // make sure we're in control of a spawn ship
            IMyEntity ship = player.Controller.ControlledEntity.Entity.Parent;
            if (!player.RespawnShip.Contains(ship.EntityId)) { return true; }
            
            // make sure the spawn ship has a cockpit
            if (!(ship is IMyCubeGrid)) { return false; }
            List<IMySlimBlock> blocks = new List<IMySlimBlock>();
            ((IMyCubeGrid)ship).GetBlocks(blocks, b => b.FatBlock != null && b.FatBlock is IMyCockpit );
            if (blocks.Count == 0)
            {
                // no cockpit found
                return true;
            }

            // make sure the cockpit isn't in gravity
            IMyCockpit cockpit = (IMyCockpit)(blocks.FirstOrDefault().FatBlock);
            if (cockpit.GetNaturalGravity().Length() > 0)
            {
                // cockpit is in gravity
                return true;
            }

            ship.SetPosition(GetRespawnPoint());

            return true;
        }

        private Vector3D GetRespawnPoint()
        {
            Vector3D possibility = new Vector3D();

            // try to find a clear spot up to 5 times
            for (int i = 0; i < 5; i++)
            {
                // pick a random location
                Vector3D unit = new Vector3D(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5);
                if (unit.Length() == 0) { new Vector3D(rnd.NextDouble() - 0.5, 1, rnd.NextDouble() - 0.5); }
                unit.Normalize();

                // move the random location out to the spawndistance
                double spawnDistance = spawnDistanceMean + spawnDistanceVariance * (rnd.NextDouble() - 0.5) * 2.0;
                possibility = new Vector3D(unit.X * spawnDistance, unit.Y * spawnDistance, unit.Z * spawnDistance);

                // make sure the random location isn't inside anything
                BoundingSphereD sphere = new BoundingSphereD(possibility, 100);
                
                if (MyAPIGateway.Entities.IsSpherePenetrating(ref sphere))
                {
                    continue;
                }
                else
                {
                    return possibility;
                }
            }

            return possibility;
        }

        private void PlayerDisconnected(long playerId)
        {
            respawning.Remove(playerId);
            respawnShipPlayers.Remove(playerId);
        }

        private void PlayerRespawnRequest(long playerId)
        {
            if (!respawning.Contains(playerId))
            {
                respawning.Add(playerId);
            }
        }

        private void PlayerEnteredCockpit(string entityName, long playerId, string gridName)
        {
            if (!respawning.Contains(playerId)) { return; }

            // remove player from the 'trying to respawn' list
            respawning.Remove(playerId);

            // add player to the 'using respawn ship' list
            if (!respawnShipPlayers.Keys.Contains(playerId))
            {
                respawnShipPlayers.Add(playerId, DateTime.Now);
            }
        }


        protected override void UnloadData()
        {
            base.UnloadData();

            MyVisualScriptLogicProvider.PlayerSpawned -= PlayerSpawned;
            MyVisualScriptLogicProvider.PlayerRespawnRequest -= PlayerRespawnRequest;
            MyVisualScriptLogicProvider.PlayerEnteredCockpit -= PlayerEnteredCockpit;
            MyVisualScriptLogicProvider.PlayerDisconnected -= PlayerDisconnected;
        }

        private void PlayerSpawned(long playerId)
        {
            // remove player from the 'trying to respawn' list
            respawning.Remove(playerId);
            respawnShipPlayers.Remove(playerId);

            IMyPlayer player = GetPlayer(playerId);
            if (player == null) { return; }

            // check for space suit spawn
            BoundingSphereD sphere = new BoundingSphereD(player.GetPosition(), 100);
            List<IMyEntity> entities = MyAPIGateway.Entities.GetEntitiesInSphere(ref sphere);
            foreach (IMyEntity foundEntity in entities)
            {
                if (!(foundEntity is IMyCubeGrid)) { continue; }
                List<IMySlimBlock> blocks = new List<IMySlimBlock>();
                ((IMyCubeGrid)foundEntity).GetBlocks(blocks, b => b.GetObjectBuilder().SubtypeName.Contains("Medical") || b.GetObjectBuilder().SubtypeName.Contains("Survival") || b.GetObjectBuilder().SubtypeName.Contains("Medpanel")); 
                if (blocks.Count > 0)
                {
                    // player spawned at a medical bay / survival kit
                    return;
                }
            }

            // player spawned as space suit
            player.Character.SetPosition(GetRespawnPoint());
        }

        private IMyPlayer GetPlayer(long playerId)
        {
            try
            {
                IMyIdentity playerIdentity = Player(playerId);
                if (playerIdentity == null) { return null; }

                var playerList = new List<IMyPlayer>();
                MyAPIGateway.Players.GetPlayers(playerList, p => p != null && p.IdentityId == playerIdentity.IdentityId);

                return playerList.FirstOrDefault();
            }
            catch (Exception e)
            {
                return null;
            }
        }

        private IMyIdentity Player(long entityId)
        {
            try
            {
                List<IMyIdentity> listIdentities = new List<IMyIdentity>();

                MyAPIGateway.Players.GetAllIdentites(listIdentities,
                    p => p != null && p.DisplayName != "" && p.IdentityId == entityId);

                if (listIdentities.Count == 1)
                    return listIdentities[0];

                return null;

            }
            catch (Exception e)
            {
                return null;
            }
        }
    }
}