// =============================================================================
// Services/CsvIo.cs
//
// SINOSSI: Lettura/scrittura CSV minimale (RFC 4180: virgola come separatore,
//   campi con virgola/virgolette/a capo racchiusi fra virgolette doppie,
//   virgolette interne raddoppiate) per l'export/import di POI e Percorsi
//   (Services/PoiService.ExportCsvAsync/ImportCsv, Services/PercorsoService.
//   ExportCsvAsync/ImportCsv) — nessun pacchetto NuGet aggiuntivo, come già
//   per KML/GPX (System.Xml.Linq built-in).
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace StradarioApp.Services
{
    internal static class CsvIo
    {
        // Decodifica i byte grezzi in testo rilevando da sola un eventuale
        // BOM UTF-8/UTF-16 (StreamReader), a differenza di un
        // Encoding.UTF8.GetString diretto che lascerebbe il BOM come primo
        // carattere del testo (rompendo il confronto dell'header "Gruppo"/
        // "Percorso" più sotto).
        public static string DecodeText(byte[] raw)
        {
            using var reader = new StreamReader(new MemoryStream(raw), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        // Un campo va racchiuso fra virgolette se contiene il separatore, una
        // virgoletta o un a capo; le virgolette interne si raddoppiano.
        public static string EscapeField(string? value)
        {
            string s = value ?? "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        public static string BuildLine(IEnumerable<string?> fields)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var f in fields)
            {
                if (!first) sb.Append(',');
                sb.Append(EscapeField(f));
                first = false;
            }
            return sb.ToString();
        }

        // Analizza l'intero testo in righe di campi, gestendo campi tra
        // virgolette che contengono a capo letterali (quindi non basta uno
        // split per riga prima di aver capito dove finisce il campo).
        public static List<string[]> ParseAll(string text)
        {
            var rows = new List<string[]>();
            var current = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;
            bool rowHasContent = false;

            void EndField() { current.Add(field.ToString()); field.Clear(); }
            void EndRow()
            {
                EndField();
                rows.Add(current.ToArray());
                current.Clear();
                rowHasContent = false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else field.Append(c);
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        rowHasContent = true;
                        break;
                    case ',':
                        EndField();
                        rowHasContent = true;
                        break;
                    case '\r':
                        break; // ignorato, gestito da \n (sia \r\n che \n soli)
                    case '\n':
                        EndRow();
                        break;
                    default:
                        field.Append(c);
                        rowHasContent = true;
                        break;
                }
            }
            // Ultima riga senza \n finale
            if (rowHasContent || field.Length > 0) EndRow();

            return rows;
        }
    }
}
