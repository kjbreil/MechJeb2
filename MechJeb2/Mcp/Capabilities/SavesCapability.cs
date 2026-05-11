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
