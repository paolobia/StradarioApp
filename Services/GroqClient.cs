// =============================================================================
// Services/GroqClient.cs
//
// SINOSSI: Client minimo per l'endpoint chat completions di Groq
//   (compatibile OpenAI, https://api.groq.com/openai/v1/chat/completions),
//   usato da PoiSearchService.FilterCandidatesByQueryAsync per scegliere,
//   dentro un elenco chiuso di POI reali già trovati su OpenStreetMap, quali
//   soddisfano un vincolo espresso in linguaggio naturale (es. "della linea
//   firenze bologna"). JSON mode (response_format=json_object) per ottenere
//   una risposta parsabile senza estrazione tollerante. Un solo retry per
//   errori transitori (timeout / 5xx / rete): un errore 4xx (chiave non
//   valida, richiesta malformata) non viene ritentato, perché ripetere la
//   stessa richiesta fallirebbe di nuovo allo stesso modo. Ogni chiamata è
//   loggata su file (vedi DebugLog) per poter diagnosticare errori remoti
//   che dipendono dal contenuto esatto della richiesta/risposta.
// =============================================================================

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StradarioApp.Services
{
    public class GroqException : Exception
    {
        public GroqException(string message, Exception? inner = null) : base(message, inner) { }
    }

    public class GroqClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };

        private const string Endpoint     = "https://api.groq.com/openai/v1/chat/completions";
        public  const string DefaultModel = "llama-3.3-70b-versatile";

        // Invia una chat completion e ritorna il contenuto testuale del
        // messaggio di risposta. enableJsonMode (default true) chiede a Groq
        // di garantire un JSON valido (response_format=json_object).
        public async Task<string> ChatJsonAsync(string apiKey, string systemPrompt, string userPrompt,
            CancellationToken ct = default, string? model = null, bool enableJsonMode = true)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new GroqException("Chiave API Groq non configurata (Impostazioni → Chiave API Groq).");

            var body = new JObject
            {
                ["model"] = model ?? DefaultModel,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = systemPrompt },
                    new JObject { ["role"] = "user",   ["content"] = userPrompt }
                },
                ["temperature"] = 0.2
            };
            if (enableJsonMode)
                body["response_format"] = new JObject { ["type"] = "json_object" };
            string bodyJson  = body.ToString(Formatting.None);
            string usedModel = model ?? DefaultModel;

            // Log su file (vedi DebugLog): a differenza di Debug.WriteLine
            // resta leggibile anche fuori da un IDE/build Debug, così l'utente
            // può incollarne il contenuto per il debug di un errore remoto
            // (es. il 413 "Request Entity Too Large" che ha portato a
            // introdurre questo log — la dimensione del body qui sotto è
            // esattamente ciò che serve per confermare/escludere quella causa).
            DebugLog.Write($"[Groq] → POST {Endpoint} model={usedModel} jsonMode={enableJsonMode} bodyBytes={Encoding.UTF8.GetByteCount(bodyJson)}");

            Exception? lastError = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

                    using var resp     = await Http.SendAsync(req, ct);
                    string    respBody = await resp.Content.ReadAsStringAsync(ct);

                    DebugLog.Write($"[Groq] ← {(int)resp.StatusCode} {resp.StatusCode} attempt={attempt} bodyBytes={Encoding.UTF8.GetByteCount(respBody)} body={Truncate(respBody, 4000)}");

                    if (!resp.IsSuccessStatusCode)
                    {
                        string msg = $"Groq API ha risposto {(int)resp.StatusCode}: {ExtractErrorMessage(respBody)}";
                        bool transient = (int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests;
                        if (transient && attempt == 0) { lastError = new GroqException(msg); continue; }
                        throw new GroqException(msg);
                    }

                    var    root    = JObject.Parse(respBody);
                    string? content = root["choices"]?[0]?["message"]?["content"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(content))
                        throw new GroqException("Risposta vuota da Groq.");
                    return content!;
                }
                catch (GroqException) { throw; }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    DebugLog.Write($"[Groq] ← timeout attempt={attempt}: {ex.Message}");
                    lastError = new GroqException("Timeout nella chiamata a Groq.", ex);
                    if (attempt == 0) continue;
                    throw lastError;
                }
                catch (HttpRequestException ex)
                {
                    DebugLog.Write($"[Groq] ← errore di rete attempt={attempt}: {ex.Message}");
                    lastError = new GroqException($"Errore di rete verso Groq: {ex.Message}", ex);
                    if (attempt == 0) continue;
                    throw lastError;
                }
                catch (JsonException ex)
                {
                    throw new GroqException($"Risposta di Groq non in formato JSON valido: {ex.Message}", ex);
                }
            }

            throw lastError ?? new GroqException("Chiamata a Groq non riuscita.");
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max) + $"…[troncato, {s.Length} caratteri totali]";

        private static string ExtractErrorMessage(string body)
        {
            try { return JObject.Parse(body)["error"]?["message"]?.Value<string>() ?? body; }
            catch { return body; }
        }
    }
}
