using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Collections;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Network.Messages;
using Robust.Shared.Placement;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Physics3D;

namespace Robust.Server.Placement
{
    public sealed partial class PlacementManager : IPlacementManager
    {
        [Dependency] private IComponentFactory _factory = default!;
        [Dependency] private ITileDefinitionManager _tileDefinitionManager = default!;
        [Dependency] private IServerNetManager _networkManager = default!;
        [Dependency] private IPlayerManager _playerManager = default!;
        [Dependency] private IPrototypeManager _prototype = default!;
        [Dependency] private IServerEntityManager _entityManager = default!;
        [Dependency] private ILogManager _logManager = default!;

        private EntityLookupSystem _lookup => _entityManager.System<EntityLookupSystem>();
        private SharedMapSystem _maps => _entityManager.System<SharedMapSystem>();
        private SharedTransformSystem _xformSystem => _entityManager.System<SharedTransformSystem>();
        private SharedTransform3DSystem _xform3D => _entityManager.System<SharedTransform3DSystem>();
        private SharedPhysics3DSystem _physics3D => _entityManager.System<SharedPhysics3DSystem>();
        private SharedMapGrid3DSystem _maps3D => _entityManager.System<SharedMapGrid3DSystem>();

        //TO-DO: Expand for multiple permission per mob?
        //       Add support for multi-use placeables (tiles etc.).
        public List<PlacementInformation> BuildPermissions { get; set; } = new();

        //Holds build permissions for all mobs. A list of mobs and the objects they're allowed to request and how. One permission per mob.

        public Func<MsgPlacement, bool>? AllowPlacementFunc { get; set; }

        private ISawmill _sawmill = default!;

        #region IPlacementManager Members

        public void Initialize()
        {
            // Someday PlacementManagerSystem my beloved.
            _sawmill = _logManager.GetSawmill("placement");

            _networkManager.RegisterNetMessage<MsgPlacement>(HandleNetMessage);
        }

        /// <summary>
        ///  Handles placement related client messages.
        /// </summary>
        public void HandleNetMessage(MsgPlacement msg)
        {
            if (AllowPlacementFunc != null && !AllowPlacementFunc(msg))
            {
                return;
            }

            switch (msg.PlaceType)
            {
                case PlacementManagerMessage.StartPlacement:
                    break;
                case PlacementManagerMessage.CancelPlacement:
                    break;
                case PlacementManagerMessage.RequestPlacement:
                    HandlePlacementRequest(msg);
                    break;
                case PlacementManagerMessage.RequestEntRemove:
                    HandleEntRemoveReq(msg);
                    break;
                case PlacementManagerMessage.RequestRectRemove:
                    HandleRectRemoveReq(msg);
                    break;
            }
        }

        public void HandlePlacementRequest(MsgPlacement msg)
        {
            var alignRcv = msg.Align;
            var isTile = msg.IsTile;

            int tileType = 0;
            var entityTemplateName = "";

            if (isTile) tileType = msg.TileType;
            else entityTemplateName = msg.EntityTemplateName;

            var dirRcv = msg.DirRcv;

            var session = _playerManager.GetSessionByChannel(msg.MsgChannel);
            if (session.AttachedEntity is not { Valid: true } placer)
                return;

            var plyEntity = _entityManager.GetComponentOrNull<TransformComponent>(placer);

            // Don't have an entity, don't get to place.
            if (plyEntity == null)
                return;

            //TODO: Distance check, so you can't place things off of screen.
            // I don't think that's this manager's biggest problem

            var netCoordinates = msg.NetCoordinates;
            var coordinates = _entityManager.GetCoordinates(netCoordinates);
            PhysicsRayHit3D? placementHit3D = null;
            if (_entityManager.TryGetComponent(placer, out View3DComponent? view3D) && view3D.Enabled)
            {
                var permissionRange = GetPermission(placer, alignRcv)?.Range ?? 16;
                if (!TryReconstructPlacement3D(placer, plyEntity, view3D, Math.Max(1, permissionRange), out coordinates, out var hit3D))
                    return;

                placementHit3D = hit3D;
                dirRcv = new Angle(view3D.Yaw).GetCardinalDir();
            }

            if (!coordinates.IsValid(_entityManager))
            {
                _sawmill.Warning($"{session} tried to place {msg.ObjType} at invalid coordinate {coordinates}");
                return;
            }

            /* TODO: Redesign permission system, or document what this is supposed to be doing
            var permission = GetPermission(session.attachedEntity.Uid, alignRcv);
            if (permission == null)
                return;

            if (permission.Uses > 0)
            {
                permission.Uses--;
                if (permission.Uses <= 0)
                {
                    BuildPermissions.Remove(permission);
                    SendPlacementCancel(session.attachedEntity);
                }
            }
            else
            {
                BuildPermissions.Remove(permission);
                SendPlacementCancel(session.attachedEntity);
                return;
            }
            */
            if (!isTile)
            {
                // Replace existing entities if relevant.
                if (msg.Replacement && _prototype.Index<EntityPrototype>(entityTemplateName).Components.TryGetValue(
                        _factory.GetComponentName<PlacementReplacementComponent>(), out var compRegistry))
                {
                    var key = ((PlacementReplacementComponent)compRegistry.Component).Key;
                    var gridUid = _xformSystem.GetGrid(coordinates);

                    if (_entityManager.TryGetComponent<MapGridComponent>(gridUid, out var grid))
                    {
                        var replacementQuery = _entityManager.GetEntityQuery<PlacementReplacementComponent>();
                        var anc = _maps.GetAnchoredEntities(gridUid.Value, grid, _maps.LocalToTile(gridUid.Value, grid, coordinates));
                        var toDelete = new ValueList<EntityUid>();

                        foreach (var ent in anc)
                        {
                            if (!replacementQuery.TryGetComponent(ent, out var repl) ||
                                repl.Key != key)
                            {
                                continue;
                            }

                            toDelete.Add(ent);
                        }

                        foreach (var ent in toDelete)
                        {
                            var placementEraseEvent = new PlacementEntityEvent(ent, coordinates, PlacementEventAction.Erase, msg.MsgChannel.UserId);
                            _entityManager.EventBus.RaiseEvent(EventSource.Local, placementEraseEvent);

                            _entityManager.DeleteEntity(ent);
                        }
                    }
                }

                var created = _entityManager.SpawnAttachedTo(entityTemplateName, coordinates, rotation: dirRcv.ToAngle());
                if (placementHit3D is { } entityHit)
                {
                    var position3D = entityHit.Position + entityHit.Normal * 0.46f;
                    PromotePlacedEntity3D(created, position3D, dirRcv.ToAngle());
                }

                var placementCreateEvent = new PlacementEntityEvent(created, coordinates, PlacementEventAction.Create, msg.MsgChannel.UserId);
                _entityManager.EventBus.RaiseEvent(EventSource.Local, placementCreateEvent);
            }
            else
            {
                if (placementHit3D is { } tileHit &&
                    _entityManager.TryGetComponent(tileHit.Entity, out MapGrid3DComponent? grid3D))
                {
                    var sample = tileType == 0
                        ? tileHit.Position - tileHit.Normal * 0.05f
                        : tileHit.Position + tileHit.Normal * 0.05f;
                    var cell = _maps3D.WorldToCell((tileHit.Entity, grid3D), sample);
                    if (_maps3D.SetVoxel((tileHit.Entity, grid3D), cell, new Voxel3D(tileType)))
                    {
                        var placementEvent = new PlacementTileEvent(tileType, coordinates, msg.MsgChannel.UserId);
                        _entityManager.EventBus.RaiseEvent(EventSource.Local, placementEvent);
                    }

                    return;
                }

                if (_tileDefinitionManager[tileType].AllowRotationMirror)
                    PlaceNewTile(tileType, coordinates, msg.MsgChannel.UserId, Tile.DirectionToByte(dirRcv), msg.Mirrored);
                else
                    PlaceNewTile(tileType, coordinates, msg.MsgChannel.UserId, Tile.DirectionToByte(Direction.South), false);
            }
        }

        private bool TryReconstructPlacement3D(
            EntityUid placer,
            TransformComponent placerTransform,
            View3DComponent view,
            float range,
            out EntityCoordinates coordinates,
            out PhysicsRayHit3D hit)
        {
            coordinates = EntityCoordinates.Invalid;
            hit = default;
            var origin = _xform3D.GetWorldPosition3D(placer, placerTransform) + Vector3.UnitZ * view.EyeHeight;
            var horizontal = MathF.Cos(view.Pitch);
            var direction = Vector3.Normalize(new Vector3(
                MathF.Sin(view.Yaw) * horizontal,
                MathF.Cos(view.Yaw) * horizontal,
                MathF.Sin(view.Pitch)));
            if (!_physics3D.TryRayCast(
                    placerTransform.MapID,
                    new Ray3D(origin, direction),
                    range,
                    int.MaxValue,
                    placer,
                    false,
                    out hit))
            {
                return false;
            }

            var mapPoint = new MapCoordinates(new Vector2(hit.Position.X, hit.Position.Y), placerTransform.MapID);
            coordinates = _xformSystem.ToCoordinates(placerTransform.ParentUid, mapPoint);
            return coordinates.IsValid(_entityManager);
        }

        private void PromotePlacedEntity3D(EntityUid entity, Vector3 position, Angle angle)
        {
            _xform3D.SetAuthoritative(entity, true);
            _xform3D.SetWorldPosition3D(entity, position);
            _xform3D.SetWorldRotation3D(entity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float) angle.Theta));

            var body = _entityManager.EnsureComponent<PhysicsBody3DComponent>(entity);
            body.BodyType = PhysicsBodyType3D.Static;
            body.GravityScale = 0f;
            body.CanCollide = true;

            var collider = _entityManager.EnsureComponent<Collider3DComponent>(entity);
            if (collider.Shapes.Count == 0)
            {
                collider.Shapes.Add(new BoxShape3D
                {
                    Size = new Vector3(0.9f),
                    CollisionLayer = int.MaxValue,
                    CollisionMask = int.MaxValue,
                    Friction = 0.75f,
                });
            }

            var primitive = _entityManager.EnsureComponent<Primitive3DComponent>(entity);
            primitive.Size = new Vector3(0.9f);
            primitive.Color = new Color(0.48f, 0.55f, 0.62f);
            body.Dirty(_entityManager);
            collider.Dirty(_entityManager);
            primitive.Dirty(_entityManager);
            _physics3D.RefreshBody(entity);
        }

        private void PlaceNewTile(int tileType, EntityCoordinates coordinates, NetUserId placingUserId, byte direction, bool mirrored)
        {
            if (!coordinates.IsValid(_entityManager)) return;

            var mapSystem = _maps;

            MapGridComponent? grid;

            EntityUid gridId = coordinates.EntityId;
            if (_entityManager.TryGetComponent(coordinates.EntityId, out grid)
                || mapSystem.TryFindGridAt(_xformSystem.ToMapCoordinates(coordinates), out gridId, out grid))
            {
                mapSystem.SetTile(gridId, grid, coordinates, new Tile(tileType, rotationMirroring: (byte)(direction + (mirrored ? 4 : 0))));

                var placementEraseEvent = new PlacementTileEvent(tileType, coordinates, placingUserId);
                _entityManager.EventBus.RaiseEvent(EventSource.Local, placementEraseEvent);
            }
            else if (tileType != 0) // create a new grid
            {
                var newGrid = mapSystem.CreateGridEntity(_xformSystem.GetMapId(coordinates));
                var newGridXform = new Entity<TransformComponent>(
                    newGrid.Owner,
                    _entityManager.GetComponent<TransformComponent>(newGrid));

                _xformSystem.SetWorldPosition(newGridXform, coordinates.Position - newGrid.Comp.TileSizeHalfVector); // assume bottom left tile origin
                var tilePos = mapSystem.WorldToTile(newGrid.Owner, newGrid.Comp, coordinates.Position);
                mapSystem.SetTile(newGrid.Owner, newGrid.Comp, tilePos, new Tile(tileType, rotationMirroring: (byte)(direction + (mirrored ? 4 : 0))));

                var placementEraseEvent = new PlacementTileEvent(tileType, coordinates, placingUserId);
                _entityManager.EventBus.RaiseEvent(EventSource.Local, placementEraseEvent);
            }
        }

        /// <summary>
        /// Deletes any existing entity.
        /// </summary>
        /// <param name="msg"></param>
        private void HandleEntRemoveReq(MsgPlacement msg)
        {
            //TODO: Some form of admin check
            var entity = _entityManager.GetEntity(msg.EntityUid);

            if (!_entityManager.EntityExists(entity))
                return;

            var placementEraseEvent = new PlacementEntityEvent(entity,
                _entityManager.GetComponent<TransformComponent>(entity).Coordinates,
                PlacementEventAction.Erase,
                msg.MsgChannel.UserId);

            _entityManager.EventBus.RaiseEvent(EventSource.Local, placementEraseEvent);
            _entityManager.DeleteEntity(entity);
        }

        /// <summary>
        /// Deletes almost any existing entity within a selection box.
        /// </summary>
        /// <param name="msg"></param>
        private void HandleRectRemoveReq(MsgPlacement msg)
        {
            var centerCoords = _xformSystem.ToMapCoordinates(msg.NetCoordinates);
            var centerPos = centerCoords.Position;

            var box = Box2.CenteredAround(centerPos, msg.RectSize);
            var boxRotated = new Box2Rotated(box, msg.RectRotation, centerPos);

            foreach (var entity in _lookup.GetEntitiesIntersecting(centerCoords.MapId, boxRotated))
            {
                if (_entityManager.Deleted(entity)
                    || _entityManager.HasComponent<MapGridComponent>(entity)
                    || _entityManager.HasComponent<ActorComponent>(entity))
                    continue;

                var xform = _entityManager.GetComponent<TransformComponent>(entity);
                var parent = xform.ParentUid;
                var isChildOfActor = false;

                while (parent.IsValid())
                {
                    if (_entityManager.HasComponent<ActorComponent>(parent))
                    {
                        isChildOfActor = true;
                        break;
                    }

                    if (_entityManager.TryGetComponent<TransformComponent>(parent, out var parentXform))
                    {
                        parent = parentXform.ParentUid;
                    }
                    else
                    {
                        break;
                    }
                }

                if (isChildOfActor)
                    continue;

                var placementEraseEvent = new PlacementEntityEvent(entity,
                    _entityManager.GetComponent<TransformComponent>(entity).Coordinates,
                    PlacementEventAction.Erase,
                    msg.MsgChannel.UserId);

                _entityManager.EventBus.RaiseEvent(EventSource.Local, placementEraseEvent);
                _entityManager.DeleteEntity(entity);
            }
        }

        /// <summary>
        ///  Places mob in entity placement mode with given settings.
        /// </summary>
        public void SendPlacementBegin(EntityUid mob, int range, string objectType, string alignOption)
        {
            if (!_entityManager.TryGetComponent(mob, out ActorComponent? actor))
                return;

            var playerConnection = actor.PlayerSession.Channel;

            var message = new MsgPlacement
            {
                PlaceType = PlacementManagerMessage.StartPlacement,
                Range = range,
                IsTile = false,
                ObjType = objectType,
                AlignOption = alignOption
            };
            _networkManager.ServerSendMessage(message, playerConnection);
        }

        /// <summary>
        ///  Places mob in tile placement mode with given settings.
        /// </summary>
        public void SendPlacementBeginTile(EntityUid mob, int range, string tileType, string alignOption)
        {
            if (!_entityManager.TryGetComponent(mob, out ActorComponent? actor))
                return;

            var playerConnection = actor.PlayerSession.Channel;

            var message = new MsgPlacement
            {
                PlaceType = PlacementManagerMessage.StartPlacement,
                Range = range,
                IsTile = true,
                ObjType = tileType,
                AlignOption = alignOption
            };
            _networkManager.ServerSendMessage(message, playerConnection);
        }

        /// <summary>
        ///  Cancels object placement mode for given mob.
        /// </summary>
        public void SendPlacementCancel(EntityUid mob)
        {
            if (!_entityManager.TryGetComponent(mob, out ActorComponent? actor))
                return;

            var playerConnection = actor.PlayerSession.Channel;

            var message = new MsgPlacement
            {
                PlaceType = PlacementManagerMessage.CancelPlacement
            };
            _networkManager.ServerSendMessage(message, playerConnection);
        }

        /// <summary>
        ///  Gives Mob permission to place entity and places it in object placement mode.
        /// </summary>
        public void StartBuilding(EntityUid mob, int range, string objectType, string alignOption)
        {
            AssignBuildPermission(mob, range, objectType, alignOption);
            SendPlacementBegin(mob, range, objectType, alignOption);
        }

        /// <summary>
        ///  Gives Mob permission to place tile and places it in object placement mode.
        /// </summary>
        public void StartBuildingTile(EntityUid mob, int range, string tileType, string alignOption)
        {
            AssignBuildPermission(mob, range, tileType, alignOption);
            SendPlacementBeginTile(mob, range, tileType, alignOption);
        }

        /// <summary>
        ///  Revokes open placement Permission and cancels object placement mode.
        /// </summary>
        public void CancelBuilding(EntityUid mob)
        {
            RevokeAllBuildPermissions(mob);
            SendPlacementCancel(mob);
        }

        /// <summary>
        ///  Gives a mob a permission to place a given Entity.
        /// </summary>
        public void AssignBuildPermission(EntityUid mob, int range, string objectType, string alignOption)
        {
            var newPermission = new PlacementInformation
            {
                MobUid = mob,
                Range = range,
                IsTile = false,
                EntityType = objectType,
                PlacementOption = alignOption
            };

            IEnumerable<PlacementInformation> mobPermissions = from PlacementInformation permission in BuildPermissions
                                                               where permission.MobUid == mob
                                                               select permission;

            if (mobPermissions.Any()) //Already has one? Revoke the old one and add this one.
            {
                RevokeAllBuildPermissions(mob);
                BuildPermissions.Add(newPermission);
            }
            else
            {
                BuildPermissions.Add(newPermission);
            }
        }

        /// <summary>
        ///  Gives a mob a permission to place a given Tile.
        /// </summary>
        public void AssignBuildPermissionTile(EntityUid mob, int range, string tileType, string alignOption)
        {
            var newPermission = new PlacementInformation
            {
                MobUid = mob,
                Range = range,
                IsTile = true,
                TileType = _tileDefinitionManager[tileType].TileId,
                PlacementOption = alignOption
            };

            IEnumerable<PlacementInformation> mobPermissions = from PlacementInformation permission in BuildPermissions
                                                               where permission.MobUid == mob
                                                               select permission;

            if (mobPermissions.Any()) //Already has one? Revoke the old one and add this one.
            {
                RevokeAllBuildPermissions(mob);
                BuildPermissions.Add(newPermission);
            }
            else
            {
                BuildPermissions.Add(newPermission);
            }
        }

        /// <summary>
        ///  Removes all building Permissions for given mob.
        /// </summary>
        public void RevokeAllBuildPermissions(EntityUid mob)
        {
            var mobPermissions = BuildPermissions
                .Where(permission => permission.MobUid == mob)
                .ToList();

            if (mobPermissions.Count != 0)
                BuildPermissions.RemoveAll(x => mobPermissions.Contains(x));
        }

        #endregion IPlacementManager Members

        private PlacementInformation? GetPermission(EntityUid uid, string alignOpt)
        {
            foreach (var buildPermission in BuildPermissions)
            {
                if (buildPermission.MobUid == uid && buildPermission.PlacementOption == alignOpt)
                {
                    return buildPermission;
                }
            }

            return null;
        }
    }
}
