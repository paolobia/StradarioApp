// =============================================================================
// Services/KmlIo.cs
//
// SINOSSI: Lettura tollerante di documenti KML/KMZ, condivisa da PoiService
//   e PercorsoService (entrambi importano POI/percorsi dallo stesso tipo di
//   file). Gestisce tre contenitori:
//   - .kmz  : zip (firma "PK"), il primo file *.kml al suo interno
//   - .kml.gz talvolta rinominato ".kml" da alcuni tool: gzip (firma 1F 8B)
//   - .kml  : XML grezzo
//   Usa XDocument.Load su uno Stream (non una string già decodificata a
//   forza di UTF-8) così che sia il parser XML stesso, che sa gestire BOM
//   e dichiarazioni di encoding diverse da UTF-8, a occuparsi della
//   decodifica. Se anche questo fallisce (preamboli anomali visti in
//   export di alcuni tool), un fallback ripropone il parsing ripartendo dal
//   primo carattere '<' del testo.
// =============================================================================

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace StradarioApp.Services
{
    internal static class KmlIo
    {
        public static XDocument LoadDocument(byte[] raw)
        {
            if (raw.Length > 2 && raw[0] == 'P' && raw[1] == 'K')
                return LoadFromZip(raw);

            if (raw.Length > 2 && raw[0] == 0x1F && raw[1] == 0x8B)
                return ParseLeniently(Decompress(raw));

            return ParseLeniently(raw);
        }

        private static XDocument LoadFromZip(byte[] raw)
        {
            using var ms  = new MemoryStream(raw);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Nessun file .kml trovato nel KMZ.");

            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);
            return ParseLeniently(buffer.ToArray());
        }

        private static byte[] Decompress(byte[] raw)
        {
            using var input  = new MemoryStream(raw);
            using var gzip   = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }

        // Prova prima con XDocument.Load su uno Stream (il parser XML
        // riconosce da solo BOM e dichiarazione di encoding); se il file ha
        // comunque un preambolo anomalo che manda in errore il parser,
        // riprova ripartendo dal primo '<' del testo decodificato.
        private static XDocument ParseLeniently(byte[] bytes)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                return XDocument.Load(ms);
            }
            catch (XmlException)
            {
                string text  = Encoding.UTF8.GetString(bytes);
                int    start = text.IndexOf('<');
                if (start < 0) throw;
                return XDocument.Parse(text.Substring(start));
            }
        }

        // Alcuni tool di tracking GPS/foto valorizzano <name> con l'intero
        // path della cartella/file sorgente (visto in pratica: un'app che
        // esporta ogni traccia col nome dell'album fotografico corrispondente,
        // es. "/mnt/nas/.../2026.03.07", talvolta anche fra virgolette
        // letterali incluse nel testo). Se il nome letto dal placemark somiglia
        // a un path filesystem, ne teniamo solo l'ultimo segmento: è quello
        // l'unica parte con significato per l'utente.
        private static readonly System.Text.RegularExpressions.Regex WindowsDriveRoot =
            new(@"^[A-Za-z]:[\\/]", System.Text.RegularExpressions.RegexOptions.Compiled);

        public static string SanitizeName(string raw)
        {
            string trimmed = raw.Trim().Trim('"', '\'').Trim();
            if (trimmed.IndexOfAny(new[] { '/', '\\' }) < 0) return trimmed;

            // Un nome "somiglia a un path filesystem" solo se comincia con la
            // radice di un path assoluto (Unix "/..." o Windows "C:\...");
            // una "/" in mezzo al testo (es. "Ristoranti/Bar", "2026/03/11",
            // "A/B roads") è quasi sempre significato reale del nome, non un
            // path — tagliarla all'ultimo segmento perderebbe informazione.
            bool looksLikePath = trimmed.StartsWith('/') || trimmed.StartsWith('\\')
                || WindowsDriveRoot.IsMatch(trimmed);
            if (!looksLikePath) return trimmed;

            var segments = trimmed.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return segments.Length > 0 ? segments[^1] : trimmed;
        }
    }
}
