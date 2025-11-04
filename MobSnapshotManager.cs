using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace HardcoreLogOff
{
    /// <summary>
    /// Snapshot/restore manager for "lore" mobs.
    ///
    /// Design:
    /// - On player disconnect: take a snapshot of nearby entities filtered by code prefixes.
    /// - On next player join (same zone): top-up missing entities using the snapshot counts.
    /// - Chunk columns are pinned during restore; despawn behaviors are temporarily frozen.
    /// - Snapshots are kept in-memory only (lost on server restart).
    /// </summary>
    public sealed class MobSnapshotManager
    {
        private readonly ICoreServerAPI sapi;
        private readonly HLOConfig cfg;

        private readonly Dictionary<ZoneKey, ZoneSnapshot> zoneSnapshots = new();
        private readonly Dictionary<ZoneKey, bool> zoneRestoreBusy = new();
        private readonly object zoneLock = new object();

        // ----------- Data Models -----------

        public struct ZoneKey
        {
            public int ZX;
            public int ZZ;
            public ZoneKey(int zx, int zz) { ZX = zx; ZZ = zz; }
        }

        private sealed class LoreMobSnapshot
        {
            public string Code = "";  // e.g. "game:drifter-tainted"
            public double X, Y, Z;    // spawn position
        }

        private sealed class ZoneSnapshot
        {
            public List<LoreMobSnapshot> Mobs = new();
            public DateTime ExpiresAtUtc;
        }

        // ----------- Ctor -----------

        public MobSnapshotManager(ICoreServerAPI sapi, HLOConfig cfg)
        {
            this.sapi = sapi;
            this.cfg = cfg;
        }

        // ----------- Pretty logs -----------

        private BlockPos Origin
        {
            get
            {
                var dsp = sapi.World.DefaultSpawnPosition;
                return (dsp != null ? dsp.AsBlockPos : new BlockPos(0, 0, 0));
            }
        }

        public string FormatPretty(BlockPos p)
        {
            var o = Origin;
            return $"({p.X - o.X},{p.Y},{p.Z - o.Z})";
        }

        private int JitterMs() => sapi.World.Rand.Next(200, 601); // 200..600ms

        // -----------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------

        public ZoneKey ZoneOf(BlockPos pos)
        {
            int size = Math.Max(32, cfg.ZoneSizeBlocks);
            return new ZoneKey(FloorDiv(pos.X, size), FloorDiv(pos.Z, size));
        }

        public void SnapshotAtDisconnect(BlockPos center, string who, ZoneKey zk)
        {
            var list = CaptureLoreAround(center, cfg.CaptureRadiusBlocks);
            if (list.Count == 0)
            {
                if (cfg.VerboseLogging)
                    sapi.Logger.Debug($"[HLO] Zone [{zk.ZX},{zk.ZZ}] empty snapshot ignored @ {FormatPretty(center)} (disconnect {who}).");
                return;
            }

            int ttl = RandTtlMinutes();
            zoneSnapshots[zk] = new ZoneSnapshot
            {
                Mobs = list,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(ttl)
            };

            sapi.Logger.Notification($"[HLO] Zone [{zk.ZX},{zk.ZZ}] sealed ({list.Count} mobs) @ {FormatPretty(center)}. Expires in ~{ttl} min.");
        }

        public void TryRestoreOnJoin(IServerPlayer sp)
        {
            if (sp == null || sp.Entity == null) return;

            var ent = sp.Entity;
            string playerName = sp.PlayerName ?? "Unknown";

            if (cfg.VerboseLogging)
                sapi.Logger.Debug($"[HLO] NowPlaying {playerName} at {FormatPretty(ent.ServerPos.AsBlockPos)}");

            var pos = ent.ServerPos.AsBlockPos;
            var zk = ZoneOf(pos);

            if (!zoneSnapshots.TryGetValue(zk, out var snap))
            {
                if (cfg.VerboseLogging)
                    sapi.Logger.Debug($"[HLO] Join {playerName}: no snapshot for zone [{zk.ZX},{zk.ZZ}] @ {FormatPretty(pos)}.");
                return;
            }

            if (DateTime.UtcNow > snap.ExpiresAtUtc)
            {
                zoneSnapshots.Remove(zk);
                if (cfg.VerboseLogging)
                    sapi.Logger.Debug($"[HLO] Zone [{zk.ZX},{zk.ZZ}] snapshot expired → removed.");
                return;
            }

            bool triggersPresent = AnyAliveWithPrefixesAround(pos, cfg.CaptureRadiusBlocks, cfg.PresenceTriggers);
            List<string> allowedPrefixes = triggersPresent ? cfg.TargetRestorePrefixes : cfg.SavePrefixes;

            var want = CountsFromSnapshot(snap, allowedPrefixes);
            var have = AliveCountsByPrefix(pos, cfg.CaptureRadiusBlocks, allowedPrefixes);
            var need = ComputeNeed(want, have);

            if (Sum(need) <= 0)
            {
                if (cfg.VerboseLogging)
                {
                    string mode = triggersPresent ? "TRIGGER→TARGETS" : "FULL";
                    sapi.Logger.Debug($"[HLO] Zone [{zk.ZX},{zk.ZZ}] @ {FormatPretty(pos)} nothing to top-up (mode={mode}). Want={FmtCounts(want)} Have={FmtCounts(have)}");
                }
                zoneSnapshots.Remove(zk);
                return;
            }

            lock (zoneLock)
            {
                if (zoneRestoreBusy.TryGetValue(zk, out var busy) && busy)
                {
                    sapi.Event.RegisterCallback(_ => TryRestoreOnJoin(sp), JitterMs());
                    if (cfg.VerboseLogging)
                        sapi.Logger.Debug($"[HLO] Zone [{zk.ZX},{zk.ZZ}] busy → re-schedule with jitter.");
                    return;
                }
                zoneRestoreBusy[zk] = true;
            }

            var pinned = PinColumnsForZone(zk, cfg.ChunkRadiusPin);

            int attempts = 0;
            Action tryRestore = null;

            tryRestore = () =>
            {
                attempts++;

                if (!EnsureColumnsReadyForZone(zk))
                {
                    if (attempts < cfg.MaxRestoreAttempts)
                    {
                        if (cfg.VerboseLogging)
                            sapi.Logger.Debug($"[HLO] Zone [{zk.ZX},{zk.ZZ}] columns not ready (try {attempts}) → retry in {cfg.RetryIntervalMs} ms.");
                        sapi.Event.RegisterCallback(_ => tryRestore(), Math.Max(50, cfg.RetryIntervalMs));
                        return;
                    }

                    if (cfg.ForceRestoreIfStillNotReady)
                    {
                        sapi.Logger.Warning($"[HLO] Zone [{zk.ZX},{zk.ZZ}] columns not ready after {attempts} tries → FORCED RESTORE (top-up).");
                        DoRestoreTopUp(snap, zk, allowedPrefixes, need, pinned, pos);
                    }
                    else
                    {
                        sapi.Logger.Warning($"[HLO] Zone [{zk.ZX},{zk.ZZ}] abandon restore (columns not ready).");
                        UnpinColumns(pinned);
                    }

                    lock (zoneLock) zoneRestoreBusy[zk] = false;
                    return;
                }

                DoRestoreTopUp(snap, zk, allowedPrefixes, need, pinned, pos);
                lock (zoneLock) zoneRestoreBusy[zk] = false;
            };

            int startDelay = Math.Max(50, cfg.RestoreDelayMsAfterJoin) + JitterMs();
            if (cfg.VerboseLogging)
            {
                string mode = triggersPresent ? "TRIGGER→TARGETS (top-up)" : "FULL (top-up)";
                sapi.Logger.Debug($"[HLO] Attempt restore zone [{zk.ZX},{zk.ZZ}] @ {FormatPretty(pos)} in {startDelay} ms. Mode={mode} Need={FmtCounts(need)}");
            }

            sapi.Event.RegisterCallback(_ => tryRestore(), startDelay);
        }

        // -----------------------------------------------------------------
        // Capture / Restore
        // -----------------------------------------------------------------

        private List<LoreMobSnapshot> CaptureLoreAround(BlockPos center, int radius)
        {
            var centerV = center.ToVec3d();
            var ents = sapi.World.GetEntitiesAround(centerV, radius, radius, null);

            var list = new List<(double dist2, LoreMobSnapshot snap)>();
            if (ents == null) return new List<LoreMobSnapshot>();

            foreach (var e in ents)
            {
                if (e == null || e is EntityPlayer) continue;
                if (!e.Alive) continue;
                if (IsExcluded(e)) continue;

                string path = e.Code != null ? (e.Code.Path ?? "") : "";
                if (!HasAnyPrefix(path, cfg.SavePrefixes)) continue;

                var snap = new LoreMobSnapshot
                {
                    Code = e.Code != null ? (e.Code.ToShortString() ?? "") : "",
                    X = e.ServerPos.X,
                    Y = e.ServerPos.Y,
                    Z = e.ServerPos.Z
                };

                double dx = e.ServerPos.X - centerV.X;
                double dy = e.ServerPos.Y - centerV.Y;
                double dz = e.ServerPos.Z - centerV.Z;
                list.Add((dx * dx + dy * dy + dz * dz, snap));
            }

            // Nearest-first ordering (helps spawn closest first on restore)
            list.Sort((a, b) => a.dist2.CompareTo(b.dist2));

            var ordered = new List<LoreMobSnapshot>(list.Count);
            for (int i = 0; i < list.Count; i++) ordered.Add(list[i].snap);

            if (cfg.UseMaxMobs && ordered.Count > cfg.MaxMobsSave)
            {
                ordered.RemoveRange(cfg.MaxMobsSave, ordered.Count - cfg.MaxMobsSave);
            }

            return ordered;
        }

        /// <summary>Top-up restore: only spawn the missing counts per prefix.</summary>
        private void DoRestoreTopUp(
            ZoneSnapshot snap,
            ZoneKey zk,
            List<string> allowedPrefixes,
            Dictionary<string, int> need,
            List<Vec2i> pinned,
            BlockPos refPos)
        {
            int ok = 0, fail = 0;
            var spawnedIds = new List<long>();

            // Group snapshot entries by prefix for quick selection.
            var byPrefix = new Dictionary<string, List<LoreMobSnapshot>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in snap.Mobs)
            {
                string codePath = CodePathOf(m.Code);
                string prefix = MatchPrefix(codePath, allowedPrefixes);
                if (prefix == null) continue;

                if (!byPrefix.TryGetValue(prefix, out var lst))
                {
                    lst = new List<LoreMobSnapshot>();
                    byPrefix[prefix] = lst;
                }
                lst.Add(m);
            }

            foreach (var kv in need)
            {
                string prefix = kv.Key;
                int toSpawn = kv.Value;
                if (toSpawn <= 0) continue;

                if (!byPrefix.TryGetValue(prefix, out var pool) || pool == null || pool.Count == 0) continue;

                // Prefer entities closest to the player reference position.
                pool.Sort((A, B) =>
                {
                    double da = (A.X - refPos.X) * (A.X - refPos.X)
                              + (A.Y - refPos.Y) * (A.Y - refPos.Y)
                              + (A.Z - refPos.Z) * (A.Z - refPos.Z);
                    double db = (B.X - refPos.X) * (B.X - refPos.X)
                              + (B.Y - refPos.Y) * (B.Y - refPos.Y)
                              + (B.Z - refPos.Z) * (B.Z - refPos.Z);
                    return da.CompareTo(db);
                });

                int count = Math.Min(toSpawn, pool.Count);

                for (int i = 0; i < count; i++)
                {
                    var m = pool[i];
                    long id = RestoreOneGetId(m);
                    if (id >= 0)
                    {
                        ok++;
                        spawnedIds.Add(id);

                        if (cfg.VerboseLogging)
                        {
                            var e = sapi.World.GetEntityById(id);
                            if (e != null)
                            {
                                sapi.Logger.Debug($"[HLO] Spawn {m.Code} @ {FormatPretty(e.ServerPos.AsBlockPos)}");
                            }
                        }
                    }
                    else
                    {
                        fail++;
                    }
                }
            }

            // Simple presence check after 1 second.
            sapi.Event.RegisterCallback(_ =>
            {
                int still = 0;
                for (int i = 0; i < spawnedIds.Count; i++)
                {
                    var e = sapi.World.GetEntityById(spawnedIds[i]);
                    if (e != null && e.Alive) still++;
                }
                sapi.Logger.Notification($"[HLO] Zone [{zk.ZX},{zk.ZZ}] @ {FormatPretty(refPos)} check+1s: {still}/{spawnedIds.Count} still present.");
            }, 1000);

            // Unpin later to let AI settle a bit if requested.
            sapi.Event.RegisterCallback(_ => UnpinColumns(pinned), Math.Max(0, cfg.KeepPinnedAfterRestoreMs));

            sapi.Logger.Notification($"[HLO] Zone [{zk.ZX},{zk.ZZ}] @ {FormatPretty(refPos)} restore (top-up): {ok} spawn(s){(fail > 0 ? $", {fail} fail(s)" : "")}.");
            zoneSnapshots.Remove(zk);
        }

        private long RestoreOneGetId(LoreMobSnapshot m)
        {
            if (string.IsNullOrEmpty(m.Code)) return -1;

            var etype = sapi.World.GetEntityType(new AssetLocation(m.Code));
            if (etype == null) return -1;

            var ent = sapi.World.ClassRegistry.CreateEntity(etype);
            if (ent == null) return -1;

            var bp = new BlockPos((int)m.X, (int)m.Y, (int)m.Z);
            var safe = FindSafeGround(bp, 8, 24);

            if (safe == null) ent.ServerPos.SetPos(m.X, m.Y, m.Z);
            else ent.ServerPos.SetPos(safe.X + 0.5, safe.Y + 1.01, safe.Z + 0.5);

            // No yaw/pitch restore by design (lighter & less error-prone)
            ent.ServerPos.Roll = 0;

            sapi.World.SpawnEntity(ent);

            // Small anti-stuck bump if placed on a solid cap.
            try
            {
                var ba = sapi.World.BlockAccessor;
                var under = new BlockPos((int)ent.ServerPos.X, (int)ent.ServerPos.Y - 1, (int)ent.ServerPos.Z);
                var b = ba.GetBlock(under);
                if (b != null && b.CollisionBoxes != null && b.CollisionBoxes.Length > 0)
                {
                    ent.ServerPos.Y += 0.05;
                    ent.Pos.Y = ent.ServerPos.Y;
                    ent.PositionBeforeFalling.Y = ent.ServerPos.Y;
                }
            }
            catch { /* best effort */ }

            FreezeAllDespawnsFor(ent, cfg.GraceSecondsAfterRestore);
            return ent.EntityId;
        }

        // -----------------------------------------------------------------
        // Presence / counting
        // -----------------------------------------------------------------

        private bool AnyAliveWithPrefixesAround(BlockPos center, int radius, List<string> prefixes)
        {
            var ents = sapi.World.GetEntitiesAround(center.ToVec3d(), radius, radius, null);
            if (ents == null) return false;

            foreach (var e in ents)
            {
                if (e == null || e is EntityPlayer) continue;
                if (!e.Alive) continue;

                string path = e.Code != null ? (e.Code.Path ?? "") : "";
                if (HasAnyPrefix(path, prefixes) && !IsExcluded(e)) return true;
            }
            return false;
        }

        private Dictionary<string, int> AliveCountsByPrefix(BlockPos center, int radius, List<string> prefixes)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ents = sapi.World.GetEntitiesAround(center.ToVec3d(), radius, radius, null);
            if (ents == null) return map;

            foreach (var e in ents)
            {
                if (e == null || e is EntityPlayer) continue;
                if (!e.Alive) continue;

                string path = e.Code != null ? (e.Code.Path ?? "") : "";
                string prefix = MatchPrefix(path, prefixes);
                if (prefix != null && !IsExcluded(e))
                {
                    if (!map.TryGetValue(prefix, out var n)) n = 0;
                    map[prefix] = n + 1;
                }
            }
            return map;
        }

        private static Dictionary<string, int> CountsFromSnapshot(ZoneSnapshot snap, List<string> prefixes)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var m in snap.Mobs)
            {
                string path = CodePathOf(m.Code);
                string prefix = MatchPrefix(path, prefixes);
                if (prefix == null) continue;

                if (!map.TryGetValue(prefix, out var n)) n = 0;
                map[prefix] = n + 1;
            }
            return map;
        }

        private static Dictionary<string, int> ComputeNeed(Dictionary<string, int> want, Dictionary<string, int> have)
        {
            var need = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in want)
            {
                int already = have.TryGetValue(kv.Key, out var h) ? h : 0;
                int n = kv.Value - already;
                if (n > 0) need[kv.Key] = n;
            }
            return need;
        }

        private static int Sum(Dictionary<string, int> map)
        {
            int s = 0;
            foreach (var kv in map) s += kv.Value;
            return s;
        }

        private static string MatchPrefix(string path, List<string> prefixes)
        {
            if (string.IsNullOrEmpty(path) || prefixes == null) return null;
            for (int i = 0; i < prefixes.Count; i++)
            {
                string p = prefixes[i];
                if (!string.IsNullOrWhiteSpace(p) &&
                    path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        // -----------------------------------------------------------------
        // Chunk columns / pin / readiness
        // -----------------------------------------------------------------

        private bool EnsureColumnsReadyForZone(ZoneKey zk)
        {
            int size = Math.Max(32, cfg.ZoneSizeBlocks);
            int ccx = (zk.ZX * size) >> 5; // /32
            int ccz = (zk.ZZ * size) >> 5;

            int chunksY = sapi.World.BlockAccessor.MapSizeY >> 5;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    int cx = ccx + dx, cz = ccz + dz;
                    bool ready = false;
                    for (int cy = 0; cy < chunksY; cy++)
                    {
                        var ch = sapi.WorldManager.GetChunk(cx, cy, cz);
                        if (ch != null) { ready = true; break; }
                    }
                    if (!ready) return false;
                }
            }
            return true;
        }

        private List<Vec2i> PinColumnsForZone(ZoneKey zk, int r)
        {
            var pinned = new List<Vec2i>();
            if (r <= 0) return pinned;

            int size = Math.Max(32, cfg.ZoneSizeBlocks);
            int ccx = (zk.ZX * size) >> 5;
            int ccz = (zk.ZZ * size) >> 5;

            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    int cx = ccx + dx, cz = ccz + dz;
                    sapi.WorldManager.LoadChunkColumn(cx, cz, keepLoaded: true);
                    pinned.Add(new Vec2i(cx, cz));
                }
            }
            return pinned;
        }

        private void UnpinColumns(List<Vec2i> pinned)
        {
            if (pinned == null) return;
            for (int i = 0; i < pinned.Count; i++)
            {
                var col = pinned[i];
                sapi.WorldManager.LoadChunkColumn(col.X, col.Y, keepLoaded: false);
            }
            pinned.Clear();
        }

        // -----------------------------------------------------------------
        // Anti-despawn (reflection-based best effort)
        // -----------------------------------------------------------------

        private void FreezeAllDespawnsFor(Entity ent, int seconds)
        {
            if (seconds <= 0 || ent == null) return;

            var list = FindAllDespawnBehaviors(ent);
            if (list.Count == 0) return;

            var originals = new List<(object bh, int dist, int sec)>();
            for (int i = 0; i < list.Count; i++)
            {
                var bh = list[i];
                if (TryReadDespawn(bh, out var od, out var os))
                {
                    originals.Add((bh, od, os));
                    SetDespawn(bh, 999_999, 999_999);
                }
            }

            sapi.Event.RegisterCallback(_ =>
            {
                for (int i = 0; i < originals.Count; i++)
                {
                    var entry = originals[i];
                    try { SetDespawn(entry.bh, entry.dist, entry.sec); } catch { }
                }
            }, Math.Max(1, seconds) * 1000);
        }

        private static List<object> FindAllDespawnBehaviors(Entity ent)
        {
            var result = new List<object>();
            if (ent == null) return result;

            var tEnt = ent.GetType();
            var pBeh = tEnt.GetProperty("Behaviors", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var fBeh = tEnt.GetField("Behaviors", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            object raw = (pBeh != null) ? pBeh.GetValue(ent) : (fBeh != null ? fBeh.GetValue(ent) : null);
            var enumerable = raw as System.Collections.IEnumerable;
            if (enumerable == null) return result;

            foreach (var bh in enumerable)
            {
                if (bh == null) continue;

                string codePath = null;
                var pCode = bh.GetType().GetProperty("Code", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object vCode = (pCode != null) ? pCode.GetValue(bh) : null;
                if (vCode != null)
                {
                    var pPath = vCode.GetType().GetProperty("Path", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    codePath = (pPath != null) ? (pPath.GetValue(vCode) as string) : null;
                }

                if (codePath != null && codePath.Equals("despawn", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(bh);
                }
            }
            return result;
        }

        private static bool TryReadDespawn(object bh, out int minDist, out int minSec)
        {
            minDist = 0;
            minSec = 0;
            if (bh == null) return false;

            var t = bh.GetType();
            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var pDist = t.GetProperty("minPlayerDistance", BF);
            var pSecs = t.GetProperty("minSeconds", BF);
            var fDist = t.GetField("minPlayerDistance", BF);
            var fSecs = t.GetField("minSeconds", BF);

            object vd = (pDist != null && pDist.CanRead) ? pDist.GetValue(bh) : (fDist != null ? fDist.GetValue(bh) : null);
            object vs = (pSecs != null && pSecs.CanRead) ? pSecs.GetValue(bh) : (fSecs != null ? fSecs.GetValue(bh) : null);

            bool ok = false;
            if (TryToInt(vd, out var md)) { minDist = md; ok = true; }
            if (TryToInt(vs, out var ms)) { minSec = ms; ok = true; }

            return ok;
        }

        private static void SetDespawn(object bh, int minDist, int minSec)
        {
            if (bh == null) return;

            var t = bh.GetType();
            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var pDist = t.GetProperty("minPlayerDistance", BF);
            var pSecs = t.GetProperty("minSeconds", BF);
            var fDist = t.GetField("minPlayerDistance", BF);
            var fSecs = t.GetField("minSeconds", BF);

            if (pDist != null && pDist.CanWrite) pDist.SetValue(bh, minDist);
            else if (fDist != null) fDist.SetValue(bh, minDist);

            if (pSecs != null && pSecs.CanWrite) pSecs.SetValue(bh, minSec);
            else if (fSecs != null) fSecs.SetValue(bh, minSec);
        }

        private static bool TryToInt(object val, out int res)
        {
            if (val == null) { res = 0; return false; }

            if (val is int i) { res = i; return true; }
            if (val is long l) { try { res = checked((int)l); return true; } catch { res = 0; return false; } }
            if (val is float f) { res = (int)Math.Round(f); return true; }
            if (val is double d) { res = (int)Math.Round(d); return true; }
            if (val is string s)
            {
                if (int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var si))
                { res = si; return true; }
                res = 0; return false;
            }
            if (val is IConvertible conv)
            {
                try { res = Convert.ToInt32(conv, CultureInfo.InvariantCulture); return true; }
                catch { res = 0; return false; }
            }

            res = 0; return false;
        }

        // -----------------------------------------------------------------
        // Utilities
        // -----------------------------------------------------------------

        private bool IsExcluded(Entity e)
        {
            string path = e.Code != null ? (e.Code.Path ?? "") : "";
            return HasAnyPrefix(path, cfg.ExcludePrefixes);
        }

        private static bool HasAnyPrefix(string path, List<string> prefixes)
        {
            if (string.IsNullOrEmpty(path) || prefixes == null) return false;
            for (int i = 0; i < prefixes.Count; i++)
            {
                string p = prefixes[i];
                if (!string.IsNullOrWhiteSpace(p) &&
                    path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string CodePathOf(string shortCode)
        {
            if (string.IsNullOrEmpty(shortCode)) return "";
            var al = new AssetLocation(shortCode);
            return al.Path ?? "";
        }

        private int RandTtlMinutes()
        {
            int min = Math.Max(1, cfg.SnapshotTtlMinMinutes);
            int max = Math.Max(min, cfg.SnapshotTtlMaxMinutes);
            return sapi.World.Rand.Next(min, max + 1);
        }

        private static int FloorDiv(int a, int b)
        {
            int q = a / b;
            int r = a % b;
            if ((r != 0) && ((r < 0) != (b < 0))) q--;
            return q;
        }

        /// <summary>
        /// Finds a safe floor with 2 air blocks above; returns null if not found.
        /// </summary>
        private BlockPos FindSafeGround(BlockPos around, int scanUp, int scanDown)
        {
            var ba = sapi.World.BlockAccessor;

            // Scan downward: find a solid block with 2 air blocks above
            for (int dy = 0; dy < scanDown; dy++)
            {
                var below = new BlockPos(around.X, around.Y - dy, around.Z);
                if (!ba.IsValidPos(below)) break;
                var b = ba.GetBlock(below);
                if (b != null && b.CollisionBoxes != null && b.CollisionBoxes.Length > 0)
                {
                    var head = below.UpCopy();
                    var head2 = head.UpCopy();
                    var b1 = ba.GetBlock(head);
                    var b2 = ba.GetBlock(head2);
                    bool air1 = !(b1 != null && b1.CollisionBoxes != null && b1.CollisionBoxes.Length > 0);
                    bool air2 = !(b2 != null && b2.CollisionBoxes != null && b2.CollisionBoxes.Length > 0);
                    if (air1 && air2) return below;
                }
            }

            // Scan upward: find 2 air blocks, then a solid floor somewhere below them
            for (int dy = 0; dy < scanUp; dy++)
            {
                var pos = new BlockPos(around.X, around.Y + dy, around.Z);
                var b1 = ba.GetBlock(pos);
                var b2 = ba.GetBlock(pos.UpCopy());
                bool air1 = !(b1 != null && b1.CollisionBoxes != null && b1.CollisionBoxes.Length > 0);
                bool air2 = !(b2 != null && b2.CollisionBoxes != null && b2.CollisionBoxes.Length > 0);
                if (air1 && air2)
                {
                    var down = pos.DownCopy();
                    for (int dd = 0; dd < scanDown; dd++)
                    {
                        var test = new BlockPos(down.X, down.Y - dd, down.Z);
                        if (!ba.IsValidPos(test)) break;
                        var b = ba.GetBlock(test);
                        if (b != null && b.CollisionBoxes != null && b.CollisionBoxes.Length > 0) return test;
                    }
                }
            }

            return null;
        }

        private static string FmtCounts(Dictionary<string, int> map)
        {
            if (map.Count == 0) return "(none)";
            return string.Join(", ", map.Select(kv => $"{kv.Key}:{kv.Value}"));
        }
    }

    // ---------------------------------------------------------
    // Small helpers
    // ---------------------------------------------------------
    internal static class BlockPosExt
    {
        public static Vec3d ToVec3d(this BlockPos bp)
        {
            return new Vec3d(bp.X + 0.5, bp.Y + 0.5, bp.Z + 0.5);
        }

        public static double SquareDistanceTo(this Vec3d a, Vec3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
