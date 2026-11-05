//====================[ Imports ]====================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT;
using Newtonsoft.Json.Linq;

namespace KMABodyDrag.Presentation
{
    //====================[ Localization ]====================
    // Player-visible text by key. English is embedded in the DLL (Resources/Localization/en.json) and always complete; extra
    // languages are loose <code>.json files in BepInEx/plugins/KeepMeAlive/Localizations (shared by every KMA mod), named by EFT language code (ru, ge, fr...).
    // The Language setting picks one, or Auto follows the game's language. A key a file lacks falls back to English.
    internal static class Localization
    {
        public const string Auto = "Auto";
        public const string English = "en";

        private const string EmbeddedEnglish = "KMABodyDrag.Resources.Localization.en.json";

        public static string Folder => Path.Combine(BepInEx.Paths.PluginPath, "KeepMeAlive", "Localizations");

        //====================[ State ]====================
        // Kept from KeepMeAlive, where text is also read off the main thread: the tables are filled under a lock.
        private static readonly object Gate = new object();
        private static Dictionary<string, string> _english;
        // Each loose file once read, by lower-case code; an empty table when the file could not be read.
        private static readonly Dictionary<string, Dictionary<string, string>> Loose =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        // Keys already reported missing from English, so a bad key logs once instead of every frame.
        private static readonly HashSet<string> ReportedMissing = new HashSet<string>();

        //====================[ Discovery ]====================
        // The Language setting's choices: Auto, English, then every language file found, by code.
        public static string[] Choices()
        {
            var codes = new List<string> { Auto, English };
            try
            {
                if (Directory.Exists(Folder))
                {
                    codes.AddRange(Directory.GetFiles(Folder, "*.json")
                        .Select(path => Path.GetFileNameWithoutExtension(path).ToLowerInvariant())
                        .Where(code => code != English)
                        .OrderBy(code => code, StringComparer.Ordinal));
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[Localization] Could not scan {Folder}: {ex.Message}");
            }
            Plugin.LogSource.LogInfo($"[Localization] Languages: {string.Join(", ", codes.Skip(1))}");
            return codes.ToArray();
        }

        //====================[ Lookup ]====================
        // The text for key in language code, else English; null when neither has it. Callers read ActiveCode once and pass it
        // down, so a Language change mid-lookup (the setting, or another thread) cannot mix two languages in one text.
        private static string TryText(string code, string key)
        {
            if (TryLooseText(code, key, out string text)) return text;
            // A regional code (es-mx) falls back to its base language file (es) before English.
            int dash = code.IndexOf('-');
            if (dash > 0 && TryLooseText(code.Substring(0, dash), key, out text)) return text;
            // A loose en.json may reword English; the embedded one fills anything it lacks.
            if (TryLooseText(English, key, out text)) return text;
            return EnglishTable().TryGetValue(key, out text) ? text : null;
        }

        // The text for key in the chosen language, else English, else the key itself (a bug: the key is not in en.json).
        public static string Text(string key) => TextIn(ActiveCode(), key);

        private static string TextIn(string code, string key)
        {
            string text = TryText(code, key);
            if (text != null) return text;

            if (FirstReport(key)) Plugin.LogSource.LogError($"[Localization] Missing key {key} in the shared Localizations/en.json or the embedded English");
            return key;
        }

        // key.One / key.Few / key.Other filled with count, the form picked by the language's own rule. A language that does
        // not write a form (English has no .Few) uses key.Other.
        public static string Plural(string key, int count)
        {
            string code = ActiveCode();
            string form = key + "." + PluralForm(code, count);
            if (TryText(code, form) == null) form = key + ".Other";
            // A broken translation falls back to English's own form for count (English has no .Few to fall back to).
            return FormatIn(code, form, key + "." + PluralForm(English, count), count);
        }

        // One, Few or Other for a count in a language (EFT's code; a regional code uses its base language's rule).
        private static string PluralForm(string code, int count)
        {
            int dash = code.IndexOf('-');
            string language = (dash > 0 ? code.Substring(0, dash) : code).ToLowerInvariant();
            int n = count;
            int last = n % 10, lastTwo = n % 100;
            bool slavicFew = last >= 2 && last <= 4 && (lastTwo < 12 || lastTwo > 14);
            switch (language)
            {
                case "ru": // 1, 21, 31 one | 2-4, 22-24 few | 0, 5-20, 25-30 other
                    if (last == 1 && lastTwo != 11) return "One";
                    return slavicFew ? "Few" : "Other";
                case "pl": // only exactly 1 is one; few as in Russian
                    return n == 1 ? "One" : slavicFew ? "Few" : "Other";
                case "cz":
                case "sk": // 1 one | 2-4 few | the rest other
                    return n == 1 ? "One" : n >= 2 && n <= 4 ? "Few" : "Other";
                case "fr":
                case "po": // 0 and 1 are one
                    return n <= 1 ? "One" : "Other";
                default: // en, ge, es, it, hu, ro, tu, jp, kr, ch
                    return n == 1 ? "One" : "Other";
            }
        }

        // Text(key) with {0}... filled in; a translation with broken placeholders falls back to the English wording.
        public static string Format(string key, params object[] args) => FormatIn(ActiveCode(), key, key, args);

        // Format in language code, falling back to englishKey's English wording when key's text has broken placeholders.
        private static string FormatIn(string code, string key, string englishKey, params object[] args)
        {
            string template = TextIn(code, key);
            try
            {
                return string.Format(template, args);
            }
            catch (FormatException)
            {
                if (FirstReport("format:" + key)) Plugin.LogSource.LogError($"[Localization] Bad placeholders in {key}: \"{template}\"");
                return EnglishTable().TryGetValue(englishKey, out string english) ? string.Format(english, args) : key;
            }
        }

        // The setting's code, or for Auto the game's current language (EFT's code, e.g. ru, ge, es-mx).
        private static string ActiveCode()
        {
            string chosen = DragSettings.LANGUAGE?.Value;
            string code = string.IsNullOrEmpty(chosen) || chosen == Auto ? LocalizationManager.Instance.Culture : chosen;
            return string.IsNullOrEmpty(code) ? English : code;
        }

        //====================[ Files ]====================
        private static bool TryLooseText(string code, string key, out string text)
        {
            Dictionary<string, string> table;
            lock (Gate)
            {
                if (!Loose.TryGetValue(code, out table))
                {
                    string path = Path.Combine(Folder, code + ".json");
                    table = File.Exists(path) ? ReadTable(path, File.ReadAllText) : new Dictionary<string, string>();
                    Loose[code] = table;
                }
            }
            // A table is never changed once stored, so reading it needs no lock.
            return table.TryGetValue(key, out text) && !string.IsNullOrEmpty(text);
        }

        private static Dictionary<string, string> EnglishTable()
        {
            lock (Gate)
            {
                return _english ??= ReadTable(EmbeddedEnglish, resource =>
                {
                    using var reader = new StreamReader(EmbeddedData.Open(resource));
                    return reader.ReadToEnd();
                });
            }
        }

        private static bool FirstReport(string id)
        {
            lock (Gate) return ReportedMissing.Add(id);
        }

        // A flat { "key": "text" } file; keys starting with _ are comments. Empty (logged) when it cannot be read.
        private static Dictionary<string, string> ReadTable(string source, Func<string, string> read)
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var property in JObject.Parse(read(source)).Properties())
                {
                    if (property.Name.StartsWith("_", StringComparison.Ordinal)) continue;
                    if (property.Value.Type == JTokenType.String) table[property.Name] = (string)property.Value;
                    else Plugin.LogSource.LogWarning($"[Localization] {Path.GetFileName(source)}: {property.Name} is not text, skipped");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[Localization] Could not read {source}: {ex.Message}");
            }
            return table;
        }
    }
}
