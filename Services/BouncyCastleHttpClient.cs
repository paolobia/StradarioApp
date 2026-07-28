// =============================================================================
// Services/BouncyCastleHttpClient.cs
//
// SINOSSI: Client HTTPS GET minimale che fa l'handshake TLS da solo, tramite
//   BouncyCastle (implementazione TLS interamente gestita, C# puro, nessuna
//   dipendenza nativa) invece di passare per lo stack TLS del sistema
//   operativo (SChannel su Windows). Usato SOLO come ultimo tentativo da
//   Services/RouteInstradationService.cs, quando anche il fallback a solo
//   TLS 1.2 fallisce.
//
//   Caso reale che ha reso necessario questo file: un utente in beta test
//   otteneva sempre `AuthenticationException: ... TLS alert
//   'HandshakeFailure'` instradando un Percorso via OSRM, anche col
//   fallback TLS 1.2. Diagnosi (fatta passo passo, non per tentativi):
//   1) antivirus/firewall esclusi; 2) il browser apriva
//   router.project-osrm.org regolarmente — ma Chrome/Edge NON usano
//   SChannel, hanno un proprio stack TLS (BoringSSL), quindi questo non
//   dimostra che .NET funzioni sulla stessa macchina; 3) `Invoke-WebRequest`
//   in PowerShell (stesso stack .NET/SChannel dell'app) falliva con lo
//   STESSO errore — isola il problema in SChannel, non nell'app; 4) IIS
//   Crypto sulla macchina dell'utente mostrava, nell'elenco Ciphers, SOLO
//   cifrari CBC (AES 128/128, AES 256/256) e NESSUNA suite GCM — non una
//   casella da spuntare: vuol dire che quel Windows non supporta proprio le
//   suite GCM a livello di sistema. Un server moderno come quello dietro
//   OSRM spesso accetta solo suite GCM/AEAD (niente CBC, per sicurezza):
//   zero suite in comune → HandshakeFailure, sempre, qualunque
//   combinazione di protocolli TLS venga richiesta lato .NET, perché il
//   problema non è la versione del protocollo ma l'assenza totale di
//   supporto GCM nella libreria crittografica del sistema operativo.
//   Nessun override di SslClientAuthenticationOptions può aggirarlo: bisogna
//   proprio non usare SChannel. BouncyCastle porta con sé il proprio
//   supporto GCM (DefaultTlsClient offre suite GCM/ChaCha20 di default),
//   indipendentemente da cosa sa fare il sistema operativo sottostante.
//
//   La VALIDAZIONE DEL CERTIFICATO resta necessaria per sicurezza (BouncyCastle
//   fa l'handshake ma non decide da solo se fidarsi del server): usa
//   X509Chain di .NET contro l'archivio certificati di sistema (lo stesso
//   di cui si fida il browser) — l'archivio certificati è un problema
//   completamente indipendente dalla negoziazione di protocollo/cifrari che
//   è rotta su quella macchina, quindi rimane affidabile usarlo. Stesso
//   livello di sicurezza di una normale richiesta HTTPS.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace StradarioApp.Services
{
    internal static class BouncyCastleHttpClient
    {
        private const string UserAgent = "StradarioApp/1.0 (educational use)";
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
        private const int SocketTimeoutMs = 25000;

        public static Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            var uri = new Uri(url);
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new NotSupportedException("BouncyCastleHttpClient supporta solo https.");

            // TlsClientProtocol/TcpClient sono sincroni: girati su un thread
            // pool thread, non c'è una vera API async in BouncyCastle per
            // questo scenario. Chiudere il socket alla cancellazione è
            // l'unico modo di sbloccare una Read bloccante in corso.
            return Task.Run(() =>
            {
                using var tcp = new TcpClient { ReceiveTimeout = SocketTimeoutMs, SendTimeout = SocketTimeoutMs };
                using var ctReg = ct.Register(() => { try { tcp.Close(); } catch { /* già chiuso */ } });

                if (!tcp.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 443).Wait(ConnectTimeout))
                    throw new TimeoutException($"Connessione TCP a {uri.Host} scaduta.");
                ct.ThrowIfCancellationRequested();

                using var networkStream = tcp.GetStream();
                var protocol = new TlsClientProtocol(networkStream);
                protocol.Connect(new ValidatingTlsClient(uri.Host));
                ct.ThrowIfCancellationRequested();

                using var tlsStream = protocol.Stream;
                WriteRequest(tlsStream, uri);
                return ReadResponseBody(tlsStream);
            }, ct);
        }

        private static void WriteRequest(Stream stream, Uri uri)
        {
            var sb = new StringBuilder();
            sb.Append("GET ").Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(uri.Host).Append("\r\n");
            sb.Append("User-Agent: ").Append(UserAgent).Append("\r\n");
            sb.Append("Accept: */*\r\n");
            sb.Append("Connection: close\r\n"); // il corpo finisce con la chiusura della connessione, niente keep-alive da gestire
            sb.Append("\r\n");
            byte[] buf = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(buf, 0, buf.Length);
            stream.Flush();
        }

        // Legge fino alla chiusura della connessione (Connection: close
        // sopra), poi separa intestazioni/corpo a mano — niente libreria
        // HTTP disponibile qui, solo il minimo indispensabile per un GET.
        private static string ReadResponseBody(Stream stream)
        {
            using var buffer = new MemoryStream();
            var readBuf = new byte[8192];
            int n;
            while ((n = stream.Read(readBuf, 0, readBuf.Length)) > 0)
                buffer.Write(readBuf, 0, n);
            byte[] all = buffer.ToArray();

            int headerEnd = IndexOfHeaderEnd(all);
            if (headerEnd < 0)
                throw new IOException("Risposta HTTP non valida: fine delle intestazioni non trovata.");

            string headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
            string[] headerLines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);

            string[] statusParts = headerLines[0].Split(' ');
            if (statusParts.Length < 2 || !int.TryParse(statusParts[1], out int statusCode))
                throw new IOException($"Riga di stato HTTP non valida: \"{headerLines[0]}\".");

            bool chunked = headerLines.Any(l =>
                l.StartsWith("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                l.Contains("chunked", StringComparison.OrdinalIgnoreCase));

            int bodyStart = headerEnd + 4; // lunghezza di "\r\n\r\n"
            byte[] bodyBytes = chunked ? DecodeChunked(all, bodyStart) : all[bodyStart..];
            string body = Encoding.UTF8.GetString(bodyBytes);

            if (statusCode < 200 || statusCode >= 300)
                throw new IOException($"HTTP {statusCode}: {body}");

            return body;
        }

        private static int IndexOfHeaderEnd(byte[] data)
        {
            for (int i = 0; i + 3 < data.Length; i++)
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                    return i;
            return -1;
        }

        // Transfer-Encoding: chunked (RFC 7230 §4.1): ogni chunk inizia con
        // la sua lunghezza in esadecimale su una riga, seguita dai byte del
        // chunk e da \r\n; un chunk di lunghezza 0 segna la fine.
        private static byte[] DecodeChunked(byte[] data, int start)
        {
            using var result = new MemoryStream();
            int pos = start;
            while (pos < data.Length)
            {
                int lineEnd = Array.IndexOf(data, (byte)'\n', pos);
                if (lineEnd < 0) break;
                string sizeLine = Encoding.ASCII.GetString(data, pos, lineEnd - pos).Trim();
                int semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine[..semi];
                if (!int.TryParse(sizeLine, System.Globalization.NumberStyles.HexNumber, null, out int chunkSize))
                    break;
                pos = lineEnd + 1;
                if (chunkSize == 0) break;
                result.Write(data, pos, Math.Min(chunkSize, data.Length - pos));
                pos += chunkSize + 2; // salta il chunk e il \r\n finale
            }
            return result.ToArray();
        }

        // TlsClient minimale: eredita da DefaultTlsClient (che offre già di
        // default suite GCM/ChaCha20 moderne, vedi motivazione in testa al
        // file) e imposta solo SNI (obbligatorio: molti server, incluso
        // quello dietro OSRM, rifiutano l'handshake senza) e l'autenticazione
        // del certificato.
        private sealed class ValidatingTlsClient : DefaultTlsClient
        {
            private readonly string _hostname;

            public ValidatingTlsClient(string hostname) : base(new BcTlsCrypto())
            {
                _hostname = hostname;
            }

            protected override IList<ServerName> GetSniServerNames() =>
                new List<ServerName> { new(NameType.host_name, Encoding.ASCII.GetBytes(_hostname)) };

            public override TlsAuthentication GetAuthentication() => new CertValidatingAuthentication(_hostname);
        }

        // Valida il certificato del server con X509Chain di .NET (archivio
        // certificati di sistema — vedi motivazione in testa al file) invece
        // di accettarlo ciecamente: BouncyCastle fa solo l'handshake
        // crittografico, la decisione di fidarsi o no del server resta a
        // carico nostro.
        private sealed class CertValidatingAuthentication : TlsAuthentication
        {
            private readonly string _hostname;
            public CertValidatingAuthentication(string hostname) { _hostname = hostname; }

            public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
            {
                var chain = serverCertificate?.Certificate?.GetCertificateList();
                if (chain == null || chain.Length == 0)
                    throw new TlsFatalAlert(AlertDescription.bad_certificate, "Nessun certificato ricevuto dal server.");

                using var x509Chain = new X509Chain();
                x509Chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // niente controllo revoca: eviterebbe un'altra chiamata di rete che potrebbe fallire allo stesso modo
                for (int i = 1; i < chain.Length; i++)
                    x509Chain.ChainPolicy.ExtraStore.Add(new X509Certificate2(chain[i].GetEncoded()));

                using var leaf = new X509Certificate2(chain[0].GetEncoded());
                bool chainOk = x509Chain.Build(leaf) &&
                               x509Chain.ChainStatus.All(s => s.Status == X509ChainStatusFlags.NoError);
                if (!chainOk)
                {
                    string reasons = string.Join(", ", x509Chain.ChainStatus.Select(s => s.StatusInformation.Trim()));
                    throw new TlsFatalAlert(AlertDescription.bad_certificate,
                        new AuthenticationException($"Catena del certificato non valida per \"{_hostname}\": {reasons}"));
                }

                if (!HostnameMatches(leaf, _hostname))
                    throw new TlsFatalAlert(AlertDescription.bad_certificate,
                        new AuthenticationException($"Il certificato del server non è valido per l'host \"{_hostname}\"."));
            }

            // Nessuna autenticazione client (mTLS): un GET pubblico verso
            // OSRM non ne richiede una, e il server comunque non la chiede.
            public TlsCredentials? GetClientCredentials(Org.BouncyCastle.Tls.CertificateRequest certificateRequest) => null;

            // X509Chain valida solo che il certificato sia emesso da
            // un'autorità fidata, non che sia valido PER QUESTO host — va
            // controllato a parte contro i Subject Alternative Name (o, in
            // mancanza, il Common Name), con supporto per un singolo livello
            // di wildcard ("*.esempio.com").
            private static bool HostnameMatches(X509Certificate2 cert, string hostname)
            {
                var names = new List<string>();
                // X509Extension.Format(false) per un Subject Alternative Name
                // NON ha un formato univoco fra piattaforme: su Windows è
                // "DNS Name=valore", su Linux (backend OpenSSL) è
                // "DNS:valore" — verificato rendendo esplicito il certificato
                // reale di router.project-osrm.org, che con un parsing
                // "cerca solo '='" veniva scartato per intero (nessun nome
                // estratto, quindi il certificato legittimo falliva la
                // verifica hostname). La regex accetta entrambi i separatori.
                var sanExt = cert.Extensions["2.5.29.17"]; // OID Subject Alternative Name
                if (sanExt != null)
                {
                    foreach (var part in sanExt.Format(false).Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(part.Trim(),
                            @"^DNS(?:\s*Name)?\s*[:=]\s*(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success)
                            names.Add(m.Groups[1].Value.Trim());
                    }
                }
                if (names.Count == 0)
                {
                    string cn = cert.GetNameInfo(X509NameType.SimpleName, false);
                    if (!string.IsNullOrEmpty(cn)) names.Add(cn);
                }

                foreach (var name in names)
                {
                    if (string.Equals(name, hostname, StringComparison.OrdinalIgnoreCase)) return true;
                    if (name.StartsWith("*.", StringComparison.Ordinal) &&
                        hostname.EndsWith(name[1..], StringComparison.OrdinalIgnoreCase))
                    {
                        string remainder = hostname[..^(name.Length - 1)];
                        if (remainder.Length > 0 && !remainder.Contains('.')) return true; // wildcard = un solo livello
                    }
                }
                return false;
            }
        }
    }
}
