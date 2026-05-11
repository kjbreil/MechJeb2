using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace MuMech.Mcp
{
    // Converts an MCP `arguments` JsonObject into a typed parameter array
    // ready for MethodBase.Invoke. Validation failures are reported as a
    // single error string (used by InvokeTool to return SCHEMA_INVALID).
    internal static class ArgumentBinder
    {
        public static bool TryBind(ParameterInfo[] parameters, JsonValue argsValue, out object[] bound, out string error)
        {
            error = null;
            bound = new object[parameters.Length];

            JsonObject argObj = argsValue as JsonObject;
            // Permit a null/missing arguments object when all parameters have defaults.
            if (argObj == null && (argsValue == null || argsValue.IsNull))
                argObj = new JsonObject();
            if (argObj == null)
            {
                error = "arguments must be a JSON object";
                return false;
            }

            // Reject unknown keys.
            var paramNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (ParameterInfo p in parameters) paramNames.Add(p.Name);
            foreach (string key in argObj.Keys)
            {
                if (!paramNames.Contains(key))
                {
                    error = "unknown argument: " + key;
                    return false;
                }
            }

            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo p = parameters[i];
                if (!argObj.TryGet(p.Name, out JsonValue raw))
                {
                    if (p.IsOptional)
                    {
                        bound[i] = p.HasDefaultValue ? p.DefaultValue : DefaultFor(p.ParameterType);
                        continue;
                    }
                    error = "missing required argument: " + p.Name;
                    return false;
                }
                try
                {
                    bound[i] = Convert(raw, p.ParameterType, p.Name);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
            return true;
        }

        public static object Convert(JsonValue value, Type targetType, string context)
        {
            if (value == null || value.IsNull)
            {
                if (Nullable.GetUnderlyingType(targetType) != null) return null;
                if (!targetType.IsValueType) return null;
                throw new ArgumentException("argument '" + context + "' cannot be null");
            }

            Type underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (underlying == typeof(bool))
            {
                if (value.Type != JsonType.Bool) throw new ArgumentException("argument '" + context + "' must be a boolean");
                return value.AsBool;
            }
            if (underlying == typeof(string))
            {
                if (value.Type != JsonType.String) throw new ArgumentException("argument '" + context + "' must be a string");
                return value.AsString;
            }
            if (underlying.IsEnum)
            {
                if (value.Type != JsonType.String) throw new ArgumentException("argument '" + context + "' must be a string (enum value)");
                try { return Enum.Parse(underlying, value.AsString, ignoreCase: false); }
                catch
                {
                    string allowed = string.Join(",", Enum.GetNames(underlying));
                    throw new ArgumentException("argument '" + context + "' must be one of: " + allowed);
                }
            }
            if (IsNumericIntegral(underlying))
            {
                if (value.Type != JsonType.Number) throw new ArgumentException("argument '" + context + "' must be a number");
                double d = value.AsNumber;
                if (Math.Floor(d) != d) throw new ArgumentException("argument '" + context + "' must be an integer (got " + d.ToString("R", CultureInfo.InvariantCulture) + ")");
                long asLong = (long)d;
                return System.Convert.ChangeType(asLong, underlying, CultureInfo.InvariantCulture);
            }
            if (IsNumericFloat(underlying))
            {
                if (value.Type != JsonType.Number) throw new ArgumentException("argument '" + context + "' must be a number");
                return System.Convert.ChangeType(value.AsNumber, underlying, CultureInfo.InvariantCulture);
            }

            // Arrays.
            if (underlying.IsArray)
            {
                if (value.Type != JsonType.Array) throw new ArgumentException("argument '" + context + "' must be an array");
                JsonArray arr = value.AsArray;
                Type elem = underlying.GetElementType();
                Array result = Array.CreateInstance(elem, arr.Count);
                for (int i = 0; i < arr.Count; i++)
                    result.SetValue(Convert(arr[i], elem, context + "[" + i + "]"), i);
                return result;
            }

            // List<T>.
            if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(List<>))
            {
                if (value.Type != JsonType.Array) throw new ArgumentException("argument '" + context + "' must be an array");
                JsonArray arr = value.AsArray;
                Type elem = underlying.GetGenericArguments()[0];
                IList list = (IList)Activator.CreateInstance(underlying);
                for (int i = 0; i < arr.Count; i++)
                    list.Add(Convert(arr[i], elem, context + "[" + i + "]"));
                return list;
            }

            // JsonValue passthrough (rare, for opaque-payload commands).
            if (typeof(JsonValue).IsAssignableFrom(underlying))
                return value;

            throw new ArgumentException("argument '" + context + "' targets unsupported type " + underlying.FullName);
        }

        private static bool IsNumericIntegral(Type t)
        {
            return t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
                || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);
        }

        private static bool IsNumericFloat(Type t)
        {
            return t == typeof(float) || t == typeof(double) || t == typeof(decimal);
        }

        private static object DefaultFor(Type t)
        {
            return t.IsValueType ? Activator.CreateInstance(t) : null;
        }
    }
}
