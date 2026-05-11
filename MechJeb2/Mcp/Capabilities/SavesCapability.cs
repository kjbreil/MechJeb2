using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace MuMech.Mcp
{
    // Save-game management. Phase 3 ships the read surface only:
    // saves/list, saves/metadata, saves/quicksave.
    //
    // Phase 4 will add the write/destructive verbs (load, save, create,
    // copy, rename, delete, quickload) with the rename-to-trash protection
    // and ClearToSave re-check after MCP-user disengage.
    [McpDescription("KSP save-game management. Phase 3: list/metadata/quicksave. Phase 4 adds load/save/create/copy/rename/delete.", Version = "1.0.0")]
    public static class SavesCapability
    {
        // -- Phase 3 read verbs ---------------------------------------------
        public sealed class SaveSummaryDto
        {
            public string name;
            public long size_bytes;
            public string modified_utc;
        }

        public sealed class SaveListDto
        {
            public string save_folder;
            public string directory;
            public List<SaveSummaryDto> saves;
        }

        [McpCommand("saves/list",
            Description = "List .sfs files in the current save folder. " +
                          "Returns name, size, mtime — not the save contents.",
            SideEffect = SideEffect.ReadOnly,
            Version = "1.0.0")]
        public static SaveListDto List()
        {
            string folder = HighLogic.SaveFolder ?? "default";
            string dir = SaveFolderPath(folder);
            var result = new SaveListDto
            {
                save_folder = folder,
                directory = dir,
                saves = new List<SaveSummaryDto>(),
            };
            if (!Directory.Exists(dir)) return result;
            foreach (string path in Directory.GetFiles(dir, "*.sfs"))
            {
                try
                {
                    var info = new FileInfo(path);
                    result.saves.Add(new SaveSummaryDto
                    {
                        name = Path.GetFileNameWithoutExtension(info.Name),
                        size_bytes = info.Length,
                        modified_utc = info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture),
                    });
                }
                catch { /* skip unreadable */ }
            }
            return result;
        }

        public sealed class SaveMetadataDto
        {
            public string name;
            public bool exists;
            public long size_bytes;
            public string modified_utc;
            public double? ut;             // game time, from the .sfs header if parseable
            public string game_mode;       // SANDBOX / CAREER / SCIENCE / null
            public int vessel_count;       // approximate, by counting "VESSEL" nodes
            public bool has_loadmeta;
            public int backup_count;
        }

        [McpCommand("saves/metadata",
            Description = "Parses a .sfs file header to extract UT, game mode, vessel count, " +
                          "and reports presence of the .loadmeta sidecar and Backups/ entries. " +
                          "Does NOT load the save into memory.",
            SideEffect = SideEffect.ReadOnly,
            Version = "1.0.0")]
        public static SaveMetadataDto Metadata(
            [McpParam(Description = "Save file name (without .sfs extension).")]
            string name)
        {
            if (!IsValidSaveName(name))
                throw new McpException(ErrorCode.SchemaInvalid, "Invalid save name: " + name);

            string dir = SaveFolderPath(HighLogic.SaveFolder ?? "default");
            string sfsPath = Path.Combine(dir, name + ".sfs");
            var dto = new SaveMetadataDto { name = name };
            if (!File.Exists(sfsPath)) return dto;

            var info = new FileInfo(sfsPath);
            dto.exists = true;
            dto.size_bytes = info.Length;
            dto.modified_utc = info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture);
            dto.has_loadmeta = File.Exists(Path.Combine(dir, name + ".loadmeta"));

            // Lightweight parse — read the file once and pull values via ConfigNode.
            try
            {
                ConfigNode root = ConfigNode.Load(sfsPath);
                ConfigNode game = root?.GetNode("GAME") ?? root;
                if (game != null)
                {
                    if (game.HasValue("UT") && double.TryParse(game.GetValue("UT"), NumberStyles.Float, CultureInfo.InvariantCulture, out double ut))
                        dto.ut = ut;
                    if (game.HasValue("Mode")) dto.game_mode = game.GetValue("Mode");
                    ConfigNode flightState = game.GetNode("FLIGHTSTATE");
                    if (flightState != null) dto.vessel_count = flightState.GetNodes("VESSEL").Length;
                }
            }
            catch { /* corrupt or unparseable; leave fields default */ }

            // Backup count: <dir>/Backups/<name>-*.sfs
            string backupsDir = Path.Combine(dir, "Backups");
            if (Directory.Exists(backupsDir))
            {
                try { dto.backup_count = Directory.GetFiles(backupsDir, name + "-*.sfs").Length; }
                catch { dto.backup_count = 0; }
            }
            return dto;
        }

        public sealed class QuickSaveResultDto
        {
            public bool saved;
            public string clear_to_save;       // ClearToSaveStatus value
            public double ut;
            public string save_folder;
            public string quicksave_path;
        }

        [McpCommand("saves/quicksave",
            Description = "QuickSaveLoad.QuickSave(). Returns NOT_CLEAR_TO_SAVE if KSP refuses " +
                          "(active engines, near-collision, etc.). Refuse reason is in the error result.",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT,SPACECENTER,TRACKSTATION",
            Version = "1.0.0")]
        public static QuickSaveResultDto QuickSave()
        {
            ClearToSaveStatus cts = ClearToSaveStatus.CLEAR;
            try { cts = FlightGlobals.ClearToSave(); } catch { }
            if (cts != ClearToSaveStatus.CLEAR)
                throw new McpException(ErrorCode.NotClearToSave,
                    "KSP refused quicksave: " + cts);

            try
            {
                QuickSaveLoad.QuickSave();
            }
            catch (Exception ex)
            {
                throw new McpException(ErrorCode.Internal, "QuickSaveLoad.QuickSave threw: " + ex.Message);
            }

            string dir = SaveFolderPath(HighLogic.SaveFolder ?? "default");
            return new QuickSaveResultDto
            {
                saved = true,
                clear_to_save = cts.ToString(),
                ut = Planetarium.GetUniversalTime(),
                save_folder = HighLogic.SaveFolder ?? "default",
                quicksave_path = Path.Combine(dir, "quicksave.sfs"),
            };
        }

        // -- Phase 4 write verbs --------------------------------------------
        //
        // All write verbs serialize through a single lock — KSP's
        // QuickSaveLoad / GamePersistence share internal coroutine state
        // and overlapping calls have produced truncated .sfs files in the
        // past (deepen-plan finding D4). Per-path mutexes are not enough.
        private static readonly object _savesWriteLock = new object();

        // The most-recently loaded save name (tracked across saves/load and
        // saves/quicksave/quickload calls). Used to refuse rename/delete of
        // the active save unless force:true (deepen-plan finding D3).
        private static string _activeSaveName;

        public sealed class SaveResultDto
        {
            public string name;
            public string path;
            public bool wrote;
            public bool deleted;
            public bool renamed;
            public bool loaded;
            public string clear_to_save;
            public double ut;
            public List<string> disengaged_modules;     // populated on force:true load
            public List<string> remaining_blockers;     // populated when load refused
            public string trash_path;                   // populated when rename-to-trash
        }

        [McpCommand("saves/save",
            Description = "Persist the current game state as a named save (writes <name>.sfs and .loadmeta). " +
                          "Returns NOT_CLEAR_TO_SAVE if KSP refuses. Refuses RESERVED_NAME for 'persistent'/" +
                          "'quicksave'/'Backups' (use quicksave instead). NAME_EXISTS unless if_exists is set.",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT,SPACECENTER,TRACKSTATION",
            Version = "1.0.0")]
        public static SaveResultDto SaveAs(
            [McpParam(Description = "New save name (1-64 chars, no path separators).")]
            string name,
            [McpParam(Description = "Collision policy: fail | overwrite | suffix.",
                EnumValues = new[] { "fail", "overwrite", "suffix" })]
            string if_exists = "fail")
        {
            if (!IsValidSaveName(name)) throw new McpException(ErrorCode.SchemaInvalid, "Invalid save name: " + name);
            if (ReservedSaveNames.Contains(name)) throw new McpException(ErrorCode.ReservedName, "Reserved name: " + name);

            lock (_savesWriteLock)
            {
                ClearToSaveStatus cts = TryClearToSave();
                if (cts != ClearToSaveStatus.CLEAR)
                    throw new McpException(ErrorCode.NotClearToSave, "KSP refused save: " + cts);

                string saveFolder = HighLogic.SaveFolder ?? "default";
                string dir = SaveFolderPath(saveFolder);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string finalName = ResolveCollision(dir, name, if_exists);
                if (finalName == null) throw new McpException(ErrorCode.NameExists, "Save exists: " + name);

                try { GamePersistence.SaveGame(finalName, saveFolder, SaveMode.OVERWRITE); }
                catch (Exception ex) { throw new McpException(ErrorCode.Internal, "SaveGame failed: " + ex.Message); }

                return new SaveResultDto
                {
                    name = finalName,
                    path = Path.Combine(dir, finalName + ".sfs"),
                    wrote = true,
                    clear_to_save = cts.ToString(),
                    ut = Planetarium.GetUniversalTime(),
                };
            }
        }

        [McpCommand("saves/create",
            Description = "Persist the current game state under a new name. Source must be \"current\" — " +
                          "\"empty\" template creation is not supported in v1 (no in-game API). " +
                          "Equivalent to saves/save with stricter source semantics.",
            SideEffect = SideEffect.Mutating,
            RequiredScenes = "FLIGHT,SPACECENTER,TRACKSTATION",
            Version = "1.0.0")]
        public static SaveResultDto Create(
            string name,
            [McpParam(EnumValues = new[] { "current" })]
            string source = "current",
            string if_exists = "fail")
        {
            if (source != "current")
                throw new McpException(ErrorCode.SchemaInvalid, "Only source='current' is supported in v1");
            return SaveAs(name, if_exists);
        }

        [McpCommand("saves/copy",
            Description = "Filesystem copy of <from>.sfs and <from>.loadmeta to <to>.{sfs,loadmeta}. " +
                          "Does NOT touch MechJeb's per-vessel-name settings cfg in PluginData/MechJeb2 — " +
                          "MechJeb settings will be SHARED with the source for any vessels in it.",
            SideEffect = SideEffect.Mutating,
            Version = "1.0.0")]
        public static SaveResultDto Copy(string from, string to, string if_exists = "fail")
        {
            if (!IsValidSaveName(from)) throw new McpException(ErrorCode.SchemaInvalid, "Invalid 'from': " + from);
            if (!IsValidSaveName(to)) throw new McpException(ErrorCode.SchemaInvalid, "Invalid 'to': " + to);
            if (ReservedSaveNames.Contains(to)) throw new McpException(ErrorCode.ReservedName, "Reserved name: " + to);

            lock (_savesWriteLock)
            {
                string dir = SaveFolderPath(HighLogic.SaveFolder ?? "default");
                string fromSfs = Path.Combine(dir, from + ".sfs");
                if (!File.Exists(fromSfs)) throw new McpException(ErrorCode.NameNotFound, from);
                string resolvedTo = ResolveCollision(dir, to, if_exists);
                if (resolvedTo == null) throw new McpException(ErrorCode.NameExists, to);

                string toSfs = Path.Combine(dir, resolvedTo + ".sfs");
                File.Copy(fromSfs, toSfs, overwrite: true);
                string fromMeta = Path.Combine(dir, from + ".loadmeta");
                string toMeta = Path.Combine(dir, resolvedTo + ".loadmeta");
                if (File.Exists(fromMeta)) File.Copy(fromMeta, toMeta, overwrite: true);

                return new SaveResultDto { name = resolvedTo, path = toSfs, wrote = true };
            }
        }

        [McpCommand("saves/rename",
            Description = "Rename <from>.{sfs,loadmeta} to <to>. Refuses SAVE_IN_USE if 'from' is the " +
                          "currently-loaded save (next autosave would silently re-create it under the old " +
                          "name) unless force:true.",
            SideEffect = SideEffect.Mutating,
            Version = "1.0.0")]
        public static SaveResultDto Rename(string from, string to, bool force = false)
        {
            if (!IsValidSaveName(from)) throw new McpException(ErrorCode.SchemaInvalid, "Invalid 'from': " + from);
            if (!IsValidSaveName(to)) throw new McpException(ErrorCode.SchemaInvalid, "Invalid 'to': " + to);
            if (ReservedSaveNames.Contains(to)) throw new McpException(ErrorCode.ReservedName, "Reserved name: " + to);

            lock (_savesWriteLock)
            {
                if (!force && IsActiveSave(from))
                    throw new McpException(ErrorCode.SaveInUse,
                        "'" + from + "' is the currently-loaded save. Pass force:true to rename anyway " +
                        "(KSP's next autosave will silently re-create it under the old name).");

                string dir = SaveFolderPath(HighLogic.SaveFolder ?? "default");
                string fromSfs = Path.Combine(dir, from + ".sfs");
                if (!File.Exists(fromSfs)) throw new McpException(ErrorCode.NameNotFound, from);
                string toSfs = Path.Combine(dir, to + ".sfs");
                if (File.Exists(toSfs)) throw new McpException(ErrorCode.NameExists, to);

                File.Move(fromSfs, toSfs);
                string fromMeta = Path.Combine(dir, from + ".loadmeta");
                if (File.Exists(fromMeta)) File.Move(fromMeta, Path.Combine(dir, to + ".loadmeta"));

                return new SaveResultDto { name = to, path = toSfs, renamed = true };
            }
        }

        [McpCommand("saves/delete",
            Description = "Rename-to-trash protection: by default moves <name>.{sfs,loadmeta} to " +
                          ".mcp-trash/<timestamp>-<name>.{sfs,loadmeta} where it can be manually recovered " +
                          "for 7 days. Pass skip_backup:true to File.Delete directly (audit-logged).",
            SideEffect = SideEffect.Mutating,
            Version = "1.0.0")]
        public static SaveResultDto Delete(string name, bool skip_backup = false, bool force = false)
        {
            if (!IsValidSaveName(name)) throw new McpException(ErrorCode.SchemaInvalid, "Invalid name: " + name);
            if (ReservedSaveNames.Contains(name)) throw new McpException(ErrorCode.ReservedName, "Refusing to delete reserved name: " + name);
            if (!force && IsActiveSave(name))
                throw new McpException(ErrorCode.SaveInUse, "'" + name + "' is the currently-loaded save; pass force:true");

            lock (_savesWriteLock)
            {
                string dir = SaveFolderPath(HighLogic.SaveFolder ?? "default");
                string sfs = Path.Combine(dir, name + ".sfs");
                string meta = Path.Combine(dir, name + ".loadmeta");
                if (!File.Exists(sfs)) throw new McpException(ErrorCode.NameNotFound, name);

                if (skip_backup)
                {
                    File.Delete(sfs);
                    if (File.Exists(meta)) File.Delete(meta);
                    return new SaveResultDto { name = name, deleted = true };
                }

                // Rename-to-trash.
                string trashDir = Path.Combine(dir, ".mcp-trash");
                if (!Directory.Exists(trashDir)) Directory.CreateDirectory(trashDir);
                string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
                string trashSfs = Path.Combine(trashDir, ts + "-" + name + ".sfs");
                string trashMeta = Path.Combine(trashDir, ts + "-" + name + ".loadmeta");
                File.Move(sfs, trashSfs);
                if (File.Exists(meta)) File.Move(meta, trashMeta);

                return new SaveResultDto { name = name, deleted = true, trash_path = trashSfs };
            }
        }

        [McpCommand("saves/load",
            Description = "Load a saved game by name. Refuses NOT_CLEAR_TO_SAVE unless force:true; force:true " +
                          "first aborts all running MCP ops and disengages MCP-engaged autopilots, then " +
                          "RE-CHECKS ClearToSave — if it's still not CLEAR (human-engaged burn etc.), refuses " +
                          "with remaining_blockers populated. Pass force_unsafe:true to bypass ClearToSave " +
                          "entirely (crash risk; audit-logged at elevated severity).",
            SideEffect = SideEffect.Mutating,
            Version = "1.0.0")]
        public static SaveResultDto Load(string name, bool force = false, bool force_unsafe = false)
        {
            if (!IsValidSaveName(name)) throw new McpException(ErrorCode.SchemaInvalid, "Invalid save name: " + name);
            lock (_savesWriteLock)
            {
                string saveFolder = HighLogic.SaveFolder ?? "default";
                string dir = SaveFolderPath(saveFolder);
                if (!File.Exists(Path.Combine(dir, name + ".sfs")))
                    throw new McpException(ErrorCode.NameNotFound, name);

                List<string> disengagedModules = null;
                ClearToSaveStatus cts = TryClearToSave();
                if (cts != ClearToSaveStatus.CLEAR && !force_unsafe)
                {
                    if (!force)
                    {
                        var details = new JsonObject().Set("clear_to_save", cts.ToString());
                        throw new McpException(ErrorCode.NotClearToSave,
                            "ClearToSave=" + cts + ". Pass force:true to abort MCP ops + disengage MCP " +
                            "autopilots and re-check, or force_unsafe:true to bypass entirely (crash risk).",
                            details);
                    }
                    // force:true → abort ops + disengage MCP user → re-check
                    var addon = McpServerAddon.Instance;
                    addon?.Ops?.AbortAllSync("forced_save_load");
                    disengagedModules = MechJebMcpUser.Instance.DisengageAll("forced_save_load");
                    cts = TryClearToSave();
                    if (cts != ClearToSaveStatus.CLEAR)
                    {
                        var details = new JsonObject()
                            .Set("clear_to_save", cts.ToString())
                            .Set("cleared_mcp", true);
                        if (disengagedModules != null)
                        {
                            var arr = new JsonArray();
                            foreach (string m in disengagedModules) arr.Add(m);
                            details.Set("disengaged_modules", arr);
                        }
                        throw new McpException(ErrorCode.NotClearToSave,
                            "After disengaging MCP, ClearToSave is still " + cts + ". " +
                            "Pass force_unsafe:true to load anyway (crash risk).", details);
                    }
                }

                try
                {
                    Game g = GamePersistence.LoadGame(name, saveFolder, true, false);
                    if (g == null || g.flightState == null)
                        throw new McpException(ErrorCode.Internal, "GamePersistence.LoadGame returned null");
                    HighLogic.CurrentGame = g;
                    HighLogic.SaveFolder = saveFolder;
                    HighLogic.LoadScene(GameScenes.SPACECENTER);
                    _activeSaveName = name;
                }
                catch (McpException) { throw; }
                catch (Exception ex)
                {
                    throw new McpException(ErrorCode.Internal, "LoadGame threw: " + ex.Message);
                }

                return new SaveResultDto
                {
                    name = name,
                    loaded = true,
                    clear_to_save = cts.ToString(),
                    ut = Planetarium.GetUniversalTime(),
                    disengaged_modules = disengagedModules,
                };
            }
        }

        [McpCommand("saves/quickload",
            Description = "QuickSaveLoad.QuickLoad(). Same force/force_unsafe semantics as saves/load.",
            SideEffect = SideEffect.Mutating,
            Version = "1.0.0")]
        public static SaveResultDto QuickLoad(bool force = false, bool force_unsafe = false)
        {
            lock (_savesWriteLock)
            {
                ClearToSaveStatus cts = TryClearToSave();
                List<string> disengagedModules = null;
                if (cts != ClearToSaveStatus.CLEAR && !force_unsafe)
                {
                    if (!force)
                    {
                        var details = new JsonObject().Set("clear_to_save", cts.ToString());
                        throw new McpException(ErrorCode.NotClearToSave, "ClearToSave=" + cts, details);
                    }
                    McpServerAddon.Instance?.Ops?.AbortAllSync("forced_quickload");
                    disengagedModules = MechJebMcpUser.Instance.DisengageAll("forced_quickload");
                    cts = TryClearToSave();
                    if (cts != ClearToSaveStatus.CLEAR && !force_unsafe)
                    {
                        var details = new JsonObject().Set("clear_to_save", cts.ToString()).Set("cleared_mcp", true);
                        throw new McpException(ErrorCode.NotClearToSave,
                            "After disengage, ClearToSave still " + cts + ". force_unsafe:true to override.",
                            details);
                    }
                }
                // KSP's QuickSaveLoad has no public QuickLoad() — its UI pops a
                // confirmation dialog. Load the well-known "quicksave" name
                // through GamePersistence directly (same path as saves/load).
                try
                {
                    string saveFolder = HighLogic.SaveFolder ?? "default";
                    Game g = GamePersistence.LoadGame("quicksave", saveFolder, true, false);
                    if (g == null || g.flightState == null)
                        throw new McpException(ErrorCode.Internal, "Quickload returned null game");
                    HighLogic.CurrentGame = g;
                    HighLogic.LoadScene(GameScenes.SPACECENTER);
                }
                catch (McpException) { throw; }
                catch (Exception ex) { throw new McpException(ErrorCode.Internal, "QuickLoad threw: " + ex.Message); }

                _activeSaveName = "quicksave";
                return new SaveResultDto
                {
                    name = "quicksave",
                    loaded = true,
                    clear_to_save = cts.ToString(),
                    ut = Planetarium.GetUniversalTime(),
                    disengaged_modules = disengagedModules,
                };
            }
        }

        // -- Helpers (shared between read + write verbs) --------------------

        private static ClearToSaveStatus TryClearToSave()
        {
            try { return FlightGlobals.ClearToSave(); }
            catch { return ClearToSaveStatus.CLEAR; }   // outside flight, just say clear
        }

        private static bool IsActiveSave(string name)
        {
            if (_activeSaveName == null) return false;
            return string.Equals(_activeSaveName, name, StringComparison.OrdinalIgnoreCase);
        }

        // Returns the final name to use, or null if the collision is unresolvable.
        private static string ResolveCollision(string dir, string name, string policy)
        {
            string sfs = Path.Combine(dir, name + ".sfs");
            if (!File.Exists(sfs)) return name;
            switch (policy)
            {
                case "overwrite": return name;
                case "suffix":
                    for (int i = 2; i < 1000; i++)
                    {
                        string candidate = name + "_" + i;
                        if (!File.Exists(Path.Combine(dir, candidate + ".sfs"))) return candidate;
                    }
                    return null;
                case "fail":
                default:
                    return null;
            }
        }

        // -- Helpers (shared with Phase 4 write verbs) ----------------------
        private static readonly Regex SaveNameRegex =
            new Regex(@"^[A-Za-z0-9_\-](?:[A-Za-z0-9 _\-]{0,62}[A-Za-z0-9_\-])?$", RegexOptions.Compiled);

        internal static readonly HashSet<string> ReservedSaveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "persistent", "quicksave", "Backups", "saves",
            "CON", "PRN", "AUX", "NUL",
            "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9",
        };

        internal static bool IsValidSaveName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            // NFC normalization for macOS HFS+ filename comparison stability.
            string normalized = name.Normalize(System.Text.NormalizationForm.FormC);
            if (!SaveNameRegex.IsMatch(normalized)) return false;
            return true;
        }

        internal static string SaveFolderPath(string saveFolder)
        {
            string root = KSPUtil.ApplicationRootPath ?? "";
            try { root = Path.GetFullPath(root); } catch { }
            return Path.Combine(root, "saves", saveFolder ?? "default");
        }
    }
}
