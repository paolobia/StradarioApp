@echo off

cd \
mkdir osm_data
mkdir osm_data\continents
cd osm_data\continents

aria2c -x 2 -c https://download.geofabrik.de/africa-latest.osm.pbf
aria2c -x 2 -c https://download.geofabrik.de/antarctica-latest.osm.pbf
aria2c -x 2 -c https://download.geofabrik.de/asia-latest.osm.pbf
aria2c -x 2 -c https://download.geofabrik.de/australia-oceania-latest.osm.pbf
aria2c -x 2 -c https://download.geofabrik.de/central-america-latest.osm.pbf
aria2c -x 2 -c https://download.geofabrik.de/europe-latest.osm.pbf
aria2c -x 2 -c https://download.geofabrik.de/north-america-latest.osm.pbf
aria2c -x 2 -c https://download.geofabrik.de/south-america-latest.osm.pbf

echo "Download completato!"