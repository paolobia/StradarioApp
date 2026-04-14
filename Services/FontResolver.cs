using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharpCore.Fonts;

namespace StradarioApp.Services
{
    /// <summary>
    /// Custom font resolver for PdfSharpCore on Linux (where Arial may not be present).
    /// Scans system font directories and maps "familyname|bold|italic" keys to font files.
    /// </summary>
    public class FontResolver : IFontResolver
    {
        // IFontResolver requires this property — without it: CS0535
        public string DefaultFontName => "dejavusans|false|false";

        private static readonly FontResolver _instance = new FontResolver();
        private readonly Dictionary<string, string> _fontMap = new(StringComparer.OrdinalIgnoreCase);

        private FontResolver()
        {
            var searchDirs = new List<string>
            {
                "/usr/share/fonts",
                "/usr/local/share/fonts",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fonts"),
                @"C:\Windows\Fonts",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                             "AppData", "Local", "Microsoft", "Windows", "Fonts"),
            };

            foreach (var dir in searchDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.ttf", SearchOption.AllDirectories))
                        RegisterFont(file);
                    foreach (var file in Directory.EnumerateFiles(dir, "*.otf", SearchOption.AllDirectories))
                        RegisterFont(file);
                }
                catch { /* skip inaccessible directories */ }
            }
        }

        private void RegisterFont(string filePath)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
                // normalise: remove spaces and hyphens for matching
                name = name.Replace(" ", "").Replace("-", "");

                bool bold   = name.Contains("bold");
                bool italic = name.Contains("italic") || name.Contains("oblique");

                // Family = everything before bold/italic suffix
                string family = name
                    .Replace("bolditalic", "").Replace("italic", "")
                    .Replace("oblique", "").Replace("bold", "")
                    .Trim();

                if (string.IsNullOrEmpty(family)) family = name;

                string key = $"{family}|{bold.ToString().ToLower()}|{italic.ToString().ToLower()}";
                if (!_fontMap.ContainsKey(key))
                    _fontMap[key] = filePath;

                // Also register under the full normalised name as a fallback
                string fullKey = $"{name}|false|false";
                if (!_fontMap.ContainsKey(fullKey))
                    _fontMap[fullKey] = filePath;
            }
            catch { /* skip unreadable font files */ }
        }

        public byte[] GetFont(string faceName)
        {
            if (_fontMap.TryGetValue(faceName, out var path))
                return File.ReadAllBytes(path);

            // Fallback: try to find any font whose key starts with the family portion
            string family = faceName.Split('|')[0];
            var fallback = _fontMap.Keys.FirstOrDefault(k => k.StartsWith(family, StringComparison.OrdinalIgnoreCase));
            if (fallback != null)
                return File.ReadAllBytes(_fontMap[fallback]);

            // Last resort: return first available font
            if (_fontMap.Count > 0)
                return File.ReadAllBytes(_fontMap.Values.First());

            throw new FileNotFoundException($"No font found for '{faceName}' and no fallback available.");
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            string normFamily = familyName.ToLowerInvariant().Replace(" ", "").Replace("-", "");
            string key = $"{normFamily}|{isBold.ToString().ToLower()}|{isItalic.ToString().ToLower()}";

            if (_fontMap.ContainsKey(key))
                return new FontResolverInfo(key);

            // Try without style variants
            string baseKey = $"{normFamily}|false|false";
            if (_fontMap.ContainsKey(baseKey))
                return new FontResolverInfo(baseKey);

            // Partial match on family
            var partial = _fontMap.Keys.FirstOrDefault(k => k.Split('|')[0].Contains(normFamily, StringComparison.OrdinalIgnoreCase));
            if (partial != null)
                return new FontResolverInfo(partial);

            return new FontResolverInfo(DefaultFontName);
        }

        /// <summary>Call this once at startup, before creating any XFont.</summary>
        public static void Register()
        {
            GlobalFontSettings.FontResolver = _instance;
        }
    }
}
