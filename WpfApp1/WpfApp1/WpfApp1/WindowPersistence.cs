using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace WpfApp1
{
    /// <summary>
    /// Serializable object containing all persistable window state.
    /// </summary>
    public class WindowSettings
    {
        public double Top { get; set; } = double.NaN;
        public double Left { get; set; } = double.NaN;
        public double Width { get; set; } = double.NaN;
        public double Height { get; set; } = double.NaN;
        public WindowState WindowState { get; set; } = WindowState.Normal;

        // Stores the preferred size for each monitor ID (Device Name)
        public Dictionary<string, Size> MonitorSizeCache { get; set; } = new Dictionary<string, Size>();
    }

    /// <summary>
    /// Interface for storage mechanism (File, Database, Registry, etc.)
    /// </summary>
    public interface IWindowSettingsStore
    {
        void Save(string windowId, WindowSettings settings);
        WindowSettings Load(string windowId);
    }

    /// <summary>
    /// Default implementation that saves settings to a JSON file in the application directory.
    /// </summary>
    public class JsonFileSettingsStore : IWindowSettingsStore
    {
        private readonly string _storageFolder;

        public JsonFileSettingsStore(string storageFolder = null)
        {
            _storageFolder = storageFolder ?? AppDomain.CurrentDomain.BaseDirectory;
        }

        private string GetPath(string windowId) => Path.Combine(_storageFolder, $"window_settings_{windowId}.json");

        public void Save(string windowId, WindowSettings settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetPath(windowId), json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        public WindowSettings Load(string windowId)
        {
            try
            {
                string path = GetPath(windowId);
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<WindowSettings>(json);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load settings: {ex.Message}");
            }
            return null;
        }
    }
}