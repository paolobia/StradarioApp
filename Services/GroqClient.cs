// =============================================================================
// Services/GroqClient.cs
//
// SINOSSI: Client minimo per l'endpoint chat completions di Groq
//   (compatibile OpenAI, https://api.groq.com/openai/v1/chat/completions),
//   usato dalla ricerca POI in linguaggio naturale (vedi PoiSearchService:
//   GenerateHypothesesAsync = Fase A, WebSearchVerifyAsync = Fase D —
//   entrambe usano CompoundModel, web search integrata: il modello "puro"
//   DefaultModel si è rivelato con conoscenza troppo debole per generare da
//   solo ipotesi su luoghi meno comuni nei suoi dati di addestramento). JSON
//   mode (response_format=json_object) di default per ottenere un JSON
//   parsabile, disattivato (enableJsonMode:false) per CompoundModel che non
//   lo garantisce — in quel caso il chiamante estrae l'oggetto JSON dal testo
//   in modo tollerante (vedi PoiSearchService.ExtractJsonObject).
//   Un solo retry per errori transitori (timeout / 5xx / rete): un errore
//   4xx (chiave non valida, richiesta malformata) non viene ritentato,
//   perché ripetere la stessa richiesta fallirebbe di nuovo allo stesso modo.
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
        // Timeout generoso: copre anche CompoundModel (Fase D, ricerca web
        // integrata lato Groq), sensibilmente più lenta di una chat completion
        // "pura" perché include l'esecuzione della ricerca prima di rispondere.
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };

        private const string Endpoint     = "https://api.groq.com/openai/v1/chat/completions";
        public  const string DefaultModel = "llama-3.3-70b-versatile";

        // Modello "compound" di Groq: stessa API/chiave, ma con web search
        // (ed eventuale esecuzione di codice) integrata lato Groq nella
        // stessa chiamata — usato dalla Fase D come ultima verifica per un
        // luogo non confermato dai dati OpenStreetMap. NOTA: i modelli
        // "compound" sono un'offerta relativamente nuova di Groq, l'id esatto
        // può cambiare — verificare su console.groq.com/docs/model se questa
        // chiamata inizia a fallire con "model not found".
        public const string CompoundModel = "compound-beta-mini";

        // Invia una chat completion e ritorna il contenuto testuale del
        // messaggio di risposta. enableJsonMode (default true) chiede a Groq
        // di garantire un JSON valido (response_format=json_object); va
        // disattivato per CompoundModel, che non lo supporta in modo
        // affidabile — in quel caso il prompt chiede comunque JSON "a parole"
        // e il chiamante estrae l'oggetto dal testo in modo tollerante.
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
            string bodyJson = body.ToString(Formatting.None);

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
                    lastError = new GroqException("Timeout nella chiamata a Groq.", ex);
                    if (attempt == 0) continue;
                    throw lastError;
                }
                catch (HttpRequestException ex)
                {
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

        private static string ExtractErrorMessage(string body)
        {
            try { return JObject.Parse(body)["error"]?["message"]?.Value<string>() ?? body; }
            catch { return body; }
        }
    }
}
