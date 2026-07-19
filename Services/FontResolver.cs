// =============================================================================
// Services/FontResolver.cs
//
// SINOSSI: Font resolver per PdfSharpCore compatibile Linux e Windows.
//   PdfSharpCore non sa cercare i font di sistema da solo su Linux.
//   Questo resolver cerca i file .ttf nelle cartelle standard di entrambi
//   i sistemi operativi e li fornisce a PdfSharpCore come stream di byte.
//
//   Font cercati (in ordine di preferenza):
//     Linux:   /usr/share/fonts, ~/.fonts, /usr/local/share/fonts
//     Windows: C:\Windows\Fonts
//   Fallback:  DejaVu Sans (spesso presente su Linux) o Liberation Sans.
//
//   Uso: chiamare FontResolver.Register() una volta all'avvio, prima di
//   creare qualsiasi XFont.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using PdfSharpCore.Fonts;

namespace StradarioApp.Services
{
    public class FontResolver : IFontResolver
    {
        // Proprietà richiesta da IFontResolver: nome del font usato come ultimo fallback
        public string DefaultFontName => "dejavusans|false|false";
        // Mappa normalizzata: "familyname|bold|italic" -> percorso file .ttf
        private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

        // Singleton per evitare scansioni ripetute
        private static FontResolver? _instance;

        private FontResolver()
        {
            ScanFontDirectories();
        }

        // Registra questo resolver come resolver globale di PdfSharpCore.
        // Chiamare una sola volta prima di creare qualsiasi XFont.
        public static void Register()
        {
            if (_instance != null) return;
            _instance = new FontResolver();
            GlobalFontSettings.FontResolver = _instance;
        }

        // Cartelle di font per Linux e Windows
        private static IEnumerable<string> GetFontDirectories()
        {
            // Windows
            yield return @"C:\Windows\Fonts";
            yield return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");

            // Linux / macOS
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            yield return "/usr/share/fonts/truetype";
            yield return "/usr/share/fonts/opentype";

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".fonts");
            yield return Path.Combine(home, ".local", "share", "fonts");

            // macOS
            yield return "/Library/Fonts";
            yield return "/System/Library/Fonts";
        }

        // Scansiona le cartelle e popola la mappa family->file
        private void ScanFontDirectories()
        {
            foreach (var dir in GetFontDirectories())
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.ttf",
                    SearchOption.AllDirectories))
                {
                    RegisterFont(file);
                }
                // Alcuni sistemi usano .otf
                foreach (var file in Directory.EnumerateFiles(dir, "*.otf",
                    SearchOption.AllDirectories))
                {
                    RegisterFont(file);
                }
            }
        }

        // Inferisce la famiglia, bold e italic dal nome file e lo registra
        private void RegisterFont(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

            bool bold   = name.Contains("bold");
            bool italic = name.Contains("italic") || name.Contains("oblique");

            // Estrai la famiglia rimuovendo i suffissi comuni
            string family = name
                .Replace("-bolditalic", "").Replace("-boldoblique", "")
                .Replace("-bold",   "").Replace("-italic",  "")
                .Replace("-oblique","").Replace("-regular", "")
                .Replace("_bold",   "").Replace("_italic",  "")
                .Replace("bold",    "").Replace("italic",   "")
                .Replace("oblique", "").Replace("regular",  "")
                .Trim('-', '_', ' ');

            string key = $"{family}|{bold}|{italic}";

            // Non sovrascrive: il primo trovato vince (priorità cartelle nell'ordine)
            _map.TryAdd(key, path);

            // Registra anche senza varianti per fallback
            _map.TryAdd($"{family}|false|false", path);
        }

        // Interfaccia IFontResolver: restituisce i byte del font richiesto
        public byte[] GetFont(string faceName)
        {
            if (_map.TryGetValue(faceName, out string? path) && File.Exists(path))
                return File.ReadAllBytes(path);

            // Fallback assoluto: cerca qualcosa con "sans" nel nome
            foreach (var kv in _map)
                if (kv.Key.Contains("sans") || kv.Key.Contains("arial") ||
                    kv.Key.Contains("liberation") || kv.Key.Contains("dejavu"))
                    if (File.Exists(kv.Value))
                        return File.ReadAllBytes(kv.Value);

            // Se non trova nulla ritorna array vuoto (PdfSharpCore userà il suo fallback interno)
            return Array.Empty<byte>();
        }

        // Interfaccia IFontResolver: risolve il nome della faccia dal nome famiglia
        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            string fam = familyName.ToLowerInvariant()
                .Replace(" ", "").Replace("-", "");

            // Prova corrispondenza esatta con varianti
            string key = $"{fam}|{isBold}|{isItalic}";
            if (_map.ContainsKey(key))
                return new FontResolverInfo(key);

            // Prova senza italic
            if (isItalic)
            {
                key = $"{fam}|{isBold}|false";
                if (_map.ContainsKey(key))
                    return new FontResolverInfo(key);
            }

            // Prova senza bold
            if (isBold)
            {
                key = $"{fam}|false|{isItalic}";
                if (_map.ContainsKey(key))
                    return new FontResolverInfo(key);
            }

            // Prova solo famiglia, qualsiasi variante
            key = $"{fam}|false|false";
            if (_map.ContainsKey(key))
                return new FontResolverInfo(key);

            // Cerca corrispondenza parziale (es. "arial" trova "arialmtblack")
            foreach (var k in _map.Keys)
                if (k.StartsWith(fam))
                    return new FontResolverInfo(k);

            // Fallback a DejaVu Sans o Liberation Sans, molto comuni su Linux
            foreach (var fallback in new[] { "dejavusans", "liberationsans", "freesans", "noto" })
                foreach (var k in _map.Keys)
                    if (k.Contains(fallback))
                        return new FontResolverInfo(k);

            return null; // PdfSharpCore userà il suo fallback interno
        }
    }
}
