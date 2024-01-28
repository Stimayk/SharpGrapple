using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using System.Drawing;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace SharpGrapple
{
    public class PlayerGrappleInfo
    {
        public bool IsPlayerGrappling { get; set; }
        public Vector? GrappleRaycast { get; set; }
        public CBeam? GrappleWire { get; set; }
    }

    [MinimumApiVersion(125)]
    public partial class SharpGrapple : BasePlugin
    {
        public override string ModuleName => "SharpGrapple";
        public override string ModuleVersion => "0.1";
        public override string ModuleAuthor => "DEAFPS https://github.com/DEAFPS/";
        private readonly Dictionary<int, PlayerGrappleInfo> playerGrapples = new();
        private readonly Dictionary<int, CCSPlayerController> connectedPlayers = new();

        public void InitPlayer(CCSPlayerController player)
        {
            if (player.IsBot || !player.IsValid)
            {
                return;
            }
            else
            {
                connectedPlayers[player.Slot] = player;

                playerGrapples[player.Slot] = new PlayerGrappleInfo();
            }
        }
        public override void Load(bool hotReload)
        {
            Console.WriteLine("[SharpGrapple] Loading...");

            ConVar.Find("player_ping_token_cooldown")?.SetValue(0f);

            if (hotReload) Utilities.GetPlayers().ForEach(InitPlayer);

            RegisterEventHandler<EventPlayerConnectFull>((@event, info) =>
            {
                InitPlayer(@event.Userid);
                return HookResult.Continue;
            });

            RegisterEventHandler<EventPlayerDisconnect>((@event, info) =>
            {
                var player = @event.Userid;

                if (player.IsBot || !player.IsValid)
                {
                    return HookResult.Continue;
                }
                else
                {
                    if (connectedPlayers.TryGetValue(player.Slot, out var connectedPlayer))
                    {
                        connectedPlayers.Remove(player.Slot);
                    }

                    playerGrapples.Remove(player.Slot);

                    return HookResult.Continue;
                }
            });

            RegisterEventHandler<EventPlayerDeath>((@event, info) =>
            {
                DetachGrapple(@event.Userid);
                return HookResult.Continue;
            });

            RegisterEventHandler<EventRoundEnd>((@event, info) =>
            {
                var conVar = ConVar.Find("mp_round_restart_delay");
                if (conVar != null)
                {
                    var round_restart_delay = conVar.GetPrimitiveValue<float>();
                    AddTimer(round_restart_delay - 0.1f, () =>
                    {
                        Utilities.GetPlayers().ForEach(DetachGrapple);
                    });
                    return HookResult.Continue;
                }
                return HookResult.Continue;
            });

            RegisterEventHandler<EventPlayerPing>((@event, info) =>
            {
                var player = @event.Userid;

                if (player.IsBot || !player.IsValid)
                {
                    return HookResult.Continue;
                }
                else
                {
                    GrappleHandler(player, @event);
                }
                return HookResult.Continue;
            });

            RegisterListener<Listeners.OnTick>(() =>
            {
                foreach (var playerEntry in connectedPlayers)
                {
                    var player = playerEntry.Value;

                    if (player == null || !player.IsValid || player.IsBot || !player.PawnIsAlive)
                        continue;

                    if (playerGrapples.TryGetValue(player.Slot, out var grappleInfo) && grappleInfo.IsPlayerGrappling)
                    {
                        if (player == null || player.PlayerPawn == null || player.PlayerPawn?.Value?.CBodyComponent == null || !player.IsValid || !player.PawnIsAlive)
                            continue;

                        Vector? playerPosition = player.PlayerPawn?.Value.CBodyComponent?.SceneNode?.AbsOrigin;
                        QAngle? viewAngles = player.PlayerPawn?.Value?.EyeAngles;

                        if (playerPosition == null || viewAngles == null)
                            continue;

                        Vector? grappleTarget = playerGrapples[player.Slot].GrappleRaycast;
                        if (grappleTarget == null)
                        {
                            continue;
                        }

                        if (playerGrapples[player.Slot].GrappleWire == null)
                        {
                            playerGrapples[player.Slot].GrappleWire = Utilities.CreateEntityByName<CBeam>("beam");

                            if (playerGrapples[player.Slot].GrappleWire == null)
                            {
                                return;
                            }

                            var grappleWire = playerGrapples[player.Slot]?.GrappleWire;
                            if (grappleWire != null)
                            {
                                grappleWire.Render = Color.LimeGreen;
                                grappleWire.Width = 1.5f;
                                grappleWire.EndPos.X = grappleTarget.X;
                                grappleWire.EndPos.Y = grappleTarget.Y;
                                grappleWire.EndPos.Z = grappleTarget.Z;
                                grappleWire.DispatchSpawn();
                            }
                        }


                        if (IsPlayerCloseToTarget(grappleTarget, playerPosition, 100))
                        {
                            DetachGrapple(player);
                            continue;
                        }

                        var angleDifference = CalculateAngleDifference(new Vector(viewAngles.X, viewAngles.Y, viewAngles.Z), grappleTarget - playerPosition);
                        if (angleDifference > 180.0f)
                        {
                            DetachGrapple(player);
                            continue;
                        }

                        if (player == null || player.PlayerPawn == null || player.PlayerPawn.Value.CBodyComponent == null || !player.IsValid || !player.PawnIsAlive || grappleTarget == null || viewAngles == null)
                        {
                            continue;
                        }

                        PullPlayer(player, grappleTarget, playerPosition, viewAngles);

                        if (IsPlayerCloseToTarget(grappleTarget, playerPosition, 100))
                        {
                            DetachGrapple(player);
                        }
                    }
                }
            });

            Console.WriteLine("[SharpGrapple] Plugin Loaded");
        }

        public void GrappleHandler(CCSPlayerController player, EventPlayerPing ping)
        {
            if (player == null) return;

            if (!playerGrapples.ContainsKey(player.Slot))
                playerGrapples[player.Slot] = new PlayerGrappleInfo();


            DetachGrapple(player);
            playerGrapples[player.Slot].IsPlayerGrappling = true;
            playerGrapples[player.Slot].GrappleRaycast = new Vector(ping.X, ping.Y, ping.Z);
        }

        private void PullPlayer(CCSPlayerController player, Vector grappleTarget, Vector playerPosition, QAngle viewAngles)
        {
            if (!player.IsValid || !player.PawnIsAlive)
            {
                return;
            }

            if (player.PlayerPawn?.Value?.CBodyComponent?.SceneNode == null)
            {
                return;
            }

            var direction = grappleTarget - playerPosition;
            var distance = direction.Length();
            direction = new Vector(direction.X / distance, direction.Y / distance, direction.Z / distance); // Normalize manually
            float grappleSpeed = 500.0f;

            var buttons = player.Buttons;

            float adjustmentFactor = 0.5f;

            var rightVector = CalculateRightVector(new Vector(viewAngles.X, viewAngles.Y, viewAngles.Z));

            if ((buttons & PlayerButtons.Moveright) != 0)
            {
                direction += rightVector * adjustmentFactor;
            }
            else if ((buttons & PlayerButtons.Moveleft) != 0)
            {
                direction -= rightVector * adjustmentFactor;
            }

            direction = new Vector(direction.X / direction.Length(), direction.Y / direction.Length(), direction.Z / direction.Length());

            var newVelocity = new Vector(
                direction.X * grappleSpeed,
                direction.Y * grappleSpeed,
                direction.Z * grappleSpeed
            );

            if (player.PlayerPawn.Value.AbsVelocity != null)
            {
                player.PlayerPawn.Value.AbsVelocity.X = newVelocity.X;
                player.PlayerPawn.Value.AbsVelocity.Y = newVelocity.Y;
                player.PlayerPawn.Value.AbsVelocity.Z = newVelocity.Z;
            }

            var grappleWire = playerGrapples[player.Slot].GrappleWire;
            grappleWire?.Teleport(playerPosition, new QAngle(0, 0, 0), new Vector(0, 0, 0));
        }

        private static Vector CalculateRightVector(Vector viewAngles)
        {
            float yaw = (viewAngles.Y - 90.0f) * (float)Math.PI / 180.0f;

            float x = (float)Math.Cos(yaw);
            float y = (float)Math.Sin(yaw);
            float z = 0.0f;

            return new Vector(x, y, z);
        }

        private static bool IsPlayerCloseToTarget(Vector grappleTarget, Vector playerPosition, float thresholdDistance)
        {
            var direction = grappleTarget - playerPosition;
            var distance = direction.Length();

            return distance < thresholdDistance;
        }

        private void DetachGrapple(CCSPlayerController player)
        {
            if (playerGrapples.TryGetValue(player.Slot, out var grappleInfo))
            {
                grappleInfo.IsPlayerGrappling = false;
                grappleInfo.GrappleRaycast = null;

                if (grappleInfo.GrappleWire != null)
                {
                    grappleInfo?.GrappleWire.Remove();
                    grappleInfo!.GrappleWire = null;
                }
            }
        }

        private static float CalculateAngleDifference(Vector angles1, Vector angles2)
        {
            float pitchDiff = Math.Abs(angles1.X - angles2.X);
            float yawDiff = Math.Abs(angles1.Y - angles2.Y);

            pitchDiff = pitchDiff > 180.0f ? 360.0f - pitchDiff : pitchDiff;
            yawDiff = yawDiff > 180.0f ? 360.0f - yawDiff : yawDiff;

            return Math.Max(pitchDiff, yawDiff);
        }
    }
}
