using System;
using System.IO;
using Newtonsoft.Json;
using StradarioApp.Models;

namespace StradarioApp.Services
{
    public static class ProjectService
    {
        public static StradarioProject Load(string path)
        {
            var json    = File.ReadAllText(path);
            var project = JsonConvert.DeserializeObject<StradarioProject>(json)
                          ?? new StradarioProject();
            return project;
        }

        public static void Save(StradarioProject project, string path)
        {
            project.LastModified = DateTime.Now;
            var json = JsonConvert.SerializeObject(project, Formatting.Indented);
            File.WriteAllText(path, json);
        }

        /// <summary>Update the view state fields in the project (before saving).</summary>
        public static void UpdateViewState(StradarioProject project,
            double centerLon, double centerLat, double zoom)
        {
            project.ViewCenterLon = centerLon;
            project.ViewCenterLat = centerLat;
            project.ViewZoom      = zoom;
        }
    }
}
