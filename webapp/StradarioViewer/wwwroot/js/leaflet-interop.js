// Wrapper JS minimale attorno a Leaflet (caricato via CDN in index.html), invocato
// da Shared/MapView.razor tramite IJSRuntime. Un solo Map/LayerGroup alla volta,
// tenuti in variabili di modulo — coerente con l'uso (un'unica mappa nella pagina).
let map = null;
let layerGroup = null;

export function initMap(elementId, centerLat, centerLon, zoom) {
    if (map) {
        map.remove();
        map = null;
    }

    map = L.map(elementId).setView([centerLat, centerLon], zoom);
    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    }).addTo(map);

    layerGroup = L.layerGroup().addTo(map);
}

export function clearLayers() {
    if (layerGroup) layerGroup.clearLayers();
}

export function drawRoute(pointsLatLon, colorHex, weight, dotNetRef, entryIndex) {
    if (!layerGroup) return;
    const line = L.polyline(pointsLatLon, { color: colorHex, weight: weight, opacity: 0.9 });
    line.on('click', () => dotNetRef.invokeMethodAsync('OnEntryClicked', entryIndex));
    line.addTo(layerGroup);
}

export function drawMarker(lat, lon, colorHex, dotNetRef, entryIndex) {
    if (!layerGroup) return;
    const marker = L.circleMarker([lat, lon], {
        radius: 7,
        color: '#ffffff',
        weight: 2,
        fillColor: colorHex,
        fillOpacity: 1,
    });
    marker.on('click', () => dotNetRef.invokeMethodAsync('OnEntryClicked', entryIndex));
    marker.addTo(layerGroup);
}

export function fitToPoints(pointsLatLon) {
    if (!map || !pointsLatLon || pointsLatLon.length === 0) return;
    if (pointsLatLon.length === 1) {
        map.setView(pointsLatLon[0], 14);
        return;
    }
    map.fitBounds(pointsLatLon, { padding: [24, 24], maxZoom: 16 });
}
