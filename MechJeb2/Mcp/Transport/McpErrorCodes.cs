using System;

namespace MuMech.Mcp
{
    // Typed error codes carried in tool-result `structuredContent.error_code`.
    // English identifiers, never localized. The human-readable `message` field
    // is allowed to be localized; the code is the stable contract for the LLM.
    //
    // See plan §"Typed error_code enum" and the deepen-plan agent-native review
    // (Findings B6, G6) for the rationale behind each code.
    public enum ErrorCode
    {
        Ok = 0,
        WrongScene,
        NoVessel,
        NoTarget,
        Busy,                       // path already has an active op
        WarpTooHigh,
        NotClearToSave,
        NameExists,
        NameNotFound,
        ReservedName,
        SaveInUse,
        PathNotFound,
        SchemaInvalid,
        DeprecatedPath,             // warning, not a hard failure
        DispatcherBacklog,          // transient — retry
        MainThreadHung,             // permanent — do not retry
        OpAborted,
        HandleNotFound,
        ModuleHidden,
        ModuleNotUnlocked,          // career mode R&D
        VesselWrongSituation,
        NoManeuverNode,
        GuidanceFailed,
        ProtocolVersionMissing,
        ProtocolVersionUnsupported,
        Internal,
    }

    public static class ErrorCodes
    {
        // Stable string identifiers exposed in JSON. Do NOT rename — the LLM
        // pattern-matches on them.
        public static string ToWire(ErrorCode code)
        {
            switch (code)
            {
                case ErrorCode.Ok: return "OK";
                case ErrorCode.WrongScene: return "WRONG_SCENE";
                case ErrorCode.NoVessel: return "NO_VESSEL";
                case ErrorCode.NoTarget: return "NO_TARGET";
                case ErrorCode.Busy: return "BUSY";
                case ErrorCode.WarpTooHigh: return "WARP_TOO_HIGH";
                case ErrorCode.NotClearToSave: return "NOT_CLEAR_TO_SAVE";
                case ErrorCode.NameExists: return "NAME_EXISTS";
                case ErrorCode.NameNotFound: return "NAME_NOT_FOUND";
                case ErrorCode.ReservedName: return "RESERVED_NAME";
                case ErrorCode.SaveInUse: return "SAVE_IN_USE";
                case ErrorCode.PathNotFound: return "PATH_NOT_FOUND";
                case ErrorCode.SchemaInvalid: return "SCHEMA_INVALID";
                case ErrorCode.DeprecatedPath: return "DEPRECATED_PATH";
                case ErrorCode.DispatcherBacklog: return "DISPATCHER_BACKLOG";
                case ErrorCode.MainThreadHung: return "MAIN_THREAD_HUNG";
                case ErrorCode.OpAborted: return "OP_ABORTED";
                case ErrorCode.HandleNotFound: return "HANDLE_NOT_FOUND";
                case ErrorCode.ModuleHidden: return "MODULE_HIDDEN";
                case ErrorCode.ModuleNotUnlocked: return "MODULE_NOT_UNLOCKED";
                case ErrorCode.VesselWrongSituation: return "VESSEL_WRONG_SITUATION";
                case ErrorCode.NoManeuverNode: return "NO_MANEUVER_NODE";
                case ErrorCode.GuidanceFailed: return "GUIDANCE_FAILED";
                case ErrorCode.ProtocolVersionMissing: return "PROTOCOL_VERSION_MISSING";
                case ErrorCode.ProtocolVersionUnsupported: return "PROTOCOL_VERSION_UNSUPPORTED";
                case ErrorCode.Internal: return "INTERNAL";
                default: return "INTERNAL";
            }
        }
    }

    // JSON-RPC 2.0 error codes. Used for *protocol-layer* failures (parse,
    // invalid request, method-not-found). Tool execution failures flow via
    // result.isError:true, NOT as JSON-RPC errors, per MCP spec 2025-06-18.
    public static class JsonRpcErrorCode
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;
        // -32000 to -32099 reserved for application-defined server errors.
        public const int ResourceUnavailable = -32802;
    }

    public sealed class McpException : Exception
    {
        public ErrorCode Code { get; }
        public JsonObject Details { get; }

        public McpException(ErrorCode code, string message, JsonObject details = null)
            : base(message)
        {
            Code = code;
            Details = details;
        }
    }
}
