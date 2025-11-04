using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

[assembly: ModInfo(
    "HardcoreLogOff",
    "hardcorelogoff",
    Authors = new[] { "KO Teknik" },
    Description = "Snapshot & restore of mobs around disconnect/connect events.",
    Version = "0.0.1"
)]

namespace HardcoreLogOff
{
    /// <summary>
    /// Server-side entrypoint.
    /// 
    /// What this mod does (RAM-only):
    /// - On player disconnect: takes a local snapshot of nearby “lore” mobs (filtered by prefixes in config).
    /// - On the same player join (PlayerNowPlaying): restores only the missing mobs (top-up), so it’s idempotent and safe.
    /// - Drifters’ crawling posture is captured and restored when possible.
    ///
    /// Notes:
    /// - No disk persistence yet (by design here). Snapshots live in memory; a server restart loses them.
    /// - You can add persistent saves later (scheduling and /hlsave) without changing this Core.
    ///
    /// Admin command included:
    /// - /lorecount : list how many living entities are around you by code path, within the configured radius.
    /// </summary>
    /// 
    /// Future ideas:
    /// save - /hlsave : force-save all current RAM snapshots to disk (per-zone files?)
    /// Auto-save : periodic background save to disk custom hour to match auto server restarts
    /// Add Admin command to save current zone on-demand
    /// respawn mobs with Skin Variant, pitch & yaw , crawling....
    /// Auto ban players on logoff few seconds with incrementing duration
    /// Spawn a clone player entity that stays when player disconnects and can take damage
    /// Drink a gin tonic and stay quiet

    public sealed class Core : ModSystem
    {
        private ICoreServerAPI sapi = null!;
        private HLOConfig cfg = null!;
        private MobSnapshotManager mgr = null!;

        // Filenames are kept for future extensibility (even if we stay RAM-only for now).
        private const string CfgName = "HardcoreLogOffConfig.json";
        private const string CfgBackupName = "HardcoreLogOffConfig.backup.json";
        private const string CurrentConfigVersion = "1.0.0";  // Update here and in Core.cs

        /// <summary>
        /// Server bootstrap: load config, create snapshot manager, hook events, register commands.
        /// </summary>
        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            // Config is fully handled by HLConfig (versioning, safe upgrade, backup)
            cfg = HLOConfig.LoadOrCreate(
                sapi,
                CfgName,
                CfgBackupName,
                CurrentConfigVersion,
                copyFieldsFromOldOnUpgrade: true
            );
            sapi.Logger.Notification("[HLO] Config loaded.");

            // Manager (RAM-only snapshots)
            mgr = new MobSnapshotManager(sapi, cfg);

            // Hooks
            sapi.Event.PlayerDisconnect += OnPlayerDisconnect;
            sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;

            // Admin command(s)
            sapi.ChatCommands
                .Create("lorecount")
                .WithDescription("List living mobs around you (within the configured radius).")
                .RequiresPrivilege(Privilege.root)
                .HandleWith(LoreCountCmd);

            sapi.Logger.Notification("[HLO] Server-side started.");
        }

        /// <summary>
        /// Clean event unsubscription to avoid double-registration on hot-reload or multi-load scenarios.
        /// </summary>
        public override void Dispose()
        {
            if (sapi != null)
            {
                sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
                sapi.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
            }
            base.Dispose();
        }

        /// <summary>
        /// /lorecount — shows counts of living entities around the caller, grouped by Code.Path.
        /// Useful to debug prefixes, radius, or check what will be snapshotted.
        /// </summary>
        private TextCommandResult LoreCountCmd(TextCommandCallingArgs args)
        {
            var sp = args.Caller.Player as IServerPlayer;
            if (sp?.Entity == null)
                return TextCommandResult.Error("This command must be executed in-game by a player.");

            int radius = cfg.CaptureRadiusBlocks;
            var pos = sp.Entity.ServerPos.AsBlockPos;

            var ents = sapi.World.GetEntitiesAround(pos.ToVec3d(), radius, radius, null);
            if (ents == null || ents.Length == 0)
                return TextCommandResult.Success("[HLO] No mobs found.");

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var e in ents)
            {
                if (e == null || e is EntityPlayer) continue;
                if (!e.Alive) continue;

                var path = e.Code?.Path ?? "";
                if (string.IsNullOrWhiteSpace(path)) continue;

                counts[path] = counts.TryGetValue(path, out var n) ? n + 1 : 1;
            }

            if (counts.Count == 0)
                return TextCommandResult.Success($"[HLO] No living mobs inside {radius} blocks.");

            var ordered = counts.OrderByDescending(kv => kv.Value).ToList();
            var summary = string.Join(", ", ordered.Select(kv => $"{kv.Key}:{kv.Value}"));

            sapi.Logger.Notification($"[HLO] /lorecount by {sp.PlayerName} @ ({pos.X},{pos.Y},{pos.Z}) -> {summary}");
            return TextCommandResult.Success($"[HLO] Alive in {radius} blocks: {summary}");
        }

        /// <summary>
        /// On player disconnect: build a zone key and snapshot nearby lore mobs into RAM.
        /// </summary>
        private void OnPlayerDisconnect(IServerPlayer? sp)
        {
            if (sp?.Entity is not { } ent) return;

            var pos = ent.ServerPos.AsBlockPos;
            var zk = mgr.ZoneOf(pos);

            if (cfg.VerboseLogging)
                sapi.Logger.Debug($"[HLO] Disconnect {sp.PlayerName}: snapshot zone [{zk.ZX},{zk.ZZ}] @ {mgr.FormatPretty(pos)}.");

            mgr.SnapshotAtDisconnect(pos, sp.PlayerName ?? "Unknown", zk);
        }

        /// <summary>
        /// On player now playing: schedule a short delayed restore attempt (top-up from RAM snapshot).
        /// </summary>
        private void OnPlayerNowPlaying(IServerPlayer? sp)
        {
            if (sp?.Entity is not { } ent) return;

            sapi.Event.RegisterCallback(_ =>
            {
                // sp captured; null-checked above
                mgr.TryRestoreOnJoin(sp);
            }, Math.Max(0, cfg.RestoreDelayMsAfterJoin));
        }
    }
}
