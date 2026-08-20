using Microsoft.JSInterop;

namespace StradarioViewer.Services;

// Wrapper minimale su window.localStorage via JS interop diretto (nessun pacchetto
// NuGet aggiuntivo, coerente con lo stile del progetto principale).
public sealed class LocalStorageService
{
    private readonly IJSRuntime _js;

    public LocalStorageService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<string?> GetItemAsync(string key)
        => await _js.InvokeAsync<string?>("localStorage.getItem", key);

    public async Task SetItemAsync(string key, string value)
        => await _js.InvokeVoidAsync("localStorage.setItem", key, value);

    public async Task RemoveItemAsync(string key)
        => await _js.InvokeVoidAsync("localStorage.removeItem", key);
}
