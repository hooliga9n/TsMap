using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System;
using System.Text;
using TsMap.FileSystem;

namespace TsMap.Helpers
{
    public class LocalizationManager
    {
        private readonly Dictionary<string, Dictionary<string, string>> _localization = new Dictionary<string, Dictionary<string, string>>();

        public string SelectedLocalization = "";

        public LocalizationManager()
        {
            _localization.Add("None", new Dictionary<string, string>());
        }

        public void LoadLocaleValues()
        {
            var localeDir = UberFileSystem.Instance.GetDirectory("locale");
            if (localeDir == null)
            {
                Logger.Logger.Instance.Error("Could not find locale directory.");
                return;
            }
            foreach (var localeDirDirectoryName in localeDir.GetSubDirectoryNames())
            {
                var localeDirDirectory = UberFileSystem.Instance.GetDirectory($"locale/{localeDirDirectoryName}");

                foreach (var localeFilePath in localeDirDirectory.GetFilesByExtension($"locale/{localeDirDirectoryName}", ".sui", ".sii"))
                {
                    ParseLocaleFile(localeFilePath, localeDirDirectoryName);
                }
            }
        }

        private void ParseLocaleFile(string localeFilePath, string locale)
        {
            var localeFile = UberFileSystem.Instance.GetFile(localeFilePath);
            var entryContents = localeFile.Entry.Read();
            var magic = MemoryHelper.ReadUInt32(entryContents, 0);
            var fileContents = (magic == 21720627) ? MemoryHelper.Decrypt3Nk(entryContents) : Encoding.UTF8.GetString(entryContents);
            if (fileContents == null)
            {
                Logger.Logger.Instance.Error($"Could not read locale file '{localeFilePath}'");
                return;
            }

            var key = string.Empty;

            foreach (var l in fileContents.Split('\n'))
            {
                if (!l.Contains(":")) continue;

                if (l.Contains("key[]"))
                {
                    key = NormalizeKey(l.Split('\"')[1]);
                }
                else if (l.Contains("val[]"))
                {
                    var val = l.Split('\"')[1];
                    if (key != string.Empty && val != string.Empty)
                    {
                        AddLocaleValue(locale, key, val);
                    }
                }
            }
        }

        private static string NormalizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;
            key = key.Trim().Trim('\"');
            while (key.StartsWith("@", StringComparison.Ordinal))
            {
                key = key.Substring(1);
            }
            return key.Trim();
        }

        /// <summary>
        /// Change the selected localization to the provided one
        /// </summary>
        /// <param name="localeName">Localization to change to</param>
        public void ChangeLocalization(string localeName)
        {
            SelectedLocalization = localeName;
            Logger.Logger.Instance.Debug($"Switched localization to '{localeName}'");
        }

        private void AddLocale(string localeName)
        {
            if (!_localization.ContainsKey(localeName))
            {
                _localization.Add(localeName, new Dictionary<string, string>());
            }
        }

        /// <summary>
        /// Gets the localized name for the given locale and key.
        /// </summary>
        /// <param name="localized_name_key">Key for the localized name</param>
        /// <param name="localeName">Name of the locale eg. 'en_gb' to get the value in, if not provided will use <see cref="SelectedLocalization"/></param>
        /// <returns>
        /// String - If key exists for the given locale name
        /// <para>Null - If it could not be found</para>
        /// </returns>
        public string GetLocaleValue(string localized_name_key, string localeName = "")
        {
            var key = NormalizeKey(localized_name_key);
            if (string.IsNullOrEmpty(key)) return null;
            if (string.IsNullOrEmpty(localeName)) localeName = SelectedLocalization;

            if (_localization.ContainsKey(localeName) && _localization[localeName].TryGetValue(key, out var value))
            {
                return value;
            }

            // Always prefer English over the technical city_name when a Polish
            // translation is missing. This keeps generated maps free of raw
            // names from city_name which may be Cyrillic in map mods.
            if (!string.Equals(localeName, "en_gb", StringComparison.OrdinalIgnoreCase) &&
                _localization.ContainsKey("en_gb") &&
                _localization["en_gb"].TryGetValue(key, out var englishValue))
            {
                return englishValue;
            }

            return null;
        }

        public string GetPreferredLocaleValue(string localized_name_key)
        {
            return GetLocaleValue(localized_name_key, "en_gb")
                ?? GetLocaleValue(localized_name_key, "en_gb")
                ?? GetLocaleValue(localized_name_key, SelectedLocalization);
        }

        public void AddLocaleValue(string localeName, string localized_name_key, string localized_name)
        {
            var key = NormalizeKey(localized_name_key);
            if (string.IsNullOrEmpty(localeName) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(localized_name)) return;

            if (!_localization.ContainsKey(localeName))
            {
                AddLocale(localeName);
            }

            // Later locale files should not overwrite an existing value from
            // the same locale, preserving the original project's precedence.
            if (!_localization[localeName].ContainsKey(key))
            {
                _localization[localeName].Add(key, localized_name);
            }
        }

        public List<string> GetLocales() => _localization.Keys.ToList();
    }
}
