using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TsMap.Helpers.Logger;

namespace TsMap.FileSystem
{
    /// <summary>
    /// Represents a directory in the filesystem
    /// <para>Is unaware of it's own location/path</para>
    /// </summary>
    public class UberDirectory
    {
        /// <summary>
        /// All the entries (hash or zip) for the current directory
        /// </summary>
        private readonly List<Entry> _entries = new List<Entry>();
        /// <summary>
        /// Names of subdirectories in the current directory, name does NOT include it's absolute path
        /// </summary>
        private readonly List<string> _subDirectoryNames = new List<string>();
        /// <summary>
        /// Names of subfiles in the current directory, name does NOT include it's absolute path
        /// </summary>
        private readonly List<string> _subFilesNames = new List<string>();

        public UberDirectory() { }

        /// <summary>
        /// Adds a new <see cref="Entry"/> for the current directory
        /// </summary>
        public void AddNewEntry(Entry entry)
        {
            _entries.Add(entry);
        }

        /// <summary>
        /// Adds the name of a subdirectory to the current directory
        /// </summary>
        /// <param name="name">Name of the directory. Just the name, not the path</param>
        public void AddSubDirName(string name)
        {
            name = CleanEntryName(name);
            if (string.IsNullOrEmpty(name)) return;
            _subDirectoryNames.Add(name);
        }

        /// <summary>
        /// Adds the name of a subfile to the current directory
        /// </summary>
        /// <param name="name">Name of the file. Just the name, not the path</param>
        public void AddSubFileName(string name)
        {
            name = CleanEntryName(name);
            if (string.IsNullOrEmpty(name)) return;
            _subFilesNames.Add(name);
        }

        /// <summary>
        /// Some mods (eg. Alaska_North_to_the_Future.scs) pack directory listings with a
        /// stray trailing '\r' (or other control character) appended to every entry name,
        /// which breaks exact-match file lookups. Strip it here so the rest of the system
        /// works with clean names.
        /// </summary>
        private static string CleanEntryName(string name)
        {
            if (name == null) return null;
            return name.TrimEnd('\r', '\n', '\0');
        }

        /// <summary>
        /// Returns the files in the folder
        /// </summary>
        /// <param name="filter">Optional filter to only return files that contain the filter</param>
        public List<string> GetFiles(string filter = "")
        {
            if (filter == "")
            {
                return _subFilesNames;
            }
            else
            {
                return _subFilesNames.Where(f => f.Contains(filter))
                                     .ToList();
            }
        }

        /// <summary>
        /// Get files for the specified extensions with a path added to the start.
        /// </summary>
        /// <param name="path">Path to be prefixed to the file names</param>
        /// <param name="extensionFilter">One or more extensions to get files for. Should start with a dot(.) (eg. ".sii")</param>
        public List<string> GetFilesByExtension(string path, params string[] extensionFilter)
        {
            if (path.Length > 0 && !path.EndsWith("/")) path += "/";
            if (extensionFilter == null)
            {
                return _subFilesNames.Select(f => $"{path}{f}")
                                     .ToList();
            }
            else
            {
                var result = new List<string>();
                foreach (var f in _subFilesNames)
                {
                    string ext;
                    try
                    {
                        ext = Path.GetExtension(f);
                    }
                    catch (ArgumentException)
                    {
                        Logger.Instance.Warning($"Skipping directory entry with invalid/corrupted file name (likely from a mod's directory listing): '{f}'");
                        continue;
                    }

                    if (extensionFilter.Contains(ext))
                    {
                        result.Add($"{path}{f}");
                    }
                }
                return result;
            }
        }

        public List<string> GetSubDirectoryNames()
        {
            return _subDirectoryNames;
        }
    }
}
