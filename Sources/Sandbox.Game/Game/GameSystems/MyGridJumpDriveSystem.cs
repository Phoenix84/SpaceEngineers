﻿using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.Localization;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using Sandbox.Graphics.GUI;
using Sandbox.Graphics.TransparentGeometry.Particles;
using SteamSDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using VRage;
using VRageMath;

namespace Sandbox.Game.GameSystems
{
    public class MyGridJumpDriveSystem
    {
        public const double MIN_JUMP_DISTANCE = 5000.0;

        private MyCubeGrid m_grid;
        
        private HashSet<MyJumpDrive> m_jumpDrives = new HashSet<MyJumpDrive>();
        private HashSet<MyCubeGrid> m_connectedGrids = new HashSet<MyCubeGrid>();
        private HashSet<MyEntity> m_shipInfo = new HashSet<MyEntity>();

        private List<MyEntity> m_entitiesInRange = new List<MyEntity>();
        private List<MyObjectSeed> m_objectsInRange = new List<MyObjectSeed>();
        private List<BoundingBoxD> m_obstaclesInRange = new List<BoundingBoxD>();
        private List<MyCharacter> m_characters = new List<MyCharacter>();

        private Vector3D m_selectedDestination;
        private bool m_isJumping = false;
        private float m_prevJumpTime = 0f;
        private bool m_jumped = false;

        private DateTime m_jumpStartTime;
        private bool m_playEffect = false;
        private float m_playerFov;

        private MySoundPair m_chargingSound = new MySoundPair("ShipJumpDriveCharging");
        private MySoundPair m_jumpInSound = new MySoundPair("ShipJumpDriveJumpIn");
        private MySoundPair m_jumpOutSound = new MySoundPair("ShipJumpDriveJumpOut");
        protected MyEntity3DSoundEmitter m_soundEmitter;

        private MySyncJumpDriveSystem SyncObject;

        public MyGridJumpDriveSystem(MyCubeGrid grid)
        {
            m_grid = grid;

            SyncObject = new MySyncJumpDriveSystem(m_grid);
            m_soundEmitter = new MyEntity3DSoundEmitter(m_grid);
        }

        public void RegisterJumpDrive(MyJumpDrive jumpDrive)
        {
            m_jumpDrives.Add(jumpDrive);
        }

        public void UnregisterJumpDrive(MyJumpDrive jumpDrive)
        {
            m_jumpDrives.Remove(jumpDrive);
        }

        public void UpdateBeforeSimulation()
        {
            UpdateJumpDriveSystem();
        }

        public double GetMaxJumpDistance()
        {
            double absoluteMaxDistance = 0f;
            double maxDistance = 0f;
            double mass = GetMass();
            foreach (var jumpDrive in m_jumpDrives)
            {
                if (jumpDrive.CanJump)
                {
                    absoluteMaxDistance += jumpDrive.BlockDefinition.MaxJumpDistance;
                    maxDistance += jumpDrive.BlockDefinition.MaxJumpDistance * (jumpDrive.BlockDefinition.MaxJumpMass / mass);
                }
            }
            return Math.Min(absoluteMaxDistance, maxDistance);
        }

        private void DepleteJumpDrives(double distance)
        {
            double mass = GetMass();
            foreach (var jumpDrive in m_jumpDrives)
            {
                if (jumpDrive.CanJump)
                {   
                    double massRatio = (jumpDrive.BlockDefinition.MaxJumpMass) / (mass);
                    if (massRatio > 1.0)
                    {
                        massRatio = 1.0;
                    }
                    double jumpDistance = jumpDrive.BlockDefinition.MaxJumpDistance * massRatio;
                    if (jumpDistance < distance)
                    {
                        distance -= jumpDistance;
                        jumpDrive.SetStoredPower(0f);
                    }
                    else
                    {
                        double ratio = distance / jumpDistance;
                        jumpDrive.SetStoredPower(1.0f - (float)ratio);
                        return;
                    }
                }
            }
        }

        private bool IsJumpValid()
        {
            UpdateConnectedGrids();
            foreach (var grid in m_connectedGrids)
            {
                if (grid.IsStatic)
                {
                    return false;
                }
                if (grid.GridSystems.JumpSystem.m_isJumping)
                {
                    return false;
                }
            }

            double maxJumpDistance = GetMaxJumpDistance();
            if (maxJumpDistance < MIN_JUMP_DISTANCE)
            {
                return false;
            }

            return true;
        }

        public void RequestJump(string destinationName, Vector3D destination)
        {
            if (!IsJumpValid())
            {
                return;
            }

            m_selectedDestination = destination;
            double actualDistance = 0;
            var jumpDirection = GetJumpDirection(m_selectedDestination, ref actualDistance);

            if (actualDistance < MIN_JUMP_DISTANCE)
            {
                MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(
                    buttonType: MyMessageBoxButtonsType.OK,
                    messageText: GetWarningText(actualDistance),
                    messageCaption: MyTexts.Get(MySpaceTexts.MessageBoxCaptionWarning)
                    ));
            }
            else
            {
                MyGuiSandbox.AddScreen(MyGuiSandbox.CreateMessageBox(
                    buttonType: MyMessageBoxButtonsType.YES_NO,
                    messageText: GetConfimationText(destinationName, jumpDirection.Length(), actualDistance),
                    messageCaption: MyTexts.Get(MySpaceTexts.MessageBoxCaptionPleaseConfirm),
                    size: new Vector2(0.839375f, 0.3675f), callback: SendJumpRequestToServer
                    ));
            }

        }

        private StringBuilder GetConfimationText(string name, double distance, double actualDistance)
        {
            int totalJumpDrives = m_jumpDrives.Count;
            int operationalJumpDrives = m_jumpDrives.Count((x) => x.CanJump);

            distance /= 1000.0;
            actualDistance /= 1000.0;

            float percent = (float)(actualDistance / distance);
            if (percent > 1.0f)
            {
                percent = 1.0f;
            }

            GetCharactersInBoundingBox(GetAggregateBBox(), m_characters);
            int totalCharacters = 0;
            int seatedCharacters = 0;
            foreach (var character in m_characters)
            {
                if (!character.IsDead)
                {
                    totalCharacters++;
                    if (character.Parent != null)
                    {
                        seatedCharacters++;
                    }
                }
            }
            m_characters.Clear();

            StringBuilder result = new StringBuilder();

            result.Append("Jump destination: ").Append(name).Append("\n");
            result.Append("Distance to the proximity of coordinate: ").Append(distance.ToString("N")).Append(" Kilometers\n");
            result.Append("Achievable percentage of the jump: ").Append(percent.ToString("P")).Append(" (").Append(actualDistance.ToString("N")).Append(" Kilometers)\n");
            result.Append("Weight of transported mass: ").Append(GetMass().ToString("N")).Append(" kg\n");
            result.Append("Operational jump drives: ").Append(operationalJumpDrives).Append("/").Append(totalJumpDrives).Append("\n");
            result.Append("Seated crew on board: ").Append(seatedCharacters).Append("/").Append(totalCharacters).Append("\n");

            return result;
        }

        private StringBuilder GetWarningText(double actualDistance)
        {
            StringBuilder result = new StringBuilder();
            result.Append("Distance to destination: ").Append(actualDistance.ToString("N")).Append(" Meters\n");
            result.Append("Minimum jump distance: ").Append(MIN_JUMP_DISTANCE.ToString("N")).Append(" Meters\n");
            return result;
        }

        private double GetMass()
        {
            double mass = 0f;

            foreach (var grid in m_connectedGrids)
            {
                mass += grid.Physics.Mass;
            }
            return mass;
        }

        private void UpdateConnectedGrids()
        {
            m_connectedGrids.Clear();
            foreach (var node in MyCubeGridGroups.Static.Physical.GetGroup(m_grid).Nodes)
            {
                if (node.NodeData.Physics != null)
                {
                    m_connectedGrids.Add(node.NodeData);
                }
            }

            foreach (var node in MyCubeGridGroups.Static.Logical.GetGroup(m_grid).Nodes)
            {
                if (!m_connectedGrids.Contains(node.NodeData))
                {
                    if (node.NodeData.Physics != null)
                    {
                        m_connectedGrids.Add(node.NodeData);
                    }
                }
            }
        }

        private BoundingBoxD GetAggregateBBox()
        {
            BoundingBoxD bbox = m_grid.PositionComp.WorldAABB;
            foreach (var grid in m_connectedGrids)
            {
                bbox.Include(grid.PositionComp.WorldAABB);
            }
            return bbox;
        }

        private void GetCharactersInBoundingBox(BoundingBoxD boundingBox, List<MyCharacter> characters)
        {
            
            MyGamePruningStructure.GetAllTopMostEntitiesInBox<MyEntity>(ref boundingBox, m_entitiesInRange);
            foreach (var entity in m_entitiesInRange)
            {
                var character = entity as MyCharacter;
                if (character != null)
                {
                    characters.Add(character);
                }
            }
            m_entitiesInRange.Clear();
        }

        private Vector3D? FindSuitableJumpLocation(Vector3D desiredLocation)
        {
            BoundingBoxD shipBBox = GetAggregateBBox();
            BoundingBoxD regionBBox = shipBBox.GetInflated(shipBBox.HalfExtents * 100);
            regionBBox.Translate(desiredLocation - regionBBox.Center);
            shipBBox.Inflate(shipBBox.HalfExtents);

            
            MyProceduralWorldGenerator.Static.OverlapAllPlanetSeedsInSphere(new BoundingSphereD(regionBBox.Center, regionBBox.HalfExtents.AbsMax()), m_objectsInRange);
            Vector3D currentSearchPosition = desiredLocation;
            foreach (var planet in m_objectsInRange)
            {
                if (planet.BoundingVolume.Contains(currentSearchPosition) != ContainmentType.Disjoint)
                {
                    Vector3D v = currentSearchPosition - planet.BoundingVolume.Center;
                    v.Normalize();
                    v *= planet.BoundingVolume.HalfExtents * 1.5;
                    currentSearchPosition = planet.BoundingVolume.Center + v;
                    break;
                }
            }
            m_objectsInRange.Clear();

            MyProceduralWorldGenerator.Static.OverlapAllAsteroidSeedsInSphere(new BoundingSphereD(regionBBox.Center, regionBBox.HalfExtents.AbsMax()), m_objectsInRange);
            foreach (var asteroid in m_objectsInRange)
            {
                m_obstaclesInRange.Add(asteroid.BoundingVolume);
            }
            m_objectsInRange.Clear();

            MyGamePruningStructure.GetAllTopMostEntitiesInBox<MyEntity>(ref regionBBox, m_entitiesInRange);

            // Inflate the obstacles so we only need to check the center of the ship for collisions
            foreach (var entity in m_entitiesInRange)
            {
                if (!(entity is MyPlanet))
                {
                    m_obstaclesInRange.Add(entity.PositionComp.WorldAABB.GetInflated(shipBBox.HalfExtents));
                }
            }

            int maxStepCount = 10;
            int stepCount = 0;

            // When we collide with an obsticle, we add it here
            BoundingBoxD? aggregateCollidedObstacles = null;
            bool obstructed = false;
            bool found = false;

            while (stepCount < maxStepCount)
            {
                stepCount++;
                obstructed = false;
                foreach (var obstacle in m_obstaclesInRange)
                {
                    var contains = obstacle.Contains(currentSearchPosition);
                    if (contains == ContainmentType.Contains || 
                        contains == ContainmentType.Intersects)
                    {
                        if (!aggregateCollidedObstacles.HasValue)
                        {
                            aggregateCollidedObstacles = obstacle;
                        }
                        aggregateCollidedObstacles = aggregateCollidedObstacles.Value.Include(obstacle);
                        aggregateCollidedObstacles = aggregateCollidedObstacles.Value.Inflate(1.0);
                        currentSearchPosition = ClosestPointOnBounds(aggregateCollidedObstacles.Value, currentSearchPosition);
                        obstructed = true;
                        break;
                    }
                }

                if (!obstructed)
                {
                    // No obstacle found, return current search position
                    found = true;
                    break;
                }
            }

            m_obstaclesInRange.Clear();
            m_entitiesInRange.Clear();
            m_objectsInRange.Clear();

            if (found)
            {
                return currentSearchPosition;
            }
            else
            {
                return null;
            }

        }

        private Vector3D ClosestPointOnBounds(BoundingBoxD b, Vector3D p)
        {
            Vector3D pp = (p - b.Center) / b.HalfExtents;
            int maxComp = pp.AbsMaxComponent();
            if (maxComp == 0)
            {
                if (pp.X > 0) p.X = b.Max.X;
                else p.X = b.Min.X;
            }
            else if (maxComp == 1)
            {
                if (pp.Y > 0) p.Y = b.Max.Y;
                else p.Y = b.Min.Y;
            }
            else if (maxComp == 2)
            {
                if (pp.Z > 0) p.Z = b.Max.Z;
                else p.Z = b.Min.Z;
            }

            return p;
        }

        private bool IsLocalCharacterAffectedByJump()
        {
            if (MySession.LocalCharacter == null)
            {
                return false;
            }

            GetCharactersInBoundingBox(GetAggregateBBox(), m_characters);
            foreach (var character in m_characters)
            {
                if (character == MySession.LocalCharacter)
                {
                    if (character.Parent != null)
                    {
                        m_characters.Clear();
                        return true;
                    }
                }
            }
            m_characters.Clear();

            return false;
        }

        private void Jump(Vector3D jumpTarget)
        {
            m_selectedDestination = jumpTarget;
            m_isJumping = true;
            m_jumped = false;
            m_jumpStartTime = TimeUtil.LocalTime;

            if (IsLocalCharacterAffectedByJump())
            {
                m_playEffect = true;
                m_playerFov = MySandboxGame.Config.FieldOfView;
            }
            else
            {
                m_playEffect = false;
            }

            m_soundEmitter.PlaySound(m_chargingSound);
            m_prevJumpTime = 0f;
        }

        private void UpdateJumpDriveSystem()
        {
            // Using this instead of game time because it cannot be affected by sim speed
            float jumpTime = (float)(TimeUtil.LocalTime - m_jumpStartTime).TotalMilliseconds;

            float warmupTime = 10000f;
            float startJumpTime = 1500f;
            float endJumpTime = 500f;

            if (m_isJumping)
            {
                if (jumpTime < warmupTime)
                {
                    int prevTimeInt = (int)(m_prevJumpTime / 1000);
                    int timeInt = (int)(jumpTime / 1000);
                    if (prevTimeInt != timeInt)
                    {
                        if (IsLocalCharacterAffectedByJump())
                        {
                            var notification = new MyHudNotification(MySpaceTexts.NotificationJumpWarmupTime, 500);
                            int secondsRemaining = (int)(Math.Round((warmupTime - jumpTime) / 1000));
                            notification.SetTextFormatArguments(secondsRemaining);
                            MyHud.Notifications.Add(notification);
                        }
                    }
                } 
                else if (jumpTime < startJumpTime + warmupTime)
                {
                    if (m_soundEmitter.SoundId != m_jumpOutSound.SoundId)
                    {
                        m_soundEmitter.PlaySound(m_jumpOutSound);
                    }
                    UpdateJumpEffect(MathHelper.SmoothStep(1f, 0f, (jumpTime - warmupTime) / startJumpTime));
                }
                else if (!m_jumped)
                {
                    UpdateConnectedGrids();

                    double actualDistance = 0;
                    var jumpDirection = GetJumpDirection(m_selectedDestination, ref actualDistance);

                    DepleteJumpDrives(actualDistance);

                    UpdateNearbyEntityList();

                    MyParticleEffect effect;
                    if (MyParticlesManager.TryCreateParticleEffect(53, out effect))
                    {
                        effect.WorldMatrix = MatrixD.CreateFromTransformScale(Quaternion.Identity, m_grid.WorldMatrix.Translation, Vector3D.One);
                        effect.UserScale = (float)m_grid.PositionComp.WorldAABB.HalfExtents.AbsMax() / 25f;
                        effect.AutoDelete = true;
                    }

                    MyThirdPersonSpectator.Static.ResetPosition(0.0, null);
                    MyThirdPersonSpectator.Static.ResetDistance();
                    MyThirdPersonSpectator.Static.RecalibrateCameraPosition();

                    m_jumped = true;

                    foreach (var entity in m_shipInfo)
                    {
                        MatrixD gridMatrix = entity.WorldMatrix;
                        gridMatrix.Translation = gridMatrix.Translation + jumpDirection;
                        entity.WorldMatrix = gridMatrix;
                    }

                    UpdateJumpEffect(0f);
                    MyThirdPersonSpectator.Static.ResetPosition(0.0, null);
                    MyThirdPersonSpectator.Static.ResetDistance();
                    MyThirdPersonSpectator.Static.RecalibrateCameraPosition();

                }
                else if (jumpTime < startJumpTime + endJumpTime + warmupTime)
                {
                    if (m_soundEmitter.SoundId != m_jumpInSound.SoundId)
                    {
                        m_soundEmitter.PlaySound(m_jumpInSound);
                    }
                    UpdateJumpEffect(MathHelper.SmoothStep(0f, 1f, (jumpTime - startJumpTime - warmupTime) / (endJumpTime)));
                }
                else
                {
                    UpdateJumpEffect(1f);
                    m_jumped = false;
                    m_isJumping = false;
                }
            }
            m_prevJumpTime = jumpTime;
        }

        private void UpdateJumpEffect(float t)
        {
            if (m_playEffect)
            {
                float maxFov = MathHelper.ToRadians(170.0f);
                float fov = MathHelper.SmoothStep(m_playerFov, maxFov, 1f - t);
                MySector.MainCamera.FieldOfView = fov;
            }
        }

        private Vector3D GetJumpDirection(Vector3D jumpTarget, ref double actualDistance)
        {
            Vector3D jumpDirection = jumpTarget - m_grid.WorldMatrix.Translation;
            double maxJumpDistance = GetMaxJumpDistance();
            double jumpDistance = jumpDirection.Length();
            actualDistance = jumpDistance;
            if (jumpDistance > maxJumpDistance)
            {
                double ratio = maxJumpDistance / jumpDistance;
                actualDistance = maxJumpDistance;
                jumpDirection *= ratio;
            }
            return jumpDirection;
        }

        private void UpdateNearbyEntityList()
        {
            m_shipInfo.Clear();

            foreach (var grid in m_connectedGrids)
            {
                m_shipInfo.Add(grid);
                var boundingBox = grid.PositionComp.WorldAABB;

                MyGamePruningStructure.GetAllTopMostEntitiesInBox<MyEntity>(ref boundingBox, m_entitiesInRange);
                foreach (var entity in m_entitiesInRange)
                {
                    var floating = entity;
                    if (floating != null && 
                        (floating is MyFloatingObject || 
                            (floating is MyCharacter && !((floating as MyCharacter).IsUsing is MyCockpit))))    // Only handle player if not in cockpit
                    {
                        m_shipInfo.Add(floating);
                    }
                }
                m_entitiesInRange.Clear();
            }
        }

        #region Sync
        private void SendJumpRequestToServer(MyGuiScreenMessageBox.ResultEnum result)
        {
            if (result == MyGuiScreenMessageBox.ResultEnum.YES && IsJumpValid())
            {
                SyncObject.RequestJump(m_selectedDestination);
            }
        }

        private void OnRequestJumpFromClient(Vector3D jumpTarget)
        {
            Debug.Assert(Sync.IsServer);

            if (!IsJumpValid())
            {
                SyncObject.SendJumpFailure();
                return;
            }

            double actualDistance = 0;
            var jumpDirection = GetJumpDirection(jumpTarget, ref actualDistance);

            if (actualDistance < MIN_JUMP_DISTANCE)
            {
                SyncObject.SendJumpFailure();
                return;
            }

            Vector3D? suitableJumpLocation = FindSuitableJumpLocation(jumpTarget);
            if (!suitableJumpLocation.HasValue)
            {
                SyncObject.SendJumpFailure();
                return;
            }

            SyncObject.SendJumpSuccess(suitableJumpLocation.Value);
        }

        [PreloadRequired]
        internal class MySyncJumpDriveSystem
        {
            [MessageIdAttribute(8500, P2PMessageEnum.Reliable)]
            protected struct RequestJumpMsg
            {
                public long EntityId;
                public Vector3D JumpTarget;
            }

            [MessageIdAttribute(8501, P2PMessageEnum.Reliable)]
            protected struct JumpSuccessMsg
            {
                public long EntityId;
                public Vector3D JumpTarget;
            }

            [MessageIdAttribute(8502, P2PMessageEnum.Reliable)]
            protected struct JumpFailureMsg
            {
                public long EntityId;
            }

            private MyCubeGrid m_grid;

            static MySyncJumpDriveSystem()
            {
                MySyncLayer.RegisterMessage<RequestJumpMsg>(OnJumpRequested, MyMessagePermissions.ToServer);
                MySyncLayer.RegisterMessage<JumpSuccessMsg>(OnJumpSuccess, MyMessagePermissions.FromServer);
                MySyncLayer.RegisterMessage<JumpFailureMsg>(OnJumpFailure, MyMessagePermissions.FromServer);
            }

            public MySyncJumpDriveSystem(MyCubeGrid cubeGrid)
            {
                m_grid = cubeGrid;
            }

            public void RequestJump(Vector3D jumpTarget)
            {
                var msg = new RequestJumpMsg();
                msg.EntityId = m_grid.EntityId;
                msg.JumpTarget = jumpTarget;

                Sync.Layer.SendMessageToServer(ref msg);
            }

            private static void OnJumpRequested(ref RequestJumpMsg msg, MyNetworkClient sender)
            {
                MyCubeGrid cubeGrid;
                MyEntities.TryGetEntityById(msg.EntityId, out cubeGrid);
                if (cubeGrid != null)
                {
                    cubeGrid.GridSystems.JumpSystem.OnRequestJumpFromClient(msg.JumpTarget);
                }
            }

            public void SendJumpSuccess(Vector3D jumpTarget)
            {
                var msg = new JumpSuccessMsg();
                msg.EntityId = m_grid.EntityId;
                msg.JumpTarget = jumpTarget;

                Sync.Layer.SendMessageToAllAndSelf(ref msg);
            }

            private static void OnJumpSuccess(ref JumpSuccessMsg msg, MyNetworkClient sender)
            {
                MyCubeGrid cubeGrid;
                MyEntities.TryGetEntityById(msg.EntityId, out cubeGrid);
                if (cubeGrid != null)
                {
                    cubeGrid.GridSystems.JumpSystem.Jump(msg.JumpTarget);
                }
            }

            public void SendJumpFailure()
            {
                var msg = new JumpFailureMsg();
                msg.EntityId = m_grid.EntityId;

                Sync.Layer.SendMessageToAllAndSelf(ref msg);
            }

            private static void OnJumpFailure(ref JumpFailureMsg msg, MyNetworkClient sender)
            {
                MyCubeGrid cubeGrid;
                MyEntities.TryGetEntityById(msg.EntityId, out cubeGrid);
                if (cubeGrid != null)
                {
                    //TODO(AF) Add a notification, maybe a reason
                }
            }
        }
        #endregion
    }
}
