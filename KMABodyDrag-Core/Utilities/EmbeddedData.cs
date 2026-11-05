//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KMABodyDrag.Utilities
{
    //====================[ EmbeddedData ]====================
    // What the mod ships inside the DLL (Resources/: baked JSON): opening it, reading it as bytes or as a JSON file checked
    // for its schema, and keeping what was built from it so each file is read once.
    internal static class EmbeddedData
    {
        // Built from each resource, or null when it could not be (so it is not tried again).
        private static readonly Dictionary<string, object> Loaded = new Dictionary<string, object>();

        // The resource's bytes as a stream; throws when it is not in the DLL.
        public static Stream Open(string resource) =>
            Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidDataException($"resource {resource} missing");

        // The resource's bytes; throws when it is not in the DLL.
        public static byte[] ReadBytes(string resource)
        {
            using var stream = Open(resource);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        // A JSON resource, checked to be of the schema the reader expects.
        public static JObject ReadJson(string resource, string schema)
        {
            using var stream = Open(resource);
            using var reader = new StreamReader(stream);
            var doc = JObject.Parse(reader.ReadToEnd());
            if ((string)doc["schema"] != schema) throw new InvalidDataException($"{resource}: unknown schema");
            return doc;
        }

        // A unit quaternion from a JSON array [x, y, z, w].
        public static Quaternion ReadQuaternion(JToken q) =>
            new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]).normalized;

        // What build makes of a resource, built on first use and kept; null (logged once, as tag) when it cannot be.
        public static T Get<T>(string resource, string tag, Func<T> build) where T : class
        {
            if (Loaded.TryGetValue(resource, out var kept)) return (T)kept;
            T value;
            try
            {
                value = build();
            }
            catch (Exception ex)
            {
                value = null;
                Plugin.LogSource.LogError($"[{tag}] Could not load {resource}: {ex.Message}");
            }
            Loaded[resource] = value;
            return value;
        }
    }
}
