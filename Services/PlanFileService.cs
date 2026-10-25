using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using KompasTrochoidCam.Models;

namespace KompasTrochoidCam.Services
{
    /// <summary>Сохранение и загрузка проекта (.tcplan) — дерево обработки вместе с геометрией.</summary>
    public static class PlanFileService
    {
        public const string Extension = "tcplan";

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static void Save(MachiningProject project, string path)
        {
            string json = JsonSerializer.Serialize(project, Options);
            File.WriteAllText(path, json);
        }

        public static MachiningProject Load(string path)
        {
            string json = File.ReadAllText(path);
            var project = JsonSerializer.Deserialize<MachiningProject>(json, Options);
            if (project == null) throw new InvalidOperationException("Файл проекта пуст или повреждён.");
            if (project.Plan == null) project.Plan = new MachiningPlan();
            return project;
        }
    }
}
