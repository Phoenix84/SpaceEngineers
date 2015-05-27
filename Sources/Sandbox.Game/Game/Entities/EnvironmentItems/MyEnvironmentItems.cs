﻿#region Using

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Havok;
using Sandbox.Common;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Common.ObjectBuilders.Serializer;
using Sandbox.Definitions;
using Sandbox.Engine.Models;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using VRage.Utils;
using VRageMath;
using VRageRender;
using Sandbox.Engine.Utils;
using Sandbox.Common.Components;
using Sandbox.Game;
using VRage;
using VRage.Library.Utils;
using Sandbox;
using Medieval.ObjectBuilders.Definitions;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Game.Weapons;
using Sandbox.Game.Components;
using Medieval.ObjectBuilders;
using Sandbox.Game.Multiplayer;

#endregion

namespace Sandbox.Game.Entities.EnvironmentItems
{
    /// <summary>
    /// Base class for collecting environment items (of one type) in entity. Useful for drawing of instanced data, or physical shapes instances.
    /// </summary>
    [MyEntityType(typeof(MyObjectBuilder_EnvironmentItems))]
    public class MyEnvironmentItems : MyEntity
    {
        protected struct MyEnvironmentItemData
        {
            public int Id;
            public MyTransformD Transform;
            public MyStringId SubtypeId;
            public bool Enabled;
            public int SectorInstanceId;
        }

        public class MyEnvironmentItemsSpawnData
        {
            // Baase entity object.
            public MyEnvironmentItems EnvironmentItems;

            // Physics shapes for subtypes.
            public Dictionary<MyStringId, HkShape> SubtypeToShapes = new Dictionary<MyStringId, HkShape>();
            // Root physics shapes per sector id.
            public HkStaticCompoundShape SectorRootShape = new HkStaticCompoundShape(HkReferencePolicy.None);
            // Bounding box of all environment items transformed to world space.
            public BoundingBoxD AabbWorld = BoundingBoxD.CreateInvalid();
        }

        public struct ItemInfo
        {
            public int LocalId;
            public MyTransformD Transform;
            public MyStringId SubtypeId;
        }

        private readonly MyInstanceFlagsEnum m_instanceFlags;

        // Items data.
        protected readonly Dictionary<int, MyEnvironmentItemData> m_itemsData = new Dictionary<int, MyEnvironmentItemData>();
        // Map from Havok's instance identifier to key in items data.
        protected readonly Dictionary<int, int> m_physicsShapeInstanceIdToLocalId = new Dictionary<int, int>();

        // Map from key in items data to Havok's instance identifier.
        protected readonly Dictionary<int, int> m_localIdToPhysicsShapeInstanceId = new Dictionary<int, int>();
        // Map from environment item subtypes to their models
        protected static readonly Dictionary<MyStringId, int> m_subtypeToModel = new Dictionary<MyStringId, int>();

        // Sectors.
        protected readonly Dictionary<Vector3I, MyEnvironmentSector> m_sectors = new Dictionary<Vector3I, MyEnvironmentSector>();
        public Dictionary<Vector3I, MyEnvironmentSector> Sectors { get { return m_sectors; } }

        protected List<HkdShapeInstanceInfo> m_childrenTmp = new List<HkdShapeInstanceInfo>();
        HashSet<Vector3I> m_updatedSectorsTmp = new HashSet<Vector3I>();
        List<HkdBreakableBodyInfo> m_tmpBodyInfos = new List<HkdBreakableBodyInfo>();
        protected static List<HkRigidBody> m_tmpResults = new List<HkRigidBody>();

        private MyEnvironmentItemsDefinition m_definition;
        public MyEnvironmentItemsDefinition Definition { get { return m_definition; } }

        public event Action<int> ItemRemoved;

        public MyEnvironmentItems()
        {
            m_instanceFlags = MyInstanceFlagsEnum.ShowLod1 | MyInstanceFlagsEnum.CastShadows | MyInstanceFlagsEnum.EnableColorMask;
            m_definition = null;

            this.Render = new MyRenderComponentEnvironmentItems(this);
            AddDebugRenderComponent(new MyEnviromentItemsDebugDraw(this));
        }

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);

            Init(null, null, null, null);

            BoundingBoxD aabbWorld = BoundingBoxD.CreateInvalid();
            Dictionary<MyStringId, HkShape> subtypeIdToShape = new Dictionary<MyStringId, HkShape>();
            HkStaticCompoundShape sectorRootShape = new HkStaticCompoundShape(HkReferencePolicy.None);
            var builder = (MyObjectBuilder_EnvironmentItems)objectBuilder;

            MyDefinitionId defId = new MyDefinitionId(builder.TypeId, builder.SubtypeId);

            // Compatibility
            if (builder.SubtypeId == MyStringId.NullOrEmpty)
            {
                if (objectBuilder is MyObjectBuilder_Bushes)
                {
                    defId = new MyDefinitionId(typeof(MyObjectBuilder_DestroyableItems), "Bushes");
                }
                else if (objectBuilder is MyObjectBuilder_TreesMedium)
                {
                    defId = new MyDefinitionId(typeof(MyObjectBuilder_Trees), "TreesMedium");
                }
                else if (objectBuilder is MyObjectBuilder_Trees)
                {
                    defId = new MyDefinitionId(typeof(MyObjectBuilder_Trees), "Trees");
                }
            }

            if (!MyDefinitionManager.Static.TryGetDefinition<MyEnvironmentItemsDefinition>(defId, out m_definition))
            {
                Debug.Assert(false, "Could not find definition " + defId.ToString() + " for environment items!");
                return;
            }

            if (builder.Items != null)
            {
                foreach (var item in builder.Items)
                {
                    MyStringId itemSubtype = MyStringId.GetOrCompute(item.SubtypeName);
                    Debug.Assert(m_definition.ContainsItemDefinition(itemSubtype));
                    if (!m_definition.ContainsItemDefinition(itemSubtype))
                    {
                        continue;
                    }

                    MatrixD worldMatrix = item.PositionAndOrientation.GetMatrix();
                    AddItem(m_definition.GetItemDefinition(itemSubtype), ref worldMatrix, ref aabbWorld, sectorRootShape, subtypeIdToShape);
                }
            }

            PrepareItems(sectorRootShape, ref aabbWorld);

            foreach (var pair in subtypeIdToShape)
            {
                pair.Value.RemoveReference();
            }

            NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
        }

        public override MyObjectBuilder_EntityBase GetObjectBuilder(bool copy = false)
        {
            var builder = (MyObjectBuilder_EnvironmentItems)base.GetObjectBuilder(copy);
            builder.SubtypeName = this.Definition.Id.SubtypeName;

            int numEnabled = 0;
            foreach (var itemsData in m_itemsData)
            {
                if (itemsData.Value.Enabled)
                    numEnabled++;
            }

            builder.Items = new MyObjectBuilder_EnvironmentItems.MyOBEnvironmentItemData[numEnabled];

            int insertIndex = 0;
            foreach (var itemsData in m_itemsData)
            {
                if (!itemsData.Value.Enabled)
                    continue;

                builder.Items[insertIndex].SubtypeName = itemsData.Value.SubtypeId.ToString();
                builder.Items[insertIndex].PositionAndOrientation = new MyPositionAndOrientation(itemsData.Value.Transform.TransformMatrix);
                insertIndex++;
            }

            return builder;
        }

        /// <summary>
        /// Spawn Environment Items instance (e.g. forest) object which can be then used for spawning individual items (e.g. trees).
        /// </summary>
        public static MyEnvironmentItemsSpawnData BeginSpawn(MyEnvironmentItemsDefinition itemsDefinition)
        {
            var builder = MyObjectBuilderSerializer.CreateNewObject(itemsDefinition.Id.TypeId, itemsDefinition.Id.SubtypeName) as MyObjectBuilder_EnvironmentItems;
            builder.PersistentFlags |= MyPersistentEntityFlags2.Enabled | MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
            var envItems = MyEntities.CreateFromObjectBuilderAndAdd(builder) as MyEnvironmentItems;

            MyEnvironmentItemsSpawnData spawnData = new MyEnvironmentItemsSpawnData();
            spawnData.EnvironmentItems = envItems;
            return spawnData;
        }

        /// <summary>
        /// Spawn environment item with the definition subtype on world position.
        /// </summary>
        public static bool SpawnItem(MyEnvironmentItemsSpawnData spawnData, MyEnvironmentItemDefinition itemDefinition, Vector3D position, Vector3D up)
        {
            if (!MyFakes.ENABLE_ENVIRONMENT_ITEMS)
                return true;

            Debug.Assert(spawnData != null && spawnData.EnvironmentItems != null);
            Debug.Assert(itemDefinition != null);
            if (spawnData == null || spawnData.EnvironmentItems == null || itemDefinition == null)
            {
                return false;
            }

            Vector3D forward = MyUtils.GetRandomPerpendicularVector(ref up);
            MatrixD worldMatrix = MatrixD.CreateWorld(position, forward, up);

            return spawnData.EnvironmentItems.AddItem(itemDefinition, ref worldMatrix, ref spawnData.AabbWorld, spawnData.SectorRootShape, spawnData.SubtypeToShapes);
        }

        /// <summary>
        /// Ends spawning - finishes preparetion of items data.
        /// </summary>
        public static void EndSpawn(MyEnvironmentItemsSpawnData spawnData)
        {
            spawnData.EnvironmentItems.PrepareItems(spawnData.SectorRootShape, ref spawnData.AabbWorld);

            foreach (var pair in spawnData.SubtypeToShapes)
            {
                pair.Value.RemoveReference();
            }
            spawnData.SubtypeToShapes.Clear();
        }

        /// <summary>
        /// Adds environment item to internal collections. Creates render and physics data. 
        /// </summary>
        /// <returns>True if successfully added, otherwise false.</returns>
        private bool AddItem(MyEnvironmentItemDefinition itemDefinition, ref MatrixD worldMatrix, ref BoundingBoxD aabbWorld,
            HkStaticCompoundShape sectorRootShape, Dictionary<MyStringId, HkShape> subtypeIdToShape)
        {
            if (!MyFakes.ENABLE_ENVIRONMENT_ITEMS)
                return true;

            Debug.Assert(m_definition.ContainsItemDefinition(itemDefinition),
                String.Format("Environment item with definition '{0}' not found in class '{1}'", itemDefinition.Id, m_definition.Id));
            if (!m_definition.ContainsItemDefinition(itemDefinition))
            {
                return false;
            }

            //MyDefinitionId defId = new MyDefinitionId(envItemObjectBuilderType, subtypeId.ToString());

            MyModel model = MyModels.GetModelOnlyData(itemDefinition.Model);
            if (model == null)
            {
                //Debug.Fail(String.Format("Environment item model of '{0}' not found, skipping the item...", itemDefinition.Id));
                return false;
            }

            int localId = worldMatrix.Translation.GetHashCode();

            MyEnvironmentItemData data = new MyEnvironmentItemData()
            {
                Id = localId,
                SubtypeId = itemDefinition.Id.SubtypeId,
                Transform = new MyTransformD(ref worldMatrix),
                Enabled = true,
                SectorInstanceId = -1
            };

            //Preload split planes
            //VRageRender.MyRenderProxy.PreloadMaterials(model.AssetName); 

            aabbWorld.Include(model.BoundingBox.Transform(worldMatrix));

            CheckModelConsistency(itemDefinition);

            MatrixD transform = data.Transform.TransformMatrix;

            Vector3I sectorId = MyEnvironmentSector.GetSectorId(transform.Translation, m_definition.SectorSize);
            MyEnvironmentSector sector;
            if (!m_sectors.TryGetValue(sectorId, out sector))
            {
                sector = new MyEnvironmentSector(sectorId);
                m_sectors.Add(sectorId, sector);
            }

            // Adds instance of the given model. Local matrix specified might be changed internally in renderer.
            Matrix transformL = (Matrix)transform;
            data.SectorInstanceId = sector.AddInstance(itemDefinition.Id.SubtypeId, ref transformL, model.BoundingBox, m_instanceFlags, m_definition.MaxViewDistance);

            int physicsShapeInstanceId;

            if (AddPhysicsShape(data.SubtypeId, model, ref transform, sectorId, sectorRootShape, subtypeIdToShape, out physicsShapeInstanceId))
            {
                // Map to data index - note that itemData is added after this to its list!
                m_physicsShapeInstanceIdToLocalId[physicsShapeInstanceId] = localId;
                m_localIdToPhysicsShapeInstanceId[localId] = physicsShapeInstanceId;
            }

            data.Transform = new MyTransformD(transform);

            if (m_itemsData.ContainsKey(localId))
            {
                Debug.Fail("More items on same place! " + transform.Translation.ToString());
            }
            else
            {
                m_itemsData.Add(localId, data);
            }

            return true;
        }

        private static void CheckModelConsistency(MyEnvironmentItemDefinition itemDefinition)
        {
            int modelId = MyModel.GetId(itemDefinition.Model);
            int savedModelId;
            if (m_subtypeToModel.TryGetValue(itemDefinition.Id.SubtypeId, out savedModelId))
            {
                Debug.Assert(modelId == savedModelId, "Environment item subtype id maps to a different model id than it used to!");
            }
            else
            {
                m_subtypeToModel.Add(itemDefinition.Id.SubtypeId, modelId);
            }
        }

        /// <summary>
        /// Prepares data for renderer and physics. Must be called after all items has been added.
        /// </summary>
        public void PrepareItems(HkStaticCompoundShape sectorRootShape, ref BoundingBoxD aabbWorld)
        {
            PositionComp.LocalAABB = (BoundingBox)aabbWorld;

            if (sectorRootShape.InstanceCount > 0)
            {
                Debug.Assert(m_physicsShapeInstanceIdToLocalId.Count > 0);

                Physics = new Sandbox.Engine.Physics.MyPhysicsBody(this, RigidBodyFlag.RBF_STATIC)
                {
                    MaterialType = m_definition.Material,
                    AngularDamping = MyPerGameSettings.DefaultAngularDamping,
                    LinearDamping = MyPerGameSettings.DefaultLinearDamping,
                    IsStaticForCluster = true,
                };

                sectorRootShape.Bake();
                HkMassProperties massProperties = new HkMassProperties();
                Physics.CreateFromCollisionObject((HkShape)sectorRootShape, Vector3.Zero, WorldMatrix, massProperties);
                if (Sandbox.Game.MyPerGameSettings.Destruction)
                {
                    Physics.ContactPointCallback += Physics_ContactPointCallback;
                    Physics.RigidBody.ContactPointCallbackEnabled = true;
                }
                sectorRootShape.Base.RemoveReference();

                Physics.Enabled = true;
            }

            foreach (var pair in m_sectors)
            {
                pair.Value.UpdateRenderInstanceData();
            }

            foreach (var pair in m_sectors)
            {
                pair.Value.UpdateRenderEntitiesData(WorldMatrix, m_subtypeToModel);
            }
        }

		public bool TryGetItemInfoById(int itemId, out ItemInfo result)
		{
			result = new ItemInfo();
			MyEnvironmentItemData data;
			if (m_itemsData.TryGetValue(itemId, out data))
			{
				if (data.Enabled)
				{
					result = new ItemInfo() { LocalId = itemId, SubtypeId = data.SubtypeId, Transform = data.Transform };
					return true;
				}
			}
			return false;
		}

        public void GetItemsInRadius(Vector3D position, float radius, List<ItemInfo> result)
        {
            double radiusSq = radius * radius;
            if (this.Physics != null && this.Physics.RigidBody != null)
            {
                // CH: This iterates through all the child shapes and test their position, but there's currently no better way.
                HkStaticCompoundShape shape = (HkStaticCompoundShape)Physics.RigidBody.GetShape();
                HkShapeContainerIterator it = shape.GetIterator();
                while (it.IsValid)
                {
                    uint shapeKey = it.CurrentShapeKey;
                    int physicsInstanceId, localId;
                    uint childKey;
                    shape.DecomposeShapeKey(shapeKey, out physicsInstanceId, out childKey);
                    if (m_physicsShapeInstanceIdToLocalId.TryGetValue(physicsInstanceId, out localId))
                    {
                        MyEnvironmentItemData data;
                        if (m_itemsData.TryGetValue(localId, out data))
                        {
                            if (data.Enabled && Vector3D.DistanceSquared(data.Transform.Position, position) < radiusSq)
                            {
                                result.Add(new ItemInfo() { LocalId = localId, SubtypeId = data.SubtypeId, Transform = data.Transform });
                            }
                        }
                    }

                    it.Next();
                }
            }
        }

        public void RemoveItemsAroundPoint(Vector3D point, double radius)
        {
            double radiusSq = radius * radius;
            if (this.Physics != null && this.Physics.RigidBody != null)
            {
                // CH: This iterates through all the child shapes and test their position, but there's currently no better way.
                HkStaticCompoundShape shape = (HkStaticCompoundShape)Physics.RigidBody.GetShape();
                HkShapeContainerIterator it = shape.GetIterator();
                while (it.IsValid)
                {
                    uint shapeKey = it.CurrentShapeKey;
                    int physicsInstanceId;
                    uint childKey;
                    shape.DecomposeShapeKey(shapeKey, out physicsInstanceId, out childKey);
                    if (m_physicsShapeInstanceIdToLocalId.ContainsKey(physicsInstanceId))
                    {
                        if (DisableRenderInstanceIfInRadius(point, radiusSq, m_physicsShapeInstanceIdToLocalId[physicsInstanceId], hasPhysics: true))
                            shape.EnableShapeKey(shapeKey, false);
                    }

                    it.Next();
                }
            }

            foreach (var itemsData in m_itemsData)
            {
                if (itemsData.Value.Enabled == false)
                    continue;

                //If itemsData has physical representation
                if (m_localIdToPhysicsShapeInstanceId.ContainsKey(itemsData.Key))
                    DisableRenderInstanceIfInRadius(point, radiusSq, itemsData.Key);
            }

            foreach (var sector in m_updatedSectorsTmp)
            {
                Sectors[sector].UpdateRenderInstanceData();
            }

            m_updatedSectorsTmp.Clear();
        }

        public bool RemoveItem(int itemInstanceId, bool sync)
        {
            int physicsInstanceId;

            if (m_localIdToPhysicsShapeInstanceId.TryGetValue(itemInstanceId, out physicsInstanceId))
            {
                return RemoveItem(itemInstanceId, physicsInstanceId, sync);
            }

            return false;
        }

        public bool RemoveItem(int itemInstanceId, int physicsInstanceId, bool sync)
        {
            Debug.Assert(sync == false || Sync.IsServer, "Synchronizing env. item removal from the client is forbidden!");
            Debug.Assert(m_physicsShapeInstanceIdToLocalId.ContainsKey(physicsInstanceId), "Could not find env. item shape!");
            Debug.Assert(m_localIdToPhysicsShapeInstanceId.ContainsKey(itemInstanceId), "Could not find env. item instance!");

            m_physicsShapeInstanceIdToLocalId.Remove(physicsInstanceId);
            m_localIdToPhysicsShapeInstanceId.Remove(itemInstanceId);

            MyEnvironmentItemData itemData = m_itemsData[itemInstanceId];
            itemData.Enabled = false;
            m_itemsData[itemInstanceId] = itemData;

            HkStaticCompoundShape shape = (HkStaticCompoundShape)Physics.RigidBody.GetShape();
            var shapeKey = shape.ComposeShapeKey(physicsInstanceId, 0);
            shape.EnableShapeKey(shapeKey, false);

            Matrix matrix = itemData.Transform.TransformMatrix;
            var sectorId = MyEnvironmentSector.GetSectorId(matrix.Translation, Definition.SectorSize);
            var disabled = Sectors[sectorId].DisableInstance(itemData.SectorInstanceId, itemData.SubtypeId);
            Debug.Assert(disabled, "Env. item instance render not disabled");
            Sectors[sectorId].UpdateRenderInstanceData();

            OnRemoveItem(itemInstanceId, ref matrix, itemData.SubtypeId);

            if (sync)
            {
                MySyncDestructions.RemoveEnvironmentItem(EntityId, itemInstanceId);
            }

            return true;
        }

        protected virtual void OnRemoveItem(int localId, ref Matrix matrix, MyStringId myStringId)
        {
            if (ItemRemoved != null)
                ItemRemoved(localId);
        }

        private bool DisableRenderInstanceIfInRadius(Vector3D center, double radiusSq, int itemInstanceId, bool hasPhysics = false)
        {
            MyEnvironmentItemData itemData = m_itemsData[itemInstanceId];
            Vector3 translation = itemData.Transform.Position;
            if (Vector3D.DistanceSquared(new Vector3D(translation), center) <= radiusSq)
            {
                int physicsInstanceId;
                bool itemRemoved = false;
                if (m_localIdToPhysicsShapeInstanceId.TryGetValue(itemInstanceId, out physicsInstanceId))
                {
                    m_physicsShapeInstanceIdToLocalId.Remove(physicsInstanceId);
                    m_localIdToPhysicsShapeInstanceId.Remove(itemInstanceId);
                    itemRemoved = true;
                }

                if (!hasPhysics || itemRemoved)
                {
                    Vector3I sectorId = MyEnvironmentSector.GetSectorId(translation, m_definition.SectorSize);
                    bool disabled = Sectors[sectorId].DisableInstance(itemData.SectorInstanceId, itemData.SubtypeId);
                    Debug.Assert(disabled, "Env. item render not disabled");

                    if (disabled)
                    {
                        m_updatedSectorsTmp.Add(sectorId);
                        itemData.Enabled = false;
                        m_itemsData[itemInstanceId] = itemData;
                    }
                    return true;
                }
            }
            return false;
        }

        /// Default implementation does nothing. If you want env. items to react to damage, subclass this
        public virtual void DoDamage(float damage, int instanceId, Vector3D position, Vector3 normal, MyDamageType type = MyDamageType.Unknown) { }

        void Physics_ContactPointCallback(ref MyPhysics.MyContactPointEvent e)
        {
            var vel = Math.Abs(e.ContactPointEvent.SeparatingVelocity);
            var other = e.ContactPointEvent.GetOtherEntity(this);

            if (other == null || other.Physics == null) return;

            float otherMass = MyDestructionHelper.MassFromHavok(other.Physics.Mass);

            if (other is Sandbox.Game.Entities.Character.MyCharacter)
                otherMass = other.Physics.Mass;

            float impactEnergy = vel * vel * otherMass;

            // If environment item is hit by a gun, nothing happens here. If you want a weapon damage to env. items, call DoDamage there
            if (impactEnergy > 200000 && !(other is IMyHandheldGunObject<MyDeviceBase>))
            {
                int bodyId = e.ContactPointEvent.Base.BodyA.GetEntity() == this ? 0 : 1;
                var shapeKey = e.ContactPointEvent.GetShapeKey(bodyId);
                var position = Physics.ClusterToWorld(e.ContactPointEvent.ContactPoint.Position);
                var sectorId = MyEnvironmentSector.GetSectorId(position, m_definition.SectorSize);
                HkStaticCompoundShape shape = (HkStaticCompoundShape)Physics.RigidBody.GetShape();
                int physicsInstanceId;
                uint childKey;
                if (shapeKey == uint.MaxValue) //jn: TODO find out why this happens, there is ticket for it https://app.asana.com/0/9887996365574/26645443970236
                {
                    return;
                }
                shape.DecomposeShapeKey(shapeKey, out physicsInstanceId, out childKey);

                int itemInstanceId;
                if (m_physicsShapeInstanceIdToLocalId.TryGetValue(physicsInstanceId, out itemInstanceId))
                {
                    DoDamage(1.0f, itemInstanceId, e.Position, -e.ContactPointEvent.ContactPoint.Normal);
                }
            }
        }

        void DestructionBody_AfterReplaceBody(ref HkdReplaceBodyEvent e)
        {
            e.GetNewBodies(m_tmpBodyInfos);
            foreach (var b in m_tmpBodyInfos)
            {
                var m = b.Body.GetRigidBody().GetRigidBodyMatrix();
                var t = m.Translation;
                var o = Quaternion.CreateFromRotationMatrix(m.GetOrientation());
                Physics.HavokWorld.GetPenetrationsShape(b.Body.BreakableShape.GetShape(), ref t, ref o, m_tmpResults, MyPhysics.DefaultCollisionLayer);
                foreach (var res in m_tmpResults)
                {
                    if (res.GetEntity() is MyVoxelMap)
                    {
                        b.Body.GetRigidBody().Quality = HkCollidableQualityType.Fixed;
                        break;
                    }
                }

                m_tmpResults.Clear();
                b.Body.GetRigidBody();
                b.Body.Dispose();
            }
        }

        /// <summary>
        /// Adds item physics shape to rootShape and returns instance id of added shape instance.
        /// </summary>
        /// <returns>true if ite physics shape has been added, otherwise false.</returns>
        private bool AddPhysicsShape(MyStringId subtypeId, MyModel model, ref MatrixD worldMatrix, Vector3I sectorId, HkStaticCompoundShape sectorRootShape,
            Dictionary<MyStringId, HkShape> subtypeIdToShape, out int physicsShapeInstanceId)
        {
            physicsShapeInstanceId = 0;

            HkShape physicsShape;
            if (!subtypeIdToShape.TryGetValue(subtypeId, out physicsShape))
            {
                HkShape[] shapes = model.HavokCollisionShapes;
                if (shapes == null || shapes.Length == 0)
                    return false;

                Debug.Assert(shapes.Length == 1);

                //List<HkShape> listShapes = new List<HkShape>();
                //for (int i = 0; i < shapes.Length; i++)
                //{
                //    listShapes.Add(shapes[i]);
                //    HkShape.SetUserData(shapes[i], shapes[i].UserData | (int)HkShapeUserDataFlags.EnvironmentItem);
                //}

                //physicsShape = new HkListShape(listShapes.GetInternalArray(), listShapes.Count, HkReferencePolicy.None);
                //HkShape.SetUserData(physicsShape, physicsShape.UserData | (int)HkShapeUserDataFlags.EnvironmentItem);
                physicsShape = shapes[0];
                physicsShape.AddReference();
                subtypeIdToShape[subtypeId] = physicsShape;
            }

            physicsShapeInstanceId = sectorRootShape.AddInstance(physicsShape, worldMatrix);
            Debug.Assert(physicsShapeInstanceId >= 0 && physicsShapeInstanceId < int.MaxValue, "Shape key space overflow");
            return true;
        }

        public void GetItems(ref Vector3D point, List<Vector3D> output)
        {
            Vector3I sectorId = MyEnvironmentSector.GetSectorId(point, m_definition.SectorSize);
            MyEnvironmentSector sector = null;
            if (m_sectors.TryGetValue(sectorId, out sector))
            {
                sector.GetItems(WorldMatrix, output);
            }
        }

        public MyEnvironmentSector GetSector(ref Vector3D worldPosition)
        {
            Vector3I sectorId = MyEnvironmentSector.GetSectorId(worldPosition, m_definition.SectorSize);
            MyEnvironmentSector sector = null;
            if (m_sectors.TryGetValue(sectorId, out sector))
            {
                return sector;
            }

            return null;
        }

        public override void UpdateAfterSimulation()
        {
            base.UpdateAfterSimulation();

            if (MyDebugDrawSettings.ENABLE_DEBUG_DRAW && MyDebugDrawSettings.DEBUG_DRAW_ENVIRONMENT_ITEMS)
            {
                DebugDraw();
            }
        }

        protected override void ClampToWorld()
        {
            return;
        }

        public int GetItemInstanceId(uint shapeKey)
        {
            HkStaticCompoundShape shape = (HkStaticCompoundShape)Physics.RigidBody.GetShape();
            int physicsInstanceId;
            uint childKey;
            if (shapeKey == uint.MaxValue)
                return -1;

            shape.DecomposeShapeKey(shapeKey, out physicsInstanceId, out childKey);
            // Item instance id 
            int itemInstanceId;
            if (!m_physicsShapeInstanceIdToLocalId.TryGetValue(physicsInstanceId, out itemInstanceId))
                return -1;

            return itemInstanceId;
        }

        public MyEnvironmentItemDefinition GetItemDefinition(int itemInstanceId)
        {
            MyEnvironmentItemData itemData = m_itemsData[itemInstanceId];

            MyDefinitionId defId = new MyDefinitionId(m_definition.ItemDefinitionType, itemData.SubtypeId);
            return MyDefinitionManager.Static.GetEnvironmentItemDefinition(defId) as MyEnvironmentItemDefinition;
        }

        public MyEnvironmentItemDefinition GetItemDefinitionFromShapeKey(uint shapeKey)
        {
            int itemInstanceId = GetItemInstanceId(shapeKey);
            if (itemInstanceId == -1)
                return null;

            MyEnvironmentItemData itemData = m_itemsData[itemInstanceId];

            MyDefinitionId defId = new MyDefinitionId(m_definition.ItemDefinitionType, itemData.SubtypeId);
            return MyDefinitionManager.Static.GetEnvironmentItemDefinition(defId) as MyEnvironmentItemDefinition;
        }

        public bool GetItemWorldMatrix(int itemInstanceId, out MatrixD worldMatrix)
        {
            worldMatrix = MatrixD.Identity;

            MyEnvironmentItemData itemData = m_itemsData[itemInstanceId];

            worldMatrix = itemData.Transform.TransformMatrix;
            return true;
        }

        class MyEnviromentItemsDebugDraw : MyDebugRenderComponentBase
        {
            private MyEnvironmentItems m_items;
            public MyEnviromentItemsDebugDraw(MyEnvironmentItems items)
            {
                m_items = items;
            }
            public override bool DebugDraw()
            {
                foreach (var sec in m_items.Sectors)
                {
                    sec.Value.DebugDraw(sec.Key, m_items.m_definition.SectorSize);
                }
                return true;
            }

            public override void DebugDrawInvalidTriangles()
            {
            }
        }
    }
}
