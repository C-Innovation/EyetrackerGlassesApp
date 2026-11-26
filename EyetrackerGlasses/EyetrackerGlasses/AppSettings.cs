using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace EyetrackerGlasses
{
    /// <summary>
    /// Сохраняемые настройки приложения.
    /// <para>
    /// Добавляя своё поле: объявите свойство с <see cref="XmlElementAttribute"/>
    /// и задайте значение по умолчанию в конструкторе. Старые файлы настроек
    /// читаются без ошибок — отсутствующие элементы остаются со значением
    /// по умолчанию.
    /// </para>
    /// </summary>
    [Serializable]
    [XmlRoot("AppSettings")]
    [XmlType("AppSettings")]
    public class AppSettings
    {
        /// <summary>
        /// Версия приложения, записавшего файл. Помогает при миграции настроек.
        /// </summary>
        [XmlAttribute("Version")]
        public string AppVersion { get; set; }

        /// <summary>
        /// Ключ выбранной цветовой темы.
        /// </summary>
        [XmlElement("ThemeKey")]
        public string ThemeKey { get; set; }

        /// <summary>
        /// Ключ выбранного языка интерфейса.
        /// </summary>
        [XmlElement("LanguageKey")]
        public string LanguageKey { get; set; }

        public AppSettings()
        {
            AppVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
            ThemeKey = AppThemes.Light;
            LanguageKey = AppLanguages.English;
        }
    }

    [Serializable]
    [XmlRoot("EyetrackerDevice")]
    [XmlType("EyetrackerDevice")]
    public class EyetrackerDevice
    {
        [XmlElement("HostName")]
        public string HostName { get; set; }

        [XmlElement("GroupLSL")]
        public string GroupLSL { get; set; }

        [XmlElement("UserLSL")]
        public string UserLSL { get; set; }

        [XmlArray("DeviceAiModels")]
        [XmlArrayItem("AiModelName")]
        public List<string> AiModels = new List<string>();

        public EyetrackerDevice()
        {
            HostName = "";
            GroupLSL = "";
            UserLSL = "";
            AiModels = new List<string>();
        }
    }

    [Serializable]
    [XmlRoot("EyetrackerDevices")]
    [XmlInclude(typeof(EyetrackerDevice))] // include type class Person
    public class EyetrackerDevices
    {
        [XmlArray("Devices")]
        [XmlArrayItem("EyetrackerDevice")]
        public List<EyetrackerDevice> Devices = new List<EyetrackerDevice>();

        public EyetrackerDevices()
        {
            Devices = new List<EyetrackerDevice>();
        }
    }

    [Serializable]
    [XmlRoot("AiModelParams")]
    [XmlType("AiModelParams")]
    public class AiModelParams
    {
        [XmlElement("ModelName")]
        public string ModelName { get; set; }

        [XmlElement("Width")]
        public int Width { get; set; }

        [XmlElement("Height")]
        public int Height { get; set; }

        [XmlElement("Channels")]
        public int Channels { get; set; }

        [XmlElement("MaxBatchSize")]
        public int MaxBatchSize { get; set; }

        [XmlElement("NumClasses")]
        public int NumClasses { get; set; }

        public AiModelParams()
        {
            ModelName = "";
            Width = 640;
            Height = 640;
            Channels = 3;
            MaxBatchSize = 1;
            NumClasses = 80;
        }
    }
}
