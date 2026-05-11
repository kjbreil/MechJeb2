using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace MuMech.Mcp
{
    // Hand-rolled C# → JSON Schema generator. Supports the narrow set of
    // types we actually expose: primitives, nullable<primitive>, enums (as
    // string-enum schemas), arrays/lists, and "plain DTO" reference types
    // (public fields/properties of supported types).
    //
    // Explicitly does NOT support: Dictionary<,>, polymorphism, generic open
    // types, recursive types. Detected at registry build time → throws.
    //
    // Refuses Unity/KSP runtime types (Vessel, Part, Orbit, CelestialBody,
    // Vector3, Vector3d, Transform, etc.) — these must round-trip through a
    // hand-rolled DTO. The forbidden-types check fires at registry build,
    // not at first invoke, so registry validation is loud.
    internal static class JsonSchemaGenerator
    {
        private static readonly HashSet<string> ForbiddenTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "UnityEngine.Vector3",
            "UnityEngine.Vector3d",      // not a real Unity type but covers a common shorthand
            "UnityEngine.Quaternion",
            "UnityEngine.Transform",
            "UnityEngine.GameObject",
            "UnityEngine.MonoBehaviour",
            "Vector3d",
            "Vessel",
            "Part",
            "PartModule",
            "Orbit",
            "CelestialBody",
            "ProtoVessel",
            "ProtoPartSnapshot",
        };

        public static JsonObject ForParameters(ParameterInfo[] parameters)
        {
            var props = new JsonObject();
            var required = new JsonArray();
            foreach (ParameterInfo p in parameters)
            {
                var ann = p.GetCustomAttribute<McpParamAttribute>();
                JsonObject schema = ForType(p.ParameterType, p.Name);
                if (ann != null)
                {
                    if (!string.IsNullOrEmpty(ann.Description)) schema.Set("description", ann.Description);
                    if (!double.IsNaN(ann.Min)) schema.Set("minimum", ann.Min);
                    if (!double.IsNaN(ann.Max)) schema.Set("maximum", ann.Max);
                    if (ann.EnumValues != null && ann.EnumValues.Length > 0)
                    {
                        var arr = new JsonArray();
                        foreach (string v in ann.EnumValues) arr.Add(v);
                        schema.Set("enum", arr);
                    }
                }
                if (p.HasDefaultValue && p.DefaultValue != null && !(p.DefaultValue is DBNull))
                {
                    schema.Set("default", BoxDefault(p.DefaultValue));
                }
                props.Set(p.Name, schema);
                if (!p.IsOptional) required.Add(p.Name);
            }
            var root = new JsonObject()
                .Set("type", "object")
                .Set("properties", props)
                .Set("additionalProperties", false);
            if (required.Count > 0) root.Set("required", required);
            return root;
        }

        public static JsonObject ForType(Type t, string context = null)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));
            string fullName = t.FullName ?? t.Name;
            if (ForbiddenTypeNames.Contains(fullName))
                throw new InvalidOperationException(
                    "MCP schema rejected: type " + fullName +
                    " is a Unity/KSP runtime type. Wrap it in a hand-rolled DTO." +
                    (context != null ? " (in " + context + ")" : ""));

            // Nullable<T> → unwrap, mark nullable.
            Type nullable = Nullable.GetUnderlyingType(t);
            if (nullable != null)
            {
                JsonObject inner = ForType(nullable, context);
                MakeNullable(inner);
                return inner;
            }

            // Void return: object with empty properties.
            if (t == typeof(void))
                return new JsonObject().Set("type", "object").Set("additionalProperties", false);

            // Bool.
            if (t == typeof(bool)) return new JsonObject().Set("type", "boolean");

            // Integers.
            if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
                || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong))
                return new JsonObject().Set("type", "integer");

            // Floats.
            if (t == typeof(float) || t == typeof(double) || t == typeof(decimal))
                return new JsonObject().Set("type", "number");

            // Strings.
            if (t == typeof(string)) return new JsonObject().Set("type", "string");

            // Enums → string-enum.
            if (t.IsEnum)
            {
                var arr = new JsonArray();
                foreach (string n in Enum.GetNames(t)) arr.Add(n);
                return new JsonObject().Set("type", "string").Set("enum", arr);
            }

            // Arrays.
            if (t.IsArray)
            {
                JsonObject items = ForType(t.GetElementType(), context);
                return new JsonObject().Set("type", "array").Set("items", items);
            }

            // List<T> / IEnumerable<T> / IReadOnlyList<T> → arrays.
            if (t.IsGenericType)
            {
                Type def = t.GetGenericTypeDefinition();
                if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>)
                    || def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>))
                {
                    JsonObject items = ForType(t.GetGenericArguments()[0], context);
                    return new JsonObject().Set("type", "array").Set("items", items);
                }
                if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>))
                {
                    Type[] args = t.GetGenericArguments();
                    if (args[0] != typeof(string))
                        throw new InvalidOperationException(
                            "MCP schema: Dictionary keys must be string (got " + args[0].FullName + ")");
                    JsonObject valueSchema = ForType(args[1], context);
                    return new JsonObject()
                        .Set("type", "object")
                        .Set("additionalProperties", valueSchema);
                }
            }

            // JsonValue → opaque (we trust the producer).
            if (typeof(JsonValue).IsAssignableFrom(t))
                return new JsonObject().Set("description", "Arbitrary JSON value");

            // Plain reference / value types: enumerate public instance fields + properties.
            return ForPlainPoco(t, context);
        }

        private static JsonObject ForPlainPoco(Type t, string context)
        {
            if (t.IsAbstract || t.IsInterface)
                throw new InvalidOperationException(
                    "MCP schema: abstract/interface type " + t.FullName + " not supported");
            var props = new JsonObject();
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                JsonObject inner = ForType(f.FieldType, context + "." + f.Name);
                props.Set(f.Name, inner);
            }
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetGetMethod(false) == null) continue;
                if (p.GetIndexParameters().Length > 0) continue;
                JsonObject inner = ForType(p.PropertyType, context + "." + p.Name);
                props.Set(p.Name, inner);
            }
            return new JsonObject()
                .Set("type", "object")
                .Set("properties", props);
        }

        private static void MakeNullable(JsonObject schema)
        {
            if (!schema.TryGet("type", out JsonValue t)) return;
            // JSON Schema draft-07: type can be a single string or an array.
            if (t.Type == JsonType.String)
            {
                var arr = new JsonArray();
                arr.Add(t.AsString);
                arr.Add("null");
                schema.Set("type", arr);
            }
        }

        private static JsonValue BoxDefault(object defaultValue)
        {
            if (defaultValue == null) return JsonNull.Instance;
            switch (defaultValue)
            {
                case bool b: return new JsonBool(b);
                case string s: return new JsonString(s);
                case sbyte i: return new JsonNumber(i);
                case byte i: return new JsonNumber(i);
                case short i: return new JsonNumber(i);
                case ushort i: return new JsonNumber(i);
                case int i: return new JsonNumber(i);
                case uint i: return new JsonNumber(i);
                case long i: return new JsonNumber(i);
                case ulong i: return new JsonNumber((long)i);
                case float f: return new JsonNumber(f);
                case double d: return new JsonNumber(d);
                case Enum e: return new JsonString(e.ToString());
            }
            return new JsonString(Convert.ToString(defaultValue, CultureInfo.InvariantCulture));
        }
    }
}
